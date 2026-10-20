using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChunsonSpace.Accounts;
using ChunsonSpace.Application;
using ChunsonSpace.Core;
using ChunsonSpace.NodeTesting;

namespace ChunsonSpace.Desktop;

// Explicit offline entry: every HTTP/CDP/system dependency is synthetic.
internal static class IpSeparationVerification
{
    private const string V4 = "8.8.8.8", V6 = "2606:4700:4700::1111", Proxy = "http://127.0.0.1:17891";
    public static async Task RunAsync(string directory)
    {
        if (Directory.Exists(directory)) throw new IOException("Verification directory must be new.");
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException(name);
        }
        var transport = new FakeTransport();
        var service = new IpInfoService(transport);
        var repository = new AccountRepository(Path.Combine(directory, "repository"));
        var egress = new EgressService(repository, transport);
        var quality = new IpQualityService(transport);

        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        transport.Handle = async (request, token) =>
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
            return FakeTransport.Text(request.Url == IpInfoService.Ipv4Endpoint ? V4 : V6);
        };
        var addresses = await service.ProbeAsync(Proxy);
        Check("both families start independently before either response completes", started == 2 && addresses.Ipv4.Ip == V4 && addresses.Ipv6.Ip == V6);
        Check("distinct IPv4 and IPv6 exits coexist; IPv4 is the compatibility primary", addresses.PrimaryIp == V4);
        transport.Handle = null;
        var before = transport.Requests.Count;
        var measured = await quality.ProbeScreeningExitAsync(Proxy);
        Check("node screening measures two IPinfo addresses without location or score", transport.Requests.Count == before + 2 && measured.HasVerifiedExit && measured.Timezone == "" && measured.Score is null);
        var geo = await egress.ProbeRegionProxyAsync(Proxy);
        Check("timezone uses measured-primary IPinfo JSON and no score", geo.Ok && geo.Ip == V4 && geo.CountryCode == "US" && geo.Timezone == "America/Los_Angeles"
            && transport.Requests.Last().Url == IpInfoService.LocationEndpoint(V4) && transport.Requests.All(r => r.Url != IpQualityService.ScoreEndpoint));
        Check("explicit proxy and HTTPS are preserved without account headers", transport.Requests.All(r => r.RequireProxy && !r.UseSystemProxy && r.Proxy == Proxy && r.Url.Scheme == "https" && r.Body is null && r.Headers.Count == 0));

        foreach (var failingV4 in new[] { false, true })
        {
            transport.Handle = (r, _) => Task.FromResult(r.Url == (failingV4 ? IpInfoService.Ipv4Endpoint : IpInfoService.Ipv6Endpoint)
                ? FakeTransport.Text("unavailable", 429) : transport.Default(r));
            var partial = await service.ProbeAsync(Proxy);
            Check(failingV4 ? "IPv6-only success survives IPv4 429" : "IPv4-only success survives IPv6 429", partial.PrimaryIp == (failingV4 ? V6 : V4)
                && (failingV4 ? partial.Ipv4 : partial.Ipv6).Message.Contains("429"));
            var region = await egress.ProbeRegionProxyAsync(Proxy);
            Check("single-family location binds to the successful primary " + failingV4, region.Ok && region.Ip == partial.PrimaryIp && region.Addresses == partial);
        }
        foreach (var value in new[] { "", "text", "8.8.8.8\n1.1.1.1", "127.0.0.1", "0.0.0.0", "10.0.0.1", "172.16.0.1", "192.168.1.1", "100.64.0.1", "169.254.1.1", "224.0.0.1", "203.0.113.10", "0x08080808", "8.8.2056", "008.8.8.8", "::1", "fe80::1%2", "fc00::1", "ff02::1", "::ffff:8.8.8.8", "2001:db8::1", "2001:2::1", "192.88.99.1" })
            Check("invalid/nonpublic address is unavailable: " + value.Replace('\n', ' '), !IpInfoService.TryPublicAddress(value, out _));
        Check("wrong-family replies cannot masquerade as a successful measurement", !IpInfoService.ParseAddress(V4, AddressFamily.InterNetworkV6).Available && !IpInfoService.ParseAddress(V6, AddressFamily.InterNetwork).Available);
        Check("canonical IPv6 equality and trailing newline normalization", IpInfoService.ParseAddress("2606:4700:4700:0:0:0:0:1111\n", AddressFamily.InterNetworkV6).Ip == V6
            && EgressSources.SameIp(V6, "2606:4700:4700:0000:0000:0000:0000:1111"));
        transport.Handle = (r, _) => r.Url == IpInfoService.Ipv6Endpoint ? throw new OperationCanceledException("synthetic timeout") : Task.FromResult(transport.Default(r));
        addresses = await service.ProbeAsync(Proxy);
        Check("family timeout is unavailable without invalidating healthy IPv4", addresses.PrimaryIp == V4 && addresses.Ipv6.Message.Contains("超时"));
        transport.Handle = (r, _) => Task.FromResult(r.Url == IpInfoService.Ipv6Endpoint ? FakeTransport.Text(new string('a', 257)) : transport.Default(r));
        Check("address response size is bounded per family", !(await service.ProbeAsync(Proxy)).Ipv6.Available);
        transport.Handle = (r, _) => Task.FromResult(r.Url.Host == "ipinfo.io" ? FakeTransport.Text(new string('a', IpInfoService.LocationBodyLimit + 1)) : transport.Default(r));
        geo = await egress.ProbeRegionProxyAsync(Proxy);
        Check("oversized location preserves both measured addresses", !geo.Ok && geo.Addresses.PrimaryIp == V4);
        transport.Handle = null;
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); before = transport.Requests.Count;
            try { await service.ProbeAsync(Proxy, cancel.Token); throw new InvalidOperationException("pre-cancel ignored"); }
            catch (OperationCanceledException) { Check("pre-cancel sends no request", transport.Requests.Count == before); }
        }
        using (var cancel = new CancellationTokenSource())
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.Handle = async (r, token) => { if (r.Url == IpInfoService.Ipv6Endpoint) { waiting.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } return transport.Default(r); };
            var pending = service.ProbeAsync(Proxy, cancel.Token); await waiting.Task; cancel.Cancel();
            try { await pending; throw new InvalidOperationException("inflight cancel ignored"); }
            catch (OperationCanceledException) { Check("caller cancellation reaches the pending family", true); }
        }
        transport.Handle = null;
        foreach (var data in new[] { "{}", "{\"ip\":\"8.8.4.4\",\"country\":\"US\",\"timezone\":\"America/Los_Angeles\"}", "{\"ip\":\"8.8.8.8\",\"country\":\"US\"}" })
        {
            transport.Location = data;
            geo = await egress.ProbeRegionProxyAsync(Proxy);
            Check("missing/mismatched location never guesses US timezone: " + data, !geo.Ok && geo.Timezone == "" && geo.Ip == V4);
        }
        transport.Location = null;
        foreach (var status in new[] { 429, 503 })
        {
            before = transport.Requests.Count;
            transport.Handle = (r, _) => Task.FromResult(r.Url.Host == "ipinfo.io" ? FakeTransport.Text("private error body", status) : transport.Default(r));
            geo = await egress.ProbeRegionProxyAsync(Proxy);
            Check("location HTTP failure is bounded, redacted and has no retry/provider fallback " + status, !geo.Ok && transport.Requests.Count == before + 3 && !geo.Message.Contains("private error body"));
        }
        transport.Handle = null;
        foreach (var risk in new[] { 0, 15, 16, 100 })
        {
            transport.Score = JsonSerializer.Serialize(new { ip = V4, fraudScore = risk, isResidential = true, asn = 15169, asOrganization = "Synthetic ISP", timezone = "Asia/Tokyo" });
            var report = await quality.AddScoreAsync(measured, Proxy);
            Check("IPPure retains quality=100-risk and exact admission boundary " + risk, report.Score?.Quality == 100 - risk && report.Admission.Allowed == (risk <= 15)
                && report.Score?.Provider == "IPPure" && report.Timezone == "" && report.Addresses == measured.Addresses);
        }
        foreach (var score in new[] { "", ",\"fraudScore\":null", ",\"fraudScore\":\"0\"", ",\"fraudScore\":false", ",\"fraudScore\":0.5", ",\"fraudScore\":-1", ",\"fraudScore\":101" })
        {
            transport.Score = "{\"ip\":\"" + V4 + "\"" + score + "}";
            var report = await quality.AddScoreAsync(measured, Proxy);
            Check("missing/malformed score stays unknown " + score, report.Score?.Value is null && !report.Admission.Allowed && report.Ip == V4);
        }
        transport.Score = "{\"ip\":\"8.8.8.8\",\"fraudScore\":0,\"error\":\"fixture\"}";
        Check("API error cannot masquerade as a zero-risk score", !(await quality.AddScoreAsync(measured, Proxy)).Admission.Allowed);
        transport.Score = new string('x', 16385);
        Check("score body is bounded and cannot erase addresses", (await quality.AddScoreAsync(measured, Proxy)) is { Ip: V4, Score.Quality: null });
        transport.Score = "{\"ip\":\"" + V6 + "\",\"fraudScore\":0}";
        var mismatched = await quality.AddScoreAsync(measured, Proxy);
        Check("score IP mismatch discards only score, never changes measured address or labels account anomalous", mismatched.Score?.Quality is null && mismatched.Ip == V4 && mismatched.Success && !mismatched.Message.Contains("异常"));
        var only6 = measured with { Ip = V6, Addresses = new(new(), new(V6, "IPinfo")) };
        transport.Score = "{\"ip\":\"2606:4700:4700:0:0:0:0:1111\",\"fraudScore\":15}";
        Check("canonical IPv6 score is supported when provider actually returns a valid matching score", (await quality.AddScoreAsync(only6, Proxy)).Admission.Allowed);
        transport.Handle = (r, _) => Task.FromResult(r.Url == IpQualityService.ScoreEndpoint ? FakeTransport.Text("limited", 429) : transport.Default(r));
        Check("score 429 does not erase address success", (await quality.AddScoreAsync(measured, Proxy)).Score?.Quality is null);
        before = transport.Requests.Count;
        geo = await egress.ProbeRegionProxyAsync(Proxy);
        Check("timezone still succeeds after scoring failure, with zero score requests", geo.Ok && transport.Requests.Skip(before).Count() == 3 && transport.Requests.Skip(before).All(r => r.Url != IpQualityService.ScoreEndpoint));
        transport.Handle = null;
        var cached = new CachedIpScore(V4, new(5, "fixture", true), DateTimeOffset.UtcNow, measured with { Timezone = "Asia/Tokyo", CountryCode = "JP" });
        var fresh = measured with { Timezone = "America/Los_Angeles", CountryCode = "US", Addresses = new(new(V4), new(Message: "本次查询超时")) };
        var applied = cached.Apply(fresh);
        Check("cached score cannot overwrite fresh geography or per-family availability", applied.Timezone == fresh.Timezone && applied.CountryCode == fresh.CountryCode && applied.Addresses == fresh.Addresses && applied.Admission.Allowed);
        Check("cache for a different IP cannot authorize current measurement", cached.Apply(fresh with { Ip = "8.8.4.4" }).Score is null);
        var oldScore = new IpPureScore(null, "historical") { Provider = "Net.Coffee", DirectQuality = 91 };
        Check("historical provider direction remains truthful", JsonSerializer.Deserialize<IpPureScore>(JsonSerializer.Serialize(oldScore)) is { Provider: "Net.Coffee", Quality: 91 });
        Check("existing residential exemption and ordinary admission remain unchanged", (measured with { ResidentialScoreExempt = true }).Admission.Allowed && !measured.Admission.Allowed);
        Check("production requests used only IPinfo and selected IPPure", transport.Requests.All(r => r.Url.Host is "v4.api.ipinfo.io" or "v6.api.ipinfo.io" or "ipinfo.io" or "my.ippure.com"));

        transport.Score = "{\"ip\":\"8.8.8.8\",\"fraudScore\":15}";
        var realAdmission = new IpAdmissionService(async (proxy, token) => await quality.AddScoreAsync(await quality.ProbeScreeningExitAsync(proxy, token), proxy, token));
        var localLaunch = new LocalEnvironmentService(_ => Task.FromResult(new LoginProxySelection(Proxy, "fixture")), egress.ProbeRegionProxyAsync,
            realAdmission, () => TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"), () => "US");
        Check("real score-only admission composes with fresh IPinfo launch timezone", (await localLaunch.ProbeAsync(new(IdentityKind.Account, "Synthetic"), Proxy, default)).Success);
        transport.Score = "{\"ip\":\"8.8.8.8\",\"fraudScore\":16}"; before = transport.Requests.Count;
        Check("real below-threshold admission stops before timezone lookup", !(await localLaunch.ProbeAsync(new(IdentityKind.Account, "Synthetic"), Proxy, default)).Success
            && transport.Requests.Count == before + 3 && transport.Requests.Skip(before).All(r => r.Url.Host != "ipinfo.io"));

        await CheckRendererAsync(Check);
        await CheckUiAsync(directory, geo, Check);
        await LocalEnvironmentVerification.RunTimezoneMatchAsync(Path.Combine(directory, "timezone-match"));
        File.WriteAllText(Path.Combine(directory, "ip-separation-verification.json"), JsonSerializer.Serialize(new { passed = true, checks, fakeHttpRequests = transport.Requests.Count,
            liveIpCalls = 0, actualAccountOperations = 0, actualSystemWrites = 0, operationalAcceptance = "NOT RUN" }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task CheckRendererAsync(Action<string, bool> check)
    {
        var method = typeof(ChromeLoginBrowser).GetMethod("ReadBrowserEgressAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var cdp = new FakeCdp();
        var expected = new EgressResult(true, V4, CountryCode: "US", Timezone: "America/Los_Angeles");
        Task<EgressResult> Probe() => (Task<EgressResult>)method.Invoke(null, [cdp, "fixture", expected, expected.Timezone, CancellationToken.None])!;
        check("renderer accepts distinct family exits and preserves primary route check", (await Probe()).Ok && cdp.Expressions.Count == 3);
        cdp.V4Reply = "8.8.4.4";
        check("renderer still rejects a changed same-family primary", !(await Probe()).Ok);
        cdp.V4Reply = ""; expected = expected with { Ip = V6 };
        check("renderer supports IPv6-only route and explicit IPv6 location", (await Probe()).Ok);
        check("renderer uses bounded streaming and no navigation retries or old providers", cdp.Expressions.All(s => s.Contains("getReader") && s.Contains("AbortController") && s.Contains("ipinfo.io") && !s.Contains("ippure")));
    }

    private static async Task CheckUiAsync(string directory, EgressResult geo, Action<string, bool> check)
    {
        var fixture = Path.Combine(directory, "ui-fixture"); Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var workspace = DesktopWorkspace.CreateManagedFixture(fixture);
        try
        {
            var local = new LocalEnvironmentService(_ => Task.FromResult(new LoginProxySelection(Proxy, "fixture")), (_, _) => Task.FromResult(geo),
                new RejectAdmission(), () => TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"), () => "US");
            workspace.LocalEnvironment = local;
            workspace.Profiles.CreateBoundAccount("DualStackFixture", new("http://127.0.0.1:7890", "America/Chicago"));
            var model = new ShellViewModel(workspace);
            await model.RefreshLocalEnvironmentAsync();
            check("homepage shows both family fields without calling admission", model.LocalEnvironmentStatus.Contains("IPv4：" + V4) && model.LocalEnvironmentStatus.Contains("IPv6：" + V6)
                && model.LocalEnvironmentDetail.Contains(geo.Addresses.Summary));
            model.Selected = model.FilteredItems.Single(i => i.Name == "DualStackFixture");
            await model.RunAccountActionAsync(AccountAction.Egress);
            check("account details and persisted observation carry both families", model.SelectedExitDetails.Contains("IPv4：" + V4) && model.SelectedExitDetails.Contains("IPv6：" + V6)
                && workspace.Profiles.ReadExitObservation(model.Selected.Identity)?.Addresses.Ipv6.Ip == V6);
            var window = UiVerification.CreateWindow(model);
            try
            {
                foreach (var dark in new[] { false, true })
                {
                    model.DarkTheme = dark; model.Navigate("Accounts");
                    window.RenderRoot.Measure(new Size(1040, 670)); window.RenderRoot.Arrange(new Rect(0, 0, 1040, 670)); window.RenderRoot.UpdateLayout();
                    var status = (TextBlock)window.FindName("LocalEnvironmentStatusText");
                    check("account panel visibly binds both address fields " + dark, ((TextBlock)window.FindName("SelectedExitIpText")).Text == V4 && ((TextBlock)window.FindName("SelectedExitIpv6Text")).Text == V6);
                    check("minimum homepage renders both families " + dark, status.Text.Contains("IPv4：") && status.Text.Contains("IPv6：") && status.ActualHeight > 0);
                    var bitmap = new RenderTargetBitmap(1040, 670, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window.RenderRoot);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, dark ? "dual-stack-dark.png" : "dual-stack-light.png")); encoder.Save(file);
                }
            }
            finally { window.Close(); }
            workspace.LocalEnvironment = new(_ => Task.FromResult(new LoginProxySelection(Proxy, "fixture")),
                (_, _) => Task.FromResult(geo with { Addresses = new(new(V4), new(Message: "本次查询超时")) }), new RejectAdmission(),
                () => TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"), () => "US");
            await model.RefreshLocalEnvironmentAsync();
            check("unavailable family is neutral and does not invalidate IPv4 timezone", model.LocalEnvironmentStatus.Contains("已核对") && model.LocalEnvironmentDetail.Contains("IPv6：不可用（本次查询超时）"));
        }
        finally { await workspace.Accounts!.DisposeAsync(); }
    }

    private sealed class RejectAdmission : ILaunchEgressProbe
    { public Task<LaunchEgressEvidence> ProbeAsync(IdentityRef identity, string proxy, CancellationToken token) => throw new InvalidOperationException("timezone-only invoked scoring"); }

    internal sealed class FakeTransport : IAccountTransport
    {
        public ConcurrentQueue<AccountHttpRequest> Requests { get; } = new();
        public Func<AccountHttpRequest, CancellationToken, Task<AccountHttpResponse>>? Handle { get; set; }
        public string? Location { get; set; }
        public string Score { get; set; } = "{}";
        public Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Requests.Enqueue(request); return Handle?.Invoke(request, token) ?? Task.FromResult(Default(request)); }
        public static AccountHttpResponse Text(string value, int status = 200) => new(status, new MemoryStream(Encoding.UTF8.GetBytes(value)));
        public AccountHttpResponse Default(AccountHttpRequest request)
        {
            if (request.Url == IpInfoService.Ipv4Endpoint) return Text(V4);
            if (request.Url == IpInfoService.Ipv6Endpoint) return Text(V6);
            if (request.Url == IpQualityService.ScoreEndpoint) return Text(Score);
            if (request.Url.Host == "ipinfo.io") return Text(Location ?? JsonSerializer.Serialize(new { ip = Uri.UnescapeDataString(request.Url.AbsolutePath.Split('/')[1]), country = "US", city = "Synthetic", timezone = "America/Los_Angeles" }));
            throw new InvalidOperationException("Unapproved provider: " + request.Url.Host);
        }
    }

    private sealed class FakeCdp : ICdpConnection
    {
        public List<string> Expressions { get; } = [];
        public string V4Reply { get; set; } = V4;
        public Task<JsonObject> CallAsync(string method, JsonObject parameters, string? sessionId, CancellationToken token)
        {
            if (method != "Runtime.evaluate") throw new InvalidOperationException("Unexpected browser operation");
            var expression = parameters["expression"]!.GetValue<string>(); Expressions.Add(expression);
            var value = expression.Contains("v4.api.ipinfo.io") ? V4Reply : expression.Contains("v6.api.ipinfo.io") ? V6
                : JsonSerializer.Serialize(new { ip = expression.Contains("8.8.8.8") ? V4 : V6, country = "US", timezone = "America/Los_Angeles" });
            return Task.FromResult(new JsonObject { ["result"] = new JsonObject { ["value"] = value } });
        }
        public IAsyncEnumerable<JsonObject> Events(CancellationToken token) => System.Threading.Channels.Channel.CreateUnbounded<JsonObject>().Reader.ReadAllAsync(token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
