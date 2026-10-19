using System.Text.Json.Nodes;
using ChunsonSpace.Proxy;

namespace ChunsonSpace.NodeTesting;

public static class ProbeConfiguration
{
    public const string DnsSummary = "独立核心允许双栈目标；节点域名经加密 DNS 引导，目标 DNS 与连接仅经选中节点，无直连回退。IPv4 / IPv6 分别测量；不可用不代表已验证无泄漏。";
    public static string Build(NodeCandidate node, int port, int controllerPort = 0, string controllerSecret = "")
    {
        if (!node.Supported || port is <= 0 or > 65535) throw new InvalidOperationException("节点或本机测试端口无效。");
        var proxy = node.Definition.DeepClone().AsObject(); proxy["name"] = "ChunsonProbeNode"; proxy["ip-version"] = "dual";
        var configuration = new JsonObject
        {
            ["port"] = port, ["socks-port"] = 0, ["mixed-port"] = 0, ["redir-port"] = 0, ["tproxy-port"] = 0,
            ["allow-lan"] = false, ["bind-address"] = "127.0.0.1", ["mode"] = "rule", ["log-level"] = "silent", ["ipv6"] = true,
            ["find-process-mode"] = "off", ["geo-auto-update"] = false, ["geodata-mode"] = false,
            ["tun"] = new JsonObject { ["enable"] = false }, ["dns"] = new JsonObject
            {
                ["enable"] = true, ["listen"] = "", ["ipv6"] = true, ["enhanced-mode"] = "redir-host", ["prefer-h3"] = false,
                ["use-hosts"] = false, ["use-system-hosts"] = false, ["respect-rules"] = true,
                ["default-nameserver"] = new JsonArray("https://1.1.1.1/dns-query"),
                ["proxy-server-nameserver"] = new JsonArray("https://1.1.1.1/dns-query"),
                ["nameserver"] = new JsonArray("https://1.1.1.1/dns-query#ChunsonProbeNode")
            },
            ["profile"] = new JsonObject { ["store-selected"] = false, ["store-fake-ip"] = false },
            ["proxies"] = new JsonArray(proxy), ["rules"] = new JsonArray("MATCH,ChunsonProbeNode")
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
