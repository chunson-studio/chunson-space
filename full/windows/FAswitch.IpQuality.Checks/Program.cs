using System.Text;
using System.Text.Json.Nodes;
using FAswitch.Accounts;

int count = 0;
void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "ip-quality-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var network = new FixtureNetwork();
var repository = new AccountRepository(root, systemProxyProvider: () => "http://127.0.0.1:7899");
var clock = new FixtureClock();
var service = new IpQualityService(repository, network, clock);
const string ip = "203.0.113.10";
const string proxy = "http://127.0.0.1:7899";
void QueueEgress(string traceIp = ip, string traceCountry = "US")
{
    network.Json(200, new() { ["success"] = true, ["ip"] = ip, ["country"] = "United States", ["country_code"] = "US", ["city"] = "Verified City", ["timezone"] = new JsonObject { ["id"] = "America/Chicago" } });
    network.Text(200, "ip=" + traceIp + "\nloc=" + traceCountry + "\n");
}
try
{
    await NetCoffeeChecks.Run(Check);
    QueueEgress(); network.Json(200, new() { ["ip"] = ip, ["company"] = "Fixture Network", ["asn"] = "AS64500 Fixture Network", ["country"] = "United States", ["city"] = "Fixture city", ["timezone"] = "America/Chicago" });
    var report = await service.ProbeProxyAsync(proxy);
    Check(report.Success && report.Ip == ip && report.Company == "Fixture Network" && report.Asn == "AS64500 Fixture Network" && report.Timezone == "America/Chicago", "anonymous ownership and location parsed");
    Check(!report.HasRiskSignals && report.IsProxy is null && report.IsVpn is null && report.IsDatacenter is null && report.IsTor is null && report.IsAbuser is null, "missing anonymous risks remain unknown rather than false");
    Check(network.Requests.Count == 3 && network.Requests.All(request => request.RequireProxy && request.Proxy == proxy && request.Url.Scheme == "https"), "all three sources share explicit final HTTP proxy over HTTPS");
    Check(network.Requests[^1].Url == IpQualityService.Endpoint && network.Requests[^1].Url.Query.Length == 0 && network.Requests[^1].Body is null && network.Requests[^1].Headers.Keys.All(key => !key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)), "quality queries outgoing request IP without API key or account authorization");
    QueueEgress(); network.Json(200, new() { ["ip"] = ip, ["company"] = new JsonObject { ["name"] = "Object Company" }, ["asn"] = new JsonObject { ["asn"] = 64500, ["org"] = "Object ASN" }, ["location"] = new JsonObject { ["country"] = "United States", ["city"] = "Object City", ["timezone"] = "America/Chicago" }, ["is_datacenter"] = true, ["is_proxy"] = false, ["is_vpn"] = true, ["is_tor"] = false, ["is_abuser"] = true });
    report = await service.ProbeProxyAsync(proxy);
    Check(report.Success && report.Company == "Object Company" && report.Asn == "AS64500 Object ASN" && report.City == "Object City", "full response object schema supported without enabling key setup");
    Check(report.HasRiskSignals && report.IsDatacenter == true && report.IsProxy == false && report.IsVpn == true && report.IsTor == false && report.IsAbuser == true, "explicit risk booleans preserved without a fabricated aggregate score");
    QueueEgress(); network.Json(200, new() { ["ip"] = "203.0.113.11", ["is_proxy"] = false });
    report = await service.ProbeProxyAsync(proxy);
    Check(!report.Success && report.IsProxy is null && report.Ip == ip, "quality-domain exit mismatch refuses labels");
    Check(report.HasVerifiedExit && report.Country == "United States" && report.City == "Verified City" && report.Timezone == "America/Chicago"
        && report.Source.Contains("ipwho.is"), "risk source failure preserves verified location and explicit provenance");
    QueueEgress(); network.Text(503, "unavailable"); network.Json(200, new() { ["ip"] = ip, ["fraudScore"] = 5 });
    report = await service.ProbeProxyWithScoreAsync(proxy);
    Check(!report.Success && report.HasVerifiedExit && !report.HasRiskSignals && report.Score?.Value == 5 && report.City == "Verified City", "partial result keeps confirmed exit location and independent valid score without claiming complete risk labels");
    QueueEgress("203.0.113.11");
    var before = network.Requests.Count;
    report = await service.ProbeProxyAsync(proxy);
    Check(!report.Success && network.Requests.Count == before + 2 && network.Requests[^1].Url != IpQualityService.Endpoint, "inconsistent strict egress sends no quality request");
    QueueEgress(); network.Json(429, new() { ["error_code"] = "ERR_FREE_TIER_EXHAUSTED" });
    before = network.Requests.Count;
    report = await service.ProbeProxyAsync(proxy);
    Check(!report.Success && network.Requests.Count == before + 3 && report.Message.Contains("30", StringComparison.Ordinal), "quota refusal produces one quality request and no retry");
    QueueEgress(); before = network.Requests.Count; report = await service.ProbeProxyAsync(proxy);
    Check(!report.Success && network.Requests.Count == before + 2 && network.Requests[^1].Url != IpQualityService.Endpoint, "429 backoff avoids repeating quality request for same measured exit");
    clock.Advance(TimeSpan.FromDays(1));
    QueueEgress(); network.Json(200, new() { ["ip"] = ip, ["is_proxy"] = "false", ["is_vpn"] = 0, ["is_tor"] = null, ["company"] = null });
    report = await service.ProbeProxyAsync(proxy);
    Check(report.Success && !report.HasRiskSignals && report.Company.Length == 0, "null and unsupported flag types remain unknown");
    Check(network.Requests[^1].Url == IpQualityService.Endpoint, "anonymous backoff expires at conservative UTC midnight");
    QueueEgress(); network.Json(403, new()); report = await service.ProbeProxyAsync(proxy);
    Check(!report.Success, "403 response initiates per-exit refusal backoff");
    clock.Advance(TimeSpan.FromHours(23)); QueueEgress(); before = network.Requests.Count; report = await service.ProbeProxyAsync(proxy);
    Check(!report.Success && network.Requests.Count == before + 2, "403 backoff prevents another request within twenty four hours");
    clock.Advance(TimeSpan.FromHours(2)); QueueEgress(); network.Json(200, new() { ["ip"] = ip });
    Check((await service.ProbeProxyAsync(proxy)).Success, "403 backoff expires after twenty four hours");
    QueueEgress(); network.Json(200, new() { ["ip"] = ip, ["error"] = "fixture-error" });
    Check(!(await service.ProbeProxyAsync(proxy)).Success, "HTTP success with API error is not successful diagnostics");
    before = network.Requests.Count;
    try { await service.ProbeProxyAsync(""); throw new InvalidOperationException("missing proxy allowed"); }
    catch (AccountException error) { Check(error.Code == AccountError.ExitUnavailable && network.Requests.Count == before, "missing proxy performs no request"); }
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    try { await service.ProbeProxyAsync(proxy, cancel.Token); throw new InvalidOperationException("cancel ignored"); }
    catch (OperationCanceledException) { Check(network.Requests.Count == before, "cancellation sends no request"); }
    repository.Profiles.CreateBoundAccount("fixture", new(FollowSystemProxy: true));
    QueueEgress(); network.Json(200, new() { ["ip"] = ip });
    report = await service.ProbeAsync("fixture");
    Check(report.Success && network.Requests.TakeLast(3).All(request => request.Proxy == proxy), "account diagnostics resolve the selected system route before probing");
    Check(repository.Profiles.ReadEnvironment("fixture").FollowSystemProxy && !File.Exists(Path.Combine(repository.AccountPath("fixture"), "auth.json")), "IP diagnostics mutate no account auth or route configuration");
    Check(IpQualityService.AnonymousDailyLimitPerExit == 30 && report.Source == "ipapi.is", "anonymous limit and single database source explicit");
    foreach (var pair in new[] { (Risk: 0, Quality: 100, Allowed: true), (Risk: 15, Quality: 85, Allowed: true), (Risk: 16, Quality: 84, Allowed: false), (Risk: 100, Quality: 0, Allowed: false) })
    {
        var assessment = (report with { Score = new(pair.Risk, "fixture") }).Admission;
        Check(assessment.QualityScore == pair.Quality && assessment.Allowed == pair.Allowed, "quality boundary " + pair.Quality);
    }
    foreach (int? invalid in new int?[] { null, -1, 101 })
        Check(!(report with { Score = new(invalid, "fixture") }).Admission.Allowed, "unknown or invalid score fails closed " + invalid);
    Check(!(report with { Ip = "", Score = new(0, "fixture") }).Admission.Allowed, "score without verified IP cannot authorize use");
    Check((report with { IsDatacenter = null, Score = new(5, "fixture", true) }).NetworkGroup == "家庭宽带组", "IPPure residential classification");
    Check((report with { IsDatacenter = null, Score = new(5, "fixture", false) }).NetworkGroup == "机房组", "IPPure documented non-residential category maps to datacenter group");
    Check((report with { IsDatacenter = true, Score = new(5, "fixture", true) }).NetworkGroup == "待分类", "conflicting databases do not invent a group");
    Check((report with { IsDatacenter = null, Score = new(5, "fixture") }).NetworkGroup == "待分类", "missing category never becomes datacenter by default");
    QueueEgress(); network.Json(503, new()); network.Json(200, new() { ["ip"] = ip, ["fraudScore"] = 15, ["isResidential"] = true, ["isBroadcast"] = false, ["asn"] = 64500, ["asOrganization"] = "Fixture ISP" });
    report = await service.ProbeProxyWithScoreAsync(proxy);
    Check(report.Admission.Allowed && report.NetworkGroup == "家庭宽带组" && report.Company == "Fixture ISP" && report.Asn == "AS64500" && report.Score!.IsBroadcast == false, "documented public API keeps grade and attributes when supplemental provider fails");
    var admission = new IpAdmissionService(async (proxy, token) =>
        await service.AddScoreAsync(await service.ProbeScreeningExitAsync(proxy, token), proxy, token));
    QueueEgress(); network.Json(200, new() { ["ip"] = ip, ["fraudScore"] = 16 });
    try { await admission.RequireProxyAsync(proxy, default); throw new InvalidOperationException("low quality login accepted"); }
    catch (AccountException error) { Check(error.Message.Contains("84") && error.Message.Contains("85"), "fresh low score blocks login with actionable reason"); }
    QueueEgress(); network.Json(200, new() { ["ip"] = ip });
    var launchEvidence = await admission.ProbeAsync(new(FAswitch.Core.IdentityKind.Account, "fixture"), proxy, default);
    Check(!launchEvidence.Success && launchEvidence.Message.Contains("未知"), "launch rejects missing grade even with a valid exit");
    var beforeFast = network.Requests.Count;
    QueueEgress(); network.Json(200, new() { ["ip"] = ip, ["fraudScore"] = 15, ["isResidential"] = true });
    var fastEvidence = await admission.RequireVerifiedProxyAsync(proxy, default);
    Check(fastEvidence.Ok && fastEvidence.Ip == ip && fastEvidence.CountryCode == "US" && fastEvidence.Timezone == "America/Chicago" && network.Requests.Count == beforeFast + 3
        && network.Requests.Skip(beforeFast).All(request => request.Url != IpQualityService.Endpoint), "login checks exact85 with three necessary requests and no optional IP provider");
    QueueEgress(traceCountry: "JP"); var beforeCountry = network.Requests.Count;
    var inconsistentCountry = await service.ProbeScreeningExitAsync(proxy);
    Check(!inconsistentCountry.HasVerifiedExit && network.Requests.Count == beforeCountry + 2, "conflicting country evidence is rejected without another request or system guess");
    Console.WriteLine($"PASS {count} isolated IP-quality checks; fake transport only; no actual proxy/node/IP request or selection; fixture root: {root}");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine($"FAIL after {count} IP-quality checks: {error}; fixture root: {root}"); return 1; }

sealed class FixtureNetwork : IAccountTransport
{
    private readonly Queue<(int Status, string Body)> responses = new();
    public List<AccountHttpRequest> Requests { get; } = [];
    public void Json(int status, JsonObject value) => Text(status, value.ToJsonString());
    public void Text(int status, string body) => responses.Enqueue((status, body));
    public Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Requests.Add(request); if (responses.Count == 0) throw new InvalidOperationException("unexpected fixture request"); var response = responses.Dequeue(); return Task.FromResult(new AccountHttpResponse(response.Status, new MemoryStream(Encoding.UTF8.GetBytes(response.Body)))); }
}
sealed class FixtureClock : IAccountClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan duration) => UtcNow += duration;
    public Task DelayAsync(TimeSpan duration, CancellationToken token) { token.ThrowIfCancellationRequested(); Advance(duration); return Task.CompletedTask; }
}
