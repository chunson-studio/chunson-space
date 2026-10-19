using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.NodeTesting;
using ChunsonSpace.Proxy;

if (args is ["--verify-probe-config", var configReport])
{
    var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "dual-stack-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(fixture);
    var source = Path.Combine(fixture, "node.yaml");
    File.WriteAllText(source, "proxies:\n - {name: Synthetic, type: ss, server: fixture.example.invalid, port: 443, password: fixture-secret-not-in-reports, cipher: aes-128-gcm}\n");
    var node = new NodeCatalogImporter().Read(source, fixture).Nodes.Single();
    var original = node.Definition.ToJsonString();
    var yaml = ProbeConfiguration.Build(node, 17891);
    File.WriteAllText(Path.Combine(fixture, "probe-config.yaml"), yaml);
    var data = ProxyYaml.Parse(yaml);
    var checks = new Dictionary<string, bool>
    {
        ["dual stack core and DNS"] = data["ipv6"]!.GetValue<bool>() && data["dns"]!["ipv6"]!.GetValue<bool>(),
        ["dual proxy server resolution"] = data["proxies"]![0]!["ip-version"]!.GetValue<string>() == "dual",
        ["selected-node-only traffic without direct fallback"] = data["rules"]!.AsArray().Select(r => r!.GetValue<string>()).SequenceEqual(["MATCH,ChunsonProbeNode"]),
        ["target DNS uses selected node without fallback"] = data["dns"]!["nameserver"]![0]!.GetValue<string>() == "https://1.1.1.1/dns-query#ChunsonProbeNode" && !data["dns"]!.AsObject().ContainsKey("fallback"),
        ["no TUN or public listener"] = !data["tun"]!["enable"]!.GetValue<bool>() && !data["allow-lan"]!.GetValue<bool>() && data["bind-address"]!.GetValue<string>() == "127.0.0.1",
        ["input node definition preserved"] = node.Definition.ToJsonString() == original,
        ["unavailability is not a leak guarantee"] = ProbeConfiguration.DnsSummary.Contains("不可用不代表已验证无泄漏")
    };
    if (File.Exists(configReport)) throw new IOException("Report must be new");
    File.WriteAllText(configReport, JsonSerializer.Serialize(new { passed = checks.Values.All(v => v), checks, fixture, liveNodeCalls = 0 }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{checks.Count} offline probe configuration checks; fixture root: {fixture}");
    return checks.Values.All(v => v) ? 0 : 1;
}

if (args is ["--probe-subscription", var sourceRoot, var subscriptionId, var corePath, var evidencePath, var maximum])
    return await NativeProbeAcceptance.RunAsync(sourceRoot, subscriptionId, corePath, evidencePath, int.Parse(maximum));
if (args is ["--probe-managed-route", var managedRoot, var managedSubscription, var managedNode, var expectedIp, var managedCore, var managedOutput])
    return await NativeManagedRouteAcceptance.RunAsync(managedRoot, managedSubscription, managedNode, expectedIp, managedCore, managedOutput);

if (args is ["--inspect-subscriptions", var clientName, var dataRoot, var reportPath])
{
    var catalog = SubscriptionCatalogReader.Read(Enum.Parse<ProxyClientKind>(clientName), dataRoot);
    var importer = new NodeCatalogImporter();
    var subscriptions = catalog.Subscriptions.Select(subscription =>
    {
        var nodes = importer.Read(subscription.File, dataRoot);
        return new { subscription.Id, subscription.Name, subscription.IsCurrent, nodes = nodes.Nodes.Count,
            supported = nodes.Nodes.Count(node => node.Supported), issues = nodes.Issues };
    }).ToArray();
    if (File.Exists(reportPath)) throw new IOException("Report already exists");
    File.WriteAllText(reportPath, JsonSerializer.Serialize(new { readOnly = true, networkRequests = 0, subscriptions, catalog.Issues }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Read-only: {subscriptions.Length} subscriptions, {subscriptions.Sum(item => item.nodes)} nodes, {subscriptions.Count(item => item.IsCurrent)} current. No network requests.");
    return 0;
}
int count = 0;
void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "node-testing-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
JsonObject Node(string name, string type = "ss") => new() { ["name"] = name, ["type"] = type, ["server"] = "fixture.example.invalid", ["port"] = 443, ["password"] = "fixture-secret-not-in-reports", ["cipher"] = "aes-128-gcm" };
void Write(string relative, JsonObject data) { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, ProxyYaml.Serialize(data)); }
try
{
    var chain = Node("Chain"); chain["dialer-proxy"] = "Another";
    var files = Node("LocalFile"); files["certificate-path"] = "private.pem";
    Write("config.yaml", new() { ["proxies"] = new JsonArray(Node("One"), chain, Node("Direct", "direct"), files), ["proxy-providers"] = new JsonObject { ["cached"] = new JsonObject { ["type"] = "http", ["url"] = "https://no-request.invalid/secret", ["path"] = "providers/cache.yaml" }, ["missing"] = new JsonObject { ["type"] = "http", ["url"] = "https://no-request.invalid/other" } } });
    Write("providers/cache.yaml", new() { ["proxies"] = new JsonArray(Node("Two", "trojan")) });
    var importer = new NodeCatalogImporter();
    var imported = importer.Read(Path.Combine(root, "config.yaml"), root);
    var reread = importer.Read(Path.Combine(root, "config.yaml"), root);
    Check(imported.Nodes.Select(item => item.Id).SequenceEqual(reread.Nodes.Select(item => item.Id)), "node IDs remain stable across independent catalog reads");
    Write("residential.yaml", new() { ["proxies"] = new JsonArray(Node("专属住宅"), Node("普通节点"), Node("建议注册多个账号，使用多个住宅IP")) });
    var residentialNodes = importer.Read(Path.Combine(root, "residential.yaml"), root).Nodes;
    foreach (var label in new[] { "IPEqual 独享", "ipequal2人共享", "椿生IPEqual", "iqequal独享" })
        Check(NodeScreeningRules.IsPreferredSubscription(label) && NodeScreeningRules.IsScoreExempt(residentialNodes[0], label), "preferred residential alias has consistent score admission: " + label);
    Check(!NodeScreeningRules.IsScoreExempt(residentialNodes[0], "Other") && !NodeScreeningRules.IsScoreExempt(residentialNodes[0], "notipequal"), "unrelated subscription names cannot inherit residential exemption");
    Check(!NodeScreeningRules.IsScoreExempt(residentialNodes[1], "iqequal独享") && !NodeScreeningRules.IsScoreExempt(residentialNodes[2], "iqequal独享"), "ordinary nodes and subscription notices remain outside exemption");
    var changedNode = Node("One"); changedNode["port"] = 8443;
    var stableFile = Path.Combine(root, "stable.yaml");
    Write("stable.yaml", new() { ["proxies"] = new JsonArray(Node("One")) });
    var previousId = importer.Read(stableFile, root).Nodes.Single().Id;
    Write("stable.yaml", new() { ["proxies"] = new JsonArray(changedNode) });
    Check(importer.Read(stableFile, root).Nodes.Single().Id != previousId, "changed node route invalidates prior ID");
    foreach (var labelColumn in new[] { "label", "name" })
    {
        var catalogRoot = Path.Combine(root, "catalog-" + labelColumn); Directory.CreateDirectory(catalogRoot);
        var dbPath = Path.Combine(catalogRoot, "database.sqlite");
        SubscriptionFixtureDb.Create(dbPath, $"CREATE TABLE profiles(id INTEGER PRIMARY KEY,{labelColumn} TEXT,url TEXT); INSERT INTO profiles VALUES(1,'当前订阅','https://secret.invalid/token'),(2,'未使用订阅','https://secret.invalid/token2');");
        File.WriteAllText(Path.Combine(catalogRoot, "shared_preferences.json"), "{\"flutter.config\":\"{\\\"currentProfileId\\\":1}\"}");
        var before = File.ReadAllBytes(dbPath);
        var catalog = SubscriptionCatalogReader.Read(ProxyClientKind.FlClash, catalogRoot);
        Check(catalog.Subscriptions.Count == 2 && catalog.Subscriptions.Single(item => item.IsCurrent).Id == "1" && catalog.Subscriptions[1].Name == "未使用订阅", labelColumn + " schema includes inactive subscriptions without overwrite columns");
        Check(!JsonSerializer.Serialize(catalog).Contains("secret.invalid") && before.SequenceEqual(File.ReadAllBytes(dbPath)), labelColumn + " schema read preserves database and omits subscription URLs");
        Check(importer.Read(catalog.Subscriptions[1].File, catalogRoot).Issues.Count == 1, "missing local cache remains listed with a load issue");
    }
    var vergeRoot = Path.Combine(root, "catalog-verge"); Directory.CreateDirectory(vergeRoot);
    File.WriteAllText(Path.Combine(vergeRoot, "profiles.yaml"), "current: A\nitems:\n - {uid: A, name: Current, type: remote, file: A.yaml}\n - {uid: B, name: Other, type: local, file: B.yaml}\n - {uid: C, type: script, file: C.js}\n");
    var vergeCatalog = SubscriptionCatalogReader.Read(ProxyClientKind.ClashVerge, vergeRoot);
    Check(vergeCatalog.Subscriptions.Count == 2 && vergeCatalog.Subscriptions.Single(item => item.IsCurrent).Id == "A", "Verge lists all actual subscriptions and excludes scripts");
    Check(imported.Nodes.Count == 5 && imported.Nodes.Count(node => node.Supported) == 2, "imports configured and cached-provider nodes only");
    Check(imported.Issues.Count == 1 && imported.Nodes.Single(node => node.Name == "Chain").Reason.Contains("依赖") && !imported.Nodes.Single(node => node.Name == "Direct").Supported, "unresolved providers chains and direct nodes refuse independent testing");
    Check(imported.Nodes.Single(node => node.Name == "LocalFile").Reason.Contains("文件"), "external node file dependency rejected");
    var publicJson = JsonSerializer.Serialize(imported);
    Check(!publicJson.Contains("fixture-secret") && !publicJson.Contains("fixture.example.invalid") && !publicJson.Contains("no-request.invalid"), "public import data contains no server password or subscription URL");
    Write("profiles.yaml", new() { ["items"] = new JsonArray(new JsonObject { ["file"] = "downloaded.yaml", ["url"] = "https://no-request.invalid/profile" }) });
    Write("profiles/downloaded.yaml", new() { ["proxies"] = new JsonArray(Node("ProfileNode")) });
    Check(importer.Read(Path.Combine(root, "profiles.yaml"), root).Nodes.Single().Name == "ProfileNode", "Verge manifest reads referenced local profile without subscription download");
    Write("inline.yaml", new() { ["proxies"] = new JsonArray(Node("TopLevel")), ["proxy-providers"] = new JsonObject
    {
        ["local"] = new JsonObject { ["type"] = "inline", ["payload"] = new JsonArray(Node("InlineGood"), chain.DeepClone(), files.DeepClone(), Node("InlineDirect", "direct"), JsonValue.Create("malformed")) },
        ["invalid"] = new JsonObject { ["type"] = "inline", ["payload"] = new JsonObject() }
    } });
    var inlineImported = importer.Read(Path.Combine(root, "inline.yaml"), root);
    Check(inlineImported.Nodes.Count == 5 && inlineImported.Nodes.Count(node => node.Supported) == 2 && inlineImported.Issues.Count == 2, "inline payload shares top-level node validation and safely reports malformed entries");
    Check(inlineImported.Nodes.Single(node => node.Name == "InlineGood").Source == "inline.yaml" && !JsonSerializer.Serialize(inlineImported).Contains("fixture-secret"), "inline import reports source without exposing node credentials");
    var cappedPayload = new JsonArray(); for (var i = 0; i < 10001; i++) cappedPayload.Add(Node("Inline" + i));
    Write("inline-limit.yaml", new() { ["proxies"] = new JsonArray(Node("CountsAgainstSameLimit")), ["proxy-providers"] = new JsonObject { ["local"] = new JsonObject { ["type"] = "inline", ["payload"] = cappedPayload }, ["later"] = new JsonObject { ["type"] = "inline", ["payload"] = new JsonArray(Node("NotImportedAfterLimit")) } } });
    var cappedInline = importer.Read(Path.Combine(root, "inline-limit.yaml"), root);
    Check(cappedInline.Nodes.Count == 10000 && cappedInline.Nodes.All(node => node.Name != "NotImportedAfterLimit") && cappedInline.Issues.Count == 1, "top-level and multiple inline providers obey one shared batch node limit");
    var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "config.yaml"))));
    Write("escape.yaml", new() { ["proxy-providers"] = new JsonObject { ["outside"] = new JsonObject { ["path"] = "../outside.yaml" } } });
    Check(importer.Read(Path.Combine(root, "escape.yaml"), root) is { Nodes.Count: 0, Issues.Count: 1 }, "provider cache outside selected root rejected");
    var linked = Path.Combine(root, "linked.yaml");
    if (LinkFixtures.TryCreateFile(root, linked, Path.Combine(root, "config.yaml"), "linked config rejected"))
    { Check(importer.Read(linked, root) is { Nodes.Count: 0, Issues.Count: 1 }, "linked config rejected"); File.Delete(linked); }
    File.WriteAllText(Path.Combine(root, "anchors.yaml"), "proxies:\n  - &node { name: A, type: ss, server: example.invalid, port: 443 }\n  - *node\n");
    Check(importer.Read(Path.Combine(root, "anchors.yaml"), root).Nodes.Count == 0, "YAML aliases rejected using existing strict parser");
    var supported = imported.Nodes.Where(node => node.Supported).ToArray();
    NodeHistoryChecks.Run(root, supported, Check);
    var config = ProxyYaml.Parse(ProbeConfiguration.Build(supported[0], 17891));
    File.WriteAllText(Path.Combine(root, "probe-config.yaml"), ProbeConfiguration.Build(supported[0], 17891), new UTF8Encoding(false));
    Check(config["port"]?.GetValue<int>() == 17891 && config["bind-address"]?.GetValue<string>() == "127.0.0.1" && config["allow-lan"]?.GetValue<bool>() == false, "test HTTP listener bound only to dynamic loopback port");
    Check(config["tun"]?["enable"]?.GetValue<bool>() == false && config["dns"]?["listen"]?.GetValue<string>() == "" && config["geo-auto-update"]?.GetValue<bool>() == false, "test config disables TUN public DNS listener and GeoIP updates");
    Check(config["dns"]?["enable"]?.GetValue<bool>() == true && config["dns"]?["use-system-hosts"]?.GetValue<bool>() == false && config["dns"]?["default-nameserver"]?[0]?.GetValue<string>() == "https://1.1.1.1/dns-query"
        && config["dns"]?["proxy-server-nameserver"]?[0]?.GetValue<string>() == "https://1.1.1.1/dns-query", "node DNS bootstrap uses explicit encrypted resolver at an IP address without system resolver fallback");
    Check(config["dns"]?["respect-rules"]?.GetValue<bool>() == true && config["dns"]?["nameserver"]?[0]?.GetValue<string>() == "https://1.1.1.1/dns-query#ChunsonProbeNode" && !config["dns"]!.AsObject().ContainsKey("fallback"), "target DNS requests route through selected node without GeoIP fallback");
    var certificateFile = Node("CertificateFile"); certificateFile["certificate"] = "C:/outside/root/certificate.pem";
    var privateKeyFile = Node("PrivateKeyFile"); privateKeyFile["private-key"] = "../../outside.key";
    Write("external-paths.yaml", new() { ["proxies"] = new JsonArray(certificateFile, privateKeyFile) });
    Check(importer.Read(Path.Combine(root, "external-paths.yaml"), root).Nodes.All(node => !node.Supported), "certificate and private-key file values cannot load outside user root");
    Check(!config.ContainsKey("external-controller") && !config.ContainsKey("proxy-providers") && !config.ContainsKey("rule-providers") && config["rules"]!.AsArray().Select(rule => rule!.GetValue<string>()).SequenceEqual(new[] { "MATCH,ChunsonProbeNode" }), "test config routes both families to the selected node without direct fallback");
    Check(config["ipv6"]!.GetValue<bool>() && config["dns"]!["ipv6"]!.GetValue<bool>() && config["proxies"]![0]!["ip-version"]!.GetValue<string>() == "dual", "independent core, resolver and proxy server allow both families");
    Check(config["proxies"]?.AsArray().Count == 1 && config["proxies"]?[0]?["password"]?.GetValue<string>() == "fixture-secret-not-in-reports", "only selected node credentials enter temporary configuration");
    var network = new FixtureNetwork();
    var quality = new IpQualityService(new AccountRepository(Path.Combine(root, "account-fixture")), network);
    var runtime = new FixtureRuntime();
    var batch = new BatchNodeProbeService(runtime, quality);
    var progress = new List<NodeProbeProgress>();
    var report = await batch.ProbeAsync(supported, new InlineProgress(value => progress.Add(value)));
    Check(report.Results.Count == 2 && report.Results.All(result => result.Success && result.Quality?.Ip == "203.0.113.10") && !report.Cancelled, "batch measures selected nodes through independent proxy sessions");
    Check(runtime.MaximumActive == 1 && runtime.Stops == 2 && runtime.Starts == 2, "each node core fully stops before next core starts");
    Check(network.Requests.All(request => request.RequireProxy && request.Proxy.StartsWith("http://127.0.0.1:18", StringComparison.Ordinal)) && network.Requests.Count == 5 && report.UniqueIps == 1 && report.ScoreRequests == 1 && report.ReusedScores == 1, "each route confirms exit; duplicate IP receives one shared score and no optional risk query");
    Check(report.Results.All(result => result.Quality?.Score?.Value == 18), "IPPure score is read from same verified exit without local scoring formula");
    foreach (var payload in new[] { "{\"ip\":\"203.0.113.11\",\"fraudScore\":0}", "{\"ip\":\"203.0.113.10\"}", "{\"ip\":\"203.0.113.10\",\"fraudScore\":101}", "{\"ip\":\"203.0.113.10\",\"fraudScore\":\"0\"}" })
    {
        network.ScorePayload = payload;
        var scoreReport = await quality.ProbeProxyWithScoreAsync("http://127.0.0.1:18000");
        Check(scoreReport.Success && scoreReport.Score?.Value is null, "mismatched missing malformed and out of range score stay unknown while IP details survive");
    }
    network.ScorePayload = "{\"ip\":\"203.0.113.10\",\"fraudScore\":0}";
    Check((await quality.ProbeProxyWithScoreAsync("http://127.0.0.1:18000")).Score?.Value == 0, "explicit valid zero score is not confused with missing data");
    network.ScorePayload = "{\"ip\":\"203.0.113.10\",\"fraudScore\":18}";
    network.ScoreStatus = 429;
    var beforeLimited = network.Requests.Count;
    var limitedScore = await quality.ProbeProxyWithScoreAsync("http://127.0.0.1:18000");
    Check(limitedScore.Success && limitedScore.Score?.Value is null && network.Requests.Count - beforeLimited == 4, "limited score keeps IP information and does not retry");
    network.ScoreStatus = 200;
    Check(progress.Count(value => value.Phase == NodeProbePhase.Finished) == 2 && progress[^1].Completed == 2, "progress delivers serial completed-node results");
    publicJson = JsonSerializer.Serialize(report);
    Check(!publicJson.Contains("fixture-secret") && !publicJson.Contains("fixture.example.invalid"), "batch reports no node server or password");
    Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "config.yaml")))) == originalHash, "batch preserves active proxy configuration and selected node data");
    var delayRuntime = new FixtureRuntime(); var delayNetwork = new FixtureNetwork();
    var delayBatch = new BatchNodeProbeService(delayRuntime, quality, new ProxyLatencyService(delayNetwork));
    var delayReport = await delayBatch.ProbeAsync(supported, delayOnly: true);
    Check(delayReport.Results.All(result => result.Success && result.DelayOnly && result.Quality is null && result.Latency?.Milliseconds is >= 0)
        && delayNetwork.Requests.Count == 2 && delayNetwork.Requests.All(request => request.Url == ProxyLatencyService.Endpoint && request.RequireProxy), "delay only uses selected independent proxy and never requests IP score");
    delayNetwork.DelayTimeout = true;
    delayReport = await delayBatch.ProbeAsync(supported, delayOnly: true);
    Check(delayReport.Results.All(result => !result.Success && result.Latency?.State == ProxyLatencyState.Timeout && result.Latency.Milliseconds is null)
        && delayRuntime.Stops == 4, "timeouts remain distinct from zero latency and each core still cleans up");
    delayNetwork.DelayTimeout = false;
    var recoveredDelay = await delayBatch.ProbeAsync(supported);
    Check(recoveredDelay.Results.All(result => result.Latency?.State == ProxyLatencyState.Success)
        && delayRuntime.Stops == 6 && delayNetwork.Requests.Count == 6,
        "one click retry measures failed nodes again immediately instead of reusing timeout failures");
    delayNetwork.DelayStatus = 200;
    Check((await new ProxyLatencyService(delayNetwork).ProbeAsync("http://127.0.0.1:18000")).State == ProxyLatencyState.Failed, "unexpected HTTP response cannot become successful latency");
    using var delayCancel = new CancellationTokenSource(); delayCancel.Cancel();
    try { await new ProxyLatencyService(delayNetwork).ProbeAsync("http://127.0.0.1:18000", delayCancel.Token); throw new InvalidOperationException("cancelled latency succeeded"); }
    catch (OperationCanceledException) { Check(true, "user cancellation stays cancellation rather than timeout"); }
    runtime = new() { FailStop = true };
    report = await new BatchNodeProbeService(runtime, quality).ProbeAsync(supported);
    Check(report.StopUnconfirmed && report.Results.Count == 1 && runtime.Starts == 1, "unconfirmed stop prevents later nodes");
    using var cancel = new CancellationTokenSource(); runtime = new() { OnStart = cancel.Cancel };
    report = await new BatchNodeProbeService(runtime, quality).ProbeAsync(supported, token: cancel.Token);
    Check(report.Cancelled && runtime.Starts == 1 && runtime.Stops == 1 && runtime.Active == 0, "cancel after start still stops only its own test core");
    runtime = new(); report = await new BatchNodeProbeService(runtime, quality).ProbeAsync([imported.Nodes.Single(node => node.Name == "Chain")]);
    Check(runtime.Starts == 0 && !report.Results.Single().Success, "unsupported chain never starts a core or makes a request");
    runtime = new(); report = await new BatchNodeProbeService(runtime, quality).ProbeAsync(supported, new InlineProgress(_ => throw new InvalidOperationException("fixture observer failure")));
    Check(report.Results.All(result => result.Success) && runtime.Stops == 2, "progress observer failure cannot prevent process cleanup");
    var unknownExecutable = Path.Combine(root, "mihomo.exe"); File.WriteAllText(unknownExecutable, "fixture-not-executable");
    try { await new WindowsMihomoProbeRuntime(unknownExecutable, Path.Combine(root, "jobs")).StartAsync(supported[0], default); throw new InvalidOperationException("unknown executable launched"); }
    catch (MihomoProbeRuntimeException error) { Check(!error.StopUnconfirmed && !Directory.Exists(Path.Combine(root, "jobs")), "unverified binary hash blocks dispatch before secret job creation"); }
    Check(WindowsMihomoProbeRuntime.PinnedMihomoSha256 == "04F8D7FC2B314771E1ECBF15951D59F0E1CB7914503C88CFB6972FF6A824E314", "core hash pinned to acquired verified independent Mihomo version");
    var cleanupRoot = Path.Combine(root, "safe-cleanup"); Directory.CreateDirectory(cleanupRoot);
    string Job(string name)
    {
        var directory = Path.Combine(cleanupRoot, "job-" + name); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, WindowsMihomoProbeRuntime.JobMarker), new JsonObject { ["kind"] = "chunson-node-probe", ["root"] = cleanupRoot, ["job"] = directory }.ToJsonString());
        File.WriteAllText(Path.Combine(directory, "config.yaml"), "fixture-secret"); return directory;
    }
    var safeJob = Job("verified"); WindowsMihomoProbeRuntime.Cleanup(cleanupRoot, safeJob);
    await DirectoryCleanupChecks.Run(cleanupRoot, Check);
    Check(!Directory.Exists(safeJob), "marked owned temporary configuration safely deleted");
    var wrongJob = Job("wrong"); File.WriteAllText(Path.Combine(wrongJob, WindowsMihomoProbeRuntime.JobMarker), "{}");
    try { WindowsMihomoProbeRuntime.Cleanup(cleanupRoot, wrongJob); throw new InvalidOperationException("wrong marker deleted"); }
    catch (IOException) { Check(File.Exists(Path.Combine(wrongJob, "config.yaml")), "wrong job owner cannot delete temporary contents"); }
    var linkedJob = Job("linked"); LinkFixtures.CreateDirectory(Path.GetDirectoryName(root)!, Path.Combine(linkedJob, "outside-project"), root, "probe job descendant link");
    try { WindowsMihomoProbeRuntime.Cleanup(cleanupRoot, linkedJob); throw new InvalidOperationException("linked job deleted"); }
    catch (ChunsonSpace.Core.StorageException) { Check(File.Exists(Path.Combine(root, "config.yaml")) && Directory.Exists(linkedJob), "cleanup refuses linked descendants and preserves outside project"); }
    Directory.Delete(Path.Combine(linkedJob, "outside-project"));
    try { WindowsMihomoProbeRuntime.Cleanup(root, wrongJob); throw new InvalidOperationException("outside root deleted"); }
    catch (IOException) { Check(Directory.Exists(wrongJob), "cleanup verifies direct child of intended absolute root"); }
    var held = new TaskCompletionSource(); runtime = new() { HoldStart = held }; batch = new(runtime, quality);
    var firstBatch = batch.ProbeAsync([supported[0]]);
    var secondBatch = await batch.ProbeAsync([supported[1]]);
    held.SetResult(); await firstBatch;
    Check(secondBatch.Results.Count == 0 && runtime.Starts == 1 && runtime.MaximumActive == 1, "simultaneous invocation cannot create two test cores");
    runtime = new() { UnconfirmedStart = true };
    report = await new BatchNodeProbeService(runtime, quality).ProbeAsync(supported);
    Check(report.StopUnconfirmed && report.Results.Count == 1 && runtime.Starts == 1, "uncertain dispatch blocks all later nodes");
    await ParallelProbeChecks.Run(root, supported, Check);
    await ManagedRoutesChecks.Run(root, supported[0], Check);
    LinkFixtures.Report();
    Console.WriteLine($"PASS {count} isolated node-testing checks; fake cores/process/network only, unknown binary rejected without execution; no actual node requests; fixture root: {root}");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine($"FAIL after {count} node checks: {error}; fixture root: {root}"); return 1; }

sealed class InlineProgress(Action<NodeProbeProgress> action) : IProgress<NodeProbeProgress> { public void Report(NodeProbeProgress value) => action(value); }
sealed class FixtureRuntime : IMihomoProbeRuntime
{
    public int Active { get; private set; }
    public int MaximumActive { get; private set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public bool FailStop { get; init; }
    public Action? OnStart { get; init; }
    public TaskCompletionSource? HoldStart { get; init; }
    public bool UnconfirmedStart { get; init; }
    public async Task<IMihomoProbeSession> StartAsync(NodeCandidate node, CancellationToken token) { token.ThrowIfCancellationRequested(); Starts++; if (UnconfirmedStart) throw new MihomoProbeRuntimeException(true, "fixture uncertain dispatch"); if (HoldStart is not null) await HoldStart.Task.WaitAsync(token); Active++; MaximumActive = Math.Max(MaximumActive, Active); OnStart?.Invoke(); return new FixtureSession(this, "http://127.0.0.1:" + (18000 + Starts)); }
    private sealed class FixtureSession(FixtureRuntime owner, string address) : IMihomoProbeSession
    {
        public string ProxyAddress => address;
        public Task<MihomoProbeStopResult> StopAsync(CancellationToken token) { owner.Stops++; if (owner.FailStop) return Task.FromResult(new MihomoProbeStopResult(false, "fixture stop failure")); owner.Active--; return Task.FromResult(new MihomoProbeStopResult(true, "fixture confirmed stop")); }
    }
}
sealed class FixtureNetwork : IAccountTransport
{
    public List<AccountHttpRequest> Requests { get; } = [];
    public string ScorePayload { get; set; } = "{\"ip\":\"203.0.113.10\",\"fraudScore\":18}";
    public int ScoreStatus { get; set; } = 200;
    public bool DelayTimeout { get; set; }
    public int DelayStatus { get; set; } = 204;
    public Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Requests.Add(request);
        if (request.Url == IpQualityService.ScoreEndpoint) return Task.FromResult(new AccountHttpResponse(ScoreStatus, new MemoryStream(Encoding.UTF8.GetBytes(ScorePayload))));
        if (request.Url == ProxyLatencyService.Endpoint)
        {
            if (DelayTimeout) throw new AccountException(AccountError.Timeout, "fixture timeout");
            return Task.FromResult(new AccountHttpResponse(DelayStatus, new MemoryStream()));
        }
        var body = request.Url == AccountEndpoints.Egress ? "{\"success\":true,\"ip\":\"203.0.113.10\",\"country\":\"United States\",\"country_code\":\"US\",\"timezone\":{\"id\":\"America/Chicago\"}}" : request.Url == AccountEndpoints.Trace ? "ip=203.0.113.10\nloc=US\n" : request.Url == IpQualityService.Endpoint ? "{\"ip\":\"203.0.113.10\",\"company\":\"Fixture Company\"}" : throw new InvalidOperationException("unexpected request");
        return Task.FromResult(new AccountHttpResponse(200, new MemoryStream(Encoding.UTF8.GetBytes(body))));
    }
}

static class SubscriptionFixtureDb
{
    public static void Create(string file, string sql)
    {
        if (sqlite3_open_v2(file, out var db, 6, IntPtr.Zero) != 0) throw new IOException("fixture database open failed");
        try { if (sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0) throw new IOException("fixture SQL failed"); }
        finally { sqlite3_close(db); }
    }
    [System.Runtime.InteropServices.DllImport("winsqlite3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);
    [System.Runtime.InteropServices.DllImport("winsqlite3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern int sqlite3_exec(IntPtr db, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, IntPtr error);
    [System.Runtime.InteropServices.DllImport("winsqlite3", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
}
