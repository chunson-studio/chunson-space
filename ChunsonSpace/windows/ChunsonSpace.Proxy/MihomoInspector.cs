using System.Text.Json.Nodes;

namespace ChunsonSpace.Proxy;

public static class MihomoInspector
{
    public static IReadOnlyList<ProxyPortCandidate> InspectPorts(string yaml) => Ports(ProxyYaml.Parse(yaml)).ToArray();
    public static ProxyInspection Inspect(string yaml, IReadOnlyDictionary<string, string>? selectedGroups = null, string? runtimeYaml = null,
        IReadOnlyDictionary<string, string>? providerYamls = null)
    {
        var config = ProxyYaml.Parse(yaml);
        var runtime = runtimeYaml is null ? config : ProxyYaml.Parse(runtimeYaml);
        var sourceGroups = Groups(config);
        var groups = Groups(runtime);
        var nodes = Nodes(runtime);
        var baseNodes = nodes.ToArray();
        var providers = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        if (providerYamls is not null)
            foreach (var pair in providerYamls)
            {
                var providerNodes = Nodes(ProxyYaml.Parse(pair.Value));
                if (providerNodes.Count == 0) continue;
                if (providerNodes.Any(node => nodes.Any(existing => ProxyYaml.Text(existing["name"]) == ProxyYaml.Text(node["name"]) && !JsonNode.DeepEquals(existing, node)))) continue;
                providers[pair.Key] = providerNodes;
                foreach (var node in providerNodes)
                    if (!nodes.Any(n => ProxyYaml.Text(n["name"]) == ProxyYaml.Text(node["name"]))) nodes.Add(node);
            }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in nodes.Concat(groups).Concat(sourceGroups))
            if (ProxyYaml.Text(entry["name"]).Length == 0) throw ProxyYaml.Invalid();
        foreach (var entry in nodes.Concat(groups)) if (!names.Add(ProxyYaml.Text(entry["name"]))) throw ProxyYaml.Invalid();
        var map = nodes.Concat(groups).ToDictionary(n => ProxyYaml.Text(n["name"]), StringComparer.Ordinal);
        var selected = selectedGroups ?? new Dictionary<string, string>();
        bool Resolve(string name, HashSet<string> visiting)
        {
            if (name.ToUpperInvariant() is "DIRECT" or "REJECT" or "REJECT-DROP" or "PASS" or "PASS-RULE" or "COMPATIBLE" or "GLOBAL" || !visiting.Add(name)) return false;
            try
            {
                if (!map.TryGetValue(name, out var entry)) return false;
                var type = ProxyYaml.Text(entry["type"]).ToLowerInvariant();
                if (type is "direct" or "reject" or "reject-drop" or "pass" or "pass-rule") return false;
                if (!groups.Contains(entry)) return type.Length > 0;
                var members = entry["proxies"] is JsonArray a ? a.Select(ProxyYaml.Text).Where(s => s.Length > 0).ToList() : [];
                if (entry["filter"] is not null || entry["exclude-filter"] is not null || entry["exclude-type"] is not null) return false;
                if (ProxyYaml.Bool(entry["include-all"]) || ProxyYaml.Bool(entry["include-all-proxies"]))
                    members.AddRange(baseNodes.Select(n => ProxyYaml.Text(n["name"])));
                if (ProxyYaml.Bool(entry["include-all"]) || ProxyYaml.Bool(entry["include-all-providers"]))
                {
                    if (ProxyYaml.Object(runtime, "proxy-providers").Any(p => !providers.ContainsKey(p.Key))) return false;
                    members.AddRange(providers.Values.SelectMany(n => n).Select(n => ProxyYaml.Text(n["name"])));
                }
                if (entry["use"] is JsonArray use)
                    foreach (var provider in use.Select(ProxyYaml.Text))
                    {
                        if (!providers.TryGetValue(provider, out var found)) return false;
                        members.AddRange(found.Select(n => ProxyYaml.Text(n["name"])));
                    }
                members = members.Distinct(StringComparer.Ordinal).ToList();
                if (members.Count == 0) return false;
                if (type == "select")
                {
                    var choice = selected.TryGetValue(name, out var value) ? value : members[0];
                    return members.Contains(choice, StringComparer.Ordinal) && members.All(n => Resolve(n, visiting));
                }
                return type is "url-test" or "fallback" or "load-balance" && members.All(n => Resolve(n, visiting));
            }
            finally { visiting.Remove(name); }
        }
        var output = sourceGroups.Select(g =>
        {
            var name = ProxyYaml.Text(g["name"]);
            var safe = SafeName(name) && groups.Any(r => ProxyYaml.Text(r["name"]) == name) && Resolve(name, new(StringComparer.Ordinal));
            return new ProxyGroup(name, ProxyYaml.Text(g["type"]), safe, safe ? "配置中全部候选均指向代理；尚未验证网络。" : "包含直连候选、循环、未解析提供器或无效选择；请选择不含直连的代理分组。");
        }).ToArray();
        var warnings = new List<string> { "地址和分组来自配置，尚未检查监听状态或网络出口。" };
        if (runtimeYaml is null && sourceGroups.Any(g => g["use"] is not null)) warnings.Add("未读取运行配置，提供器分组可能无法确认。");
        var ports = Ports(runtime).ToArray();
        if (ports.Any(p => p.RequiresAuthentication)) warnings.Add("部分代理端口要求认证，未提取或显示认证信息。");
        var usable = output.Where(g => g.Usable).Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        var candidates = new List<string>();
        if (config["dns"] is JsonObject dns && dns["nameserver"] is JsonArray servers)
            candidates.AddRange(servers.Select(ProxyYaml.Text).Select(s => s.LastIndexOf('#') is var pos && pos >= 0 ? s[(pos + 1)..] : "").Where(usable.Contains));
        if (config["rules"] is JsonArray rules) candidates.AddRange(rules.Select(ProxyYaml.Text).Where(DnsPolicyService.Final).Select(s => s.Split(',').ElementAtOrDefault(1) ?? "").Where(usable.Contains));
        var distinct = candidates.Distinct(StringComparer.Ordinal).ToArray();
        var suggestion = distinct.Length == 1 ? distinct[0] : distinct.Length == 0 && usable.Count == 1 ? usable.Single() : null;
        return new(ports, output, suggestion, warnings);
    }
    internal static void RequireGroup(ProxyInspection inspection, string group)
    {
        ValidateGroupName(group);
        if (!inspection.Groups.Any(g => g.Name == group && g.Usable)) throw new ProxyException(ProxyError.UnsafeGroup, "所选分组无法确认指向代理；未修改配置。");
    }
    internal static void ValidateGroupName(string group)
    { if (!SafeName(group)) throw new ProxyException(ProxyError.UnsafeGroup, "分组名称含不兼容的规则或 DNS 字符。"); }
    private static bool SafeName(string name) => !string.IsNullOrWhiteSpace(name) && name == name.Trim() && !name.Any(c => c is ',' or '#' or '&' || char.IsControl(c));
    private static List<JsonObject> Groups(JsonObject config) => Entries(config, "proxy-groups");
    private static List<JsonObject> Nodes(JsonObject config) => Entries(config, "proxies");
    private static List<JsonObject> Entries(JsonObject config, string key)
    {
        if (config[key] is null) return [];
        if (config[key] is not JsonArray entries || entries.Any(n => n is not JsonObject)) throw ProxyYaml.Invalid();
        return entries.Cast<JsonObject>().ToList();
    }
    private static IEnumerable<ProxyPortCandidate> Ports(JsonObject config)
    {
        var auth = config["authentication"] is JsonArray { Count: > 0 };
        var bind = ProxyYaml.Text(config["bind-address"]);
        string Host(string address) => address is "" or "*" or "0.0.0.0" ? "127.0.0.1" : address == "::" ? "[::1]" : address.Contains(':') ? "[" + address.Trim('[', ']') + "]" : address;
        bool ValidHost(string address) => address is "" or "*" || System.Net.IPAddress.TryParse(address, out _);
        int Port(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var p) ? p : 0;
        foreach (var key in new[] { "mixed-port", "port" })
            if (Port(config[key]) is var p && p is >= 1 and <= 65535 && ValidHost(bind)) yield return new(key, "http://" + Host(bind) + ":" + p, auth);
        if (config["listeners"] is JsonArray listeners)
            foreach (var listener in listeners.OfType<JsonObject>())
                if (ProxyYaml.Text(listener["type"]) is "mixed" or "http" && Port(listener["port"]) is var p && p is >= 1 and <= 65535 && ValidHost(ProxyYaml.Text(listener["listen"])))
                    yield return new("listener:" + ProxyYaml.Text(listener["name"]), "http://" + Host(ProxyYaml.Text(listener["listen"])) + ":" + p,
                        listener["users"] is JsonArray { Count: > 0 });
    }
}
