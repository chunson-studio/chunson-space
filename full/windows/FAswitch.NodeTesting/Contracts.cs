using System.Text.Json.Nodes;
using FAswitch.Accounts;

namespace FAswitch.NodeTesting;

public sealed class NodeCandidate
{
    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public string Source { get; }
    public bool Supported { get; }
    public string Reason { get; }
    internal JsonObject Definition { get; }
    internal NodeCandidate(JsonObject definition, string source, string reason)
    {
        Definition = definition.DeepClone().AsObject(); Name = NodeRules.Text(definition, "name"); Type = NodeRules.Text(definition, "type").ToLowerInvariant(); Source = Path.GetFileName(source); Reason = reason; Supported = reason.Length == 0;
        // Stable across catalog reads; a changed route definition invalidates prior measurements.
        Id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant() + "\n" + Definition.ToJsonString()))).ToLowerInvariant();
    }
}
public sealed record NodeImportIssue(string Source, string Reason);
public sealed record NodeCatalogResult(IReadOnlyList<NodeCandidate> Nodes, IReadOnlyList<NodeImportIssue> Issues);
public enum NodeProbePhase { Starting, Probing, Stopping, Finished }
public sealed record NodeProbeResult(string Id, string Name, string Type, bool Success, IpQualityReport? Quality, string Message, ProxyLatencyReport? Latency = null, bool DelayOnly = false);
public sealed record NodeProbeProgress(int Completed, int Total, string NodeName, NodeProbePhase Phase, NodeProbeResult? Result = null);
public sealed record NodeBatchReport(IReadOnlyList<NodeProbeResult> Results, bool Cancelled, bool StopUnconfirmed = false, string Message = "", int UniqueIps = 0, int ScoreRequests = 0, int ReusedScores = 0);
public sealed record MihomoProbeStopResult(bool Success, string Message, string? RecoveryPath = null);
public interface IMihomoProbeRuntime
{
    int MaximumConcurrency => 1;
    IDisposable? BeginBatch() => null;
    Task<IMihomoProbeSession> StartAsync(NodeCandidate node, CancellationToken token);
}
public interface IMihomoProbeSession
{
    string ProxyAddress { get; }
    bool IsRunning => true;
    Task<ProxyLatencyReport?> MeasureDelayAsync(CancellationToken token) => Task.FromResult<ProxyLatencyReport?>(null);
    Task<MihomoProbeStopResult> StopAsync(CancellationToken token);
}
internal static class NodeRules
{
    private static readonly HashSet<string> Supported = new(["ss", "ssr", "vmess", "vless", "trojan", "http", "socks5", "hysteria", "hysteria2", "tuic", "snell", "anytls", "ssh"], StringComparer.OrdinalIgnoreCase);
    public static string Text(JsonObject data, string name) => data[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    public static string Reason(JsonObject node)
    {
        var name = Text(node, "name"); var type = Text(node, "type");
        if (name.Length == 0 || name.Length > 256 || name.Any(char.IsControl)) return "节点名称缺失或不兼容。";
        if (!Supported.Contains(type)) return "此节点类型尚不能作为独立代理测试；未改为直连。";
        if (node.ContainsKey("dialer-proxy") || node.ContainsKey("proxies") || node.ContainsKey("use") || node.ContainsKey("proxy")) return "节点包含链式代理或其他节点依赖，独立测试已拒绝。";
        if (Text(node, "server").Length == 0 || node["port"] is not JsonValue port || !port.TryGetValue<int>(out var number) || number is <= 0 or > 65535) return "节点服务地址或端口无法确认；节点地址不是实际出口 IP。";
        if (ContainsLocalFiles(node)) return "节点引用外部文件，无法在临时环境中确认完整配置。";
        return "";
    }
    private static bool ContainsLocalFiles(JsonNode? node)
    {
        if (node is JsonObject map) return map.Any(item => item.Key.EndsWith("-path", StringComparison.OrdinalIgnoreCase) || item.Key.EndsWith("-file", StringComparison.OrdinalIgnoreCase)
            || (item.Key is "certificate" or "client-certificate" or "client-key" or "private-key" or "ca" or "ca-str") && item.Value is JsonValue value && value.TryGetValue<string>(out var text) && !text.TrimStart().StartsWith("-----BEGIN ", StringComparison.Ordinal)
            || ContainsLocalFiles(item.Value));
        return node is JsonArray list && list.Any(ContainsLocalFiles);
    }
}
