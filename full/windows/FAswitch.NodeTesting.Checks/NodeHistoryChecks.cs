using FAswitch.Accounts;
using FAswitch.NodeTesting;

internal static class NodeHistoryChecks
{
    public static void Run(string root, IReadOnlyList<NodeCandidate> nodes, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "history"); var history = new NodeProbeHistory(directory);
        var report = new IpQualityReport(true, "203.0.113.3", "United States", "", "America/New_York", "", "", null, null, null, null, null, DateTimeOffset.UtcNow, "fixture", "fixture", new(30, "fixture"));
        var node = nodes[0]; var result = new NodeProbeResult(node.Id, node.Name, node.Type, true, report, "fixture", new(ProxyLatencyState.Success, 20, "fixture"));
        check(history.Pending(nodes).Count == nodes.Count, "all new definitions are pending");
        history.Put(result);
        check(!new NodeProbeHistory(directory).Pending(nodes).Any(item => item.Id == node.Id), "completed low-score result survives reload and is skipped");
        check(!File.ReadAllText(Path.Combine(directory, node.Id + ".json")).Contains("fixture-secret-not-in-reports"), "history excludes proxy definition secrets");
        history.Put(result with { DelayOnly = true, Quality = null });
        check(NodeProbeHistory.IsComplete(history.Find(node.Id)), "delay-only result cannot erase a complete IP result");
        foreach (var retry in new[] { result with { Success = false, Quality = null, Latency = new(ProxyLatencyState.Timeout, null, "timeout") }, result with { Success = false, Quality = report with { Score = null } }, result with { Success = false, Latency = new(ProxyLatencyState.Cancelled, null, "cancelled") } })
        {
            history.Put(retry);
            check(history.Pending(nodes).Any(item => item.Id == node.Id), "failed, unknown and cancelled results remain retryable");
        }
        File.WriteAllText(Path.Combine(directory, node.Id + ".json"), "broken fixture");
        check(history.Pending(nodes).Count == nodes.Count, "corrupt history falls back to pending rather than admitting a route");
        try { history.Find("../other"); throw new InvalidOperationException("invalid ID accepted"); }
        catch (ArgumentException) { check(true, "history paths require the stable route hash"); }
    }
}
