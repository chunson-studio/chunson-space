using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;

internal static class RegionEgressChecks
{
    public static async Task<int> Run(string root)
    {
        int count = 0, scenario = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        const string proxy = "http://127.0.0.1:7899", ip = "203.0.113.10";
        (FakeTransport Network, EgressService Service) New(string traceCountry = "US", string country = "United States")
        {
            var network = new FakeTransport();
            network.Json(200, new JsonObject { ["success"] = true, ["ip"] = ip, ["country"] = country, ["country_code"] = traceCountry == "JP" ? "JP" : "US", ["timezone"] = new JsonObject { ["id"] = traceCountry == "JP" ? "Asia/Tokyo" : "America/Chicago" } });
            network.Text(200, "ip=" + ip + "\nloc=" + traceCountry + "\n");
            return (network, new(new AccountRepository(Path.Combine(root, (++scenario).ToString())), network));
        }
        JsonObject Nested(string address = ip, string country = "US", string zone = "America/Chicago") => new() { ["ip"] = address, ["location"] = new JsonObject { ["country_code"] = country, ["timezone"] = zone } };
        JsonObject Flat(string country = "United States", string zone = "America/Chicago", string address = ip) => new() { ["ip"] = address, ["country"] = country, ["timezone"] = zone };
        var (network, service) = New(); network.Json(200, Nested()); var result = await service.ProbeRegionProxyAsync(proxy);
        Check(result.Ok && result.Timezone == "America/Chicago" && network.Requests.Count == 3, "normal region uses three anonymous sources and two precise timezones without backups");
        Check(network.Requests.All(r => r.Proxy == proxy && r.RequireProxy && !r.UseSystemProxy && r.Url.Scheme == "https" && !r.Headers.ContainsKey("Authorization")), "all region providers retain the same explicit HTTPS proxy");
        foreach (var value in new[] { ("203.0.113.11", "US", "America/Chicago"), (ip, "CN", "America/Chicago"), (ip, "US", "America/Los_Angeles") })
        {
            (network, service) = New(); network.Json(200, Nested(value.Item1, value.Item2, value.Item3));
            Check(!(await service.ProbeRegionProxyAsync(proxy)).Ok && network.Requests.Count == 3, "observed exit country or timezone disagreement stops before backup selection");
        }
        (network, service) = New("CN");
        Check(!(await service.ProbeRegionProxyAsync(proxy)).Ok && network.Requests.Count == 2, "conflicting trace country stops before further requests");
        foreach (var missing in new[] { "zone", "country", "error", "429" })
        {
            (network, service) = New(); var payload = Nested(country: missing == "country" ? "" : "US", zone: missing == "zone" ? "" : "America/Chicago");
            if (missing == "error") payload["error_code"] = "ERR_FREE_TIER_EXHAUSTED";
            network.Json(missing == "429" ? 429 : 200, payload); network.Text(503, "");
            result = await service.ProbeRegionProxyAsync(proxy);
            Check(!result.Ok && result.Ip == ip && network.Requests.Count == 4 && result.Message.Contains(missing == "429" ? "额度" : missing == "error" ? "查询错误" : "未返回"), "missing third-source data tries one finite backup and retains precise failure when unavailable");
        }
        (network, service) = New(); network.Json(200, Flat());
        result = await service.ProbeRegionProxyAsync(proxy);
        Check(result.Ok && result.CountryCode == "US" && network.Requests.Count == 3, "flat anonymous country name is resolved without guessing its timezone");
        foreach (var payload in new[] { Flat(country: "Canada"), Flat(zone: "America/New_York"), Flat(address: "203.0.113.12") })
        {
            (network, service) = New(); network.Json(200, payload); result = await service.ProbeRegionProxyAsync(proxy);
            Check(!result.Ok && result.Message.Contains(payload["ip"]!.GetValue<string>() != ip ? "不同出口" : payload["country"]!.GetValue<string>() == "Canada" ? "国家定位未一致" : "时区定位未一致") && network.Requests.Count == 3, "flat source disagreements remain explicit and final");
        }
        foreach (var precise in new[] { "explicit", "coordinates" })
        {
            (network, service) = New(); network.Json(429, new()); network.Text(200, "ip=" + ip + "\nloc=US\n");
            var coffee = new JsonObject { ["ip"] = ip, ["countryCode"] = "US", ["city"] = "Chicago" };
            if (precise == "explicit") coffee["timezone"] = "America/Chicago";
            else coffee["geo_sources"] = new JsonArray(new JsonObject { ["country_code"] = "US", ["city"] = "Chicago", ["lat"] = 41.8781, ["lon"] = -87.6298, ["accuracy_km"] = 10 });
            network.Json(200, coffee); result = await service.ProbeRegionProxyAsync(proxy);
            Check(result.Ok && result.Timezone == "America/Chicago" && result.Message.Contains("备用") && network.Requests.Count == 5, "NetCoffee precise " + precise + " can replace unavailable second timezone source");
        }
        (network, service) = New("JP", "Japan"); network.Json(503, new()); network.Text(200, "ip=" + ip + "\nloc=JP\n");
        network.Json(200, new JsonObject { ["ip"] = ip, ["countryCode"] = "JP", ["country"] = "Japan" }); result = await service.ProbeRegionProxyAsync(proxy);
        Check(!result.Ok && result.Message.Contains("具体时区"), "backup never guesses timezone solely from a single-timezone country");
        (network, service) = New(); network.Json(503, new()); network.Text(200, "ip=" + ip + "\nloc=US\n");
        network.Json(200, new JsonObject { ["ip"] = ip, ["countryCode"] = "US", ["geo_sources"] = new JsonArray(
            new JsonObject { ["country_code"] = "US", ["city"] = "Chicago", ["lat"] = 41.8781, ["lon"] = -87.6298, ["accuracy_km"] = 10 },
            new JsonObject { ["country_code"] = "US", ["city"] = "New York", ["lat"] = 40.7128, ["lon"] = -74.006, ["accuracy_km"] = 10 }) });
        Check(!(await service.ProbeRegionProxyAsync(proxy)).Ok, "inconsistent coordinate-derived zones remain unknown");
        return count;
    }
}
