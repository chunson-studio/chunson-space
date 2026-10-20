using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ChunsonSpace.Windows;

public sealed record RegionState(bool Success, int GeoId, string WindowsTimezone);
public sealed record RegionConfigurationResult(bool Success, bool Changed, string Message);
public interface IRegionSystem
{
    int? ResolveGeoId(string countryCode);
    string? ResolveWindowsTimezone(string iana);
    Task<RegionState> ReadAsync(CancellationToken token);
    Task<bool> SetTimezoneExplicitlyAsync(string windowsId, CancellationToken token);
    Task<bool> SetTimezoneAutomaticallyAsync(string windowsId, CancellationToken token)
        => SetTimezoneExplicitlyAsync(windowsId, token);
    bool SetHomeLocation(string countryCode, int geoId);
}

// Explicit environment configuration or confirmed login choice; never background screening.
public sealed class SystemRegionService(IRegionSystem system)
{
    public Task<RegionConfigurationResult> ConfigureExplicitlyAsync(string countryCode, string ianaTimezone, CancellationToken token = default,
        Func<CancellationToken, Task<TimezoneResult>>? baselinePreflight = null,
        Func<string, CancellationToken, Task<TimezoneResult>>? commitBaseline = null, bool timezoneOnly = false)
        => ConfigureExplicitlyAsync(countryCode, ianaTimezone, true, null, token, baselinePreflight, commitBaseline, timezoneOnly);
    public async Task<RegionConfigurationResult> ConfigureExplicitlyAsync(string countryCode, string ianaTimezone,
        bool allowElevation, Func<CancellationToken, Task<bool>>? stillCurrent, CancellationToken token = default,
        Func<CancellationToken, Task<TimezoneResult>>? baselinePreflight = null,
        Func<string, CancellationToken, Task<TimezoneResult>>? commitBaseline = null, bool timezoneOnly = false)
    {
        var geoId = timezoneOnly ? 0 : system.ResolveGeoId(countryCode);
        var windowsId = ianaTimezone.Contains('/') ? system.ResolveWindowsTimezone(ianaTimezone) : null;
        if (geoId is null || windowsId is null) return new(false, false, "出口国家或具体时区没有可靠的 Windows 对应值，未修改系统。");
        var scope = timezoneOnly ? "系统时区" : "国家/地区和系统时区";
        if (baselinePreflight is not null)
        {
            var ready = await baselinePreflight(token);
            if (!ready.Success) return new(false, false, ready.Message);
        }
        var before = await system.ReadAsync(token);
        if (!before.Success) return new(false, false, "无法读取当前国家/地区或系统时区，未修改系统。");
        if (timezoneOnly) geoId = before.GeoId;
        if (before.GeoId == geoId && before.WindowsTimezone == windowsId)
        {
            if (commitBaseline is not null)
            {
                var committed = await commitBaseline(windowsId, token);
                if (!committed.Success) return new(false, false, committed.Message);
            }
            return new(true, false, scope + (commitBaseline is null ? "已经匹配出口，未重复修改系统值。" : "已经匹配出口，已确认新时区基线；未重复修改系统值。"));
        }
        token.ThrowIfCancellationRequested();
        if (stillCurrent is not null && !await stillCurrent(token))
            return new(false, false, "出口已变化，未应用过期地区与时区；请等待重新检测。");
        // Once a write is dispatched, finish reread/recovery even if a UI wait is cancelled.
        bool regionAttempted = false;
        try
        {
            if (before.WindowsTimezone != windowsId)
            {
                _ = allowElevation ? await system.SetTimezoneExplicitlyAsync(windowsId, CancellationToken.None)
                    : await system.SetTimezoneAutomaticallyAsync(windowsId, CancellationToken.None);
                var timeAfter = await system.ReadAsync(CancellationToken.None);
                token.ThrowIfCancellationRequested();
                if (!timeAfter.Success) return await RecoverAsync(before, geoId.Value, windowsId, false);
                if (timeAfter.WindowsTimezone != windowsId)
                    return new(false, timeAfter.WindowsTimezone != before.WindowsTimezone, "系统时区未确认，国家/地区未修改。请手动点击同步时区重试。");
                if (timeAfter.GeoId != before.GeoId) return await RecoverAsync(before, geoId.Value, windowsId, regionAttempted);
            }
            token.ThrowIfCancellationRequested();
            if (stillCurrent is not null && !await stillCurrent(token))
                return new(false, before.WindowsTimezone != windowsId, "出口已变化，保留当前系统值，等待重新检测。");
            if (!timezoneOnly && before.GeoId != geoId) { regionAttempted = true; _ = system.SetHomeLocation(countryCode, geoId.Value); }
            var after = await system.ReadAsync(CancellationToken.None);
            if (after.Success && after.GeoId == geoId && after.WindowsTimezone == windowsId)
            {
                token.ThrowIfCancellationRequested();
                if (commitBaseline is not null)
                {
                    var committed = await commitBaseline(windowsId, CancellationToken.None);
                    if (!committed.Success)
                    {
                        var recovered = await RecoverAsync(before, geoId.Value, windowsId, regionAttempted);
                        return recovered with { Message = committed.Message + " " + recovered.Message };
                    }
                }
                return new(true, true, scope + "已匹配出口，并已回读确认。关闭分身或退出工具后仍保留当前设置。");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            var recovered = await RecoverAsync(before, geoId.Value, windowsId, regionAttempted);
            return recovered with { Message = error.GetType().Name + "：" + error.Message + " " + recovered.Message };
        }
        return await RecoverAsync(before, geoId.Value, windowsId, regionAttempted);
    }
    private async Task<RegionConfigurationResult> RecoverAsync(RegionState before, int targetGeo, string targetTimezone, bool regionAttempted)
    {
        try
        {
            var current = await system.ReadAsync(CancellationToken.None);
            if (!current.Success) return new(false, true, "环境配置未确认，当前系统值无法读取；请检查 Windows 国家/地区和时区。");
            bool changed = current.GeoId != before.GeoId || current.WindowsTimezone != before.WindowsTimezone;
            return new(false, changed, "环境配置未完成，已保留当前国家/地区和时区，不自动恢复原值；请检查 Windows 设置。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        { return new(false, true, "环境配置未完成，当前值未确认；不会自动恢复时区，请检查 Windows 国家/地区和时区。"); }
    }
}

public sealed class WindowsRegionSystem(ICommandRunner commands) : IRegionSystem
{
    public static string ReadHomeCountryCode()
    {
        var name = new StringBuilder(16);
        return GetUserDefaultGeoName(name, name.Capacity) > 0 ? name.ToString().ToUpperInvariant() : "";
    }
    private readonly WindowsTimezoneSystem timezone = new(commands);
    public int? ResolveGeoId(string countryCode)
    {
        if (countryCode.Length != 2 || countryCode.Any(c => c is < 'A' or > 'Z')) return null;
        try { var region = new RegionInfo(countryCode); return region.TwoLetterISORegionName == countryCode && region.GeoId > 0 ? region.GeoId : null; }
        catch (ArgumentException) { return null; }
    }
    public string? ResolveWindowsTimezone(string iana) => ChunsonSpace.Core.ProfileRules.WindowsTimezoneId(iana);
    public async Task<RegionState> ReadAsync(CancellationToken token)
    {
        var zone = await timezone.ReadAsync(token);
        int geo = GetUserGeoID(16); // GEOCLASS_NATION
        return new(zone.Success && geo > 0, geo, zone.Id);
    }
    public bool SetHomeLocation(string countryCode, int geoId)
    {
        if (geoId <= 0 || countryCode.Length > 0 && ResolveGeoId(countryCode) != geoId) return false;
        return SetUserGeoID(geoId);
    }
    public async Task<bool> SetTimezoneExplicitlyAsync(string windowsId, CancellationToken token)
    {
        if (timezone.ResolveWindowsId(windowsId) != windowsId) return false;
        if (await timezone.SetAsync(windowsId, token)) return true;
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tzutil.exe"))
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
          Arguments = WindowsDesktopLaunchSystem.JoinArguments(["/s", windowsId]) };
        try { using var process = Process.Start(info); if (process is null) return false; await process.WaitForExitAsync(token); return process.ExitCode == 0; }
        catch (Win32Exception) { return false; }
    }
    public Task<bool> SetTimezoneAutomaticallyAsync(string windowsId, CancellationToken token)
        => timezone.SetAsync(windowsId, token); // Background attempts never launch an elevation prompt.
    [DllImport("kernel32.dll")] private static extern int GetUserGeoID(int geoClass);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetUserDefaultGeoName(StringBuilder name, int count);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetUserGeoID(int geoId);
}
