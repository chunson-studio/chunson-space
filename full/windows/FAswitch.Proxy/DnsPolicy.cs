using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FAswitch.Proxy;

public sealed record DnsPolicy(int SchemaVersion, IReadOnlyList<string> DirectDomains, IReadOnlyList<string> DirectProcesses,
    IReadOnlyList<string> ProxyDomains, IReadOnlyList<string> ProxyGeosites, string DomesticGeosite,
    string DomesticDns, IReadOnlyList<string> ProxyDns, IReadOnlyList<string> TunDnsHijack, IReadOnlyList<string>? ProxyProcesses = null)
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static DnsPolicy Default { get; } = LoadEmbedded();
    public static DnsPolicy Parse(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<DnsPolicy>(json, JsonOptions) ?? throw ProxyYaml.Invalid();
            if (value.SchemaVersion != 1 || value.DirectDomains is null || value.DirectProcesses is null || value.ProxyDomains is null ||
                value.ProxyGeosites is null || value.ProxyDns is null || value.TunDnsHijack is null ||
                value.DirectDomains.Count == 0 || value.ProxyDns.Count == 0 ||
                value.DirectDomains.Concat(value.ProxyDomains).Concat(value.ProxyGeosites).Append(value.DomesticGeosite)
                    .Any(s => s is null || !Regex.IsMatch(s, @"\A[A-Za-z0-9._-]+\z")) ||
                value.DirectProcesses.Any(s => s is null || !Regex.IsMatch(s, @"\A[A-Za-z0-9._-]+\z")) ||
                !ValidDns(value.DomesticDns, true) || value.ProxyDns.Any(s => !ValidDns(s, false)) ||
                value.TunDnsHijack.Any(s => s is not "any:53" and not "tcp://any:53")) throw ProxyYaml.Invalid();
            var protectedProcesses = value.ProxyProcesses ?? ["Codex.exe", "codex.exe", "ChatGPT.exe", "ChunsonSwitch.exe"];
            if (protectedProcesses.Count == 0 || protectedProcesses.Any(s => s is null || !Regex.IsMatch(s, @"\A[A-Za-z0-9._-]+\z"))) throw ProxyYaml.Invalid();
            return value with { ProxyProcesses = protectedProcesses };
        }
        catch (JsonException) { throw ProxyYaml.Invalid(); }
    }
    internal string Json() => JsonSerializer.Serialize(this, JsonOptions);
    private static DnsPolicy LoadEmbedded()
    {
        using var stream = typeof(DnsPolicy).Assembly.GetManifestResourceStream("FAswitch.Proxy.Resources.dns-policy.json")
            ?? throw new InvalidOperationException("缺少 DNS 共享规则。");
        using var reader = new StreamReader(stream); return Parse(reader.ReadToEnd());
    }
    private static bool ValidDns(string? value, bool direct) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.AbsolutePath == "/dns-query" &&
        uri.Fragment == (direct ? "#DIRECT" : "") && System.Net.IPAddress.TryParse(uri.Host, out _);
}

public sealed class DnsPolicyService(DnsPolicy? policy = null)
{
    public DnsPolicy Policy { get; } = policy ?? DnsPolicy.Default;
    public JsonObject PreviewConfig(string yaml, string group, bool enableTun = true,
        IReadOnlyDictionary<string, string>? selectedGroups = null)
    {
        var config = ProxyYaml.Parse(yaml);
        var inspection = MihomoInspector.Inspect(yaml, selectedGroups);
        MihomoInspector.RequireGroup(inspection, group);
        return Apply(config, group, enableTun);
    }
    internal JsonObject Apply(JsonObject source, string group, bool enableTun)
    {
        MihomoInspector.ValidateGroupName(group);
        var config = (JsonObject)source.DeepClone();
        var dns = ProxyYaml.Object(config, "dns", true);
        ValidateDns(dns);
        dns["enable"] = true; dns["respect-rules"] = true; dns["prefer-h3"] = false;
        dns["nameserver"] = Strings(Policy.ProxyDns.Select(s => s + "#" + group));
        dns["fallback"] = new JsonArray();
        dns["default-nameserver"] = Strings([Policy.DomesticDns]);
        dns["proxy-server-nameserver"] = Strings([Policy.DomesticDns]);
        var policyMap = ProxyYaml.Object(dns, "nameserver-policy", true);
        policyMap.Remove("www.baidu.com");
        foreach (var key in Policy.DirectDomains.Select(s => "+." + s).Append("geosite:" + Policy.DomesticGeosite.ToLowerInvariant())) policyMap[key] = Policy.DomesticDns;
        var tun = ProxyYaml.Object(config, "tun", true);
        tun["strict-route"] = true;
        if (enableTun)
        {
            tun["enable"] = true; tun["auto-route"] = true;
            if (tun["dns-hijack"] is not null and not JsonArray) throw ProxyYaml.Invalid();
            var hijack = (tun["dns-hijack"] as JsonArray)?.DeepClone() as JsonArray ?? new();
            foreach (var value in Policy.TunDnsHijack) if (!hijack.Any(n => ProxyYaml.Text(n) == value)) hijack.Add(value);
            tun["dns-hijack"] = hijack;
        }
        config["mode"] = "rule"; config["find-process-mode"] = "always";
        var original = config["rules"] as JsonArray ?? (config["rules"] is null ? new() : throw ProxyYaml.Invalid());
        if (original.Any(n => ProxyYaml.Text(n).Length == 0)) throw ProxyYaml.Invalid();
        var priority = Priority(group);
        var domestic = new[] { $"GEOSITE,{Policy.DomesticGeosite},DIRECT", $"GEOIP,{Policy.DomesticGeosite},DIRECT" };
        var keys = priority.Concat(domestic).Select(RuleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        config["rules"] = Strings(priority.Concat(original.Select(ProxyYaml.Text).Where(r => !Final(r) && !keys.Contains(RuleKey(r))))
            .Concat(domestic).Append("MATCH," + group));
        return config;
    }
    internal void ValidateDns(JsonObject dns)
    {
        var allowed = Policy.DirectDomains.Select(s => "+." + s).Append("www.baidu.com").Append("geosite:" + Policy.DomesticGeosite.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in ProxyYaml.Object(dns, "nameserver-policy"))
        {
            if (allowed.Contains(pair.Key)) continue;
            var values = pair.Value is JsonArray list ? list.Select(ProxyYaml.Text) : [ProxyYaml.Text(pair.Value)];
            if (!values.Any() || values.Any(s => !PrivateResolver(s)))
                throw new ProxyException(ProxyError.Conflict, "配置包含其他公共或自定义 DNS 策略，请先检查；原配置已保留。");
        }
        foreach (var key in new[] { "direct-nameserver", "proxy-server-nameserver-policy" })
        {
            var node = dns[key];
            if (node is not null && node is not JsonArray { Count: 0 } && node is not JsonObject { Count: 0 })
                throw new ProxyException(ProxyError.Conflict, "配置包含额外 DNS 设置，请先检查：" + key);
        }
    }
    internal static bool PrivateResolver(string value)
    {
        if (!System.Net.IPAddress.TryParse(value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes(); return b[0] is 10 or 127 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31;
    }
    internal string[] Priority(string group) => (Policy.ProxyProcesses ?? DnsPolicy.Default.ProxyProcesses!).SelectMany(s => Protected("PROCESS-NAME," + s, group))
        .Concat(Policy.ProxyDomains.SelectMany(s => Protected("DOMAIN-SUFFIX," + s, group)))
        .Concat(Policy.DirectProcesses.Select(s => "PROCESS-NAME," + s + ",DIRECT"))
        .Concat(Policy.DirectDomains.Select(s => "DOMAIN-SUFFIX," + s + ",DIRECT"))
        .Concat(Policy.ProxyGeosites.Select(s => "GEOSITE," + s + "," + group)).ToArray();
    private static string[] Protected(string rule, string group) => [rule + "," + group, rule + ",REJECT"];
    internal static string RuleKey(string value) => string.Join(',', value.Split(',').Take(2));
    internal static bool Final(string value) => value.StartsWith("MATCH,", StringComparison.OrdinalIgnoreCase) || value.StartsWith("FINAL,", StringComparison.OrdinalIgnoreCase);
    internal static JsonArray Strings(IEnumerable<string> values) => new(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
}
