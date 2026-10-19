using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ChunsonSpace.Windows;

public enum SilentTimezoneStatus { Enabled, Disabled, Unknown }
public sealed record SilentTimezoneState(SilentTimezoneStatus Status, bool HelperReadable, long? LastResult = null, string Message = "");
public sealed record SilentTimezoneEnableResult(bool Success, bool Enabled, bool Created, bool Elevated, long? LastResult, string Message);
public interface ISilentTimezoneSystem
{
    Task<SilentTimezoneState> InspectAsync(CancellationToken cancellationToken);
    Task<bool> SetupExplicitlyAsync(CancellationToken cancellationToken);
}

public sealed class SilentTimezoneChannelService(ISilentTimezoneSystem system)
{
    public Task<SilentTimezoneState> InspectAsync(CancellationToken cancellationToken = default) => system.InspectAsync(cancellationToken);
    // This is the sole elevated action and is only called by an explicit enable command.
    public async Task<SilentTimezoneEnableResult> EnableAsync(CancellationToken cancellationToken = default)
    {
        var before = await system.InspectAsync(cancellationToken);
        if (before.Status == SilentTimezoneStatus.Enabled && before.HelperReadable) return new(true, true, false, false, before.LastResult, "Chunson Space 静默时区通道已注册，当前切换仍走直接系统接口");
        if (before.Status == SilentTimezoneStatus.Unknown) return new(false, false, false, false, before.LastResult, "无法确认静默时区通道，本次没有提权或修改系统时区");
        bool submitted;
        try { submitted = await system.SetupExplicitlyAsync(cancellationToken); }
        catch (OperationCanceledException) { return new(false, false, false, true, null, "静默通道注册等待已取消，系统时区没有修改"); }
        catch (Exception) { return new(false, false, false, true, null, "静默通道注册未确认，系统时区没有修改"); }
        if (!submitted) return new(false, false, false, true, null, "授权被拒绝或通道注册失败，系统时区没有修改");
        var after = await system.InspectAsync(cancellationToken);
        if (after.Status != SilentTimezoneStatus.Enabled || !after.HelperReadable) return new(false, false, false, true, after.LastResult, "授权完成，但任务入口或普通用户脚本读取没有确认，系统时区没有修改");
        return new(true, true, true, true, after.LastResult, "静默时区通道已注册且普通用户可读，当前切换仍走直接系统接口");
    }
}

public sealed class WindowsSilentTimezoneSystem(ICommandRunner commands, string programFiles, string currentUser) : ISilentTimezoneSystem
{
    public const string TaskName = "ChunsonSpace-SetTZ";
    public string HelperPath { get; } = Path.Combine(Path.GetFullPath(programFiles), "ChunsonSpace", "tz", "set-tz.ps1");
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string Schtasks => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    public async Task<SilentTimezoneState> InspectAsync(CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(Schtasks, ["/query", "/tn", TaskName, "/xml"], cancellationToken);
        if (result.ExitCode != 0)
        {
            string text = (result.Output + result.Error).ToLowerInvariant();
            bool missing = result.ExitCode == 1 && (text.Contains("cannot find", StringComparison.Ordinal) || text.Contains("does not exist", StringComparison.Ordinal) || text.Contains("找不到", StringComparison.Ordinal) || text.Contains("不存在", StringComparison.Ordinal));
            return new(missing ? SilentTimezoneStatus.Disabled : SilentTimezoneStatus.Unknown, false, Message: missing ? "通道尚未注册" : "无法查询静默时区任务");
        }
        bool validAction;
        try
        {
            using var reader = XmlReader.Create(new StringReader(result.Output), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var xml = XDocument.Load(reader);
            var exec = xml.Descendants().Where(element => element.Name.LocalName == "Exec").ToArray();
            validAction = exec.Length == 1 && exec[0].Elements().FirstOrDefault(element => element.Name.LocalName == "Command") is { } command
                && (command.Value.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) || command.Value.Equals(PowerShell, StringComparison.OrdinalIgnoreCase))
                && exec[0].Elements().FirstOrDefault(element => element.Name.LocalName == "Arguments") is { } arguments
                && arguments.Value.Trim().Equals("-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + HelperPath + "\"", StringComparison.OrdinalIgnoreCase);
        }
        catch (XmlException) { return new(SilentTimezoneStatus.Unknown, false, Message: "静默通道任务XML损坏"); }
        if (!validAction) return new(SilentTimezoneStatus.Unknown, false, Message: "同名计划任务的动作不符合助手入口，未修改它");
        bool readable;
        try { using var stream = new FileStream(HelperPath, FileMode.Open, FileAccess.Read, FileShare.Read); readable = stream.Length > 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { readable = false; }
        long? lastResult = null;
        var script = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; [long](Get-ScheduledTaskInfo -TaskName '" + TaskName + "').LastTaskResult";
        var last = await commands.RunAsync(PowerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], cancellationToken);
        if (last.ExitCode == 0 && long.TryParse(last.Output, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)) lastResult = value;
        return new(SilentTimezoneStatus.Enabled, readable, lastResult, readable ? "静默通道入口已核对" : "普通用户读不到助手脚本");
    }

    public async Task<bool> SetupExplicitlyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser) || currentUser.Any(c => char.IsControl(c))) return false;
        string script = BuildSetupScript(HelperPath, currentUser, PowerShell);
        var info = new ProcessStartInfo(PowerShell) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.Arguments = WindowsDesktopLaunchSystem.JoinArguments(["-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
        try { using var process = Process.Start(info); if (process is null) return false; await process.WaitForExitAsync(cancellationToken); return process.ExitCode == 0; }
        catch (Win32Exception) { return false; }
    }

    public static string BuildSetupScript(string helper, string user, string powershell)
    {
        string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var helperData = Convert.ToBase64String(Encoding.ASCII.GetBytes(HelperScript));
        return "$ErrorActionPreference='Stop'\n$helper=" + Literal(helper) + "\n$user=" + Literal(user) + "\n$powershell=" + Literal(powershell) + "\n$body=[Text.Encoding]::ASCII.GetString([Convert]::FromBase64String('" + helperData + "'))\n" + """
            $dir=Split-Path -Parent $helper
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            [IO.File]::WriteAllText($helper,$body,[Text.Encoding]::ASCII)
            $acl=[Security.AccessControl.DirectorySecurity]::new()
            $acl.SetAccessRuleProtection($true,$false)
            $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
            foreach($sid in @('S-1-5-32-544','S-1-5-18','S-1-5-32-545')) {
                $rights=if($sid -eq 'S-1-5-32-545'){'ReadAndExecute'}else{'FullControl'}
                $identity=[Security.Principal.SecurityIdentifier]::new($sid)
                $rule=[Security.AccessControl.FileSystemAccessRule]::new($identity,$rights,'ContainerInherit,ObjectInherit','None','Allow')
                $acl.AddAccessRule($rule)
            }
            Set-Acl -LiteralPath $dir -AclObject $acl
            $fileAcl=[Security.AccessControl.FileSecurity]::new()
            $fileAcl.SetAccessRuleProtection($true,$false)
            $fileAcl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
            foreach($sid in @('S-1-5-32-544','S-1-5-18','S-1-5-32-545')) {
                $rights=if($sid -eq 'S-1-5-32-545'){'ReadAndExecute'}else{'FullControl'}
                $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),$rights,'Allow'))
            }
            Set-Acl -LiteralPath $helper -AclObject $fileAcl
            $taskAction=New-ScheduledTaskAction -Execute $powershell -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+$helper+'"')
            $principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
            $trigger=New-ScheduledTaskTrigger -Once -At ([DateTime]::Today)
            Register-ScheduledTask -TaskName 'ChunsonSpace-SetTZ' -Action $taskAction -Principal $principal -Trigger $trigger -Force | Out-Null
            exit 0
            """;
    }
    public const string HelperScript = """
        $ErrorActionPreference='Stop'
        $file=Join-Path $env:LOCALAPPDATA 'ChunsonSpace\tz\tz.txt'
        if(-not [IO.File]::Exists($file)){exit 2}
        $timezone=[IO.File]::ReadAllText($file).Trim()
        if(-not $timezone){exit 2}
        $valid=(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Time Zones').PSChildName
        if($valid -notcontains $timezone){exit 3}
        $tzutil=Join-Path ([Environment]::GetFolderPath('System')) 'tzutil.exe'
        & $tzutil /s $timezone
        exit $LASTEXITCODE
        """;
}
