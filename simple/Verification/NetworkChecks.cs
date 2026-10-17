using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace ChunsonLogin;

internal static class NetworkChecks
{
    public static async Task RunAsync(string fixtures, Action<bool, string> check)
    {
        var root = Path.Combine(fixtures, "network");
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "config.yaml");
        var entries = new List<ProxyListener>();
        var reads = 0;
        var resolver = new FlClashNetwork(root, token => { token.ThrowIfCancellationRequested(); reads++; return Task.FromResult<IReadOnlyList<ProxyListener>>(entries.ToArray()); });
        async Task Refused(Func<Task> action, string expected, string name)
        {
            try { await action(); check(false, name); }
            catch (InvalidOperationException error) { check(error.Message.Contains(expected, StringComparison.Ordinal), name); }
        }
        await File.WriteAllTextAsync(config, "mixed-port: 7890\nbind-address: '*'\ntun: {enable: true}\nlisteners: [{name: account, type: http, port: 61000}]\n");
        await Refused(async () => { _ = await resolver.ResolveAsync(default); }, "未找到运行中", "proxy: stale config never creates a direct route");
        entries.Add(new("other.exe", "127.0.0.1", 7890));
        await Refused(async () => { _ = await resolver.ResolveAsync(default); }, "未找到运行中", "proxy: listener must belong to FlClash core");
        entries.Clear(); entries.Add(new("FlClashCore.exe", "127.0.0.1", 61000));
        await Refused(async () => { _ = await resolver.ResolveAsync(default); }, "尚未监听", "proxy: account-specific listener is never selected");
        entries.Add(new("FlClashCore.exe", "0.0.0.0", 7890));
        var route = await resolver.ResolveAsync(default);
        check(route.Proxy == "http://127.0.0.1:7890", "proxy: TUN-only configuration uses verified existing main HTTP listener");
        check((await File.ReadAllTextAsync(config)).Contains("tun: {enable: true}"), "proxy: discovery does not alter FlClash configuration");
        await File.WriteAllTextAsync(config, "mixed-port: 7890\nauthentication: ['fixture-user:fixture-password']\n");
        await Refused(async () => { _ = await resolver.ResolveAsync(default); }, "需要密码", "proxy: authenticated main listener is refused without displaying credentials");
        await File.WriteAllTextAsync(config, "port: 7891\nbind-address: '::1'\nauthentication: []\n");
        entries.Clear(); entries.Add(new("FlClashCore.exe", "::", 7891));
        check((await resolver.ResolveAsync(default)).Proxy == "http://[::1]:7891", "proxy: IPv6 HTTP-only main listener is supported");
        await File.WriteAllTextAsync(config, "mixed-port: 7891\nbind-address: '192.168.1.2'\n");
        await Refused(async () => { _ = await resolver.ResolveAsync(default); }, "本机地址", "proxy: remote bind is not silently converted to a local route");
        await File.WriteAllTextAsync(config, "mixed-port: 7891\nmixed-port: 7892\n");
        await Refused(async () => { _ = await resolver.ResolveAsync(default); }, "配置格式", "proxy: duplicate YAML values are rejected");
        await File.WriteAllTextAsync(config, "mixed-port: &port 7891\nport: *port\n");
        await Refused(async () => { _ = await resolver.ResolveAsync(default); }, "配置格式", "proxy: YAML aliases cannot expand through configuration parsing");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var before = reads;
            try { _ = await resolver.ResolveAsync(cancelled.Token); check(false, "proxy: cancellation"); }
            catch (OperationCanceledException) { check(reads == before, "proxy: cancellation precedes listener inspection"); }
        }

        var timestamp = DateTimeOffset.Parse("2026-10-16T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var requested = new List<(Uri Url, bool ExplicitProxy, bool NoCookies)>();
        var ipwho = Who(); var ipapi = Api();
        HttpStatusCode status = HttpStatusCode.OK;
        bool hold = false;
        var probe = new EgressProbe(policy =>
        {
            var proxy = policy.Proxy as WebProxy;
            var explicitProxy = policy.UseProxy && proxy is not null && proxy.Address?.AbsoluteUri == "http://127.0.0.1:7890/" && !proxy.BypassProxyOnLocal;
            var noCookies = !policy.UseCookies;
            return new FakeHandler(async (request, token) =>
            {
                lock (requested) requested.Add((request.RequestUri!, explicitProxy, noCookies));
                if (hold) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                if (request.RequestUri!.Host == "ipapi.co") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return new HttpResponseMessage(request.RequestUri!.Host == "api.ipapi.is" ? status : HttpStatusCode.OK)
                { Content = new StringContent(request.RequestUri.Host == "ipwho.is" ? ipwho : ipapi, Encoding.UTF8, "application/json") };
            });
        }, () => timestamp, TimeSpan.FromMilliseconds(100));
        route = new("http://127.0.0.1:7890", "fixture");
        var result = await probe.ProbeAsync(route, default);
        check(result.Ip == "198.51.100.24" && result.CountryCode == "US" && result.IanaTimezone == "America/New_York" && result.WindowsTimezone == "Eastern Standard Time" && result.CheckedAt == timestamp,
            "egress: agreed public exit maps concrete IANA zone to Windows with fresh timestamp");
        check(requested.Count == 2 && requested.All(item => item.ExplicitProxy && item.NoCookies && item.Url.Scheme == "https"),
            "egress: both requests use exactly the selected proxy without cookies or direct fallback");
        ipapi = Api(nested: false);
        check((await probe.ProbeAsync(route, default)).WindowsTimezone == "Eastern Standard Time", "egress: flat ipapi.is schema is supported");
        ipapi = Api(nested: false, includeCountryCode: false);
        check((await probe.ProbeAsync(route, default)).WindowsTimezone == "Eastern Standard Time", "egress: current anonymous ipapi.is country name agrees without requiring an absent country code");
        ipapi = Api(nested: false, includeCountryCode: false, countryName: "Canada");
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "国家定位不一致", "egress: anonymous country disagreement cannot be hidden by a missing code");
        ipapi = Api(nested: false, includeCountryCode: false, countryName: "");
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "ipapi.is 未返回", "egress: absent anonymous country name still fails closed");
        ipapi = Api(country: "USA");
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "ipapi.is 未返回", "egress: malformed present country code is not ignored in favor of a name");
        ipapi = Api(nested: false, includeCountryCode: false, zone: "");
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "ipapi.is 未返回可识别", "egress: anonymous country-name compatibility does not infer missing timezone");
        ipapi = Api(zone: "America/Detroit");
        check((await probe.ProbeAsync(route, default)).WindowsTimezone == "Eastern Standard Time", "egress: equivalent concrete zones compare their Windows mapping");
        ipapi = Api(ip: "198.51.100.25");
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "不同出口 IP", "egress: mismatched provider IPs cannot authorize synchronization");
        ipapi = Api(country: "CA");
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "国家定位不一致", "egress: country disagreement is retained");
        ipapi = Api(zone: "America/Los_Angeles");
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "时区不一致", "egress: same country with different concrete zones is refused");
        ipapi = Api(zone: "");
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "ipapi.is 未返回", "egress: missing zone names provider without country-based guessing");
        ipapi = Api(); status = HttpStatusCode.TooManyRequests;
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "ipapi.is 查询次数受限", "egress: rate limit has a provider-specific message");
        status = HttpStatusCode.Redirect;
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "HTTP 302", "egress: redirect cannot silently change probe destination");
        status = HttpStatusCode.OK; ipapi = "{\"error\":\"fixture only\"}";
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "ipapi.is 没有返回成功", "egress: provider error payload is not treated as a location");
        ipapi = "not json";
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "ipapi.is 返回的数据格式", "egress: invalid provider JSON produces a bounded useful error");
        ipapi = new string('x', 256 * 1024 + 1);
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "数据过大", "egress: response body is bounded");
        ipapi = Api(); ipwho = Who(ip: "127.0.0.1");
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "ipwho.is 没有返回有效", "egress: local address cannot be accepted as public egress");
        ipwho = Who(); hold = true;
        await Refused(async () => { _ = await probe.ProbeAsync(route, default); }, "查询超时", "egress: a hanging provider is bounded by cancellation");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { _ = await probe.ProbeAsync(route, cancelled.Token); check(false, "egress: user cancellation"); }
            catch (OperationCanceledException) { check(true, "egress: user cancellation is not mislabeled as network timeout"); }
        }
        hold = false;
        var requestCount = requested.Count;
        await Refused(async () => { _ = await probe.ProbeAsync(new("", "fixture"), default); }, "不会改为直连", "egress: absent explicit route sends no request");
        await Refused(async () => { _ = await probe.ProbeAsync(new("http://user:account-ec22fda0d8@example.invalid:7890", "fixture"), default); }, "不会改为直连", "egress: embedded proxy credentials are not accepted");
        check(requested.Count == requestCount, "egress: invalid routes fail before constructing requests");
        await ProviderFallbackChecks.RunAsync(check);

        var current = "Eastern Standard Time";
        var writes = new List<string>(); var commands = 0; var elevations = 0;
        var ordinaryError = new TimezoneCommandResult(0, "");
        bool elevationAllowed = true, apply = true, unreadable = false;
        var system = new SystemTimezone((args, token) =>
        {
            token.ThrowIfCancellationRequested(); commands++;
            if (args.SequenceEqual(new[] { "/g" })) return Task.FromResult(new TimezoneCommandResult(unreadable ? 1 : 0, current));
            if (args.Count != 2 || args[0] != "/s") throw new InvalidOperationException("Unexpected fixture command");
            writes.Add(args[1]);
            if (ordinaryError.ExitCode == 0 && apply) current = args[1];
            return Task.FromResult(ordinaryError);
        }, (value, _) => { elevations++; if (elevationAllowed && apply) current = value; return Task.FromResult(elevationAllowed); });
        check(commands == 0, "timezone: service construction does not read or write Windows settings");
        check(await system.ReadAsync(default) == current, "timezone: explicit read returns canonical Windows ID");
        await system.SetAsync(current, default);
        check(writes.Count == 0 && elevations == 0, "timezone: matching zone is idempotent and never requests elevation");
        await Refused(() => system.SetAsync("America/New_York", default), "标识无效", "timezone: native setter rejects an unmapped IANA argument");
        await Refused(() => system.SetAsync("Eastern Standard Time\" /?", default), "标识无效", "timezone: command text cannot be passed as timezone argument");
        await system.SetAsync("Central Standard Time", default);
        check(current == "Central Standard Time" && writes.SequenceEqual(new[] { "Central Standard Time" }) && elevations == 0,
            "timezone: ordinary set changes only target timezone and verifies readback");
        ordinaryError = new(1, "", "A required privilege is not held by the client.");
        await system.SetAsync("Pacific Standard Time", default);
        check(current == "Pacific Standard Time" && elevations == 1, "timezone: permission failure alone triggers explicit Windows elevation");
        elevationAllowed = false;
        await Refused(() => system.SetAsync("Central Standard Time", default), "未取得", "timezone: declining elevation reports failure");
        check(current == "Pacific Standard Time", "timezone: declined elevation preserves existing zone without restoration");
        ordinaryError = new(1, "", "unexpected command failure");
        var elevatedBefore = elevations;
        await Refused(() => system.SetAsync("Central Standard Time", default), "未能应用", "timezone: unknown command errors do not repeatedly request elevation");
        check(elevations == elevatedBefore, "timezone: non-permission failure does not elevate");
        ordinaryError = new(0, ""); apply = false;
        var beforeWrites = writes.Count;
        await Refused(() => system.SetAsync("Central Standard Time", default), "回读与目标不一致", "timezone: successful command without matching readback is not success");
        check(writes.Count == beforeWrites + 1 && writes.All(value => value != "China Standard Time"), "timezone: failed readback never restores Beijing or prior timezone");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); beforeWrites = writes.Count;
            try { await system.SetAsync("Central Standard Time", cancelled.Token); check(false, "timezone: cancellation"); }
            catch (OperationCanceledException) { check(writes.Count == beforeWrites, "timezone: cancellation before dispatch prevents writes"); }
        }
        unreadable = true; beforeWrites = writes.Count;
        await Refused(() => system.SetAsync("Central Standard Time", default), "无法读取", "timezone: unreadable baseline blocks the write");
        check(writes.Count == beforeWrites, "timezone: all verification uses fake commands, never Windows settings");
    }

    private static string Who(string ip = "198.51.100.24") => $"{{\"success\":true,\"ip\":\"{ip}\",\"country_code\":\"US\",\"country\":\"United States\",\"city\":\"New York\",\"timezone\":{{\"id\":\"America/New_York\"}}}}";
    private static string Api(string ip = "198.51.100.24", string country = "US", string zone = "America/New_York", bool nested = true,
        bool includeCountryCode = true, string countryName = "United States")
    {
        var location = (includeCountryCode ? $"\"country_code\":\"{country}\"," : "") + $"\"country\":\"{countryName}\",\"city\":\"New York\",\"timezone\":\"{zone}\"";
        return nested ? $"{{\"ip\":\"{ip}\",\"location\":{{{location}}}}}" : $"{{\"ip\":\"{ip}\",{location}}}";
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
