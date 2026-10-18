using System.IO;
using ChunsonSpace.NodeTesting;
using ChunsonSpace.Proxy;
using ChunsonSpace.Accounts;

namespace ChunsonSpace.Desktop;

public sealed class DesktopNodeServices(DesktopWorkspace workspace)
{
    private readonly NodeCatalogImporter importer = new();
    private BatchNodeProbeService? batch;
    private IIpQualityProbe? rawQuality;
    private ProxyLatencyService? rawLatency;
    private IpScoreCache? scores;
    public NodeProbeHistory History { get; } = new(Path.Combine(workspace.Root, "node-screening-history"));
    private IpScoreCache Scores => scores ??= new(Path.Combine(workspace.Root, "node-netcoffee-scores.json"), OfflineScoreClock);
    public ManagedNodeRoutes Routes { get; private set; } = null!;
    public ManagedNodeRoutes CreateRoutes(IAccountTransport transport)
    {
        rawQuality = new NetCoffeeService(transport); rawLatency = new(transport); _ = Scores;
        return Routes = new(Path.Combine(workspace.Root, "managed-node-routes.json"), binding =>
        {
            var root = workspace.ProxyServices.FlClashRoot;
            var subscription = Subscriptions(ProxyClientKind.FlClash, root).Subscriptions.SingleOrDefault(item => item.Id == binding.SubscriptionId)
                ?? throw new IOException("原订阅已移除，请重新选择独立 IP。");
            return LoadSubscription(subscription, root).Nodes.FirstOrDefault(node => node.Id == binding.NodeId)
                ?? throw new IOException("原节点已更新或移除，请重新选择独立 IP。");
        }, binding => workspace.IsFixture ? new OfflineMihomoProbeRuntime(binding.Port)
            : new WindowsMihomoProbeRuntime(Path.Combine(AppContext.BaseDirectory, "runtimes", "mihomo", "mihomo.exe"),
                Path.Combine(workspace.Root, "managed-node-jobs", binding.Id, "session-" + Guid.NewGuid().ToString("N")), preferredPort: binding.Port),
            ProbeCachedExitAsync, binding => Subscriptions(ProxyClientKind.FlClash, workspace.ProxyServices.FlClashRoot).Subscriptions
                .SingleOrDefault(item => item.Id == binding.SubscriptionId)?.Name ?? "");
    }
    public Task<IpQualityReport> ProbeScoredExitAsync(string proxy, CancellationToken token) => ProbeExitAsync(proxy, true, token);
    public async Task<IpQualityReport> ProbeAccountExitAsync(string proxy, CancellationToken token)
        => (Routes is null ? null : await Routes.ProbeAdmissionAsync(proxy, token)) ?? await ProbeScoredExitAsync(proxy, token);
    public Task<IpQualityReport> ProbeCachedExitAsync(string proxy, CancellationToken token) => ProbeExitAsync(proxy, false, token);
    public IpQualityReport CachedLoginExit(string expectedIp, string subscriptionId, NodeCandidate node)
    {
        if (!System.Net.IPAddress.TryParse(expectedIp, out var ip)) throw new InvalidOperationException("请使用检测结果中的实际 IP。");
        var cached = Scores.Find(ip.ToString());
        var history = History.Find(node.Id);
        var report = cached?.Report ?? (history is { Success: true, DelayOnly: false } && history.Quality?.Ip == ip.ToString() ? history.Quality : null);
        if (report is null) throw new InvalidOperationException("这个 IP 没有已确认的检测结果，请先筛选。");
        report = ApplySelectedAdmission(subscriptionId, node, cached?.Apply(report) ?? report);
        if (!report.Admission.Allowed) throw new InvalidOperationException(report.Admission.Message);
        return report;
    }
    private IpQualityReport ApplySelectedAdmission(string subscriptionId, NodeCandidate node, IpQualityReport report)
    {
        var root = workspace.ProxyServices.FlClashRoot;
        var subscription = Subscriptions(ProxyClientKind.FlClash, root).Subscriptions.SingleOrDefault(item => item.Id == subscriptionId)
            ?? throw new IOException("原订阅已移除，请重新选择 IP。");
        var current = LoadSubscription(subscription, root).Nodes.SingleOrDefault(item => item.Id == node.Id)
            ?? throw new IOException("原节点已更新，请重新选择 IP。");
        return NodeScreeningRules.ApplyAdmission(report, current, subscription.Name);
    }
    public async Task<LoginProxySelection> PrepareDedicatedLoginAsync(string subscriptionId, NodeCandidate node, IpQualityReport measured, CancellationToken token)
    {
        measured = ApplySelectedAdmission(subscriptionId, node, measured);
        if (!NodeScreeningRules.IsAccountCandidate(node) || !measured.Admission.Allowed)
            throw new InvalidOperationException("请选择已确认出口且符合准入规则的非娱乐线路。");
        var routes = Routes ?? throw new InvalidOperationException("独立出口服务尚未连接。");
        return new(await routes.BindAsync(subscriptionId, node, measured, token), "独立 IP " + measured.Ip, true);
    }
    internal void ResetFixtureScore()
    {
        if (!IsFixture) throw new InvalidOperationException("仅隔离测试可重置评分夹具。");
        Scores.Forget("192.0.2.10");
    }
    private async Task<IpQualityReport> ProbeExitAsync(string proxy, bool scoreMissing, CancellationToken token)
    {
        var service = rawQuality ?? throw new InvalidOperationException("出口检测服务尚未连接。");
        var measured = await service.ProbeScreeningExitAsync(proxy, token);
        if (!measured.HasVerifiedExit) return measured;
        if (Scores.Find(measured.Ip) is { } cached) return cached.Apply(measured);
        if (!scoreMissing) return measured with { Score = new(null, "当前出口还没有有效评分。请等待后台检测，或使用一键检测；登录不会重复测分。") { Provider = "Net.Coffee" } };
        var report = await service.AddScoreAsync(measured, proxy, token);
        if (report.Score is { Quality: >= 0 and <= 100 } value) { Scores.Put(measured.Ip, value, report); Scores.Save(); }
        return report;
    }
    public bool IsFixture => workspace.IsFixture;
    public OfflineMihomoProbeRuntime? OfflineRuntime { get; } = workspace.IsFixture ? new() : null;
    public OfflineNodeScoreClock? OfflineScoreClock { get; } = workspace.IsFixture ? new() : null;
    public NodeCatalogResult Load(ProxyClientKind client, string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new InvalidOperationException("未确认客户端数据目录，请先在代理页读取配置。");
        var source = Path.Combine(dataRoot, client == ProxyClientKind.FlClash ? "config.yaml" : "clash-verge.yaml");
        if (!File.Exists(source)) throw new InvalidOperationException("未找到客户端已生成的节点配置。请先在该客户端导入并加载订阅，再重新读取；不会下载订阅。");
        return importer.Read(source, dataRoot);
    }
    public SubscriptionCatalog Subscriptions(ProxyClientKind client, string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new InvalidOperationException("未找到 FlClash，请先打开客户端并导入订阅。");
        return SubscriptionCatalogReader.Read(client, dataRoot);
    }
    public NodeCatalogResult LoadSubscription(NodeSubscription subscription, string dataRoot) => importer.Read(subscription.File, dataRoot);
    public BatchNodeProbeService CreateBatch()
    {
        if (batch is not null) return batch;
        if (workspace.Accounts is null) throw new InvalidOperationException("节点检测服务尚未连接。");
        IMihomoProbeRuntime runtime = OfflineRuntime is { } offline ? offline : new WindowsMihomoProbeRuntime(Path.Combine(AppContext.BaseDirectory, "runtimes", "mihomo", "mihomo.exe"), Path.Combine(workspace.Root, "node-testing-jobs", "session-" + Guid.NewGuid().ToString("N")));
        return batch = new(runtime, rawQuality ?? throw new InvalidOperationException("Net.Coffee 检测服务尚未连接。"), rawLatency ?? workspace.Accounts.Latency, Scores, History);
    }
    internal void ResetFixtureProbeBatch()
    {
        if (!IsFixture) throw new InvalidOperationException("仅隔离测试可重置测试批次。");
        batch = null;
    }
}

public sealed class OfflineNodeScoreClock : IAccountClock
{
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;
    public void Advance(TimeSpan value) => UtcNow += value;
    public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
}

public sealed class OfflineMihomoProbeRuntime(int preferredPort = 0) : IMihomoProbeRuntime
{
    public List<string> StartedIds { get; } = [];
    public int StopCalls { get; private set; }
    public bool FailStop { get; set; }
    public TaskCompletionSource? HoldMeasureDelay { get; set; }
    public int MeasureDelayCalls { get; private set; }
    public Task<IMihomoProbeSession> StartAsync(NodeCandidate node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); StartedIds.Add(node.Id);
        return Task.FromResult<IMihomoProbeSession>(new Session(this, "http://127.0.0.1:" + (preferredPort > 0 ? preferredPort : 40000 + StartedIds.Count)));
    }
    private sealed class Session(OfflineMihomoProbeRuntime owner, string proxy) : IMihomoProbeSession
    {
        public string ProxyAddress => proxy;
        public async Task<ProxyLatencyReport?> MeasureDelayAsync(CancellationToken token)
        {
            owner.MeasureDelayCalls++;
            if (owner.HoldMeasureDelay is { } gate) await gate.Task.WaitAsync(token);
            return null;
        }
        public Task<MihomoProbeStopResult> StopAsync(CancellationToken token)
        { owner.StopCalls++; return Task.FromResult(new MihomoProbeStopResult(!owner.FailStop, owner.FailStop ? "模拟清理未确认" : "离线测试核心已停止；未创建真实进程。")); }
    }
}
