using System.Text;
using System.Text.Json.Nodes;
using FAswitch.Accounts;

static class NetCoffeeChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var fake = new Network(); var service = new NetCoffeeService(fake); const string proxy = "http://127.0.0.1:7897";
        var current = await service.ProbeScreeningExitAsync(proxy);
        var scored = await service.AddScoreAsync(current, proxy);
        check(current.Ip == "203.0.113.10" && current.Score is null && fake.Requests.Count == 2, "NetCoffee trace then one lookup without mixed providers");
        check(fake.Requests.All(r => r.Url.Host == "ip.net.coffee" && r.RequireProxy && r.Proxy == proxy && !r.Headers.ContainsKey("Authorization")), "NetCoffee calls fixed explicit proxy without credentials");
        check(scored.Admission.QualityScore == 98 && scored.Admission.Allowed && scored.Score?.Value is null && scored.NetworkGroup == "家庭宽带组", "NetCoffee raw 98 is quality 98, never inverted or mixed with IPPure");
        check(scored.Timezone == "America/Phoenix" && scored.City == "Phoenix", "precise coordinates resolve Phoenix locally while broad country centroid is ignored");
        foreach (var value in new JsonNode?[] { null, JsonValue.Create(true), JsonValue.Create("98"), JsonValue.Create(-1), JsonValue.Create(101), JsonValue.Create(84) })
        {
            var data = JsonNode.Parse(fake.Lookup)!.AsObject(); data["trust_score"] = value;
            check(!NetCoffeeService.Parse(data, current).Admission.Allowed, "invalid unknown or low NetCoffee score cannot authorize");
        }
        var threshold = JsonNode.Parse(fake.Lookup)!.AsObject(); threshold["trust_score"] = 85;
        check(NetCoffeeService.Parse(threshold, current).Admission.Allowed, "NetCoffee 85 boundary accepted");
        threshold["ip"] = "203.0.113.11";
        check(!NetCoffeeService.Parse(threshold, current).Admission.Allowed, "NetCoffee wrong IP response rejected");
        threshold["ip"] = current.Ip; threshold["countryCode"] = "GB";
        check(!NetCoffeeService.Parse(threshold, current).Admission.Allowed, "NetCoffee contradictory country rejected");
        var uncertain = JsonNode.Parse(fake.Lookup)!.AsObject(); uncertain["geo_sources"] = new JsonArray();
        check(NetCoffeeService.Parse(uncertain, current).Timezone == "", "multi-zone country never guessed without useful coordinates");
        fake.Status = 429;
        var before = fake.Requests.Count; var limited = await service.AddScoreAsync(current, proxy);
        check(!limited.Admission.Allowed && fake.Requests.Count == before + 1 && limited.Score!.Message.Contains("限流"), "NetCoffee rate limit does not retry or fall back");
    }
    private sealed class Network : IAccountTransport
    {
        public int Status = 200;
        public List<AccountHttpRequest> Requests = [];
        public string Lookup = """
        {"ip":"203.0.113.10","countryCode":"us","country":"United States","trust_score":98,"isResidential":true,"is_datacenter":false,"asn":20012,"asOrganization":"Fixture","geo_sources":[{"country_code":"us","city":"Centroid","lat":37.751,"lon":-97.822,"accuracy_km":1000},{"country_code":"us","city":"Phoenix","lat":33.4483,"lon":-112.073}]}
        """;
        public Task<AccountHttpResponse> SendAsync(AccountHttpRequest r, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(r);
            return Task.FromResult(new AccountHttpResponse(Status, new MemoryStream(Encoding.UTF8.GetBytes(r.Url == NetCoffeeService.TraceEndpoint ? "ip=203.0.113.10\nloc=US\n" : Lookup))));
        }
    }
}
