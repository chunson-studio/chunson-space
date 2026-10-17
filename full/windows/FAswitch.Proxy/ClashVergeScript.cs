using System.Text.Json.Nodes;

namespace FAswitch.Proxy;

public sealed partial class ClashVergeScript
{
    public const string DefaultScript = "// Define main function (script entry)\n\nfunction main(config, profileName) {\n  return config;\n}\n";
    private const string Header = "// Chunson Switch DNS policy v1\n// parameters: ";
    public static string Generate(string group, bool enableTun = true, DnsPolicy? policy = null)
    {
        MihomoInspector.ValidateGroupName(group);
        var actual = DnsPolicy.Parse((policy ?? DnsPolicy.Default).Json());
        var parameters = new JsonObject { ["group"] = group, ["enableTun"] = enableTun, ["policy"] = JsonNode.Parse(actual.Json()) };
        var json = parameters.ToJsonString();
        return Header + json + "\n" + Body.Replace("__PARAMETERS__", json, StringComparison.Ordinal) + "\n";
    }
    public static bool CanReplace(string code)
    {
        var normalized = Normalize(code);
        if (normalized == Normalize(DefaultScript) || normalized == Normalize("function main(config) { return config; }")) return true;
        if (!normalized.StartsWith(Header, StringComparison.Ordinal)) return false;
        var line = normalized.Split('\n').ElementAtOrDefault(1);
        if (line is null) return false;
        try
        {
            var args = JsonNode.Parse(line["// parameters: ".Length..]) as JsonObject;
            if (args is null) return false;
            var policy = DnsPolicy.Parse(args["policy"]!.ToJsonString());
            var group = ProxyYaml.Text(args["group"]);
            if (normalized == Normalize(Generate(group, ProxyYaml.Bool(args["enableTun"]), policy))) return true;
            MihomoInspector.ValidateGroupName(group);
            using var stream = typeof(ClashVergeScript).Assembly.GetManifestResourceStream("FAswitch.Proxy.Resources.clash-verge-dns-v1.js")!;
            using var reader = new StreamReader(stream);
            var json = line["// parameters: ".Length..];
            return normalized == Normalize(Header + json + "\n" + reader.ReadToEnd().Replace("__PARAMETERS__", json, StringComparison.Ordinal));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or ProxyException or ArgumentException or NullReferenceException) { return false; }
    }
    internal static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    private const string Body = """
function main(config, profileName) {
  const settings = __PARAMETERS__;
  const p = settings.policy;
  const group = settings.group;
  const groups = Array.isArray(config["proxy-groups"]) ? config["proxy-groups"] : [];
  if (!groups.some(g => g && g.name === group)) throw new Error("Chunson Switch: selected proxy group is missing");
  const object = (value) => value && typeof value === "object" && !Array.isArray(value);
  if (config.dns != null && !object(config.dns)) throw new Error("Chunson Switch: incompatible DNS settings");
  config.dns = config.dns || {};
  const dns = config.dns;
  if (dns["nameserver-policy"] != null && !object(dns["nameserver-policy"])) throw new Error("Chunson Switch: incompatible DNS policy");
  const owned = new Set(p.directDomains.map(d => "+." + d).concat(["www.baidu.com", "geosite:" + p.domesticGeosite.toLowerCase()]));
  const privateIp = (s) => {
    if (typeof s !== "string" || !/^\d{1,3}(\.\d{1,3}){3}$/.test(s)) return false;
    const n = s.split(".").map(Number);
    return n.every(v => v >= 0 && v <= 255) && (n[0] === 10 || n[0] === 127 || (n[0] === 192 && n[1] === 168) || (n[0] === 172 && n[1] >= 16 && n[1] <= 31));
  };
  const policy = dns["nameserver-policy"] || {};
  Object.keys(policy).forEach(key => {
    if (owned.has(key)) return;
    const values = Array.isArray(policy[key]) ? policy[key] : [policy[key]];
    if (!values.length || !values.every(privateIp)) throw new Error("Chunson Switch: another custom DNS policy exists");
  });
  ["direct-nameserver", "proxy-server-nameserver-policy"].forEach(key => {
    const v = dns[key];
    if (v != null && !(Array.isArray(v) && !v.length) && !(object(v) && !Object.keys(v).length))
      throw new Error("Chunson Switch: additional DNS settings exist");
  });
  dns.enable = true;
  dns["respect-rules"] = true;
  dns["prefer-h3"] = false;
  dns.nameserver = p.proxyDns.map(s => s + "#" + group);
  dns.fallback = [];
  dns["default-nameserver"] = [p.domesticDns];
  dns["proxy-server-nameserver"] = [p.domesticDns];
  delete policy["www.baidu.com"];
  p.directDomains.map(d => "+." + d).concat(["geosite:" + p.domesticGeosite.toLowerCase()]).forEach(k => policy[k] = p.domesticDns);
  dns["nameserver-policy"] = policy;
  if (config.tun != null && !object(config.tun)) throw new Error("Chunson Switch: incompatible TUN settings");
  config.tun = config.tun || {};
  config.tun["strict-route"] = true;
  if (settings.enableTun) {
    config.tun.enable = true;
    config.tun["auto-route"] = true;
    const hijack = config.tun["dns-hijack"];
    if (hijack != null && !Array.isArray(hijack)) throw new Error("Chunson Switch: incompatible DNS hijack settings");
    config.tun["dns-hijack"] = Array.from(new Set((hijack || []).concat(p.tunDnsHijack)));
  }
  config.mode = "rule";
  config["find-process-mode"] = "always";
  if (config.rules != null && !Array.isArray(config.rules)) throw new Error("Chunson Switch: incompatible rules");
  const original = config.rules || [];
  if (!original.every(r => typeof r === "string" && r.length)) throw new Error("Chunson Switch: incompatible rule entry");
  const protectedRules = rule => [`${rule},${group}`, `${rule},REJECT`];
  const priority = p.proxyProcesses.flatMap(n => protectedRules(`PROCESS-NAME,${n}`))
    .concat(p.proxyDomains.flatMap(d => protectedRules(`DOMAIN-SUFFIX,${d}`)))
    .concat(p.directProcesses.map(n => `PROCESS-NAME,${n},DIRECT`))
    .concat(p.directDomains.map(d => `DOMAIN-SUFFIX,${d},DIRECT`))
    .concat(p.proxyGeosites.map(s => `GEOSITE,${s},${group}`));
  const domestic = [`GEOSITE,${p.domesticGeosite},DIRECT`, `GEOIP,${p.domesticGeosite},DIRECT`];
  const key = r => r.split(",").slice(0, 2).join(",").toLowerCase();
  const managed = new Set(priority.concat(domestic).map(key));
  const retained = original.filter(r => !/^(MATCH|FINAL),/i.test(r) && !managed.has(key(r)));
  config.rules = priority.concat(retained, domestic, [`MATCH,${group}`]);
  return config;
}
""";
}
