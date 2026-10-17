using System.Text.Json.Nodes;
using FAswitch.Accounts;
using FAswitch.Application;
using FAswitch.Core;

internal static class StrictEgressChecks
{
    public static async Task<int> Run(string root)
    {
        int count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        var network = new FakeTransport();
        var service = new EgressService(new AccountRepository(root), network);
        var proxy = "http://127.0.0.1:7899";
        JsonObject Payload(string ip, string timezone) => new() { ["success"] = true, ["ip"] = ip, ["timezone"] = new JsonObject { ["id"] = timezone }, ["country_code"] = "US" };
        network.Json(200, Payload("203.0.113.10", "America/Chicago"));
        network.Text(200, "ip=203.0.113.10\nloc=US\n");
        var evidence = await service.ProbeAsync(new(IdentityKind.Account, "not-yet-created"), proxy, default);
        Check(LaunchEgressRules.Verified(evidence) && evidence.Ip == "203.0.113.10" && evidence.Timezone == "America/Chicago", "egress uses measured timezone before any profile exists");
        Check(network.Requests.Count == 2 && network.Requests.All(request => request.Proxy == proxy && request.RequireProxy && request.Url.Scheme == "https") && network.Requests[0].Url == AccountEndpoints.Egress && network.Requests[1].Url == AccountEndpoints.Trace, "strict egress compares two HTTPS sources through identical final proxy without account credentials");
        foreach (var (ip, timezone) in new[] { ("invalid", "America/Chicago"), ("127.0.0.1", "America/Chicago"), ("203.0.113.10", "Unknown/Zone"), ("203.0.113.10", "") })
        {
            network.Json(200, Payload(ip, timezone)); network.Text(200, "ip=203.0.113.10\nloc=US\n");
            Check(!(await service.ProbeProxyAsync(proxy)).Ok, "invalid IP or unresolvable timezone fails closed");
        }
        network.Json(503, new()); network.Text(200, "ip=203.0.113.10\n");
        Check(!(await service.ProbeProxyAsync(proxy)).Ok, "unavailable egress endpoint fails closed");
        network.Json(200, Payload("203.0.113.10", "America/Chicago")); network.Text(200, "ip=203.0.113.11\n");
        Check(!(await service.ProbeProxyAsync(proxy)).Ok, "different domain exits fail strict route verification");
        network.Json(200, Payload("203.0.113.10", "America/Chicago")); network.Text(503, "");
        Check(!(await service.ProbeProxyAsync(proxy)).Ok, "second HTTPS source failure cannot confirm strict exit");
        network.Json(200, Payload("203.0.113.10", "America/Chicago")); network.Text(200, "ip=203.0.113.10\nip=203.0.113.10\n");
        Check(!(await service.ProbeProxyAsync(proxy)).Ok, "ambiguous trace response fails closed");
        var before = network.Requests.Count;
        try { await service.ProbeProxyAsync(""); throw new InvalidOperationException("direct probe accepted"); }
        catch (AccountException error) { Check(error.Code == AccountError.ExitUnavailable && network.Requests.Count == before, "absent explicit proxy sends no egress request"); }
        foreach (var status in new[] { 403, 429, 503 })
        {
            network.Json(200, Payload("203.0.113.10", "America/Chicago"));
            network.Text(status, "account-05d4aa4b3a@example.invalid token=do-not-display");
            before = network.Requests.Count;
            var failed = await service.ProbeProxyAsync(proxy);
            Check(!failed.Ok && failed.Ip == "203.0.113.10" && failed.Message.Contains("Cloudflare") && failed.Message.Contains(status.ToString())
                && !failed.Message.Contains("private-response") && !failed.Message.Contains("do-not-display") && network.Requests.Count == before + 2,
                "HTTP errors identify the query source, retain partial evidence and never echo body or retry");
        }
        network.Json(429, new()); network.Text(403, "private-response");
        var both = await service.ProbeProxyAsync(proxy);
        Check(!both.Ok && both.Message.Contains("ipwho.is") && both.Message.Contains("Cloudflare") && both.Message.Contains("429") && both.Message.Contains("403"), "two failed providers retain both reasons rather than alleging changed IP");
        network.Json(200, Payload("203.0.113.10", "America/Chicago")); network.Text(200, "ip=invalid\n");
        var invalid = await service.ProbeProxyAsync(proxy);
        Check(!invalid.Ok && invalid.Message.Contains("未返回唯一、有效") && !invalid.Message.Contains("不同出口"), "invalid trace data is distinguished from an observed IP disagreement");
        network.Json(200, Payload("203.0.113.10", "America/Chicago")); network.Text(200, "ip=203.0.113.11\n");
        var changed = await service.ProbeProxyAsync(proxy);
        Check(!changed.Ok && changed.Message.Contains("不同出口") && changed.Message.Contains("203.0.113.10") && changed.Message.Contains("203.0.113.11"), "actual mismatched source IPs name both measured values");
        network.Json(200, Payload("203.0.113.10", "")); network.Text(200, "ip=203.0.113.10\n");
        var missingZone = await service.ProbeProxyAsync(proxy);
        Check(!missingZone.Ok && missingZone.Ip == "203.0.113.10" && missingZone.Message.Contains("具体时区"), "missing timezone retains measured IP without reporting full verification");
        foreach (var code in new[] { AccountError.Timeout, AccountError.Network, AccountError.CorruptResponse })
        {
            network.Stream(200, () => throw new AccountException(code, "private raw error")); network.Text(200, "ip=203.0.113.10\n");
            var failed = await service.ProbeProxyAsync(proxy);
            Check(!failed.Ok && failed.Message.Contains("ipwho.is") && !failed.Message.Contains("private raw") && failed.Message.Length > 20, "query transport and data failures have bounded source-specific guidance");
        }
        network.Json(403, new()); network.Text(200, "ip=203.0.113.10\n");
        var launch = await service.ProbeAsync(new(IdentityKind.Account, "not-yet-created"), proxy, default);
        Check(!launch.Success && launch.Message.Contains("ipwho.is") && launch.Message.Contains("403"), "launch evidence preserves concrete provider failure");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        before = network.Requests.Count;
        try { await service.ProbeProxyAsync(proxy, cancelled.Token); throw new InvalidOperationException("cancelled query accepted"); }
        catch (OperationCanceledException) { Check(network.Requests.Count == before, "caller cancellation propagates without being misreported as failed IP"); }
        return count;
    }
}
