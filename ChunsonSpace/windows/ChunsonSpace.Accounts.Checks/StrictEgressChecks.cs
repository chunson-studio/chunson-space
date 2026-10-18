using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Application;
using ChunsonSpace.Core;

internal static class StrictEgressChecks
{
    public static async Task<int> Run(string root)
    {
        int count = 0, scenario = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        const string proxy = "http://127.0.0.1:7899";
        const string ip = "203.0.113.10";
        (FakeTransport Network, EgressService Service) New()
        { var network = new FakeTransport(); return (network, new(new AccountRepository(Path.Combine(root, (++scenario).ToString())), network)); }
        JsonObject Who(string address = ip, string zone = "America/Chicago") => new() { ["success"] = true, ["ip"] = address, ["timezone"] = new JsonObject { ["id"] = zone }, ["country_code"] = "US" };
        JsonObject Api(string address = ip, string zone = "America/Chicago") => new() { ["ip"] = address, ["country_code"] = "US", ["timezone"] = zone };
        JsonObject Coffee(string address = ip, string zone = "America/Chicago") => new() { ["ip"] = address, ["countryCode"] = "US", ["timezone"] = zone };
        void Trace(FakeTransport n, string address = ip) => n.Text(200, "ip=" + address + "\nloc=US\n");
        void BackupsFail(FakeTransport n) { n.Json(503, new()); n.Text(503, ""); }
        void Routes(FakeTransport n) => Check(n.Requests.All(r => r.Proxy == proxy && r.RequireProxy && !r.UseSystemProxy && r.Url.Scheme == "https" && !r.Headers.ContainsKey("authorization")), "all primary and backup requests retain one explicit HTTPS proxy without auth or direct fallback");
        var (network, service) = New(); network.Json(200, Who()); Trace(network);
        var evidence = await service.ProbeAsync(new(IdentityKind.Account, "not-yet-created"), proxy, default);
        Check(LaunchEgressRules.Verified(evidence) && evidence.Ip == ip && evidence.Timezone == "America/Chicago", "strict egress uses measured timezone before any profile exists");
        Check(network.Requests.Count == 2 && network.Requests[0].Url == AccountEndpoints.Egress && network.Requests[1].Url == AccountEndpoints.Trace, "healthy primary sources avoid backup requests"); Routes(network);

        foreach (var (address, zone) in new[] { ("invalid", "America/Chicago"), ("127.0.0.1", "America/Chicago"), (ip, "Unknown/Zone"), (ip, "") })
        {
            (network, service) = New(); network.Json(200, Who(address, zone)); Trace(network); BackupsFail(network);
            Check(!(await service.ProbeProxyAsync(proxy)).Ok && network.Requests.Count == 4, "invalid primary data remains unverified when finite backups fail");
        }
        foreach (var status in new[] { 403, 429, 503 })
        {
            (network, service) = New(); network.Json(200, Who()); network.Text(status, "account-05d4aa4b3a@example.invalid token=do-not-display"); BackupsFail(network);
            var failed = await service.ProbeProxyAsync(proxy);
            Check(!failed.Ok && failed.Ip == ip && failed.Message.Contains("Cloudflare") && failed.Message.Contains(status.ToString())
                && !failed.Message.Contains("private-response") && !failed.Message.Contains("do-not-display") && network.Requests.Count == 4,
                "all failed sources preserve partial evidence and safe reasons with one attempt per endpoint"); Routes(network);
        }
        (network, service) = New(); network.Json(429, new()); network.Text(403, "private-response"); BackupsFail(network);
        var both = await service.ProbeProxyAsync(proxy);
        Check(!both.Ok && both.Message.Contains("ipwho.is") && both.Message.Contains("Cloudflare") && both.Message.Contains("429") && both.Message.Contains("403"), "failed primary providers retain both reasons after finite backup failures");

        foreach (var trace in new[] { "ip=invalid\n", "ip=" + ip + "\nip=" + ip + "\n" })
        {
            (network, service) = New(); network.Json(200, Who()); network.Text(200, trace); BackupsFail(network);
            var failed = await service.ProbeProxyAsync(proxy);
            Check(!failed.Ok && failed.Message.Contains("未返回唯一、有效") && !failed.Message.Contains("不同出口"), "invalid or duplicate trace values are unavailable evidence rather than measured disagreement");
        }
        (network, service) = New(); network.Json(200, Who()); Trace(network, "203.0.113.11");
        var changed = await service.ProbeProxyAsync(proxy);
        Check(!changed.Ok && changed.Message.Contains("不同出口") && changed.Message.Contains(ip) && changed.Message.Contains("203.0.113.11") && network.Requests.Count == 2, "observed source conflict stops without selecting an agreeable backup");
        foreach (var code in new[] { AccountError.Timeout, AccountError.Network, AccountError.CorruptResponse })
        {
            (network, service) = New(); network.Stream(200, () => throw new AccountException(code, "private raw error")); Trace(network); BackupsFail(network);
            var failed = await service.ProbeProxyAsync(proxy);
            Check(!failed.Ok && failed.Message.Contains("ipwho.is") && !failed.Message.Contains("private raw"), "source failures stay bounded and do not expose transport exception text");
        }

        foreach (var unavailable in new[] { "who", "trace", "zone" })
        {
            (network, service) = New(); network.Json(unavailable == "who" ? 503 : 200, Who(zone: unavailable == "zone" ? "" : "America/Chicago"));
            if (unavailable == "trace") network.Text(429, ""); else Trace(network);
            network.Json(200, Api()); var result = await service.ProbeCountryProxyAsync(proxy);
            Check(result.Ok && result.Ip == ip && result.Timezone == "America/Chicago" && result.Message.Contains("ipapi.is") && result.Message.Contains("备用") && network.Requests.Count == 3, "ipapi backup replaces unavailable primary details or independent witness without changing route"); Routes(network);
        }
        (network, service) = New(); network.Json(503, new()); network.Text(503, ""); network.Json(200, Api()); Trace(network); network.Json(200, Coffee());
        var coffee = await service.ProbeRegionProxyAsync(proxy);
        Check(coffee.Ok && coffee.Message.Contains("Net.Coffee") && coffee.Message.Contains("两来源") && network.Requests.Count == 5, "NetCoffee trace and exact-IP lookup restore a second precise timezone when both primaries are unavailable"); Routes(network);
        Check(network.Requests[3].Url == NetCoffeeService.TraceEndpoint && network.Requests[4].Url.AbsoluteUri == NetCoffeeService.LookupEndpoint.AbsoluteUri + ip, "NetCoffee lookup uses current measured trace IP only");
        (network, service) = New(); network.Json(503, new()); network.Text(503, ""); network.Json(503, new()); Trace(network); network.Json(200, Coffee());
        Check(!(await service.ProbeRegionProxyAsync(proxy)).Ok && network.Requests.Count == 5, "one provider's trace and lookup cannot masquerade as two independent timezone sources");
        (network, service) = New(); network.Json(200, Who()); network.Text(503, ""); network.Json(503, new()); Trace(network); network.Json(200, Coffee("203.0.113.11"));
        Check(!(await service.ProbeProxyAsync(proxy)).Ok && network.Requests.Count == 5, "NetCoffee lookup mismatch cannot be ignored to count its trace as an agreeable backup");
        (network, service) = New(); network.Json(200, Who()); network.Text(503, ""); network.Json(200, Api(zone: "America/New_York"));
        Check(!(await service.ProbeProxyAsync(proxy)).Ok && network.Requests.Count == 3, "conflicting fallback timezone is rejected rather than selecting another source");
        (network, service) = New(); network.Json(200, Who(zone: "")); Trace(network); network.Json(200, Api("203.0.113.11"));
        Check(!(await service.ProbeProxyAsync(proxy)).Ok && network.Requests.Count == 3, "partial primary IP still prevents combining timezone from a different exit");

        (network, service) = New();
        try { await service.ProbeProxyAsync(""); throw new InvalidOperationException("direct probe accepted"); }
        catch (AccountException error) { Check(error.Code == AccountError.ExitUnavailable && network.Requests.Count == 0, "missing explicit proxy sends no requests"); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.ProbeProxyAsync(proxy, cancelled.Token); throw new InvalidOperationException("cancelled query accepted"); }
        catch (OperationCanceledException) { Check(network.Requests.Count == 0, "caller cancellation sends no primary or backup requests"); }
        return count;
    }
}
