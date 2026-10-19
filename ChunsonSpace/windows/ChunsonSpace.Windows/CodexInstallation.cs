using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ChunsonSpace.Windows;

public enum DesktopInstallationStatus { Installed, NotInstalled, Unknown }
public sealed record DesktopInstallationState(DesktopInstallationStatus Status, string Version = "", string PackageFamily = "", string Message = "");
public sealed record CodexInstallPlan(DesktopInstallationState State, bool WingetAvailable, string ProductId, string StoreUrl);
public enum CodexInstallStatus { Installed, StoreOpened, Failed, Pending, Unknown }
public sealed record CodexInstallResult(CodexInstallStatus Status, bool AlreadyInstalled, string Message, bool StoreOpened = false, int? ExitCode = null)
{
    public bool Success => Status == CodexInstallStatus.Installed;
}
public sealed record InstallCommandReceipt(bool Completed, int? ExitCode);
public interface ICodexInstallSystem
{
    Task<DesktopInstallationState> ReadStateAsync(CancellationToken cancellationToken);
    bool HasWinget();
    Task<InstallCommandReceipt> InstallAsync(CancellationToken cancellationToken);
    bool OpenStore();
}

public sealed class CodexInstallService(ICodexInstallSystem system)
{
    public const string StoreProductId = "9PLM9XGG6VKS";
    public const string StoreUrl = "ms-windows-store://pdp/?productid=" + StoreProductId;
    public async Task<CodexInstallPlan> PlanAsync(CancellationToken cancellationToken = default) => new(await system.ReadStateAsync(cancellationToken), system.HasWinget(), StoreProductId, StoreUrl);
    // Explicit user action. Not called by startup, profile preflight, or account switching.
    public async Task<CodexInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        var before = await system.ReadStateAsync(cancellationToken);
        if (before.Status == DesktopInstallationStatus.Installed) return new(CodexInstallStatus.Installed, true, "Codex 桌面端已安装");
        if (before.Status == DesktopInstallationStatus.Unknown) return new(CodexInstallStatus.Unknown, false, "Chunson Space 无法确认桌面安装状态，本次没有发起安装");
        if (!system.HasWinget())
        {
            var opened = system.OpenStore();
            return new(opened ? CodexInstallStatus.StoreOpened : CodexInstallStatus.Failed, false, opened ? "已打开 Codex 商店页面，请完成获取；安装尚未确认" : "未找到 winget，请从 Microsoft Store 安装 Codex", opened);
        }
        InstallCommandReceipt receipt;
        try { receipt = await system.InstallAsync(cancellationToken); }
        catch (OperationCanceledException) { return new(CodexInstallStatus.Pending, false, "安装等待已结束，实际安装结果尚未确认"); }
        catch (Exception) { return new(CodexInstallStatus.Pending, false, "安装请求结果无法确认，请检查 Microsoft Store 状态"); }
        var after = await system.ReadStateAsync(CancellationToken.None);
        if (after.Status == DesktopInstallationStatus.Installed) return new(CodexInstallStatus.Installed, false, "Codex 桌面安装已通过注册状态确认", ExitCode: receipt.ExitCode);
        if (!receipt.Completed || after.Status == DesktopInstallationStatus.Unknown) return new(CodexInstallStatus.Pending, false, "安装尚未确认完成，请稍后重新检查", ExitCode: receipt.ExitCode);
        bool fallback = system.OpenStore();
        return new(fallback ? CodexInstallStatus.StoreOpened : CodexInstallStatus.Failed, false, "winget 完成后没有检测到 Codex 注册包；可从 Microsoft Store 继续安装", fallback, receipt.ExitCode);
    }
}

public sealed class WindowsCodexInstallSystem(ICommandRunner commands, string localAppData) : ICodexInstallSystem
{
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private string? Winget()
    {
        var alias = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var candidate = Path.Combine(path.Trim('"'), "winget.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
    public bool HasWinget() => Winget() is not null;
    public async Task<DesktopInstallationState> ReadStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var local = WindowsDesktopLaunchSystem.FindUnpackagedTarget(localAppData);
        if (local is not null)
        {
            var version = FileVersionInfo.GetVersionInfo(local.Executable).ProductVersion ?? "";
            return new(DesktopInstallationStatus.Installed, version, Message: "已发现独立安装的 Codex 桌面程序");
        }
        const string script = """
            $ErrorActionPreference='Stop'
            [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
            $p=Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | Select-Object -First 1
            if($p){@{installed=$true;version=$p.Version.ToString();family=$p.PackageFamilyName}|ConvertTo-Json -Compress}
            else{@{installed=$false;version='';family=''}|ConvertTo-Json -Compress}
            """;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var response = await commands.RunAsync(PowerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], timeout.Token);
            if (response.ExitCode != 0) return new(DesktopInstallationStatus.Unknown, Message: "注册包查询失败");
            using var json = JsonDocument.Parse(response.Output);
            var root = json.RootElement;
            if (!root.TryGetProperty("installed", out var installed) || installed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return new(DesktopInstallationStatus.Unknown, Message: "注册包查询格式不符");
            return new(installed.GetBoolean() ? DesktopInstallationStatus.Installed : DesktopInstallationStatus.NotInstalled, root.GetProperty("version").GetString() ?? "", root.GetProperty("family").GetString() ?? "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(DesktopInstallationStatus.Unknown, Message: "注册包查询超时"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(DesktopInstallationStatus.Unknown, Message: "无法读取桌面安装状态"); }
    }
    public async Task<InstallCommandReceipt> InstallAsync(CancellationToken cancellationToken)
    {
        var winget = Winget();
        if (winget is null) return new(true, 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            var result = await commands.RunAsync(winget, ["install", "--id", CodexInstallService.StoreProductId, "--source", "msstore", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"], timeout.Token);
            return new(true, result.ExitCode);
        }
        catch (OperationCanceledException) { return new(false, null); }
    }
    public bool OpenStore()
    {
        try { using var process = Process.Start(new ProcessStartInfo(CodexInstallService.StoreUrl) { UseShellExecute = true }); return true; }
        catch (Exception) { return false; }
    }
}
