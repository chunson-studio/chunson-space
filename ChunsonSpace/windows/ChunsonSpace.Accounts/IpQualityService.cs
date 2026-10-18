using System.Net;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace ChunsonSpace.Accounts;

public sealed record IpQualityReport(bool Success, string Ip, string Country, string City, string Timezone, string Company, string Asn,
    bool? IsDatacenter, bool? IsProxy, bool? IsVpn, bool? IsTor, bool? IsAbuser, DateTimeOffset FetchedAt, string Message, string Source, IpPureScore? Score = null)
{
    public string CountryCode { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ResidentialScoreExempt { get; init; }
    public bool HasRiskSignals => IsDatacenter.HasValue || IsProxy.HasValue || IsVpn.HasValue || IsTor.HasValue || IsAbuser.HasValue;
    public bool HasVerifiedExit => IPAddress.TryParse(Ip, out _);
    public IpAdmissionDecision Admission => IpAdmissionPolicy.Evaluate(this);
    public string NetworkGroup => Score?.IsResidential == true && IsDatacenter != true ? "家庭宽带组"
        : Score?.IsResidential == false && IsDatacenter != false || Score?.IsResidential is null && IsDatacenter == true ? "机房组" : "待分类";
}

public sealed record IpPureScore(int? Value, string Message, bool? IsResidential = null, bool? IsBroadcast = null, string Organization = "", string Asn = "")
{
    public string Provider { get; init; } = "IPPure";
    public int? DirectQuality { get; init; }
    public int? Quality => Provider == "Net.Coffee" ? DirectQuality is >= 0 and <= 100 ? DirectQuality : null : Value is >= 0 and <= 100 ? 100 - Value : null;
    public string Summary => "IP 质量分：" + (Quality is { } quality ? $"{quality} / 100（≥85 达标）" : "未知，不放行")
        + (Provider == "Net.Coffee" ? "\n来源：Net.Coffee（直接采用页面 IP 评分）" : "\nIPPure 风险分：" + (Value is { } score ? $"{score} / 100（越低风险越低）" : "未知")) + "\n" + Message;
}

public interface IIpQualityProbe
{
    Task<IpQualityReport> ProbeScreeningExitAsync(string proxy, CancellationToken token = default);
    Task<IpQualityReport> AddScoreAsync(IpQualityReport report, string proxy, CancellationToken token = default);
}

/// <summary>Manual diagnostics only. No key, account credential, retry, or node selection.</summary>
public sealed class IpQualityService(AccountRepository repository, IAccountTransport transport, IAccountClock? clock = null) : IIpQualityProbe
{
    public static readonly Uri Endpoint = new("https://api.ipapi.is/");
    public static readonly Uri ScoreEndpoint = new("https://my.ippure.com/v1/info");
    public const string Source = "ipapi.is";
    public const int AnonymousDailyLimitPerExit = 30;
    private readonly AccountNetwork network = new(transport);
    private readonly EgressService egress = new(repository, transport);
    private readonly IAccountClock clock = clock ?? new SystemAccountClock();
    private readonly ConcurrentDictionary<string, DateTimeOffset> pausedExits = new(StringComparer.Ordinal);

    public Task<IpQualityReport> ProbeAsync(string profileName, CancellationToken token = default)
        => ProbeProxyAsync(repository.Proxy(profileName), token);

    public Task<IpQualityReport> ProbeWithScoreAsync(string profileName, CancellationToken token = default)
        => ProbeProxyWithScoreAsync(repository.Proxy(profileName), token);

    public async Task<IpQualityReport> ProbeProxyWithScoreAsync(string proxy, CancellationToken token = default)
    {
        var report = await ProbeProxyAsync(proxy, token);
        return await AddScoreAsync(report, proxy, token);
    }

    public async Task<IpQualityReport> ProbeScreeningExitAsync(string proxy, CancellationToken token = default)
    {
        var verified = await egress.ProbeCountryProxyAsync(proxy, token);
        if (!verified.Ok) return Failure("未确认此节点的实际出口，未采用历史评分。");
        return new(true, verified.Ip, verified.Country, verified.City, verified.Timezone, "", "", null, null, null, null, null,
            clock.UtcNow, verified.Message + " 快速筛选仅查询 IPPure 评分和类型，其他风险标签未检测。", "HTTPS 多来源（实际出口）") { CountryCode = verified.CountryCode };
    }

    public async Task<IpQualityReport> AddScoreAsync(IpQualityReport report, string proxy, CancellationToken token = default)
    {
        if (report.Ip.Length == 0) return report with { Score = new(null, "出口未确认，未请求评分。") };
        if (IPAddress.TryParse(report.Ip, out var exit) && exit.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return report with { Score = new(null, "IPPure 暂不计算 IPv6 风险系数。") };
        try
        {
            var data = await network.JsonAsync(new(HttpMethod.Get, ScoreEndpoint, proxy, true, new Dictionary<string, string>()), TimeSpan.FromSeconds(8), token);
            if (!IPAddress.TryParse(AccountJson.Text(data, "ip"), out var measured) || !IPAddress.TryParse(report.Ip, out var expected) || !measured.Equals(expected))
                return report with { Score = new(null, "IPPure 返回的出口不一致，评分未采用。") };
            var score = AccountJson.Integer(data["fraudScore"]);
            var attributes = new IpPureScore(score is >= 0 and <= 100 ? (int)score.Value : null,
                "来源：my.ippure.com 公开 API；质量分=100−风险分，住宅/机房为该库分类，第三方数据不是账号能力或官方判定。",
                AccountJson.Boolean(data["isResidential"]), AccountJson.Boolean(data["isBroadcast"]), AccountJson.Text(data, "asOrganization"),
                AccountJson.Integer(data["asn"]) is long asn && asn > 0 ? "AS" + asn.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            return report with { Score = attributes, Company = report.Company.Length > 0 ? report.Company : attributes.Organization,
                Asn = report.Asn.Length > 0 ? report.Asn : attributes.Asn, Source = report.Source + "；IPPure（评分、网络类型与 ASN）" };
        }
        catch (AccountException error) when (error.Code is not (AccountError.ExitUnavailable or AccountError.Cancelled))
        { return report with { Score = new(null, error.Status is 429 or 403 ? "IPPure 限流或拒绝访问，未重试。" : "IPPure 评分暂不可用；已有 IP 信息保留。") }; }
    }

    public async Task<IpQualityReport> ProbeProxyAsync(string resolvedProxy, CancellationToken token = default)
    {
        var proxy = HttpAccountTransport.RequireHttpProxy(resolvedProxy);
        var verified = await egress.ProbeProxyAsync(proxy, token);
        if (!verified.Ok) return Failure("出口 IP 与时区未通过 HTTPS 双来源确认，未发送 IP 体检请求。");
        IpQualityReport Partial(string message) => Failure(message, verified.Ip) with
        { Country = verified.Country, CountryCode = verified.CountryCode, City = verified.City, Timezone = verified.Timezone, Source = verified.Message + " ipapi.is 风险标签未取得" };
        if (pausedExits.TryGetValue(verified.Ip, out var pausedUntil) && pausedUntil > this.clock.UtcNow)
            return Partial("此出口的 IP 体检请求已暂停至 " + pausedUntil.ToString("yyyy-MM-dd HH:mm 'UTC'") + "；未重复请求，风险标签未知。");
        try
        {
            var data = await network.JsonAsync(new(HttpMethod.Get, Endpoint, proxy, true, new Dictionary<string, string> { ["User-Agent"] = "" }), TimeSpan.FromSeconds(8), token);
            var ip = AccountJson.Text(data, "ip");
            if (data["error"] is not null || data["error_code"] is not null)
                return Partial("IP 体检服务没有返回有效报告，未自动重试。");
            if (!IPAddress.TryParse(ip, out var reportedIp) || !IPAddress.TryParse(verified.Ip, out var expectedIp) || !reportedIp.Equals(expectedIp))
                return Partial("IP 体检返回的出口与已确认出口不一致，未使用该报告的风险标签。");
            var location = data["location"] as JsonObject;
            var timezone = Text(data, location, "timezone");
            var company = data["company"] is JsonObject companyObject ? AccountJson.Text(companyObject, "name") : AccountJson.Text(data, "company");
            var asn = data["asn"] is JsonObject asnObject ? Asn(asnObject) : AccountJson.Text(data, "asn");
            return new(true, ip, Text(data, location, "country"), Text(data, location, "city"), timezone, company, asn,
                AccountJson.Boolean(data["is_datacenter"]), AccountJson.Boolean(data["is_proxy"]), AccountJson.Boolean(data["is_vpn"]), AccountJson.Boolean(data["is_tor"]), AccountJson.Boolean(data["is_abuser"]),
                this.clock.UtcNow, "已确认相同出口；标签仅来自 ipapi.is。匿名接口不提供的字段保持未知，未计算综合评分。", Source) { CountryCode = verified.CountryCode };
        }
        catch (AccountException error) when (error.Code is not (AccountError.ExitUnavailable or AccountError.Cancelled))
        {
            if (error.Status is 429 or 403)
            {
                var until = error.Status == 429 ? new DateTimeOffset(this.clock.UtcNow.UtcDateTime.Date.AddDays(1), TimeSpan.Zero) : this.clock.UtcNow.AddHours(24);
                pausedExits.AddOrUpdate(verified.Ip, until, (_, previous) => previous > until ? previous : until);
            }
            return Partial(error.Status == 429 ? "ipapi.is 匿名额度已用尽（每出口每日 30 次），未自动重试。" : error.Status == 403 ? "ipapi.is 拒绝访问，风险标签未知；未自动重试。" : "ipapi.is 请求未完成，风险标签未知；已保留确认过的出口和地区，未自动重试。");
        }
    }
    private IpQualityReport Failure(string message, string ip = "") => new(false, ip, "", "", "", "", "", null, null, null, null, null, clock.UtcNow, message, Source);
    private static string Text(JsonObject data, JsonObject? location, string key)
    { var top = AccountJson.Text(data, key); return top.Length > 0 ? top : AccountJson.Text(location, key); }
    private static string Asn(JsonObject value)
    {
        var number = AccountJson.Integer(value["asn"]);
        var org = AccountJson.Text(value, "org");
        var id = number is > 0 ? "AS" + number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : AccountJson.Text(value, "asn");
        return (id + (org.Length > 0 ? " " + org : "")).Trim();
    }
}
