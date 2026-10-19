using System.Diagnostics;
using System.Text;

namespace ChunsonSpace.Windows;

public sealed record CommandResult(int ExitCode, string Output, string Error = "");
public static class WindowsPowerShellCommand
{
    // PowerShell 7 launchers can pass an incompatible PSModulePath to Windows PowerShell 5.1.
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(
        "$env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules'); " + script));
}
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
public sealed class WindowsCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        if (Path.GetFileName(executable).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            info.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "Modules");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("系统命令未创建进程");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        var output = await stdout;
        var error = await stderr;
        // Error text is available to purpose-specific classifiers; never display raw command diagnostics.
        return new(process.ExitCode, output.Trim('\uFEFF', ' ', '\r', '\n'), error);
    }
}

public sealed class WindowsTimezoneSystem(ICommandRunner runner) : ITimezoneSystem
{
    private static string Tzutil => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tzutil.exe");
    public string? ResolveWindowsId(string value)
    {
        value = value.Trim();
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(value, out var mapped)) return mapped;
        try { return TimeZoneInfo.FindSystemTimeZoneById(value).Id; }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
    public async Task<TimezoneReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(Tzutil, ["/g"], cancellationToken);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output) ? new(true, result.Output) : new(false, "", "无法读取系统时区");
    }
    public async Task<bool> SetAsync(string windowsId, CancellationToken cancellationToken)
    {
        if (ResolveWindowsId(windowsId) != windowsId) return false;
        return (await runner.RunAsync(Tzutil, ["/s", windowsId], cancellationToken)).ExitCode == 0;
    }
}
