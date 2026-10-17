using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace ChunsonLogin;

public interface ISystemTimezone
{
    Task<string> ReadAsync(CancellationToken token);
    Task SetAsync(string windowsId, CancellationToken token);
}
public sealed record TimezoneCommandResult(int ExitCode, string Output, string Error = "");

public sealed class SystemTimezone : ISystemTimezone
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<TimezoneCommandResult>> command;
    private readonly Func<string, CancellationToken, Task<bool>> elevate;
    private static string Tzutil => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tzutil.exe");

    public SystemTimezone(Func<IReadOnlyList<string>, CancellationToken, Task<TimezoneCommandResult>>? command = null,
        Func<string, CancellationToken, Task<bool>>? elevate = null)
    { this.command = command ?? RunAsync; this.elevate = elevate ?? ElevateAsync; }

    public async Task<string> ReadAsync(CancellationToken token)
    {
        var result = await command(["/g"], token);
        var value = result.Output.Trim();
        if (result.ExitCode != 0 || !ValidWindowsId(value)) throw new InvalidOperationException("无法读取 Windows 当前时区。");
        return value;
    }

    public async Task SetAsync(string windowsId, CancellationToken token)
    {
        if (!ValidWindowsId(windowsId)) throw new InvalidOperationException("Windows 时区标识无效，未修改系统。");
        token.ThrowIfCancellationRequested();
        if (await ReadAsync(token) == windowsId) return;
        token.ThrowIfCancellationRequested();
        // Complete the write/readback after dispatch. Cancellation must not hide an applied setting.
        var result = await command(["/s", windowsId], CancellationToken.None);
        if (result.ExitCode != 0)
        {
            if (!RequiresPrivilege(result)) throw new InvalidOperationException("Windows 未能应用时区，请检查系统设置后重试。");
            if (!await elevate(windowsId, CancellationToken.None)) throw new InvalidOperationException("未取得 Windows 时区设置权限，时区同步未完成。");
        }
        if (await ReadAsync(CancellationToken.None) != windowsId)
            throw new InvalidOperationException("系统时区回读与目标不一致，请检查 Windows 设置；未恢复旧时区。");
        TimeZoneInfo.ClearCachedData();
    }

    private static bool ValidWindowsId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim()
            || !TimeZoneInfo.TryConvertWindowsIdToIanaId(value, out _)) return false;
        try { return TimeZoneInfo.FindSystemTimeZoneById(value).Id == value; }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
    }

    private static bool RequiresPrivilege(TimezoneCommandResult result)
    {
        var detail = result.Output + " " + result.Error;
        return result.ExitCode is 5 or 1314 || new[] { "Access is denied", "privilege", "permission", "拒绝访问", "权限", "特权", "0x00000005", "0x00000522" }
            .Any(value => detail.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<TimezoneCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(Tzutil) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("无法启动 Windows 时区命令。");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { try { process.Kill(); } catch (InvalidOperationException) { } throw; }
        return new(process.ExitCode, await output, await error);
    }

    private static async Task<bool> ElevateAsync(string windowsId, CancellationToken token)
    {
        if (!ValidWindowsId(windowsId)) return false;
        var info = new ProcessStartInfo(Tzutil) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("/s"); info.ArgumentList.Add(windowsId);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return false;
            await process.WaitForExitAsync(token);
            return process.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }
    }
}
