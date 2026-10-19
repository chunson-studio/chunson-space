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

public sealed class EgressService(AccountRepository repository, IAccountTransport transport) : ILaunchEgressProbe
{
    private readonly IpInfoService ipinfo = new(transport);
    public Task<EgressResult> ProbeAsync(string profileName, CancellationToken token = default)
        => ProbeProxyAsync(repository.Proxy(profileName), token);
    public async Task<LaunchEgressEvidence> ProbeAsync(IdentityRef identity, string resolvedProxy, CancellationToken token)
    {
        var result = await ProbeProxyAsync(resolvedProxy, token);
        var evidence = new LaunchEgressEvidence(result.Ok, result.Ip, result.Timezone, result.Message) { Addresses = result.Addresses };
        return evidence with { Success = LaunchEgressRules.Verified(evidence) };
    }
    public async Task<EgressResult> ProbeProxyAsync(string resolvedProxy, CancellationToken token = default)
        => await ipinfo.LocateAsync(await ipinfo.ProbeAsync(resolvedProxy, token), resolvedProxy, token);
    public Task<EgressResult> ProbeCountryProxyAsync(string resolvedProxy, CancellationToken token = default)
        => ProbeRegionProxyAsync(resolvedProxy, token);
    public async Task<EgressResult> ProbeRegionProxyAsync(string resolvedProxy, CancellationToken token = default)
    {
        var result = await ProbeProxyAsync(resolvedProxy, token);
        return EgressSources.HasCountry(result) ? result : result with { Ok = false, Message = result.Message + " IPinfo 未返回可确认的国家。" };
    }
    public async Task<TimezoneSuggestion> SuggestTimezoneAsync(string profileName, CancellationToken token = default)
    {
        var proxy = repository.Proxy(profileName);
        var environment = repository.Profiles.ReadEnvironment(profileName);
        if (environment.Timezone.Length > 0) return new("", [], "", environment.Timezone);
        var exit = await ProbeProxyAsync(proxy, token);
        return new(exit.CountryCode, exit.Ok ? [new(exit.Timezone, exit.City)] : [], exit.Ip);
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
