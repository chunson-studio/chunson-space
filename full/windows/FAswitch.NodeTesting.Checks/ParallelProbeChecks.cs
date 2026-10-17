using System.Collections.Concurrent;
using System.Text;
using FAswitch.Accounts;
using FAswitch.NodeTesting;

static class ParallelProbeChecks
{
    public static async Task Run(string root, NodeCandidate[] sample, Action<bool, string> check)
    {
        var nodes = Enumerable.Range(0, 12).Select(i => { var definition = sample[0].Definition.DeepClone().AsObject(); definition["name"] = "Parallel fixture " + i; return new NodeCandidate(definition, "fixture", ""); }).ToArray();
        var runtime = new Runtime(); var network = new Network();
        var quality = new IpQualityService(new AccountRepository(Path.Combine(root, "parallel-accounts")), network);
        var clock = new Clock(); var path = Path.Combine(root, "ip-score-cache.json");
        var cache = new IpScoreCache(path, clock);
        var batch = new BatchNodeProbeService(runtime, quality, cache: cache);
        var concurrentReader = new IpScoreCache(path, clock);
        var visibleBeforeBatchEnds = false;
        var report = await batch.ProbeAsync(nodes, new InlineResultProgress(progress =>
        {
            if (progress.Phase == NodeProbePhase.Finished && progress.Completed < nodes.Length)
                visibleBeforeBatchEnds |= concurrentReader.Find("203.0.113.10") is not null;
        }));
        check(visibleBeforeBatchEnds, "first completed IP grade is available to a running CLI before the UI batch finishes");
        check(runtime.Peak == 4 && runtime.Active == 0 && runtime.Starts == 12 && runtime.Stops == 12, "bounded parallel native probes overlap and all cores close");
        check(network.Scores == 1 && report.ScoreRequests == 1 && report.ReusedScores == 11 && report.Results.All(r => r.Quality?.Admission.Allowed == true), "concurrent same-IP requests single-flight to one score request");
        check(network.Requests.All(r => r.Url != IpQualityService.Endpoint) && network.Exits == 24, "each node freshly identifies exit; optional diagnostics leave screening path");
        check(report.Results.Select(r => r.Id).SequenceEqual(nodes.Select(n => n.Id)), "parallel completion preserves stable report order");
        var before = network.Scores; var beforeDelays = runtime.Delays;
        report = await batch.ProbeAsync(nodes);
        check(network.Scores == before && report.ReusedScores == 12 && runtime.Delays == beforeDelays, "second click reuses fresh latency and IP grades");
        var restarted = new BatchNodeProbeService(runtime, quality, cache: new IpScoreCache(path, clock));
        report = await restarted.ProbeAsync(nodes);
        check(network.Scores == before && report.ReusedScores == 12, "valid IP scores survive service restart");
        var peer = new IpScoreCache(path, clock);
        cache.Put("203.0.113.100", new(3, "UI")); peer.Put("203.0.113.101", new(4, "CLI"));
        await Task.WhenAll(Task.Run(cache.Save), Task.Run(peer.Save));
        check(cache.Find("203.0.113.101")?.Score.Quality == 96 && peer.Find("203.0.113.100")?.Score.Quality == 97,
            "UI and CLI cache writers merge and already-running readers observe each other's results");
        network.Exit = "203.0.113.11";
        report = await restarted.ProbeAsync(nodes);
        check(network.Scores == before + 1 && report.Results.All(r => r.Quality?.Ip == network.Exit), "same nodes changing IP cannot reuse another IP's grade");
        clock.Now += TimeSpan.FromDays(366);
        report = await new BatchNodeProbeService(runtime, quality, cache: new IpScoreCache(path, clock)).ProbeAsync(nodes);
        check(network.Scores == before + 1, "same IP grade survives a year and restart without rescore");
        network.ScoreStatus = 429; network.Exit = "203.0.113.12";
        var limited = new BatchNodeProbeService(runtime, quality, cache: cache);
        before = network.Scores;
        report = await limited.ProbeAsync(nodes);
        check(network.Scores == before + 1 && report.Results.All(r => r.Quality?.Admission.Allowed == false), "one failed grade per IP per batch; unknown never passes");
        network.ScoreStatus = 200;
        report = await limited.ProbeAsync(nodes);
        check(network.Scores == before + 2 && report.Results.All(r => r.Quality?.Admission.Allowed == true), "failed scores are not persisted or made permanently sticky");
        network.ScoreIp = "203.0.113.99"; network.Exit = "203.0.113.13";
        report = await limited.ProbeAsync(nodes);
        check(report.Results.All(r => r.Quality?.Admission.Allowed == false) && cache.Find(network.Exit) is null, "score response for another IP cannot contaminate cache");
        network.ScoreIp = null;
        runtime.FailDelay = true; before = network.Exits;
        report = await limited.ProbeAsync(nodes, delayOnly: true);
        check(network.Exits == before && report.Results.All(r => r.Latency?.State == ProxyLatencyState.Timeout), "native delay-only makes no IP requests");
        var starts = runtime.Starts;
        report = await limited.ProbeAsync(nodes);
        check(runtime.Starts == starts + nodes.Length && network.Exits == before && report.Results.Count == nodes.Length,
            "one click screening retries failed latency and still skips scoring when retry fails");
        runtime.FailDelay = false;
        report = await limited.ProbeAsync(nodes);
        check(report.Results.All(r => r.Success) && runtime.Starts == starts + 2 * nodes.Length,
            "one click screening recovers prior timeouts without a separate connectivity button");
        using var cancel = new CancellationTokenSource();
        var cancelling = new Runtime { OnFour = cancel.Cancel };
        report = await new BatchNodeProbeService(cancelling, quality).ProbeAsync(nodes, token: cancel.Token);
        check(report.Cancelled && cancelling.Active == 0 && cancelling.Stops == cancelling.Starts && cancelling.Starts <= 4, "parallel cancellation drains all started cores and stops queue");
        var stopping = new Runtime { FailStop = true };
        report = await new BatchNodeProbeService(stopping, quality).ProbeAsync(nodes);
        check(report.StopUnconfirmed && stopping.Starts <= 4 && stopping.Stops == stopping.Starts, "unconfirmed cleanup stops dispatch and attempts cleanup of every active core");
        File.WriteAllText(path, "[{\"Ip\":\"203.0.113.13\",\"Score\":null}]");
        check(new IpScoreCache(path, clock).Find("203.0.113.13") is null, "malformed cache cannot allow a node");
        var config = FAswitch.Proxy.ProxyYaml.Parse(ProbeConfiguration.Build(sample[0], 19001, 19002, new string('a', 64)));
        check(config["external-controller"]?.GetValue<string>() == "127.0.0.1:19002" && config["secret"]?.GetValue<string>()?.Length == 64, "native delay API stays authenticated and loopback only");
        File.WriteAllText(Path.Combine(root, "probe-controller-config.yaml"), ProbeConfiguration.Build(sample[0], 19001, 19002, new string('a', 64)));
    }
    private sealed class Clock : IAccountClock
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public DateTimeOffset UtcNow => Now; public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token); }
    private sealed class InlineResultProgress(Action<NodeProbeProgress> report) : IProgress<NodeProbeProgress>
    { public void Report(NodeProbeProgress value) => report(value); }
    private sealed class Runtime : IMihomoProbeRuntime
    {
        public int MaximumConcurrency => 4;
        public int Active, Peak, Starts, Stops, Delays;
        public bool FailDelay, FailStop;
        public Action? OnFour;
        public Task<IMihomoProbeSession> StartAsync(NodeCandidate node, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var sequence = Interlocked.Increment(ref Starts);
            var active = Interlocked.Increment(ref Active); lock (this) Peak = Math.Max(Peak, active);
            if (sequence == 4) OnFour?.Invoke();
            return Task.FromResult<IMihomoProbeSession>(new Session(this, sequence));
        }
        private sealed class Session(Runtime owner, int id) : IMihomoProbeSession
        {
            public string ProxyAddress => "http://127.0.0.1:" + (20000 + id);
            public async Task<ProxyLatencyReport?> MeasureDelayAsync(CancellationToken token)
            { Interlocked.Increment(ref owner.Delays); await Task.Delay(30, token); return owner.FailDelay ? new(ProxyLatencyState.Timeout, null, "fixture") : new(ProxyLatencyState.Success, 12, "fixture native"); }
            public Task<MihomoProbeStopResult> StopAsync(CancellationToken token)
            { Interlocked.Increment(ref owner.Stops); Interlocked.Decrement(ref owner.Active); return Task.FromResult(new MihomoProbeStopResult(!owner.FailStop, "fixture cleanup")); }
        }
    }
    private sealed class Network : IAccountTransport
    {
        public readonly ConcurrentQueue<AccountHttpRequest> Requests = new();
        public int Scores, Exits, ScoreStatus = 200;
        public string Exit = "203.0.113.10";
        public string? ScoreIp;
        public async Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken token)
        {
            Requests.Enqueue(request);
            string text; int status = 200;
            if (request.Url == IpQualityService.ScoreEndpoint)
            { Interlocked.Increment(ref Scores); await Task.Delay(100, token); text = $"{{\"ip\":\"{ScoreIp ?? Exit}\",\"fraudScore\":15,\"isResidential\":true}}"; status = ScoreStatus; }
            else
            {
                Interlocked.Increment(ref Exits); await Task.Delay(5, token);
                text = request.Url == AccountEndpoints.Egress ? $"{{\"success\":true,\"ip\":\"{Exit}\",\"country\":\"United States\",\"country_code\":\"US\",\"timezone\":{{\"id\":\"America/Chicago\"}}}}"
                    : request.Url == AccountEndpoints.Trace ? $"ip={Exit}\nloc=US\n" : throw new InvalidOperationException("unexpected endpoint");
            }
            return new(status, new MemoryStream(Encoding.UTF8.GetBytes(text)));
        }
    }
}
