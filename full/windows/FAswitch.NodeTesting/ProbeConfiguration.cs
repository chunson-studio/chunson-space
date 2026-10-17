using System.Text.Json.Nodes;
using FAswitch.Proxy;

namespace FAswitch.NodeTesting;

public static class ProbeConfiguration
{
    public const string DnsSummary = "独立核心关闭 IPv6 解析并拒绝 IPv6 目标；节点域名经加密 DNS 引导，目标 DNS 经选中节点。上游代理自行解析域名时的出口仍需实测。";
    public static string Build(NodeCandidate node, int port, int controllerPort = 0, string controllerSecret = "")
    {
        if (!node.Supported || port is <= 0 or > 65535) throw new InvalidOperationException("节点或本机测试端口无效。");
        var proxy = node.Definition.DeepClone().AsObject(); proxy["name"] = "ChunsonProbeNode"; proxy["ip-version"] = "ipv4";
        var configuration = new JsonObject
        {
            ["port"] = port, ["socks-port"] = 0, ["mixed-port"] = 0, ["redir-port"] = 0, ["tproxy-port"] = 0,
            ["allow-lan"] = false, ["bind-address"] = "127.0.0.1", ["mode"] = "rule", ["log-level"] = "silent", ["ipv6"] = false,
            ["find-process-mode"] = "off", ["geo-auto-update"] = false, ["geodata-mode"] = false,
            ["tun"] = new JsonObject { ["enable"] = false }, ["dns"] = new JsonObject
            {
                ["enable"] = true, ["listen"] = "", ["ipv6"] = false, ["enhanced-mode"] = "redir-host", ["prefer-h3"] = false,
                ["use-hosts"] = false, ["use-system-hosts"] = false, ["respect-rules"] = true,
                ["default-nameserver"] = new JsonArray("https://1.1.1.1/dns-query"),
                ["proxy-server-nameserver"] = new JsonArray("https://1.1.1.1/dns-query"),
                ["nameserver"] = new JsonArray("https://1.1.1.1/dns-query#ChunsonProbeNode")
            },
            ["profile"] = new JsonObject { ["store-selected"] = false, ["store-fake-ip"] = false },
            ["proxies"] = new JsonArray(proxy), ["rules"] = new JsonArray("IP-CIDR6,::/0,REJECT,no-resolve", "MATCH,ChunsonProbeNode")
        };
        if (controllerPort != 0)
        {
            if (controllerPort is < 1 or > 65535 || controllerPort == port || controllerSecret.Length < 32) throw new InvalidOperationException("本地测速接口参数无效。");
            configuration["external-controller"] = "127.0.0.1:" + controllerPort;
            configuration["secret"] = controllerSecret;
        }
        return ProxyYaml.Serialize(configuration);
    }
}
