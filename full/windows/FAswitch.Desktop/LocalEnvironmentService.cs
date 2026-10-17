using System.IO;
using System.Globalization;
using FAswitch.Accounts;
using FAswitch.Application;
using FAswitch.Core;
using FAswitch.Windows;

namespace FAswitch.Desktop;

public sealed record LocalEnvironmentReport(bool Verified, bool Matches, string Proxy, string Ip, string ExitTimezone, string SystemTimezone, string CountryCode, string Message)
{
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
    public string SystemCountryCode { get; init; } = "";
}

// Main-client networking is independent from every account's browser route.
public sealed class LocalEnvironmentService(Func<CancellationToken, Task<LoginProxySelection>> resolve,
    Func<string, CancellationToken, Task<EgressResult>> probe,
    ILaunchEgressProbe admission, Func<TimeZoneInfo>? readTimezone = null, Func<string>? readCountry = null) : IIdentityEnvironmentSource, ILaunchEgressProbe
{
    private TimeZoneInfo ReadZone() { if (readTimezone is not null) return readTimezone(); TimeZoneInfo.ClearCachedData(); return TimeZoneInfo.Local; }
    private string ReadCountry() => (readCountry ?? WindowsRegionSystem.ReadHomeCountryCode)();
    private static bool CountryMatches(string current, string target)
    {
        if (target.Length != 2 || target.Any(c => c is < 'A' or > 'Z') || current != target) return false;
        try { var country = new RegionInfo(target); return country.GeoId > 0 && country.TwoLetterISORegionName == target; }
        catch (ArgumentException) { return false; }
    }
    private static string MatchMessage(bool matches) => matches ? "本机默认代理出口与 Windows 国家/地区、时区一致。" : "默认代理出口与 Windows 国家/地区或时区不一致；可点击首页“同步地区与时区”。";
    private static string Iana(TimeZoneInfo zone) => TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var id) ? id : zone.Id;
    public bool AllowSystemTimezoneChange => false;
    public EnvironmentSettings Read(IdentityRef identity)
    {
        // SwitchService runs on its operation worker. No public request occurs here.
        var route = resolve(CancellationToken.None).GetAwaiter().GetResult();
        return new(route.Proxy, Iana(ReadZone()), "en-US", false, false);
    }
    public static bool Matches(TimeZoneInfo system, string timezone)
    {
        if (!ProfileRules.ValidTimezone(timezone)) return false;
        if (system.Id.Equals(timezone, StringComparison.OrdinalIgnoreCase)) return true;
        return TimeZoneInfo.TryConvertIanaIdToWindowsId(timezone, out var windows)
            && (windows.Equals(system.Id, StringComparison.OrdinalIgnoreCase)
                || TimeZoneInfo.TryConvertIanaIdToWindowsId(system.Id, out var own) && windows.Equals(own, StringComparison.OrdinalIgnoreCase));
    }
    public async Task<LocalEnvironmentReport> CheckAsync(CancellationToken token = default)
    {
        var zone = ReadZone();
        var proxy = "";
        try
        {
            var route = await resolve(token);
            proxy = route.Proxy;
            var exit = await probe(route.Proxy, token); // Fresh public region evidence; no scoring or account request.
            zone = ReadZone();
            if ((await resolve(token)).Proxy != route.Proxy)
                return new(false, false, route.Proxy, "", "", Iana(zone), "", "核对期间本机默认代理已变化，请重新检查。");
            var verified = LaunchEgressRules.Verified(new(exit.Ok, exit.Ip, exit.Timezone, exit.Message));
            var country = ReadCountry();
            var matches = verified && Matches(zone, exit.Timezone) && CountryMatches(country, exit.CountryCode);
            return new(verified, matches, route.Proxy, exit.Ip, exit.Timezone, Iana(zone), exit.CountryCode,
                !verified ? (exit.Message.Length > 0 ? exit.Message : "默认代理出口未确认，请检查本机代理。") : MatchMessage(matches)) { SystemCountryCode = country };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var step = proxy.Length == 0 ? "读取 FlClash 当前代理" : "查询本机出口、地区与时区";
            var reason = error is AccountException accountError && accountError.Code == AccountError.ExitUnavailable
                ? "请先确认 FlClash 已启动且当前配置可用。" : "请稍后重新核对。";
            return new(false, false, proxy, "", "", Iana(zone), "", step + "未完成（" + error.GetType().Name + "）。" + reason);
        }
    }
    public LocalEnvironmentReport RereadSystemEnvironment(LocalEnvironmentReport report)
    {
        var zone = ReadZone();
        var country = ReadCountry();
        var matches = report.Verified && Matches(zone, report.ExitTimezone) && CountryMatches(country, report.CountryCode);
        return report with { SystemTimezone = Iana(zone), SystemCountryCode = country, Matches = matches, Message = !report.Verified ? report.Message : MatchMessage(matches) };
    }
    public async Task<LaunchEgressEvidence> ProbeAsync(IdentityRef identity, string resolvedProxy, CancellationToken token)
    {
        var route = await resolve(token);
        if (route.Proxy != resolvedProxy) return new(false, "", "", "本机默认代理已变化，请重新启动。");
        var exit = await admission.ProbeAsync(identity, resolvedProxy, token);
        if (!LaunchEgressRules.Verified(exit)) return exit;
        if (!Matches(ReadZone(), exit.Timezone)) return exit with { Success = false, Message = "本机默认代理出口与 Windows 时区不一致，请先按本机出口匹配系统时区；未启动分身。" };
        return exit with { Message = "已核对本机默认出口与 Windows 当前时区；账号浏览器配置独立保留。" };
    }
}

public sealed partial class ShellViewModel
{
    private string localEnvironmentStatus = "本机环境：待核对";
    private string localEnvironmentDetail = "核对本机默认出口与 Windows 国家/地区、时区；账号浏览器独立配置。";
    private bool checkingLocalEnvironment;
    private LocalEnvironmentReport? lastVerifiedLocalEnvironment;
    public DelegateCommand RecheckLocalEnvironmentCommand { get; private set; } = null!;
    public Task LocalEnvironmentOperation { get; private set; } = Task.CompletedTask;
    public string LocalEnvironmentCheckLabel => checkingLocalEnvironment ? "核对中…" : "重新核对";
    public string LocalEnvironmentStatus => localEnvironmentStatus;
    public string LocalEnvironmentDetail => localEnvironmentDetail;
    public async Task RefreshLocalEnvironmentAsync()
    {
        if (workspace?.LocalEnvironment is null || checkingLocalEnvironment || busy) return;
        checkingLocalEnvironment = true;
        localEnvironmentStatus = "正在核对本机出口、地区与时区…";
        localEnvironmentDetail = "正在通过 FlClash 当前网络查询出口国家/地区和时区，不测分、不修改配置。";
        RefreshLocalEnvironmentCommands();
        try { ApplyLocalEnvironmentReport(await workspace.LocalEnvironment.CheckAsync()); }
        finally { checkingLocalEnvironment = false; RefreshLocalEnvironmentCommands(); }
    }
    private void RefreshLocalEnvironmentCommands()
    {
        Raise(nameof(LocalEnvironmentStatus)); Raise(nameof(LocalEnvironmentDetail)); Raise(nameof(LocalEnvironmentCheckLabel));
        RecheckLocalEnvironmentCommand?.Refresh(); SyncLocalTimezoneCommand?.Refresh(); ConfigureRegionCommand?.Refresh();
    }
    private void ApplyLocalEnvironmentReport(LocalEnvironmentReport result)
    {
        var checkedAt = result.CheckedAt.ToLocalTime();
        localEnvironmentStatus = (result.Verified ? $"本机 · {result.Ip} · {result.SystemTimezone} · " + (result.Matches ? "已核对" : "地区或时区不一致") : "本机环境：本次未确认") + $" · {checkedAt:HH:mm:ss}";
        localEnvironmentDetail = result.Message + $"\n核对时间：{checkedAt:yyyy-MM-dd HH:mm:ss zzz}\n当前代理：{(result.Proxy.Length > 0 ? ErrorRedaction.Proxy(result.Proxy) : "未确认")}\n"
            + $"本次出口：{(result.Ip.Length > 0 ? result.Ip + (result.Verified ? "" : "（仅部分查询返回，尚未完成核对）") : "未确认")}\n"
            + $"出口国家/地区：{(result.CountryCode.Length > 0 ? result.CountryCode : "未确认")}\nWindows 国家/地区：{(result.SystemCountryCode.Length > 0 ? result.SystemCountryCode : "未确认")}\n"
            + $"出口时区：{(result.ExitTimezone.Length > 0 ? result.ExitTimezone : "未确认")}\nWindows 时区：{result.SystemTimezone}";
        if (result.Verified) lastVerifiedLocalEnvironment = result;
        else if (lastVerifiedLocalEnvironment is { } previous)
            localEnvironmentDetail += $"\n上次核对：{previous.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {previous.Ip} · {previous.ExitTimezone}（仅历史，不代表当前出口）。";
        localEnvironmentDetail += "\n仅核对出口、国家/地区与时区，不代表账号授权或模型可用。";
        Raise(nameof(LocalEnvironmentStatus)); Raise(nameof(LocalEnvironmentDetail));
    }
}
