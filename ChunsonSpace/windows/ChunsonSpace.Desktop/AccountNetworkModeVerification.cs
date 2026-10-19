using System.IO;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.NodeTesting;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

internal static class AccountNetworkModeVerification
{
    public static async Task RunAsync(string root, Action<string, bool> check)
    {
        Directory.CreateDirectory(root);
        var subscription = Path.Combine(root, "fixture.yaml");
        File.WriteAllText(subscription, "proxies:\n  - {name: fixture, type: http, server: fixture.invalid, port: 443}\n");
        var node = new NodeCatalogImporter().Read(subscription, root).Nodes.Single();
        var quality = new IpQualityReport(true, "192.0.2.11", "US", "", "America/New_York", "", "", null, null, null, null, null,
            DateTimeOffset.UtcNow, "fixture", "fixture", new(5, "fixture", true));
        var path = Path.Combine(root, "routes.json");
        var runtime = new FixtureRuntime();
        var probes = 0; var allocations = 0; var resolves = 0; bool unavailable = false;
        var main = "http://127.0.0.1:7890";
        var flRoot = Path.Combine(root, "FlClash"); Directory.CreateDirectory(flRoot);
        File.WriteAllText(Path.Combine(flRoot, "config.yaml"), "mixed-port: 7897\nbind-address: '*'\ntun: {enable: true}\n");
        var tunResolver = AutomaticLoginProxy.ForFlClash(flRoot, _ => Task.FromResult<IReadOnlyList<LocalProxyListener>>([new("FlClashCore.exe", "0.0.0.0", 7897)]));
        var tunRepository = new AccountRepository(Path.Combine(root, "tun-accounts"), systemProxyProvider: () => Task.Run(() => tunResolver.ResolveAsync(default)).GetAwaiter().GetResult().Proxy);
        tunRepository.Profiles.CreateBoundAccount("Follow", new(FollowSystemProxy: true));
        check("TUN-only followed account resolves FlClash main listener without Windows proxy", tunRepository.Proxy("Follow") == "http://127.0.0.1:7897");
        Task<LoginProxySelection> Resolve(CancellationToken token)
        { resolves++; if (unavailable) throw new IOException("fixture main unavailable"); return Task.FromResult(new LoginProxySelection(main, "FlClash")); }
        var repository = new AccountRepository(root, systemProxyProvider: () => main);
        var browser = new FixtureBrowser();
        await using var routes = new ManagedNodeRoutes(path, _ => node, binding => { runtime.Port = binding.Port; return runtime; },
            (_, _) => { probes++; return Task.FromResult(quality); });
        var modes = new AccountNetworkModes(repository, routes, browser, Resolve, () => TimeZoneInfo.FindSystemTimeZoneById("US Mountain Standard Time"));
        async Task<(string, string)> Allocate(CancellationToken token)
        { allocations++; return (await routes.BindAsync("fixture", node, quality, token, reuseExisting: false), quality.Timezone); }
        var initial = await routes.BindAsync("fixture", node, quality, default);
        repository.Profiles.CreateBoundAccount("East", new(initial, quality.Timezone, "en-US"));
        repository.Profiles.SaveAccount("East", new JsonObject { ["tokens"] = new JsonObject { ["access_token"] = "fixture-only" } }, overwrite: true);
        var auth = repository.ReadAuth("East").ToJsonString();
        await routes.EnsureProxyAsync(initial, default);
        browser.Open = true;
        var change = await modes.SaveAsync("East", true, Allocate);
        var settings = repository.Profiles.ReadEnvironment("East");
        check("follow mode closes only selected browser and releases unused old port without allocation or rescore", change.ReopenBrowser && browser.Closed.SequenceEqual(["East"])
            && change.Message.Contains("已释放") && runtime.Stops == 1 && probes == 0 && allocations == 0 && !File.ReadAllText(path).Contains(new Uri(initial).Port.ToString()));
        check("follow mode clears dedicated proxy and links timezone to Windows", settings.FollowSystemProxy && settings.Proxy == "" && settings.Timezone == "America/Phoenix" && repository.Proxy("East") == main);
        main = "http://127.0.0.1:7892";
        check("followed account resolves new main port dynamically without storing it", repository.Proxy("East") == main && repository.Profiles.ReadEnvironment("East").Proxy == "");
        change = await modes.SaveAsync("East", false, Allocate);
        settings = repository.Profiles.ReadEnvironment("East"); var dedicated = settings.Proxy;
        check("switch back assigns a dedicated port and matching exit timezone without score requests", !settings.FollowSystemProxy && dedicated.StartsWith("http://127.0.0.1:")
            && settings.Timezone == quality.Timezone && allocations == 1 && probes == 0 && !change.ReopenBrowser);
        await modes.SaveAsync("East", false, Allocate);
        check("saving unchanged independent mode keeps port and avoids allocation", allocations == 1 && repository.Proxy("East") == dedicated);
        repository.Profiles.CreateBoundAccount("Shared", settings);
        await routes.EnsureProxyAsync(dedicated, default);
        change = await modes.SaveAsync("East", true, Allocate);
        check("another account reference keeps shared port alive while selected account follows main", runtime.Stops == 1 && repository.Proxy("Shared") == dedicated
            && File.ReadAllText(path).Contains(new Uri(dedicated).Port.ToString()) && change.Message.Contains("其他账号"));
        await modes.SaveAsync("Shared", true, Allocate);
        check("last account releasing shared port stops its owned core", runtime.Stops == 2 && File.ReadAllText(path).Trim() == "[]");
        var before = repository.Profiles.ReadEnvironment("East");
        browser.Fail = true;
        try { await modes.SaveAsync("East", false, Allocate); check("unconfirmed browser close rejected", false); }
        catch (IOException) { check("unconfirmed browser close retains account and cleans newly allocated reference", repository.Profiles.ReadEnvironment("East") == before && File.ReadAllText(path).Trim() == "[]"); }
        browser.Fail = false;
        await modes.SaveAsync("East", false, Allocate);
        before = repository.Profiles.ReadEnvironment("East");
        await routes.EnsureProxyAsync(before.Proxy, default);
        runtime.FailStop = true;
        try { await modes.SaveAsync("East", true, Allocate); check("unconfirmed port release rejected", false); }
        catch (IOException) { check("failed core stop preserves original independent configuration and binding", repository.Profiles.ReadEnvironment("East") == before && File.ReadAllText(path).Contains(new Uri(before.Proxy).Port.ToString())); }
        runtime.FailStop = false; unavailable = true; var closes = browser.Closed.Count;
        try { await modes.SaveAsync("East", true, Allocate); check("missing main route rejected", false); }
        catch (IOException) { check("unavailable main network leaves existing browser and configuration untouched", browser.Closed.Count == closes && repository.Profiles.ReadEnvironment("East") == before); }
        check("network switches preserve account authorization", repository.ReadAuth("East").ToJsonString() == auth);
        check("mode service does not release nonowned main listener", !await routes.ReleaseUnusedAsync(main, [], default));
        var otherQuality = quality with { Ip = "192.0.2.22", Timezone = "Europe/London" };
        async Task<(string, string)> OtherIp(CancellationToken token) => (await routes.BindAsync("fixture", node, otherQuality, token, reuseExisting: false), otherQuality.Timezone);
        var priorBindings = File.ReadAllText(path); var priorProbes = probes;
        browser.Fail = true;
        try { await modes.SaveAsync("East", false, OtherIp, replaceIndependent: true); check("IP replacement with unconfirmed browser close rejected", false); }
        catch (IOException) { check("failed replacement keeps old route and removes new allocation", repository.Profiles.ReadEnvironment("East") == before && File.ReadAllText(path) == priorBindings); }
        browser.Fail = false; runtime.FailStop = true;
        try { await modes.SaveAsync("East", false, OtherIp, replaceIndependent: true); check("IP replacement with unconfirmed old port stop rejected", false); }
        catch (IOException) { check("failed old-port release rolls back replacement settings and new port", repository.Profiles.ReadEnvironment("East") == before && File.ReadAllText(path) == priorBindings); }
        runtime.FailStop = false; browser.Open = true;
        change = await modes.SaveAsync("East", false, OtherIp, replaceIndependent: true);
        var replaced = repository.Profiles.ReadEnvironment("East");
        check("explicit IP replacement updates port and timezone then releases old binding", replaced.Proxy != before.Proxy && replaced.Timezone == "Europe/London" && !replaced.FollowSystemProxy
            && await routes.AssignedIpAsync(replaced.Proxy) == otherQuality.Ip && await routes.AssignedIpAsync(before.Proxy) == "" && change.ReopenBrowser);
        check("IP replacement reuses score and keeps authorization and other account unchanged", probes == priorProbes && repository.ReadAuth("East").ToJsonString() == auth
            && repository.Profiles.ReadEnvironment("Shared").FollowSystemProxy);
    }
    private sealed class FixtureBrowser : IAccountBrowserNetwork
    {
        public bool Open, Fail;
        public List<string> Closed { get; } = [];
        public Task<bool> CloseForNetworkChangeAsync(string name, CancellationToken token)
        { if (Fail) throw new IOException("fixture cannot close"); Closed.Add(name); var opened = Open; Open = false; return Task.FromResult(opened); }
    }
    private sealed class FixtureRuntime : IMihomoProbeRuntime
    {
        public int Port, Stops;
        public bool FailStop;
        public Task<IMihomoProbeSession> StartAsync(NodeCandidate node, CancellationToken token) => Task.FromResult<IMihomoProbeSession>(new Session(this, Port));
        private sealed class Session(FixtureRuntime runtime, int port) : IMihomoProbeSession
        {
            public string ProxyAddress => "http://127.0.0.1:" + port;
            public bool IsRunning { get; private set; } = true;
            public Task<MihomoProbeStopResult> StopAsync(CancellationToken token)
            { if (runtime.FailStop) return Task.FromResult(new MihomoProbeStopResult(false, "fixture stop failed")); IsRunning = false; runtime.Stops++; return Task.FromResult(new MihomoProbeStopResult(true, "fixture")); }
        }
    }
}
