using System.Collections.Concurrent;
using ChunsonSpace.Accounts;

namespace ChunsonSpace.NodeTesting;

public sealed class BatchNodeProbeService(IMihomoProbeRuntime runtime, IIpQualityProbe quality, ProxyLatencyService? latency = null, IpScoreCache? cache = null, NodeProbeHistory? history = null)
{
    private readonly IpScoreCache scores = cache ?? new();
    private readonly ConcurrentDictionary<string, (ProxyLatencyReport Delay, DateTimeOffset At)> recentDelays = new();
    private int running;
    public async Task<NodeBatchReport> ProbeAsync(IReadOnlyList<NodeCandidate> selectedNodes, IProgress<NodeProbeProgress>? progress = null, CancellationToken token = default, bool delayOnly = false)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return new([], false, Message: "已有一批节点测试正在执行。");
        var selected = selectedNodes.DistinctBy(node => node.Id).ToArray();
        var results = new ConcurrentDictionary<string, NodeProbeResult>();
        var flights = new ConcurrentDictionary<string, Lazy<Task<IpPureScore?>>>();
        var exits = new ConcurrentDictionary<string, byte>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        int stopUnconfirmed = 0, completed = 0, reused = 0, scoreRequests = 0;
        void Report(NodeProbeProgress value) { try { progress?.Report(value); } catch (Exception) { } }
        async Task Test(NodeCandidate node, CancellationToken workToken)
        {
            IMihomoProbeSession? session = null;
            NodeProbeResult result = new(node.Id, node.Name, node.Type, false, null, node.Supported ? "测试尚未完成。" : node.Reason, DelayOnly: delayOnly);
            try
            {
                if (node.Supported)
                {
                    var previous = !delayOnly && recentDelays.TryGetValue(node.Id, out var saved)
                        && saved.Delay.State == ProxyLatencyState.Success
                        && DateTimeOffset.UtcNow - saved.At < TimeSpan.FromMinutes(5) ? saved.Delay : null;
                    Report(new(Volatile.Read(ref completed), selected.Length, node.Name, NodeProbePhase.Starting));
                    session = await runtime.StartAsync(node, workToken);
                    Report(new(Volatile.Read(ref completed), selected.Length, node.Name, NodeProbePhase.Probing));
                    var delay = previous ?? await session.MeasureDelayAsync(workToken) ?? (latency is null ? null : await latency.ProbeAsync(session.ProxyAddress, workToken));
                    if (delay is not null && delay.State != ProxyLatencyState.Cancelled) recentDelays[node.Id] = (delay, DateTimeOffset.UtcNow);
                    result = result with { Latency = delay };
                    if (delayOnly || delay is { State: not ProxyLatencyState.Success })
                        result = result with { Success = delayOnly && delay?.State == ProxyLatencyState.Success, Message = delay?.Message ?? "延迟服务未连接。" };
                    else
                    {
                        var measured = await quality.ProbeScreeningExitAsync(session.ProxyAddress, workToken);
                        if (measured.HasVerifiedExit)
                        {
                            var key = System.Net.IPAddress.Parse(measured.Ip).ToString(); exits.TryAdd(key, 0);
                            var cached = scores.Find(key);
                            IpPureScore? score;
                            if (cached is not null)
                            {
                                Interlocked.Increment(ref reused);
                                score = cached.Score with { Message = cached.Score.Message + $"\n复用此 IP 的评分（{cached.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}，历史评分）；本次已重新确认出口。" };
                            }
                            else
                            {
                                var candidate = new Lazy<Task<IpPureScore?>>(async () =>
                                {
                                    Interlocked.Increment(ref scoreRequests);
                                    var scored = await quality.AddScoreAsync(measured, session.ProxyAddress, stop.Token);
                                    if (scored.Score is { Quality: >= 0 and <= 100 } value)
                                    {
                                        scores.Put(key, value, scored);
                                        scores.Save();
                                    }
                                    return scored.Score;
                                });
                                var flight = flights.GetOrAdd(key, candidate);
                                if (!ReferenceEquals(candidate, flight)) Interlocked.Increment(ref reused);
                                score = await flight.Value;
                                if (!ReferenceEquals(candidate, flight) && score is not null) score = score with { Message = score.Message + "\n相同出口，本批评分共享。" };
                            }
                            measured = scores.Find(key) is { } stored ? stored.Apply(measured)
                                : measured with { Score = score, Company = score?.Organization ?? "", Asn = score?.Asn ?? "" };
                        }
                        result = result with { Success = measured.HasVerifiedExit, Quality = measured, Message = measured.Message };
                    }
                }
            }
            catch (OperationCanceledException) { result = result with { Message = "本节点测试已取消。", Latency = new(ProxyLatencyState.Cancelled, null, "已取消") }; }
            catch (MihomoProbeRuntimeException error) { if (error.StopUnconfirmed) { Interlocked.Exchange(ref stopUnconfirmed, 1); stop.Cancel(); } result = result with { Message = error.Message }; }
            catch (Exception) { result = result with { Message = "节点请求未完成；未更改当前 FlClash 节点。" }; }
            finally
            {
                if (session is not null)
                {
                    try
                    {
                        var stopped = await session.StopAsync(CancellationToken.None);
                        if (!stopped.Success) throw new IOException(stopped.Message);
                    }
                    catch (Exception error)
                    {
                        Interlocked.Exchange(ref stopUnconfirmed, 1); stop.Cancel();
                        result = result with { Success = false, Message = "本次测试核心退出或清理未确认，已停止后续任务。 " + ErrorRedaction.Scrub(error.Message) };
                    }
                }
                results[node.Id] = result;
                history?.Put(result);
                Report(new(Interlocked.Increment(ref completed), selected.Length, node.Name, NodeProbePhase.Finished, result));
            }
        }
        try
        {
            using var batch = selected.Any(node => node.Supported) ? runtime.BeginBatch() : null;
            try
            {
                await Parallel.ForEachAsync(selected, new ParallelOptions { MaxDegreeOfParallelism = runtime.MaximumConcurrency, CancellationToken = stop.Token },
                    async (node, workToken) => await Test(node, workToken));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            return new(selected.Where(node => results.ContainsKey(node.Id)).Select(node => results[node.Id]).ToArray(), token.IsCancellationRequested, stopUnconfirmed != 0,
                stopUnconfirmed != 0 ? "核心清理未确认，已停止派发并清理本批其他核心。" : token.IsCancellationRequested ? "已取消，已完成结果保留。"
                : delayOnly ? "核心连通性测试完成；超时/失败可筛掉，未查询 IP 分数。"
                : $"筛选完成：{exits.Count} 个不同出口，新增评分 {scoreRequests} 次，复用 {reused} 次。", exits.Count, scoreRequests, reused);
        }
        finally { scores.Save(); Interlocked.Exchange(ref running, 0); }
    }
}
