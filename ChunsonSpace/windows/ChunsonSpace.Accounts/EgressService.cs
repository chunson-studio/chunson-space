using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Core;
using ChunsonSpace.Application;

namespace ChunsonSpace.Accounts;

public static class CountryEnvironmentRules
{
    private static readonly IReadOnlyDictionary<string, string> Locales = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CN"]="zh-CN", ["HK"]="zh-HK", ["TW"]="zh-TW", ["SG"]="en-SG", ["JP"]="ja-JP", ["KR"]="ko-KR", ["US"]="en-US", ["GB"]="en-GB", ["IE"]="en-IE", ["CA"]="en-CA", ["AU"]="en-AU", ["NZ"]="en-NZ", ["IN"]="en-IN", ["PH"]="en-PH", ["MY"]="en-MY", ["TH"]="th-TH", ["VN"]="vi-VN", ["ID"]="id-ID", ["AE"]="ar-AE", ["TR"]="tr-TR", ["IL"]="he-IL", ["SA"]="ar-SA", ["EG"]="ar-EG", ["DE"]="de-DE", ["FR"]="fr-FR", ["NL"]="nl-NL", ["BE"]="nl-BE", ["CH"]="de-CH", ["AT"]="de-AT", ["IT"]="it-IT", ["ES"]="es-ES", ["PT"]="pt-PT", ["SE"]="sv-SE", ["NO"]="nb-NO", ["DK"]="da-DK", ["FI"]="fi-FI", ["PL"]="pl-PL", ["CZ"]="cs-CZ", ["HU"]="hu-HU", ["RO"]="ro-RO", ["GR"]="el-GR", ["UA"]="uk-UA", ["RU"]="ru-RU", ["KZ"]="ru-KZ", ["BR"]="pt-BR", ["MX"]="es-MX", ["AR"]="es-AR", ["CL"]="es-CL", ["CO"]="es-CO", ["ZA"]="en-ZA", ["NG"]="en-NG", ["KE"]="en-KE"
    };
    private static readonly IReadOnlyDictionary<string, string> SingleZones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["JP"]="Asia/Tokyo", ["KR"]="Asia/Seoul", ["SG"]="Asia/Singapore", ["HK"]="Asia/Hong_Kong", ["TW"]="Asia/Taipei", ["CN"]="Asia/Shanghai", ["TH"]="Asia/Bangkok", ["VN"]="Asia/Ho_Chi_Minh", ["MY"]="Asia/Kuala_Lumpur", ["PH"]="Asia/Manila", ["IN"]="Asia/Kolkata", ["AE"]="Asia/Dubai", ["TR"]="Europe/Istanbul", ["GB"]="Europe/London", ["IE"]="Europe/Dublin", ["FR"]="Europe/Paris", ["DE"]="Europe/Berlin", ["NL"]="Europe/Amsterdam", ["CH"]="Europe/Zurich", ["IT"]="Europe/Rome", ["SE"]="Europe/Stockholm", ["PL"]="Europe/Warsaw", ["ZA"]="Africa/Johannesburg"
    };
    private static readonly IReadOnlyDictionary<string, TimezoneCandidate[]> MultipleZones = new Dictionary<string, TimezoneCandidate[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["US"]=[new("America/New_York","美东"),new("America/Chicago","美中"),new("America/Denver","山地"),new("America/Los_Angeles","美西"),new("America/Anchorage","阿拉斯加"),new("Pacific/Honolulu","夏威夷")],
        ["CA"]=[new("America/St_Johns","纽芬兰"),new("America/Halifax","大西洋"),new("America/Toronto","加东"),new("America/Winnipeg","加中"),new("America/Edmonton","山地"),new("America/Vancouver","加西")],
        ["AU"]=[new("Australia/Sydney","悉尼"),new("Australia/Brisbane","布里斯班"),new("Australia/Adelaide","阿德莱德"),new("Australia/Darwin","达尔文"),new("Australia/Perth","珀斯")],
        ["ID"]=[new("Asia/Jakarta","西部"),new("Asia/Makassar","中部"),new("Asia/Jayapura","东部")],
        ["ES"]=[new("Europe/Madrid","本土"),new("Atlantic/Canary","加那利群岛")],
        ["PT"]=[new("Europe/Lisbon","本土"),new("Atlantic/Azores","亚速尔"),new("Atlantic/Madeira","马德拉")],
        ["NZ"]=[new("Pacific/Auckland","奥克兰"),new("Pacific/Chatham","查塔姆群岛")]
    };
    public static string Locale(string country) => Locales.GetValueOrDefault(country.Trim(), "");
    public static IReadOnlyList<TimezoneCandidate> Timezones(string country) => SingleZones.TryGetValue(country.Trim(), out var zone) ? [new(zone, "")] : MultipleZones.GetValueOrDefault(country.Trim(), []);
}

public sealed class EgressService(AccountRepository repository, IAccountTransport transport,
    Func<string, CancellationToken, Task<IpQualityReport>>? publicProbe = null) : ILaunchEgressProbe
{
    private readonly AccountNetwork network = new(transport);
    private async Task<EgressResult> PublicExit(string proxy, CancellationToken token)
    {
        var report = await publicProbe!(proxy, token);
        return new(report.HasVerifiedExit && ProfileRules.WindowsTimezoneId(report.Timezone) is not null,
            report.Ip, report.Country, report.CountryCode, report.City, report.Timezone, report.Message);
    }
    public async Task<EgressResult> ProbeAsync(string profileName, CancellationToken token = default)
    {
        var proxy = repository.Proxy(profileName);
        return await ProbeDetailsProxyAsync(proxy, token);
    }
    public async Task<LaunchEgressEvidence> ProbeAsync(IdentityRef identity, string resolvedProxy, CancellationToken token)
    {
        var result = await ProbeProxyAsync(resolvedProxy, token);
        var evidence = new LaunchEgressEvidence(result.Ok, result.Ip, result.Timezone, result.Message.Length > 0 ? result.Message : result.Ok ? "所选代理出口 IP 与时区已读取。" : "所选代理出口 IP 与时区探测失败。");
        return evidence with { Success = LaunchEgressRules.Verified(evidence) };
    }
    public async Task<EgressResult> ProbeProxyAsync(string resolvedProxy, CancellationToken token = default)
        => await ProbeMatchedProxyAsync(resolvedProxy, false, token);
    public Task<EgressResult> ProbeCountryProxyAsync(string resolvedProxy, CancellationToken token = default)
        => ProbeMatchedProxyAsync(resolvedProxy, true, token);
    private sealed record SourceRead(string Name, EgressResult Value, bool Location, bool Conflicting = false);
    private Task<EgressResult> ProbeMatchedProxyAsync(string resolvedProxy, bool requireCountry, CancellationToken token)
        => ProbeSourcesAsync(resolvedProxy, verifyExit: true, requireCountry, secondTimezone: false, token);
    public Task<EgressResult> ProbeRegionProxyAsync(string resolvedProxy, CancellationToken token = default)
        => ProbeSourcesAsync(resolvedProxy, verifyExit: true, requireCountry: true, secondTimezone: true, token);
    private Task<EgressResult> ProbeDetailsProxyAsync(string resolvedProxy, CancellationToken token)
        => ProbeSourcesAsync(resolvedProxy, verifyExit: false, requireCountry: false, secondTimezone: false, token);

    private async Task<EgressResult> ProbeSourcesAsync(string resolvedProxy, bool verifyExit, bool requireCountry, bool secondTimezone, CancellationToken token)
    {
        if (publicProbe is not null) return await PublicExit(resolvedProxy, token);
        var proxy = HttpAccountTransport.RequireHttpProxy(resolvedProxy);
        token.ThrowIfCancellationRequested();
        var reads = new List<SourceRead>();
        var first = ReadLocationAsync(EgressLocationSource.IpWho, proxy, token);
        if (verifyExit)
        {
            var trace = ReadTraceAsync(AccountEndpoints.Trace, "Cloudflare", proxy, token);
            await Task.WhenAll(first, trace);
            reads.Add(await first);
            reads.Add(new("Cloudflare", await trace, false));
        }
        else reads.Add(await first);
        foreach (var source in EgressSources.Locations.Skip(1).Cast<EgressLocationSource?>().Append(null))
        {
            var usable = reads.Where(read => EgressSources.ValidIp(read.Value.Ip)).ToArray();
            var details = reads.FirstOrDefault(read => read.Location && read.Value.Ok && (!requireCountry || EgressSources.HasCountry(read.Value)))?.Value;
            var partial = details ?? usable.FirstOrDefault()?.Value ?? new EgressResult(false);
            if (reads.FirstOrDefault(read => read.Conflicting) is { } rejected)
                return partial with { Ok = false, Message = rejected.Value.Message + " 未使用不一致的数据。" };
            var conflict = Conflict(usable, requireCountry);
            if (conflict.Length > 0) return partial with { Ok = false, Message = conflict + " 未使用不一致的数据。" };
            var witnesses = requireCountry ? usable.Count(read => EgressSources.HasCountry(read.Value)) : usable.Length;
            var zones = usable.Count(read => read.Location && read.Value.Ok && EgressSources.HasCountry(read.Value));
            if (details is not null && (!verifyExit || witnesses >= 2) && (!secondTimezone || zones >= 2))
            {
                var names = string.Join("、", usable.Select(read => read.Name));
                var backup = reads.Any(read => !read.Value.Ok) ? "主查询未完成，已使用备用来源。" : "";
                return details with { Message = backup + names + (secondTimezone ? " 已核对同一出口、国家及两来源具体时区。" : verifyExit ? " 已核对同一出口；具体时区已取得。" : " 已返回出口与具体时区。") };
            }
            if (source is null)
            {
                var failures = reads.Where(read => !read.Value.Ok || read.Location && requireCountry && !EgressSources.HasCountry(read.Value))
                    .Select(read => read.Value.Message + (read.Location && requireCountry && !EgressSources.HasCountry(read.Value) ? " " + read.Name + " 未返回可确认的国家。" : ""));
                return partial with { Ok = false, Message = string.Join(" ", failures) + (secondTimezone ? " 同出口、国家与两来源具体时区尚未完整核对。" : " 尚未完成出口核对。") };
            }
            token.ThrowIfCancellationRequested();
            reads.Add(await ReadLocationAsync(source.Value, proxy, token));
        }
        throw new InvalidOperationException("Egress source sequence did not terminate.");
    }
    private static string Conflict(IReadOnlyList<SourceRead> reads, bool requireCountry)
    {
        for (var i = 0; i < reads.Count; i++)
            for (var j = i + 1; j < reads.Count; j++)
            {
                var a = reads[i]; var b = reads[j];
                if (!EgressSources.SameIp(a.Value.Ip, b.Value.Ip)) return $"不同查询返回了不同出口：{a.Name} 为 {a.Value.Ip}，{b.Name} 为 {b.Value.Ip}。";
                if (requireCountry && EgressSources.HasCountry(a.Value) && EgressSources.HasCountry(b.Value) && a.Value.CountryCode != b.Value.CountryCode)
                    return $"同一 IP 的国家定位未一致：{a.Name} 为 {a.Value.CountryCode}，{b.Name} 为 {b.Value.CountryCode}。";
                if (a.Location && b.Location && a.Value.Ok && b.Value.Ok && !EgressSources.SameZone(a.Value.Timezone, b.Value.Timezone))
                    return $"同一 IP 的时区定位未一致：{a.Name} 为 {a.Value.Timezone}，{b.Name} 为 {b.Value.Timezone}。";
            }
        return "";
    }
    private async Task<EgressResult> ReadTraceAsync(Uri endpoint, string source, string proxy, CancellationToken token)
    {
        try
        {
            var response = await network.PayloadAsync(new(HttpMethod.Get, endpoint, proxy, true, new Dictionary<string, string> { ["User-Agent"] = "" }), TimeSpan.FromSeconds(8), 4096, token);
            return EgressSources.Trace(Encoding.UTF8.GetString(response.Body), source);
        }
        catch (AccountException error) when (error.Code is not (AccountError.ExitUnavailable or AccountError.Cancelled))
        { return new(false, Message: QueryFailure(source, error)); }
    }
    private async Task<SourceRead> ReadLocationAsync(EgressLocationSource source, string proxy, CancellationToken token)
    {
        var endpoint = EgressSources.Endpoint(source);
        EgressResult? measured = null;
        if (source == EgressLocationSource.NetCoffee)
        {
            measured = await ReadTraceAsync(endpoint, EgressSources.Name(source), proxy, token);
            if (!measured.Ok) return new(EgressSources.Name(source), measured, true);
            endpoint = new Uri(NetCoffeeService.LookupEndpoint, Uri.EscapeDataString(measured.Ip));
        }
        try
        {
            var data = await network.JsonAsync(new(HttpMethod.Get, endpoint, proxy, true, new Dictionary<string, string> { ["User-Agent"] = "" }), TimeSpan.FromSeconds(8), token);
            var result = EgressSources.Parse(source, data, measured);
            if (measured is not null && EgressSources.LookupConflicts(data, measured))
                return new(EgressSources.Name(source), measured with { Ok = false, Message = result.Message }, true, Conflicting: true);
            // A query-by-IP report never overwrites its independently measured trace.
            if (source == EgressLocationSource.NetCoffee && result.Ip.Length == 0)
                return new(EgressSources.Name(source), measured! with { Ok = false, Message = result.Message }, true);
            return new(EgressSources.Name(source), result, true);
        }
        catch (AccountException error) when (error.Code is not (AccountError.ExitUnavailable or AccountError.Cancelled))
        { return new(EgressSources.Name(source), (measured ?? new EgressResult(false)) with { Ok = false, Message = QueryFailure(EgressSources.Name(source), error) }, true); }
    }
    // Describe only known response metadata, never an upstream error body or account state.
    private static string QueryFailure(string source, AccountException error) => source + (error.Code switch
    {
        AccountError.Http when error.Status == 429 => " 查询额度受限（HTTP 429）。",
        AccountError.Http when error.Status == 403 => " 拒绝本次查询（HTTP 403）；这不能证明账号异常或代理不可用。",
        AccountError.Http => $" 查询返回 HTTP {error.Status}，本次未取得有效数据。",
        AccountError.Timeout => " 查询超时；仅能确认本次查询未完成，不能判断出口已改变。",
        AccountError.Network => " 连接或响应中断。",
        AccountError.CorruptResponse => " 返回的数据格式不完整，本次无法核对。",
        _ => " 查询未完成，本次无法核对。"
    });
    public async Task<TimezoneSuggestion> SuggestTimezoneAsync(string profileName, CancellationToken token = default)
    {
        var proxy = repository.Proxy(profileName);
        var environment = repository.Profiles.ReadEnvironment(profileName);
        if (environment.Timezone.Length > 0) return new("", [], "", environment.Timezone);
        if (publicProbe is not null)
        {
            var exit = await PublicExit(proxy, token);
            return new(exit.CountryCode, exit.Ok ? [new(exit.Timezone, exit.City)] : [], exit.Ip);
        }
        var trace = await ReadTraceAsync(AccountEndpoints.Trace, "Cloudflare", proxy, token);
        if (!trace.Ok) trace = await ReadTraceAsync(NetCoffeeService.TraceEndpoint, "Net.Coffee", proxy, token);
        if (!trace.Ok) throw new AccountException(AccountError.Network, trace.Message);
        return new(trace.CountryCode, CountryEnvironmentRules.Timezones(trace.CountryCode), trace.Ip);
    }
    public async Task<EnvironmentDefaultsResult> MatchEnvironmentAsync(string profileName, CancellationToken token = default)
    {
        var current = repository.Profiles.ReadEnvironment(profileName);
        var egress = await ProbeAsync(profileName, token);
        if (!egress.Ok || ProfileRules.WindowsTimezoneId(egress.Timezone) is null) return new([], current, egress with { Ok = false });
        var latest = repository.Profiles.ReadEnvironment(profileName);
        if (latest.Proxy != current.Proxy || latest.Timezone != current.Timezone || latest.Locale != current.Locale)
            throw new AccountException(AccountError.Storage, "检测期间账号配置已变化，未覆盖新设置，请重试。");
        var changed = new List<string>();
        var result = latest;
        if (latest.Timezone != egress.Timezone) { result = result with { Timezone = egress.Timezone }; changed.Add("时区"); }
        if (latest.Locale.Length == 0) { result = result with { Locale = "en-US", LocaleAuto = false }; changed.Add("界面语言"); }
        if (changed.Count > 0) repository.Profiles.SaveEnvironment(profileName, result);
        return new(changed, result, egress);
    }
    public async Task<EnvironmentDefaultsResult> ApplyDefaultsAsync(string profileName, CancellationToken token = default)
    {
        _ = repository.Proxy(profileName);
        var current = repository.Profiles.ReadEnvironment(profileName);
        if (current.Timezone.Length > 0 && current.Locale.Length > 0 && !current.LocaleAuto) return new([], current, new(false));
        var egress = await ProbeAsync(profileName, token);
        if (!egress.Ok) return new([], current, egress);
        var changed = new List<string>();
        var result = current;
        if (current.Timezone.Length == 0 && egress.Timezone.Length > 0)
        { result = result with { Timezone = egress.Timezone }; changed.Add("timezone"); }
        var locale = CountryEnvironmentRules.Locale(egress.CountryCode);
        if ((current.Locale.Length == 0 || current.LocaleAuto) && locale.Length > 0 && locale != current.Locale)
        { result = result with { Locale = locale, LocaleAuto = true }; changed.Add("locale"); }
        if (changed.Count > 0)
        {
            // Re-read before saving so user edits made during the request win over defaults.
            var latest = repository.Profiles.ReadEnvironment(profileName);
            if (latest.Proxy != current.Proxy) return new([], latest, egress);
            if (latest.Timezone != current.Timezone) { result = result with { Timezone = latest.Timezone }; changed.Remove("timezone"); }
            if (latest.Locale != current.Locale || latest.LocaleAuto != current.LocaleAuto)
            { result = result with { Locale = latest.Locale, LocaleAuto = latest.LocaleAuto }; changed.Remove("locale"); }
            if (changed.Count > 0) repository.Profiles.SaveEnvironment(profileName, result);
        }
        return new(changed, result, egress);
    }
}
