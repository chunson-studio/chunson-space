using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FAswitch.Core;

namespace FAswitch.Accounts;

// The same two endpoints used by https://ip.net.coffee/ip/.
// No cross-provider scoring, forced refresh, port scans or account requests.
public sealed class NetCoffeeService(IAccountTransport transport, IAccountClock? clock = null) : IIpQualityProbe
{
    public static readonly Uri TraceEndpoint = new("https://ip.net.coffee/cdn-cgi/trace");
    public static readonly Uri LookupEndpoint = new("https://ip.net.coffee/api/ip/lookup/");
    private readonly AccountNetwork network = new(transport);
    private readonly IAccountClock clock = clock ?? new SystemAccountClock();
    public static bool IsLookup(Uri url) => url.Host == LookupEndpoint.Host && url.AbsolutePath.StartsWith(LookupEndpoint.AbsolutePath, StringComparison.Ordinal);
    public async Task<IpQualityReport> ProbeScreeningExitAsync(string proxy, CancellationToken token = default)
    {
        proxy = HttpAccountTransport.RequireHttpProxy(proxy);
        try
        {
            var response = await network.PayloadAsync(new(HttpMethod.Get, TraceEndpoint, proxy, true, new Dictionary<string, string>()), TimeSpan.FromSeconds(8), 4096, token);
            var fields = Encoding.UTF8.GetString(response.Body).Split('\n').Select(line => line.Trim().Split('=', 2)).Where(pair => pair.Length == 2).ToArray();
            var ips = fields.Where(pair => pair[0] == "ip").Select(pair => pair[1]).ToArray();
            var countries = fields.Where(pair => pair[0] == "loc").Select(pair => pair[1]).ToArray();
            if (ips.Length != 1 || !IPAddress.TryParse(ips[0], out var ip)) return Empty("Net.Coffee 未返回有效出口 IP。");
            return Empty("已通过 Net.Coffee 确认当前出口；未请求评分。") with { Success = true, Ip = ip.ToString(), CountryCode = countries.Length == 1 ? countries[0].ToUpperInvariant() : "" };
        }
        catch (AccountException error) when (error.Code is not (AccountError.ExitUnavailable or AccountError.Cancelled))
        { return Empty("Net.Coffee 出口查询暂不可用，未切换数据来源或自动重试。"); }
    }
    public async Task<IpQualityReport> AddScoreAsync(IpQualityReport report, string proxy, CancellationToken token = default)
    {
        if (!IPAddress.TryParse(report.Ip, out var expected)) return report;
        try
        {
            var url = new Uri(LookupEndpoint, Uri.EscapeDataString(expected.ToString()));
            var data = await network.JsonAsync(new(HttpMethod.Get, url, proxy, true, new Dictionary<string, string>()), TimeSpan.FromSeconds(20), token);
            return Parse(data, report);
        }
        catch (AccountException error) when (error.Code is not (AccountError.ExitUnavailable or AccountError.Cancelled))
        { return report with { Success = false, Score = new(null, error.Status is 403 or 429 ? "Net.Coffee 限流或拒绝访问，未重试。" : "Net.Coffee 评分暂不可用。") { Provider = "Net.Coffee" } }; }
    }
    public static IpQualityReport Parse(JsonObject data, IpQualityReport current)
    {
        IpQualityReport Reject(string reason) => current with { Success = false, Score = new(null, reason) { Provider = "Net.Coffee" } };
        if (data["error"] is not null || AccountJson.Boolean(data["is_bogon"]) == true
            || !IPAddress.TryParse(AccountJson.Text(data, "ip"), out var returned) || !IPAddress.TryParse(current.Ip, out var expected) || !returned.Equals(expected))
            return Reject("Net.Coffee 返回的 IP 不一致或报告无效，未采用评分。");
        var country = AccountJson.Text(data, "countryCode").ToUpperInvariant();
        if (current.CountryCode.Length > 0 && country != current.CountryCode) return Reject("Net.Coffee 出口与报告国家不一致，未采用评分。");
        var trust = AccountJson.Integer(data["trust_score"]);
        var score = new IpPureScore(null, "Net.Coffee IP 评分，越高越好；不是 AI 场景评分，也不是账号可用保证。",
            AccountJson.Boolean(data["isResidential"]), Organization: AccountJson.Text(data, "asOrganization"),
            Asn: AccountJson.Integer(data["asn"]) is > 0 and var asn ? "AS" + asn : "")
            { Provider = "Net.Coffee", DirectQuality = trust is >= 0 and <= 100 ? (int)trust.Value : null };
        var city = AccountJson.Text(data, "city");
        var timezone = ResolveTimezone(data, country, ref city);
        return current with { Success = score.Quality.HasValue, CountryCode = country, Country = AccountJson.Text(data, "country"), City = city,
            Timezone = timezone, Company = score.Organization, Asn = score.Asn, Score = score, Source = "Net.Coffee",
            IsDatacenter = AccountJson.Boolean(data["is_datacenter"]), IsProxy = AccountJson.Boolean(data["is_proxy"]),
            IsVpn = AccountJson.Boolean(data["is_vpn"]), IsTor = AccountJson.Boolean(data["is_tor"]), IsAbuser = AccountJson.Boolean(data["is_abuser"]),
            Message = "已读取 Net.Coffee 评分与网络属性。" + (timezone.Length == 0 ? "具体时区未确定，不能自动调整系统时区。" : "时区根据返回的地理信息在本机解析，无额外查询。") };
    }
    private static string ResolveTimezone(JsonObject data, string country, ref string city)
    {
        var explicitZone = AccountJson.Text(data, "timezone");
        if (ProfileRules.WindowsTimezoneId(explicitZone) is not null) return explicitZone;
        var points = (data["geo_sources"] as JsonArray)?.OfType<JsonObject>().Where(point =>
            AccountJson.Text(point, "country_code").Equals(country, StringComparison.OrdinalIgnoreCase)
            && AccountJson.Text(point, "city").Length > 0
            && point["lat"] is JsonValue lat && lat.TryGetValue<double>(out var y) && y is >= -90 and <= 90
            && point["lon"] is JsonValue lon && lon.TryGetValue<double>(out var x) && x is >= -180 and <= 180
            && (point["accuracy_km"] is null || AccountJson.Number(point["accuracy_km"]) <= 100)).ToArray() ?? [];
        var zones = points.Select(point => GeoTimeZone.TimeZoneLookup.GetTimeZone(AccountJson.Number(point["lat"]), AccountJson.Number(point["lon"])).Result).Distinct().ToArray();
        if (zones.Length == 1 && ProfileRules.WindowsTimezoneId(zones[0]) is not null)
        { if (city.Length == 0) city = AccountJson.Text(points[0], "city"); return zones[0]; }
        if (zones.Length > 1) return "";
        var candidates = CountryEnvironmentRules.Timezones(country);
        return candidates.Count == 1 ? candidates[0].Timezone : "";
    }
    private IpQualityReport Empty(string message) => new(false, "", "", "", "", "", "", null, null, null, null, null, clock.UtcNow, message, "Net.Coffee");
}
