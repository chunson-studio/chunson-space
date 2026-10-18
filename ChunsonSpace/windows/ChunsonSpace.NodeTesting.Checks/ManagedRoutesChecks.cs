using ChunsonSpace.Accounts;
using ChunsonSpace.NodeTesting;

static class ManagedRoutesChecks
{
    public static async Task Run(string root, NodeCandidate node, Action<bool, string> check)
    {
        var path = Path.Combine(root, "managed-routes.json");
        var starts = 0; var stops = 0; var probes = 0; var available = true;
        var measured = new IpQualityReport(true, "203.0.113.50", "US", "", "America/Chicago", "", "", null, null, null, null, null,
            DateTimeOffset.UtcNow, "fixture", "fixture", new(5, "fixture", true));
        var response = measured;
        ManagedNodeRoutes Create() => new(path, binding => available ? node : throw new IOException("node removed"),
            binding => new Runtime(binding.Port, () => starts++, () => stops++),
            (_, token) => { token.ThrowIfCancellationRequested(); probes++; return Task.FromResult(response with { FetchedAt = DateTimeOffset.UtcNow }); });
        var service = Create();
        var proxy = await service.BindAsync("subscription-a", node, measured, default);
        check(proxy.StartsWith("http://127.0.0.1:") && starts == 0 && probes == 0, "selecting measured IP reserves persistent reference without network or account requests");
        check(await service.BindAsync("subscription-a", node, measured, default) == proxy, "reselecting same IP reuses automatic port");
        var separate = await service.BindAsync("subscription-a", node, measured, default, reuseExisting: false);
        check(separate != proxy && starts == 0 && probes == 0, "explicit new independent allocation gives a separate free port without probing");
        check(!await service.ReleaseUnusedAsync(separate, [separate], default) && await service.ReleaseUnusedAsync(separate, [], default)
            && !await service.ReleaseUnusedAsync(separate, [], default), "reserved reference release respects remaining owners and is idempotent");
        var text = File.ReadAllText(path);
        check(!text.Contains("fixture-secret") && !text.Contains("fixture.example.invalid"), "managed binding excludes node server credentials");
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.EnsureProxyAsync(proxy, default)));
        check(starts == 1 && probes == 0, "concurrent requests start one selected independent core without another exit query");
        var admission = await service.ProbeAdmissionAsync(proxy, default);
        check(admission?.Admission.Allowed == true && probes == 1, "only explicit admission queries the selected route");
        await service.ProbeAdmissionAsync(proxy, default);
        check(probes == 2, "subsequent explicit admission rechecks current independent exit");
        await service.EnsureProxyAsync("http://127.0.0.1:7897", default);
        check(await service.ProbeAdmissionAsync("http://127.0.0.1:7897", default) is null && starts == 1 && probes == 2, "current FlClash route remains untouched");
        await service.DisposeAsync(); check(stops == 1, "shutdown stops only owned independent core");
        service = Create(); await service.EnsureProxyAsync(proxy, default);
        check(starts == 2 && probes == 2 && await service.BindAsync("subscription-a", node, measured, default) == proxy, "new manager resumes exact saved port without repeating screening");
        await service.DisposeAsync();
        available = false; service = Create();
        try { await service.EnsureProxyAsync(proxy, default); throw new Exception("missing node accepted"); }
        catch (IOException) { check(starts == 2, "removed subscription node cannot fall back to main exit"); }
        available = true; response = measured with { Ip = "203.0.113.51" };
        var beforeResume = probes;
        await service.EnsureProxyAsync(proxy, default);
        check(starts == 3 && probes == beforeResume, "listener startup does not compare an old screened IP with another public lookup");
        try { await service.ProbeAdmissionAsync(proxy, default); throw new Exception("changed exit accepted"); }
        catch (AccountException error) { check(stops == 2 && error.Message.Contains("筛选时 203.0.113.50，本次 203.0.113.51"), "explicit admission identifies actual changed addresses without terminating an existing listener"); }
        await service.DisposeAsync();
        response = measured with { Score = new(16, "fixture", true) };
        await service.EnsureProxyAsync(proxy, default);
        check(starts == 4 && stops == 3, "saved route resumes without secondary score gate");
        try { await service.ProbeAdmissionAsync(proxy, default); throw new Exception("explicit low score accepted"); }
        catch (AccountException) { check(stops == 3, "explicit admission still rejects low score without terminating browser transport"); }
        await service.DisposeAsync();
        response = measured with { Score = null };
        service = Create(); await service.EnsureProxyAsync(proxy, default);
        check(starts == 5 && stops == 4, "missing score cache does not stop a previously selected route");
        try { await service.ProbeAdmissionAsync(proxy, default); throw new Exception("explicit unknown score accepted"); }
        catch (AccountException) { check(stops == 4, "explicit admission rejects unknown score without terminating browser transport"); }
        check(await service.BindAsync("subscription-a", node, measured with { FetchedAt = DateTimeOffset.UtcNow.AddDays(-366) }, default) == proxy && starts == 5,
            "historical successful score binds without rescore or extra startup probes");
        await service.DisposeAsync();
        service = Create(); response = measured with { Ip = "", Score = null };
        beforeResume = probes; await service.EnsureProxyAsync(proxy, default);
        check(probes == beforeResume, "screening endpoint outage cannot reject starting a previously selected listener");
        try { await service.ProbeAdmissionAsync(proxy, default); throw new Exception("missing exit accepted"); }
        catch (AccountException error) { check(starts == stops + 1 && error.Message.StartsWith("此线路暂未取得有效出口 IP")
            && !error.Message.StartsWith("此线路出口已改变"), "explicit missing exit is distinct from an actual changed exit"); }
        await service.DisposeAsync();
        service = Create(); var beforeUnmeasured = File.ReadAllText(path); response = measured with { Score = new(16, "fixture", true) };
        try { await service.BindUnmeasuredAsync("pending", node, default); throw new Exception("pending 84 accepted"); }
        catch (AccountException) { check(File.ReadAllText(path) == beforeUnmeasured && starts == stops, "pending node below 85 leaves no binding or core"); }
        response = measured with { Score = null };
        try { await service.BindUnmeasuredAsync("pending", node, default); throw new Exception("pending unknown accepted"); }
        catch (AccountException) { check(File.ReadAllText(path) == beforeUnmeasured && starts == stops, "pending node unknown score leaves no binding or core"); }
        response = measured with { Score = new(15, "fixture", true) };
        var fresh = await service.BindUnmeasuredAsync("pending", node, default); var once = probes;
        check(fresh.Quality.Admission.QualityScore == 85 && starts == stops + 1, "pending node verifies actual exit before saving at boundary 85");
        check((await service.ProbeAdmissionAsync(fresh.Proxy, default))?.Admission.Allowed == true && probes == once,
            "pending node reuses same live core and score for explicit admission");
        await service.DisposeAsync(); check(starts == stops, "pending node cleanup drains owned core");
    }
    private sealed class Runtime(int port, Action started, Action stopped) : IMihomoProbeRuntime
    {
        public Task<IMihomoProbeSession> StartAsync(NodeCandidate node, CancellationToken token)
        { token.ThrowIfCancellationRequested(); started(); return Task.FromResult<IMihomoProbeSession>(new Session(port, stopped)); }
    }
    private sealed class Session(int port, Action stopped) : IMihomoProbeSession
    {
        public string ProxyAddress => "http://127.0.0.1:" + port;
        public bool IsRunning { get; private set; } = true;
        public Task<MihomoProbeStopResult> StopAsync(CancellationToken token)
        { if (IsRunning) stopped(); IsRunning = false; return Task.FromResult(new MihomoProbeStopResult(true, "fixture")); }
    }
}
