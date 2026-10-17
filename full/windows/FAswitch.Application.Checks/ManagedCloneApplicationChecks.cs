using System.Text.Json.Nodes;
using FAswitch.Application;
using FAswitch.Core;
using FAswitch.Windows;

internal static class ManagedCloneApplicationChecks
{
    public static async Task<int> Run(string root)
    {
        int count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        var paths = new ManagedClonePaths(Path.Combine(root, "managed-application"));
        var storage = new ManagedCloneStorage(paths); var profiles = new FileProfileStore(paths.AccountRoot);
        var a = new IdentityRef(IdentityKind.Account, "A"); var b = new IdentityRef(IdentityKind.Account, "B");
        foreach (var identity in new[] { a, b })
        {
            profiles.SaveAccount(identity.Name, new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "fixture-" + identity.Name, ["access_token"] = "fixture-token-" + identity.Name } });
            profiles.SaveEnvironment(identity.Name, new("http://127.0.0.1:7897"));
        }
        storage.ApplyTarget(a, null, false);
        Directory.CreateDirectory(Path.Combine(paths.CodexHome, "sessions")); File.WriteAllText(Path.Combine(paths.CodexHome, "sessions", "fixture.jsonl"), "fixture");
        var request = new SwitchRequest(b, true, false, false, new(), paths.CodexHome);
        var probe = new FixtureEgressProbe();
        foreach (var throws in new[] { false, true })
        {
            var trace = new List<string>();
            var blockedRuntime = new FakeRuntime(trace) { PreflightFails = !throws, PreflightThrows = throws };
            var blockedService = new SwitchService(storage, blockedRuntime, new FixtureResolvedPreparation(), environments: new FileIdentityEnvironmentSource(profiles), egressProbe: probe);
            var blocked = await blockedService.SwitchAsync(request);
            Check(blocked.Outcome == OperationOutcome.Failed && blocked.RecoveryPaths.Count == 0 && blockedRuntime.PreflightCalls == 1
                && blockedRuntime.StopCalls == 0 && blockedRuntime.LaunchCalls == 0 && blockedRuntime.RestoreTimezoneCalls == 0 && !blockedRuntime.LeaseActive,
                "failed or unreadable launch protection stops before process, timezone, snapshot or rollback");
            Check(storage.ReadActiveIdentity() == a && File.ReadAllText(Path.Combine(paths.CodexHome, "sessions", "fixture.jsonl")) == "fixture"
                && !trace.Contains("ensure-timezone"), "early failure preserves the active identity and runtime sessions");
            blockedRuntime = new FakeRuntime(trace) { PreflightFails = !throws, PreflightThrows = throws };
            blocked = await new SwitchService(storage, blockedRuntime, environments: new FileIdentityEnvironmentSource(profiles), egressProbe: probe)
                .LaunchCurrentAsync(new(false, paths.CodexHome));
            Check(blocked.Outcome == OperationOutcome.Failed && blockedRuntime.StopCalls == 0 && blockedRuntime.RestoreTimezoneCalls == 0
                && !blockedRuntime.LeaseActive && storage.ReadActiveIdentity() == a, "current-account start also fails before stopping the existing runtime");
        }
        var runtime = new FakeRuntime([]) { StopFailsAt = 1 };
        var operation = await new SwitchService(storage, runtime, environments: new FileIdentityEnvironmentSource(profiles), egressProbe: probe).SwitchAsync(request);
        Check(!operation.Success && storage.ReadActiveIdentity() == a && Directory.Exists(Path.Combine(paths.CodexHome, "sessions")), "managed failed stop leaves current runtime unchanged");
        runtime = new FakeRuntime([]) { ConfirmFails = true };
        operation = await new SwitchService(storage, runtime, environments: new FileIdentityEnvironmentSource(profiles), egressProbe: probe).SwitchAsync(request);
        Check(!operation.Success && storage.ReadActiveIdentity() == a && runtime.LaunchCalls == 0, "managed unconfirmed stop cannot clear or launch");
        runtime = new FakeRuntime([]);
        operation = await new SwitchService(storage, runtime, environments: new FileIdentityEnvironmentSource(profiles), egressProbe: probe).SwitchAsync(request with { CodexHome = Path.Combine(root, "original-home") });
        Check(!operation.Success && runtime.StopCalls == 0 && storage.ReadActiveIdentity() == a, "home override refuses managed launch before stopping");
        runtime = new FakeRuntime([]) { LaunchResult = new(LaunchStatus.Failed, false, null, "fixture", "fixture failure") };
        operation = await new SwitchService(storage, runtime, environments: new FileIdentityEnvironmentSource(profiles), egressProbe: probe).SwitchAsync(request);
        Check(operation.Outcome == OperationOutcome.RolledBack && storage.ReadActiveIdentity() == a && !Directory.Exists(Path.Combine(paths.CodexHome, "sessions")), "managed failed launch restores prior credentials into clean runtime");
        runtime = new FakeRuntime([]);
        var resolvedPreparation = new FixtureResolvedPreparation();
        var verifiedService = new SwitchService(storage, runtime, resolvedPreparation, environments: new FileIdentityEnvironmentSource(profiles), egressProbe: probe);
        operation = await verifiedService.SwitchAsync(request);
        Check(operation.Success && storage.ReadActiveIdentity() == b && runtime.LastLaunch?.Environment?["CODEX_HOME"] == paths.CodexHome, "managed launch uses injected runtime home");
        Check(probe.LastProxy == "http://127.0.0.1:7897" && runtime.LastLaunch?.Environment?["HTTP_PROXY"] == probe.LastProxy && runtime.LastLaunch?.Environment?["TZ"] == "America/Chicago"
            && runtime.LastLaunch?.Arguments.Contains("--accept-lang=en-US") == true && verifiedService.LastEgressEvidence?.Ip == "203.0.113.10" && runtime.LastLaunch?.ExpectedEgressIp == verifiedService.LastEgressEvidence?.Ip, "launch probe and startup share fixed proxy with measured timezone and English locale");
        Check(resolvedPreparation.LastProxy == probe.LastProxy, "managed profile preparation shares verified launch route without re-resolution");
        Check(runtime.PreflightCalls == 1 && ReferenceEquals(runtime.LastLaunch, runtime.PreparedRequest)
            && runtime.LastLaunch?.Target?.Executable == "fixture-Codex.exe", "same prepared target and request flow from preflight to launch without rediscovery");
        profiles.SaveAccount("B", new JsonObject { ["last_refresh"] = "2026-10-13T12:00:00Z", ["tokens"] = new JsonObject { ["account_id"] = "fixture-B", ["access_token"] = "fresh-login" } }, overwrite: true);
        Directory.CreateDirectory(Path.Combine(paths.DesktopRoot, "Default")); File.WriteAllText(Path.Combine(paths.DesktopRoot, "Default", "fixture-cookie"), "fixture");
        operation = await new SwitchService(storage, new FakeRuntime([]), environments: new FileIdentityEnvironmentSource(profiles), egressProbe: probe).SwitchAsync(request);
        Check(operation.Success && File.ReadAllText(Path.Combine(paths.CodexHome, "auth.json")).Contains("fresh-login", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(paths.DesktopRoot, "Default")), "explicit managed account start rebuilds same identity and loads newer saved authorization");
        foreach (var evidence in new[] { new LaunchEgressEvidence(false, "", "", "failed"), new(true, "invalid-ip", "America/Chicago", ""), new(true, "203.0.113.10", "Not/ARealZone", ""), new(true, "127.0.0.1", "America/Chicago", "") })
        {
            runtime = new FakeRuntime([]); probe.Evidence = evidence;
            operation = await new SwitchService(storage, runtime, environments: new FileIdentityEnvironmentSource(profiles), egressProbe: probe).SwitchAsync(request);
            Check(!operation.Success && runtime.StopCalls == 0 && runtime.LaunchCalls == 0 && storage.ReadActiveIdentity() == b, "unknown or invalid egress evidence blocks before runtime mutation");
        }
        runtime = new FakeRuntime([]);
        operation = await new SwitchService(storage, runtime, environments: new FileIdentityEnvironmentSource(profiles)).LaunchCurrentAsync(new(false, paths.CodexHome));
        Check(!operation.Success && runtime.StopCalls == 0, "managed current launch without probe fails closed");
        probe.Evidence = new(true, "203.0.113.10", "America/Chicago", "fixture");
        var systemResolutions = 0;
        profiles.SaveEnvironment("B", new(FollowSystemProxy: true));
        runtime = new FakeRuntime([]);
        operation = await new SwitchService(storage, runtime, environments: new FileIdentityEnvironmentSource(profiles), systemProxy: new SystemProxyResolver(() => new(true, ++systemResolutions == 1 ? "127.0.0.1:7899" : "127.0.0.1:7900")), egressProbe: probe).LaunchCurrentAsync(new(false, paths.CodexHome));
        Check(operation.Success && systemResolutions == 1 && probe.LastProxy == "http://127.0.0.1:7899" && runtime.LastLaunch?.Environment?["HTTP_PROXY"] == probe.LastProxy, "system launch resolves once and uses identical proxy for probe and launch");
        var reset = await new MaintenanceService(new FakeRuntime([]) { ConfirmFails = true }).ResetManagedRuntimeAsync(storage);
        Check(!reset.Success && storage.ReadActiveIdentity() == b, "managed maintenance confirms stop before reset");
        reset = await new MaintenanceService(new FakeRuntime([])).ResetManagedRuntimeAsync(storage);
        Check(reset.Success && storage.ReadActiveIdentity() is null && profiles.ListAccounts().Count == 2 && profiles.ReadAccountCredentials("B")["tokens"]?["account_id"]?.GetValue<string>() == "fixture-B", "managed reset leaves account library available");
        return count;
    }
    private sealed class FixtureEgressProbe : ILaunchEgressProbe
    {
        public LaunchEgressEvidence Evidence { get; set; } = new(true, "203.0.113.10", "America/Chicago", "fixture");
        public string? LastProxy { get; private set; }
        public Task<LaunchEgressEvidence> ProbeAsync(IdentityRef identity, string proxy, CancellationToken token) { LastProxy = proxy; return Task.FromResult(Evidence); }
    }
    private sealed class FixtureResolvedPreparation : IResolvedProxyProfilePreparation
    {
        public string? LastProxy { get; private set; }
        public Task<IReadOnlyList<string>> PrepareAsync(IdentityRef identity, CancellationToken token) => throw new InvalidOperationException("resolved managed route bypassed");
        public Task<IReadOnlyList<string>> PrepareWithProxyAsync(IdentityRef identity, string proxy, CancellationToken token) { LastProxy = proxy; return Task.FromResult<IReadOnlyList<string>>([]); }
    }
}
