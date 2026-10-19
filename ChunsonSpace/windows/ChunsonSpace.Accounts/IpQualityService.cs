using ChunsonSpace.Core;
using System.Net;

namespace ChunsonSpace.Accounts;

public sealed record IpQualityReport(bool Success, string Ip, string Country, string City, string Timezone, string Company, string Asn,
    bool? IsDatacenter, bool? IsProxy, bool? IsVpn, bool? IsTor, bool? IsAbuser, DateTimeOffset FetchedAt, string Message, string Source, IpPureScore? Score = null)
{
    public string CountryCode { get; init; } = "";
    public ExitAddresses Addresses { get; init; } = ExitAddresses.Unknown;
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

/// <summary>IPinfo measures addresses; IPPure scoring is requested explicitly.</summary>
public sealed class IpQualityService : IIpQualityProbe
{
    // Retained only for legacy source parsers/reports. No current query uses it.
    public static readonly Uri Endpoint = new("https://api.ipapi.is/");
    public static readonly Uri ScoreEndpoint = new("https://my.ippure.com/v1/info");
    public const string Source = "IPinfo";
    public const int AnonymousDailyLimitPerExit = 30; // Historical ipapi.is contract.
    private readonly AccountRepository? repository;
    private readonly AccountNetwork network;
    private readonly IpInfoService ipinfo;
    private readonly IAccountClock clock;
    public IpQualityService(IAccountTransport transport, IAccountClock? clock = null)
    { network = new(transport); ipinfo = new(transport); this.clock = clock ?? new SystemAccountClock(); }
    public IpQualityService(AccountRepository repository, IAccountTransport transport, IAccountClock? clock = null) : this(transport, clock)
    { this.repository = repository; }
    public Task<IpQualityReport> ProbeAsync(string profileName, CancellationToken token = default)
        => ProbeProxyAsync((repository ?? throw new InvalidOperationException("账号服务尚未连接。")).Proxy(profileName), token);
    public Task<IpQualityReport> ProbeWithScoreAsync(string profileName, CancellationToken token = default)
        => ProbeProxyWithScoreAsync((repository ?? throw new InvalidOperationException("账号服务尚未连接。")).Proxy(profileName), token);
    public async Task<IpQualityReport> ProbeProxyWithScoreAsync(string proxy, CancellationToken token = default)
        => await AddScoreAsync(await ProbeProxyAsync(proxy, token), proxy, token);
    public async Task<IpQualityReport> ProbeScreeningExitAsync(string proxy, CancellationToken token = default)
    {
        var addresses = await ipinfo.ProbeAsync(proxy, token);
        return new(addresses.PrimaryIp.Length > 0, addresses.PrimaryIp, "", "", "", "", "", null, null, null, null, null,
            clock.UtcNow, addresses.PrimaryIp.Length > 0 ? "IPinfo 已测量出口；地区与时区未查询。" : "IPinfo 本次未取得出口地址。", Source) { Addresses = addresses };
    }
    public async Task<IpQualityReport> ProbeProxyAsync(string proxy, CancellationToken token = default)
    {
        var report = await ProbeScreeningExitAsync(proxy, token);
        var location = await ipinfo.LocateAsync(report.Addresses, proxy, token);
        return report with { Country = location.Country, CountryCode = location.CountryCode, City = location.City, Timezone = location.Timezone, Message = location.Message };
    }
    public async Task<IpQualityReport> AddScoreAsync(IpQualityReport report, string proxy, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!IpInfoService.TryPublicAddress(report.Ip, out _)) return report with { Score = new(null, "出口未确认，未请求评分。") };
        try
        {
            var response = await network.PayloadAsync(new(HttpMethod.Get, ScoreEndpoint, proxy, true, new Dictionary<string, string>()), TimeSpan.FromSeconds(8), 16384, token);
            token.ThrowIfCancellationRequested();
            var data = AccountJson.Parse(response.Body);
            if (data["error"] is not null) return report with { Score = new(null, "IPPure 返回查询错误，评分未采用。") };
            if (!EgressSources.SameIp(AccountJson.Text(data, "ip"), report.Ip))
                return report with { Score = new(null, "IPPure 评分报告未对应本次主出口 IP，评分未采用；地址测量结果保留。") };
            var score = AccountJson.Integer(data["fraudScore"]);
            var attributes = new IpPureScore(score is >= 0 and <= 100 ? (int)score.Value : null,
                "来源：my.ippure.com 公开 API；质量分=100−风险分，住宅/机房为该库分类，第三方数据不是账号能力或官方判定。",
                AccountJson.Boolean(data["isResidential"]), AccountJson.Boolean(data["isBroadcast"]), AccountJson.Text(data, "asOrganization"),
                AccountJson.Integer(data["asn"]) is long asn && asn > 0 ? "AS" + asn.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            return report with { Score = attributes, Company = attributes.Organization, Asn = attributes.Asn, Source = Source + "；IPPure（评分、网络类型与 ASN）" };
        }
        catch (AccountException error) when (error.Code != AccountError.ExitUnavailable)
        { token.ThrowIfCancellationRequested(); return report with { Score = new(null, "IPPure 评分不可用：" + IpInfoService.Failure(error) + "；已保留 IPinfo 地址信息。") }; }
    }
}
