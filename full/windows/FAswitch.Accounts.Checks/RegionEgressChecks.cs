using System.Text.Json.Nodes;
using FAswitch.Accounts;

internal static class RegionEgressChecks
{
    public static async Task<int> Run(string root)
    {
        int count = 0; void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); count++; }
        var transport = new FakeTransport(); var service = new EgressService(new AccountRepository(root), transport);
        const string proxy = "http://127.0.0.1:7899";
        void Inputs(string traceCountry = "US", string reportIp = "203.0.113.10", string reportCountry = "US", string reportTimezone = "America/Chicago")
        {
            transport.Json(200, new JsonObject { ["success"] = true, ["ip"] = "203.0.113.10", ["country_code"] = "US", ["timezone"] = new JsonObject { ["id"] = "America/Chicago" } });
            transport.Text(200, "ip=203.0.113.10\nloc=" + traceCountry + "\n");
            if (traceCountry == "US") transport.Json(200, new JsonObject { ["ip"] = reportIp, ["location"] = new JsonObject { ["country_code"] = reportCountry, ["timezone"] = reportTimezone } });
        }
        Inputs(); var result = await service.ProbeRegionProxyAsync(proxy);
        Check(result.Ok && result.Timezone == "America/Chicago", "region measures specific US timezone instead of selecting first national candidate");
        Check(transport.Requests.Count == 3 && transport.Requests.All(r => r.Proxy == proxy && r.RequireProxy && r.Url.Scheme == "https" && r.Headers?.ContainsKey("Authorization") != true), "region three anonymous sources all use identical explicit proxy");
        foreach (var value in new[] { ("203.0.113.11", "US", "America/Chicago"), ("203.0.113.10", "CN", "America/Chicago"), ("203.0.113.10", "US", "America/Los_Angeles"), ("203.0.113.10", "US", ""), ("203.0.113.10", "", "America/Chicago") })
        { Inputs(reportIp: value.Item1, reportCountry: value.Item2, reportTimezone: value.Item3); Check(!(await service.ProbeRegionProxyAsync(proxy)).Ok, "different exit, country, specific timezone or missing region source field blocks configuration"); }
        Inputs(traceCountry: "CN"); var before = transport.Requests.Count;
        Check(!(await service.ProbeRegionProxyAsync(proxy)).Ok && transport.Requests.Count == before + 2, "conflicting trace country stops before third source request");
        transport.Json(200, new JsonObject { ["success"] = true, ["ip"] = "203.0.113.10", ["country_code"] = "US", ["timezone"] = new JsonObject { ["id"] = "America/Chicago" } });
        transport.Text(200, "ip=203.0.113.10\nloc=US\n"); transport.Json(429, new());
        result = await service.ProbeRegionProxyAsync(proxy);
        Check(!result.Ok && result.Message.Contains("额度"), "anonymous source rate limit is reported and does not become unreliable region data");
        void Flat(string country = "United States", string timezone = "America/Chicago", string ip = "203.0.113.10", bool apiError = false)
        {
            transport.Json(200, new JsonObject { ["success"] = true, ["ip"] = "203.0.113.10", ["country"] = "United States", ["country_code"] = "US", ["timezone"] = new JsonObject { ["id"] = "America/Chicago" } });
            transport.Text(200, "ip=203.0.113.10\nloc=US\n");
            transport.Json(200, new JsonObject { ["ip"] = ip, ["country"] = country, ["timezone"] = timezone,
                ["docs"] = "https://ipapi.is/free-tier.html?ref=api", ["error_code"] = apiError ? "ERR_FREE_TIER_EXHAUSTED" : null });
        }
        Flat(); before = transport.Requests.Count; result = await service.ProbeRegionProxyAsync(proxy);
        Check(result.Ok && result.Timezone == "America/Chicago" && transport.Requests.Count == before + 3, "current anonymous flat response confirms exact timezone without country_code or extra request");
        Flat(country: "Canada"); result = await service.ProbeRegionProxyAsync(proxy);
        Check(!result.Ok && result.Message.Contains("国家定位未一致") && result.Message.Contains("Canada"), "flat country disagreement is rejected with a specific reason");
        Flat(timezone: "America/New_York"); result = await service.ProbeRegionProxyAsync(proxy);
        Check(!result.Ok && result.Message.Contains("时区定位未一致") && result.Message.Contains("America/Chicago") && result.Message.Contains("America/New_York"), "timezone disagreement shows both source values instead of blaming the proxy");
        Flat(ip: "203.0.113.12"); result = await service.ProbeRegionProxyAsync(proxy);
        Check(!result.Ok && result.Message.Contains("不同出口") && result.Message.Contains("203.0.113.12"), "different source exits are rejected before geographic comparison");
        Flat(timezone: ""); result = await service.ProbeRegionProxyAsync(proxy);
        Check(!result.Ok && result.Message.Contains("未返回可识别"), "missing flat timezone remains unverified rather than guessed from country");
        Flat(country: ""); result = await service.ProbeRegionProxyAsync(proxy);
        Check(!result.Ok && result.Message.Contains("未返回"), "missing flat country does not silently pass agreement");
        Flat(apiError: true); result = await service.ProbeRegionProxyAsync(proxy);
        Check(!result.Ok && result.Message.Contains("查询错误"), "error-bearing 200 response cannot confirm geography");
        Inputs(); result = await service.ProbeRegionProxyAsync(proxy);
        Check(result.Ok, "legacy nested schema remains supported after flat schema handling");
        transport.Json(200, new JsonObject { ["success"] = true, ["ip"] = "203.0.113.10", ["country_code"] = "US", ["timezone"] = new JsonObject { ["id"] = "America/Chicago" } });
        transport.Text(429, "private-provider-body"); before = transport.Requests.Count;
        result = await service.ProbeRegionProxyAsync(proxy);
        Check(!result.Ok && result.Message.Contains("Cloudflare") && result.Message.Contains("429") && !result.Message.Contains("未通过核对")
            && transport.Requests.Count == before + 2, "region keeps a rate-limit reason and skips the third query instead of alleging geographic disagreement");
        return count;
    }
}
