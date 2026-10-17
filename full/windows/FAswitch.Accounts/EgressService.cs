using System.Text;
using System.Text.Json.Nodes;
using System.Globalization;
using FAswitch.Core;
using FAswitch.Application;

namespace FAswitch.Accounts;

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
    private async Task<EgressResult> ProbeMatchedProxyAsync(string resolvedProxy, bool requireCountry, CancellationToken token)
    {
        if (publicProbe is not null) return await PublicExit(resolvedProxy, token);
        var proxy = HttpAccountTransport.RequireHttpProxy(resolvedProxy);
        var detailsTask = ProbeDetailsProxyAsync(proxy, token);
        var traceTask = ReadTraceAsync(proxy, token);
        await Task.WhenAll(detailsTask, traceTask);
        var details = await detailsTask;
        var trace = await traceTask;
        if (!details.Ok) return details with { Message = details.Message + (trace.Failure.Length > 0 ? " " + trace.Failure : "") };
        if (trace.Failure.Length > 0) return details with { Ok = false, Message = trace.Failure };
        var pairs = trace.Text.Split('\n').Select(line => line.TrimEnd('\r').Split('=', 2)).Where(pair => pair.Length == 2).ToArray();
        var ips = pairs.Where(pair => pair[0] == "ip").Select(pair => pair[1]).ToArray();
        if (ips.Length != 1 || !System.Net.IPAddress.TryParse(ips[0], out var traceIp))
            return details with { Ok = false, Message = "Cloudflare 未返回唯一、有效的出口 IP；本次无法核对，不能判断出口已改变。" };
        if (!System.Net.IPAddress.TryParse(details.Ip, out var detailIp) || !traceIp.Equals(detailIp))
            return details with { Ok = false, Message = $"不同查询返回了不同出口：ipwho.is 为 {details.Ip}，Cloudflare 为 {traceIp}。" };
        if (requireCountry)
        {
            var countries = pairs.Where(pair => pair[0] == "loc").Select(pair => pair[1]).ToArray();
            if (countries.Length != 1 || countries[0] != details.CountryCode || details.CountryCode.Length != 2)
                return details with { Ok = false, Message = "ipwho.is 与 Cloudflare 的国家定位缺失或不一致；出口 IP 相同，但地区尚未确认。" };
        }
        return details with { Message = "ipwho.is 与 Cloudflare 已核对同一出口；具体时区来自 ipwho.is。" };
    }
    private async Task<(string Text, string Failure)> ReadTraceAsync(string proxy, CancellationToken token)
    {
        try
        {
            var response = await network.PayloadAsync(new(HttpMethod.Get, AccountEndpoints.Trace, proxy, true, new Dictionary<string, string> { ["User-Agent"] = "" }), TimeSpan.FromSeconds(8), 4096, token);
            return (Encoding.UTF8.GetString(response.Body), "");
        }
        catch (AccountException error) when (error.Code is not (AccountError.ExitUnavailable or AccountError.Cancelled))
        { return ("", QueryFailure("Cloudflare", error)); }
    }
    // Describe only known response metadata, never an upstream error body or account state.
    private static string QueryFailure(string source, AccountException error) => source + (error.Code switch
    {
        AccountError.Http when error.Status == 429 => " 查询额度受限（HTTP 429），请稍后再核对。",
        AccountError.Http when error.Status == 403 => " 拒绝本次查询（HTTP 403）；这不能证明账号异常或代理不可用。",
        AccountError.Http => $" 查询返回 HTTP {error.Status}，本次未取得有效数据。",
        AccountError.Timeout => " 查询超时；仅能确认本次查询未完成，不能判断出口已改变。",
        AccountError.Network => " 连接或响应中断；请检查 FlClash 当前网络，或稍后重新核对。",
        AccountError.CorruptResponse => " 返回的数据格式不完整，本次无法核对。",
        _ => " 查询未完成，本次无法核对。"
    });
    // Explicit Windows environment configuration only. No country-based timezone guessing.
    public async Task<EgressResult> ProbeRegionProxyAsync(string resolvedProxy, CancellationToken token = default)
    {
        if (publicProbe is not null) return await PublicExit(resolvedProxy, token);
        var proxy = HttpAccountTransport.RequireHttpProxy(resolvedProxy);
        var verified = await ProbeMatchedProxyAsync(proxy, true, token);
        if (!verified.Ok) return verified;
        try
        {
            var data = await network.JsonAsync(new(HttpMethod.Get, IpQualityService.Endpoint, proxy, true, new Dictionary<string, string> { ["User-Agent"] = "" }), TimeSpan.FromSeconds(8), token);
            // Keyless ipapi.is returns a flat location; keyed/legacy responses nest it.
            var location = data["location"] as JsonObject ?? data;
            var ip = AccountJson.Text(data, "ip");
            var country = AccountJson.Text(location, "country_code").Trim().ToUpperInvariant();
            var countryName = AccountJson.Text(location, "country").Trim();
            var timezone = AccountJson.Text(location, "timezone");
            string failure;
            if (data["error"] is not null || data["error_code"] is not null)
                failure = "ipapi.is 返回查询错误，暂时无法核对时区。";
            else if (!System.Net.IPAddress.TryParse(ip, out var otherIp))
                failure = "ipapi.is 没有返回有效出口 IP。";
            else if (!System.Net.IPAddress.TryParse(verified.Ip, out var expectedIp) || !otherIp.Equals(expectedIp))
                failure = $"不同查询返回了不同出口：ipwho.is / Cloudflare 为 {verified.Ip}，ipapi.is 为 {ip}。";
            else if (!RegionCountryMatches(country, countryName, verified))
                failure = $"同一 IP 的国家定位未一致：ipwho.is / Cloudflare 为 {verified.CountryCode}，ipapi.is 为 {(country.Length > 0 ? country : countryName.Length > 0 ? countryName : "未返回")}。";
            else if (ProfileRules.WindowsTimezoneId(timezone) is not { } mapped)
                failure = "ipapi.is 未返回可识别的具体时区。";
            else if (mapped != ProfileRules.WindowsTimezoneId(verified.Timezone))
                failure = $"同一 IP 的时区定位未一致：ipwho.is 为 {verified.Timezone}，ipapi.is 为 {timezone}。";
            else return verified with { Message = "HTTPS 三来源同出口与国家、两来源具体时区已核对。" };
            return verified with { Ok = false, Message = failure + " 未修改系统时区。" };
        }
        catch (AccountException error) when (error.Code is not (AccountError.ExitUnavailable or AccountError.Cancelled))
        { return verified with { Ok = false, Message = QueryFailure("ipapi.is", error) + " 未重试，未修改系统。" }; }
    }
    private static bool RegionCountryMatches(string code, string name, EgressResult expected)
    {
        if (code.Length > 0) return code == expected.CountryCode;
        if (name.Length == 0) return false;
        if (name.Equals(expected.Country, StringComparison.OrdinalIgnoreCase)) return true;
        try { return name.Equals(new RegionInfo(expected.CountryCode).EnglishName, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
    private async Task<EgressResult> ProbeDetailsProxyAsync(string resolvedProxy, CancellationToken token)
    {
        if (publicProbe is not null) return await PublicExit(resolvedProxy, token);
        var proxy = HttpAccountTransport.RequireHttpProxy(resolvedProxy);
        try
        {
            var data = await network.JsonAsync(new(HttpMethod.Get, AccountEndpoints.Egress, proxy, true, new Dictionary<string, string> { ["User-Agent"] = "" }), TimeSpan.FromSeconds(8), token);
            var ip = AccountJson.Text(data, "ip");
            if (AccountJson.Boolean(data["success"]) != true) return new(false, Message: "ipwho.is 未返回成功的查询结果，本次无法核对出口。");
            var timezone = AccountJson.Text(data["timezone"] as JsonObject, "id");
            var valid = LaunchEgressRules.Verified(new(true, ip, timezone, ""));
            var addressValid = System.Net.IPAddress.TryParse(ip, out var address) && !System.Net.IPAddress.IsLoopback(address)
                && !address.Equals(System.Net.IPAddress.Any) && !address.Equals(System.Net.IPAddress.IPv6Any);
            return new(valid, addressValid ? address!.ToString() : "", AccountJson.Text(data, "country"), AccountJson.Text(data, "country_code").Trim().ToUpperInvariant(), AccountJson.Text(data, "city"), valid ? timezone : "",
                valid ? "ipwho.is 已返回出口与具体时区。" : !addressValid ? "ipwho.is 未返回有效的出口 IP。" : "ipwho.is 已返回出口 IP，但没有可识别的具体时区。");
        }
        catch (AccountException error) when (error.Code is not (AccountError.ExitUnavailable or AccountError.Cancelled))
        { return new(false, Message: QueryFailure("ipwho.is", error)); }
    }
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
        var response = await network.PayloadAsync(new(HttpMethod.Get, AccountEndpoints.Trace, proxy, true, new Dictionary<string, string> { ["User-Agent"] = "" }), TimeSpan.FromSeconds(8), 4096, token);
        var text = Encoding.UTF8.GetString(response.Body);
        var values = text.Split('\n').Select(line => line.TrimEnd('\r').Split('=', 2)).Where(pair => pair.Length == 2).GroupBy(pair => pair[0]).ToDictionary(group => group.Key, group => group.First()[1]);
        var country = values.GetValueOrDefault("loc", "").Trim().ToUpperInvariant();
        return new(country, CountryEnvironmentRules.Timezones(country), values.GetValueOrDefault("ip", ""));
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
