using System.Text;
using System.Text.Json.Nodes;
using FAswitch.Core;
using FAswitch.Proxy;

var count = 0;
var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "代理-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
void Assert(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
void Expect(ProxyError code, Action action, string label)
{
    try { action(); throw new InvalidOperationException("missing failure: " + label); }
    catch (ProxyException error) { Assert(error.Code == code, label + " actual=" + error.Code); }
}
var source = """
mixed-port: 7897
port: 7890
socks-port: 7891
redir-port: 7892
bind-address: 0.0.0.0
authentication: [fixture:short-secret]
secret: short-controller-secret
proxies:
  - {name: 节点一, type: ss, server: node.invalid, port: 443, password: short-node-secret}
proxy-groups:
  - {name: 海外 代理, type: select, proxies: [节点一]}
  - {name: 纯代理, type: fallback, proxies: [节点一]}
  - {name: 可选直连, type: select, proxies: [节点一, DIRECT]}
  - {name: 含直连, type: fallback, proxies: [节点一, DIRECT]}
  - {name: 循环, type: select, proxies: [循环]}
  - {name: 提供器, type: select, use: [subscription]}
rules:
  - DOMAIN-SUFFIX,user.invalid,DIRECT
  - DOMAIN-SUFFIX,openai.com,DIRECT
  - MATCH,海外 代理
dns:
  ipv6: true
  fake-ip-filter: [+.lan]
  nameserver: [https://1.1.1.1/dns-query]
  nameserver-policy:
    +.lan: 192.168.1.1
    www.baidu.com: 114.114.114.114
tun:
  dns-hijack: [udp://192.168.1.1:53]
listeners:
  - {name: authenticated, type: http, listen: '::', port: 8010, users: [{username: fixture, password: hidden}]}
  - {name: socks, type: socks, port: 8011}
""";
string MakeRoot(string name, bool script = true)
{
    var root = Path.Combine(fixture, name);
    Directory.CreateDirectory(Path.Combine(root, "profiles"));
    File.WriteAllText(Path.Combine(root, "profiles", "A.yaml"), source);
    File.WriteAllText(Path.Combine(root, "clash-verge.yaml"), source);
    File.WriteAllText(Path.Combine(root, "profiles.yaml"), """
current: A
items:
  - uid: A
    type: remote
    name: 隔离订阅
    file: A.yaml
    url: https://subscription.invalid/private-token
    selected: [{name: 海外 代理, now: 节点一}]
""" + (script ? "\n  - {uid: Script, type: script, file: Script.js}\n" : "\n"));
    if (script) File.WriteAllText(Path.Combine(root, "profiles", "Script.js"), ClashVergeScript.DefaultScript);
    File.WriteAllText(Path.Combine(root, "verge.yaml"), "enable_dns_settings: true\nenable_tun_mode: false\nprofile_dns_settings: {A: {enabled: true, confirmation: prior}}\ntheme_mode: dark\n");
    File.WriteAllText(Path.Combine(root, "clash.yaml"), "mode: global\nmixed-port: 7897\nsecret: fixture-controller\ntun: {strict-route: false, dns-hijack: [udp://192.168.1.1:53]}\n");
    return root;
}
JsonObject Read(string path) => ProxyYaml.Parse(File.ReadAllText(path));
void Write(string path, JsonObject value) => File.WriteAllText(path, ProxyYaml.Serialize(value));
try
{
    Assert(DnsPolicy.Default.DirectDomains.Count == 27 && DnsPolicy.Default.DirectProcesses.Count == 7, "exact migrated policy");
    Assert(DnsPolicy.Default.ProxyDns.All(s => !s.Contains('#')), "foreign DNS group added once");
    var inspector = MihomoInspector.Inspect(source);
    Assert(inspector.Ports.Count == 3 && inspector.Ports.Any(p => p.Address == "http://[::1]:8010"), "HTTP mixed and listener candidates");
    Assert(inspector.Ports.All(p => p.RequiresAuthentication), "authentication surfaced without secrets");
    Assert(!System.Text.Json.JsonSerializer.Serialize(inspector).Contains("short-secret", StringComparison.Ordinal) && !System.Text.Json.JsonSerializer.Serialize(inspector).Contains("node.invalid", StringComparison.Ordinal), "inspection no credentials or node URLs");
    Assert(inspector.SuggestedGroup == "海外 代理", "existing MATCH suggestion");
    Assert(inspector.Groups.Single(g => g.Name == "海外 代理").Usable, "selector proxy first");
    Assert(!inspector.Groups.Single(g => g.Name == "含直连").Usable && !inspector.Groups.Single(g => g.Name == "循环").Usable && !inspector.Groups.Single(g => g.Name == "提供器").Usable, "direct cycles unresolved provider rejected");
    Assert(!MihomoInspector.Inspect(source, new Dictionary<string, string> { ["海外 代理"] = "DIRECT" }).Groups.Single(g => g.Name == "海外 代理").Usable, "actual persisted select wins");
    Assert(!MihomoInspector.Inspect(source, new Dictionary<string, string> { ["海外 代理"] = "missing" }).Groups.Single(g => g.Name == "海外 代理").Usable, "unknown persisted select rejected");
    Expect(ProxyError.InvalidConfig, () => ProxyYaml.Parse("port: 1\nport: 2"), "duplicate YAML keys");
    Expect(ProxyError.InvalidConfig, () => ProxyYaml.Parse("one: &x {value: 1}\ntwo: *x"), "anchors refused without mutation");
    Expect(ProxyError.InvalidConfig, () => ProxyYaml.Parse("[]"), "non mapping YAML");
    Expect(ProxyError.InvalidConfig, () => ProxyYaml.Parse("---\na: 1\n---\nb: 2"), "multiple docs");
    Assert(!inspector.Groups.Single(g => g.Name == "可选直连").Usable, "selector containing DIRECT rejected even when proxy is selected");
    var policy = new DnsPolicyService();
    var repaired = policy.PreviewConfig(source, "海外 代理");
    Assert(repaired["mixed-port"]!.GetValue<int>() == 7897 && repaired["socks-port"]!.GetValue<int>() == 7891 && repaired["secret"]!.GetValue<string>() == "short-controller-secret", "ports and unrelated secret unchanged");
    Assert((repaired["dns"] as JsonObject)!["ipv6"]!.GetValue<bool>() && (repaired["dns"] as JsonObject)!["fake-ip-filter"] is JsonArray, "unowned DNS unchanged");
    var dns = repaired["dns"]!.AsObject();
    Assert(dns["nameserver"]!.AsArray().All(n => n!.GetValue<string>().EndsWith("#海外 代理", StringComparison.Ordinal)), "foreign encrypted proxy DNS");
    Assert(dns["default-nameserver"]![0]!.GetValue<string>() == DnsPolicy.Default.DomesticDns && dns["proxy-server-nameserver"]![0]!.GetValue<string>() == DnsPolicy.Default.DomesticDns, "bootstrap direct encrypted DNS");
    Assert(dns["nameserver-policy"]!["+.lan"]!.GetValue<string>() == "192.168.1.1" && dns["nameserver-policy"]!["www.baidu.com"] is null, "LAN preserved managed legacy removed");
    Assert(repaired["rules"]!.AsArray().Any(n => n!.GetValue<string>() == "DOMAIN-SUFFIX,user.invalid,DIRECT") && repaired["rules"]!.AsArray().Last()!.GetValue<string>() == "MATCH,海外 代理", "retain unowned rules and final proxy");
    Assert(repaired["rules"]!.AsArray().Count(n => n!.GetValue<string>().StartsWith("DOMAIN-SUFFIX,openai.com,", StringComparison.Ordinal)) == 2, "managed proxy and immediate reject replace old direct rule");
    Assert(repaired["rules"]![0]!.GetValue<string>() == "PROCESS-NAME,Codex.exe,海外 代理" && repaired["rules"]![1]!.GetValue<string>() == "PROCESS-NAME,Codex.exe,REJECT", "protected process precedes domestic direct and has reject fallback");
    Assert(repaired["tun"]!["dns-hijack"]!.AsArray().Count == 3, "existing DNS hijack preserved");
    Assert(JsonNode.DeepEquals(repaired, policy.PreviewConfig(ProxyYaml.Serialize(repaired), "海外 代理")), "policy idempotent");
    var withoutTun = policy.PreviewConfig(source, "海外 代理", false);
    Assert(withoutTun["tun"]!["enable"] is null && withoutTun["tun"]!["dns-hijack"]!.AsArray().Count == 1, "tun optout doesn't enable or hijack");
    Expect(ProxyError.UnsafeGroup, () => policy.PreviewConfig(source, "含直连"), "unsafe group refused");
    Expect(ProxyError.UnsafeGroup, () => ClashVergeScript.Generate("x#y"), "DNS fragment injection");
    var custom = ProxyYaml.Parse(source); custom["dns"]!["nameserver-policy"]!["user.invalid"] = "https://public.invalid/dns-query";
    Expect(ProxyError.Conflict, () => policy.PreviewConfig(ProxyYaml.Serialize(custom), "海外 代理"), "custom DNS preserved as conflict");
    custom = ProxyYaml.Parse(source); custom["dns"]!["direct-nameserver"] = new JsonArray("8.8.8.8");
    Expect(ProxyError.Conflict, () => policy.PreviewConfig(ProxyYaml.Serialize(custom), "海外 代理"), "extra DNS no silent deletion");
    var scriptCode = ClashVergeScript.Generate("海外 代理");
    Assert(ClashVergeScript.CanReplace(scriptCode) && ClashVergeScript.CanReplace(ClashVergeScript.DefaultScript), "exact tool or official scripts accepted");
    Assert(!ClashVergeScript.CanReplace(scriptCode + "\nconfig.secret='steal';") && !ClashVergeScript.CanReplace("// Chunson Switch DNS policy v1\nfunction main(c){return c;}"), "marker doesn't grant ownership");
    using (var legacyStream = typeof(ClashVergeScript).Assembly.GetManifestResourceStream("FAswitch.Proxy.Resources.clash-verge-dns-v1.js")!)
    using (var legacyReader = new StreamReader(legacyStream))
    {
        var legacyArgs = JsonNode.Parse(scriptCode.Split('\n')[1]["// parameters: ".Length..])!.AsObject();
        legacyArgs["policy"]!.AsObject().Remove("proxyProcesses");
        var legacyJson = legacyArgs.ToJsonString();
        var legacy = "// Chunson Switch DNS policy v1\n// parameters: " + legacyJson + "\n" + legacyReader.ReadToEnd().Replace("__PARAMETERS__", legacyJson, StringComparison.Ordinal);
        Assert(ClashVergeScript.CanReplace(legacy), "exact r2 legacy script can be upgraded");
        Assert(!ClashVergeScript.CanReplace(legacy + "\n// altered"), "legacy marker cannot authenticate changes");
    }
    File.WriteAllText(Path.Combine(fixture, "generated.js"), scriptCode);
    File.WriteAllText(Path.Combine(fixture, "input.json"), ProxyYaml.Parse(source).ToJsonString());
    File.WriteAllText(Path.Combine(fixture, "expected.json"), repaired.ToJsonString());
    var stopped = new FakeClient();
    var service = new ClashVergeDnsService(client: stopped);
    var root = MakeRoot("main");
    var plan = service.Preview(new(root));
    Assert(plan.ProxyGroup == "海外 代理" && plan.RequiresClientStopped && plan.Changes.Count >= 3, "stable preview summary");
    Assert(File.ReadAllText(Path.Combine(root, "profiles", "Script.js")) == ClashVergeScript.DefaultScript, "preview read only");
    stopped.State = ProxyClientState.Running;
    Expect(ProxyError.Conflict, () => service.Apply(plan, Path.Combine(fixture, "backups")), "running client refuses apply");
    stopped.State = ProxyClientState.Unknown;
    Expect(ProxyError.Conflict, () => service.Apply(plan, Path.Combine(fixture, "backups")), "unknown client refuses apply");
    stopped.State = ProxyClientState.Stopped;
    File.AppendAllText(Path.Combine(root, "clash.yaml"), "user_setting: added\n");
    Expect(ProxyError.StalePreview, () => service.Apply(plan, Path.Combine(fixture, "backups")), "stale preview blocks all writes");
    plan = service.Preview(new(root));
    var applied = service.Apply(plan, Path.Combine(fixture, "backups"));
    Assert(applied.SettingsSaved && !applied.RuntimeVerified && File.Exists(Path.Combine(applied.BackupPath, "manifest.json")), "save distinct from runtime verification");
    Assert(ClashVergeScript.CanReplace(File.ReadAllText(Path.Combine(root, "profiles", "Script.js"))), "persistent Script item installed");
    var verge = Read(Path.Combine(root, "verge.yaml"));
    Assert(!verge["enable_dns_settings"]!.GetValue<bool>() && !verge["profile_dns_settings"]!["A"]!["enabled"]!.GetValue<bool>() && verge["enable_tun_mode"]!.GetValue<bool>(), "settings page override disabled and TUN persistent");
    Assert(Read(Path.Combine(root, "clash.yaml"))["mixed-port"]!.GetValue<int>() == 7897, "persistent ports untouched");
    Assert(File.ReadAllText(Path.Combine(root, "profiles", "A.yaml")) == source && File.ReadAllText(Path.Combine(root, "clash-verge.yaml")) == source, "subscription and generated runtime untouched");
    verge["theme_mode"] = "light"; verge["new_setting"] = "later"; Write(Path.Combine(root, "verge.yaml"), verge);
    var clash = Read(Path.Combine(root, "clash.yaml")); clash["mixed-port"] = 9000; Write(Path.Combine(root, "clash.yaml"), clash);
    File.AppendAllText(Path.Combine(root, "profiles", "A.yaml"), "\n# later user rule notes\n");
    var restored = service.Restore(applied.BackupPath);
    Assert(restored.SettingsSaved && !restored.RuntimeVerified, "restore persistent only");
    verge = Read(Path.Combine(root, "verge.yaml"));
    Assert(verge["enable_dns_settings"]!.GetValue<bool>() && !verge["enable_tun_mode"]!.GetValue<bool>() && verge["profile_dns_settings"]!["A"]!["enabled"]!.GetValue<bool>(), "owned flags restored");
    Assert(verge["theme_mode"]!.GetValue<string>() == "light" && verge["new_setting"]!.GetValue<string>() == "later" && Read(Path.Combine(root, "clash.yaml"))["mixed-port"]!.GetValue<int>() == 9000, "restore retains later unowned changes and ports");
    Assert(File.ReadAllText(Path.Combine(root, "profiles", "Script.js")) == ClashVergeScript.DefaultScript, "restore original script bytes");
    Assert(File.ReadAllText(Path.Combine(root, "profiles", "A.yaml")).EndsWith("# later user rule notes\n", StringComparison.Ordinal), "later subscription edits retained");
    root = MakeRoot("created", false);
    plan = service.Preview(new(root, "纯代理")); applied = service.Apply(plan, Path.Combine(fixture, "backups"));
    var registry = Read(Path.Combine(root, "profiles.yaml"));
    Assert(registry["items"]!.AsArray().OfType<JsonObject>().Any(i => i["uid"]!.GetValue<string>() == "Script"), "missing global item bound by fixed UID");
    registry["items"]!.AsArray().Add(new JsonObject { ["uid"] = "later", ["type"] = "local", ["file"] = "later.yaml" });
    registry["unowned"] = true; Write(Path.Combine(root, "profiles.yaml"), registry);
    _ = service.Restore(applied.BackupPath);
    registry = Read(Path.Combine(root, "profiles.yaml"));
    Assert(!File.Exists(Path.Combine(root, "profiles", "Script.js")) && registry["items"]!.AsArray().Count == 2 && registry["unowned"]!.GetValue<bool>(), "remove only created binding keep later items");
    root = MakeRoot("customscript"); File.WriteAllText(Path.Combine(root, "profiles", "Script.js"), "function main(c){c.rules=[];return c;}");
    Expect(ProxyError.Conflict, () => service.Preview(new(root)), "unknown global script preserved");
    root = MakeRoot("edited"); applied = service.Apply(service.Preview(new(root)), Path.Combine(fixture, "backups"));
    File.AppendAllText(Path.Combine(root, "profiles", "Script.js"), "\n// user edit");
    Expect(ProxyError.RestoreConflict, () => service.Restore(applied.BackupPath), "later script edits never overwritten");
    Assert(!Read(Path.Combine(root, "verge.yaml"))["enable_dns_settings"]!.GetValue<bool>(), "restore conflicts preflight before settings writes");
    root = MakeRoot("changedfield"); applied = service.Apply(service.Preview(new(root)), Path.Combine(fixture, "backups"));
    verge = Read(Path.Combine(root, "verge.yaml")); verge["enable_dns_settings"] = true; Write(Path.Combine(root, "verge.yaml"), verge);
    Expect(ProxyError.RestoreConflict, () => service.Restore(applied.BackupPath), "later managed field stops all restore");
    Assert(ClashVergeScript.CanReplace(File.ReadAllText(Path.Combine(root, "profiles", "Script.js"))), "conflict does not restore script early");
    root = MakeRoot("unsafe"); registry = Read(Path.Combine(root, "profiles.yaml")); registry["items"]![0]!["file"] = "../outside.yaml"; Write(Path.Combine(root, "profiles.yaml"), registry);
    Expect(ProxyError.InvalidConfig, () => service.Inspect(root), "source path traversal rejected");
    root = MakeRoot("failure"); var fault = new FaultStorage(); var faultService = new ClashVergeDnsService(fault, stopped);
    var originalSettings = File.ReadAllBytes(Path.Combine(root, "verge.yaml")); plan = faultService.Preview(new(root));
    fault.FailPath = Path.Combine(root, "clash.yaml");
    Expect(ProxyError.Io, () => faultService.Apply(plan, Path.Combine(fixture, "backups")), "write failure rollback");
    Assert(File.ReadAllBytes(Path.Combine(root, "verge.yaml")).SequenceEqual(originalSettings) && File.ReadAllText(Path.Combine(root, "profiles", "Script.js")) == ClashVergeScript.DefaultScript, "all original bytes restored after partial apply");
    root = MakeRoot("backupfailure"); plan = faultService.Preview(new(root)); fault.FailSuffix = "manifest.json";
    Expect(ProxyError.Io, () => faultService.Apply(plan, Path.Combine(fixture, "backups")), "backup record failure blocks writes");
    Assert(File.ReadAllText(Path.Combine(root, "profiles", "Script.js")) == ClashVergeScript.DefaultScript, "backup failure didn't write configuration");
    root = MakeRoot("restorefailure"); fault = new FaultStorage(); faultService = new ClashVergeDnsService(fault, stopped);
    applied = faultService.Apply(faultService.Preview(new(root)), Path.Combine(fixture, "backups"));
    var installedScript = File.ReadAllBytes(Path.Combine(root, "profiles", "Script.js"));
    fault.FailPath = Path.Combine(root, "verge.yaml");
    Expect(ProxyError.Io, () => faultService.Restore(applied.BackupPath), "restore write failure rolls back earlier script");
    Assert(File.ReadAllBytes(Path.Combine(root, "profiles", "Script.js")).SequenceEqual(installedScript) && !Read(Path.Combine(root, "verge.yaml"))["enable_dns_settings"]!.GetValue<bool>(), "restore rollback keeps exact pre-restore values");
    root = MakeRoot("corruptbackup"); applied = service.Apply(service.Preview(new(root)), Path.Combine(fixture, "backups"));
    File.AppendAllText(Path.Combine(applied.BackupPath, "0.before"), "corrupt");
    Expect(ProxyError.InvalidConfig, () => service.Restore(applied.BackupPath), "backup hash verified before restore");
    Assert(!Read(Path.Combine(root, "verge.yaml"))["enable_dns_settings"]!.GetValue<bool>(), "corrupt backup writes nothing");
    root = MakeRoot("otheroverride"); registry = Read(Path.Combine(root, "profiles.yaml"));
    registry["items"]![0]!["option"] = new JsonObject { ["script"] = "user-script" };
    registry["items"]!.AsArray().Add(new JsonObject { ["uid"] = "user-script", ["type"] = "script", ["file"] = "user.js" });
    Write(Path.Combine(root, "profiles.yaml"), registry); File.WriteAllText(Path.Combine(root, "profiles", "user.js"), "function main(c){c.dns={};return c;}");
    Expect(ProxyError.Conflict, () => service.Preview(new(root)), "profile script can override global and is preserved");
    root = MakeRoot("restoreconcurrent"); fault = new FaultStorage(); faultService = new ClashVergeDnsService(fault, stopped);
    applied = faultService.Apply(faultService.Preview(new(root)), Path.Combine(fixture, "backups"));
    fault.ReadHook = path =>
    {
        if (path != Path.Combine(root, "clash.yaml")) return;
        fault.ReadHook = null;
        var changed = Read(Path.Combine(root, "verge.yaml")); changed["editor_added"] = "keep"; Write(Path.Combine(root, "verge.yaml"), changed);
    };
    Expect(ProxyError.RestoreConflict, () => faultService.Restore(applied.BackupPath), "restore second complete hash read protects editor changes");
    Assert(Read(Path.Combine(root, "verge.yaml"))["editor_added"]!.GetValue<string>() == "keep" && ClashVergeScript.CanReplace(File.ReadAllText(Path.Combine(root, "profiles", "Script.js"))), "concurrent conflict writes nothing");
    root = MakeRoot("provider");
    var providerSource = """
mixed-port: 7897
proxy-providers:
  fixture-provider: {type: http, url: 'https://never-request.invalid/private', path: ./cache/nodes.yaml, interval: 86400}
proxy-groups:
  - {name: 提供器代理, type: select, use: [fixture-provider]}
rules: [MATCH,提供器代理]
""".Replace("rules: [MATCH,提供器代理]", "rules: ['MATCH,提供器代理']", StringComparison.Ordinal);
    Directory.CreateDirectory(Path.Combine(root, "cache"));
    File.WriteAllText(Path.Combine(root, "cache", "nodes.yaml"), "proxies: [{name: cache-node, type: ss, server: fixture.invalid, password: cache-secret}]\n");
    File.WriteAllText(Path.Combine(root, "profiles", "A.yaml"), providerSource); File.WriteAllText(Path.Combine(root, "clash-verge.yaml"), providerSource);
    registry = Read(Path.Combine(root, "profiles.yaml")); registry["items"]![0]!["selected"] = new JsonArray(new JsonObject { ["name"] = "提供器代理", ["now"] = "cache-node" }); Write(Path.Combine(root, "profiles.yaml"), registry);
    Assert(service.Inspect(root).Groups.Single().Usable, "referenced fresh local provider resolves shared group logic");
    plan = service.Preview(new(root)); Assert(plan.ProxyGroup == "提供器代理", "provider preview eligible without fetching URL");
    File.AppendAllText(Path.Combine(root, "cache", "nodes.yaml"), "# changed\n");
    Expect(ProxyError.StalePreview, () => service.Apply(plan, Path.Combine(fixture, "backups")), "provider cache participates in preview observed hashes");
    File.SetLastWriteTimeUtc(Path.Combine(root, "cache", "nodes.yaml"), DateTime.UtcNow.AddDays(-2));
    Assert(!service.Inspect(root).Groups.Single().Usable, "expired HTTP cache remains unknown");
    File.Delete(Path.Combine(root, "cache", "nodes.yaml"));
    Assert(!service.Inspect(root).Groups.Single().Usable, "missing provider never fetches");
    var outside = ProxyYaml.Parse(providerSource); outside["proxy-providers"]!["fixture-provider"]!["path"] = "../outside-nodes.yaml";
    File.WriteAllText(Path.Combine(root, "clash-verge.yaml"), ProxyYaml.Serialize(outside));
    Expect(ProxyError.InvalidConfig, () => service.Inspect(root), "provider traversal rejects before reading outside");
    Console.WriteLine($"chun，Proxy checks passed: {count}; fixture={fixture}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"chun，Proxy checks failed after {count}: {error}"); return 1;
}

sealed class FakeClient : IClashVergeState { public ProxyClientState State { get; set; } = ProxyClientState.Stopped; public ProxyClientState Read() => State; }
sealed class FaultStorage : IFileStorage
{
    private readonly PhysicalFileStorage inner = new();
    public string? FailPath { get; set; }
    public string? FailSuffix { get; set; }
    public Action<string>? ReadHook { get; set; }
    public bool FileExists(string p) => inner.FileExists(p);
    public bool DirectoryExists(string p) => inner.DirectoryExists(p);
    public byte[] Read(string p) { var result = inner.Read(p); ReadHook?.Invoke(p); return result; }
    public long Length(string p) => inner.Length(p);
    public void WriteAtomic(string p, byte[] value)
    {
        if (p == FailPath || FailSuffix is not null && p.EndsWith(FailSuffix, StringComparison.Ordinal)) { FailPath = null; FailSuffix = null; throw new IOException("fixture write failure"); }
        inner.WriteAtomic(p, value);
    }
    public void CreateDirectory(string p) => inner.CreateDirectory(p);
    public IReadOnlyList<string> Files(string p, bool recursive) => inner.Files(p, recursive);
    public IReadOnlyList<string> Directories(string p) => inner.Directories(p);
    public void MoveDirectory(string from, string to) => inner.MoveDirectory(from, to);
    public void DeleteFile(string p) => inner.DeleteFile(p);
    public void DeleteDirectory(string p, bool recursive) => inner.DeleteDirectory(p, recursive);
}
