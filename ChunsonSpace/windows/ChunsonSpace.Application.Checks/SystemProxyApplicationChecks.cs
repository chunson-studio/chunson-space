using ChunsonSpace.Application;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;

internal static class SystemProxyApplicationChecks
{
    public static async Task<int> Run(string fixtureRoot)
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); count++; }
        var original = new IdentityRef(IdentityKind.Account, "Original");
        foreach (var kind in Enum.GetValues<IdentityKind>())
        {
            var target = new IdentityRef(kind, "Target");
            var environment = new EnvironmentSettings(FollowSystemProxy: kind == IdentityKind.Account);
            var storage = new FakeStorage(fixtureRoot, []) { Active = original };
            var runtime = new FakeRuntime([]);
            var missing = new SystemProxyResolver(() => new(false, ""));
            var failed = await new SwitchService(storage, runtime, systemProxy: missing).SwitchAsync(new(target, true, true, false, environment, "fixture-home"));
            Check(failed.Outcome == OperationOutcome.Failed && runtime.StopCalls == 0 && runtime.LeaseCalls == 0 && storage.Mutations == 0, kind + " unresolved proxy rejects before stopping or migrating");
            var proxy = new SystemProxyResolver(() => new(true, "localhost:7897"));
            var succeeded = await new SwitchService(storage, runtime, systemProxy: proxy).SwitchAsync(new(target, true, true, false, environment, "fixture-home"));
            Check(succeeded.Success && runtime.LastLaunch!.Environment!["HTTPS_PROXY"] == "http://localhost:7897", kind + " resolved proxy reaches desktop launch");
            Check(runtime.LastLaunch!.Arguments.Contains("--proxy-server=http://localhost:7897"), kind + " Chromium explicit route matches environment");
            storage = new FakeStorage(fixtureRoot, []) { Active = target };
            runtime = new FakeRuntime([]);
            failed = await new SwitchService(storage, runtime, environments: new ProxyEnvironmentSource(environment), systemProxy: missing).LaunchCurrentAsync(new(false, "fixture-home"));
            Check(failed.Outcome == OperationOutcome.Failed && runtime.StopCalls == 0 && runtime.LaunchCalls == 0, kind + " launch-current proxy failure leaves existing Codex running");
            var unchanged = await new SwitchService(storage, runtime, systemProxy: missing).SwitchAsync(new(target, false, true, false, environment, "fixture-home"));
            Check(unchanged.Outcome == OperationOutcome.Unchanged && runtime.StopCalls == 0, kind + " switch-only needs no proxy resolution");
        }
        var blankStorage = new FakeStorage(fixtureRoot, []) { Active = original };
        var blankRuntime = new FakeRuntime([]);
        var blank = await new SwitchService(blankStorage, blankRuntime, systemProxy: new SystemProxyResolver(() => new(true, "localhost:7897")))
            .SwitchAsync(new(new(IdentityKind.Account, "Target"), true, true, false, new(), "fixture-home"));
        Check(blank.Outcome == OperationOutcome.Failed && blankRuntime.StopCalls == 0, "dedicated account blank proxy does not silently select system or DIRECT");
        return count;
    }
    private sealed class ProxyEnvironmentSource(EnvironmentSettings environment) : IIdentityEnvironmentSource
    {
        public EnvironmentSettings Read(IdentityRef identity) => environment;
    }
}
