using System.Text.Json;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;

internal static class OwnedProcessChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var old = new ProcessIdentity(59412, 45120, "ChatGPT.exe", @"C:\OpenAI\Codex\ChatGPT.exe", 100, true);
        var replacement = new ProcessIdentity(old.Pid, 35828, "cmd.exe", @"C:\Windows\System32\cmd.exe", 200);
        var child = new ProcessIdentity(15464, replacement.Pid, "node.exe", @"C:\Program Files\nodejs\node.exe", 201);
        var store = new FakeOwnedStore { Value = [new(old, OwnedProcessKind.DesktopChild, "fixture", "")] };
        var system = new FakeProcessSystem([replacement, child]);
        var scope = new ManagedCloneProcessScope(system, store);
        var stopped = new CodexProcessService(system, new FakeClock(), scope).ConfirmStopped(ProcessScope.DesktopAndCli);
        check(stopped.Success, "verified reused parent and its newer child are not an old clone orphan");
        check(scope.Selected(system.Items).Count == 0 && store.Writes == 0 && system.StopCalls.Count == 0,
            "replacement processes gain no ownership and selection leaves records untouched");
        var timezone = new FakeTimezoneSystem();
        var lifecycle = new OwnedProcessLifecycleService(system, new FakeClock(), store, new(timezone, new FakeTimezoneStore()), cloneScope: scope);
        check((await lifecycle.AdoptAsync()).Success && system.StopCalls.Count == 0 && timezone.SetCalls == 0,
            "adoption accepts verified PID reuse without process or timezone actions");

        foreach (var parent in new[] { replacement, old with { CreationTime = 200 } })
        {
            var fixtureStore = new FakeOwnedStore { Value = [new(old, OwnedProcessKind.DesktopChild, "fixture", "")] };
            var fixtureSystem = new FakeProcessSystem([parent, child]);
            check(new ManagedCloneProcessScope(fixtureSystem, fixtureStore).Selected(fixtureSystem.Items).Count == 0,
                "newer parent excludes its child even when executable path is reused: " + parent.Name);
        }

        async Task Reject(List<ProcessIdentity> snapshot, string name, string reason)
        {
            var fixtureStore = new FakeOwnedStore { Value = [new(old, OwnedProcessKind.DesktopChild, "fixture", "")] };
            var originalRecords = fixtureStore.Value;
            var fixtureSystem = new FakeProcessSystem(snapshot);
            var fixtureTimezone = new FakeTimezoneSystem();
            var fixtureScope = new ManagedCloneProcessScope(fixtureSystem, fixtureStore);
            var fixture = new OwnedProcessLifecycleService(fixtureSystem, new FakeClock(), fixtureStore,
                new(fixtureTimezone, new FakeTimezoneStore()), cloneScope: fixtureScope);
            var adopted = await fixture.AdoptAsync();
            var warning = string.Join(";", adopted.Warnings);
            check(!adopted.Success && adopted.DesktopAlive && warning.Contains("阶段=clone-scope")
                && warning.Contains("HResult=0x80131620") && warning.Contains(reason), name + " is diagnosed and refused");
            check(!(await fixture.PollAsync()).Success && !(await fixture.CloseTrackedAsync()).Success
                && await fixture.TrackedDesktopAliveAsync() && fixtureStore.Value.SequenceEqual(originalRecords)
                && fixtureStore.Writes == 0 && fixtureSystem.StopCalls.Count == 0 && fixtureTimezone.SetCalls == 0,
                name + " retains evidence and blocks stop/restore");
        }
        await Reject([child], "missing parent with unobserved child", "unobserved-orphan");
        await Reject([replacement, child with { CreationTime = 150 }], "child predating replacement parent", "unobserved-orphan");
        await Reject([replacement with { CreationTime = 0 }, child], "unreadable parent creation", "identity-unreadable");
        await Reject([replacement with { Executable = "" }, child], "unreadable parent path", "identity-unreadable");
        await Reject([replacement with { CreationTime = old.CreationTime }, child], "changed path without newer creation", "unobserved-orphan");
        await Reject([replacement, child with { CreationTime = 0 }], "unreadable child creation", "unobserved-orphan");
        await Reject([replacement, child with { Executable = "" }], "unreadable child path", "unobserved-orphan");
        await Reject([child with { CreationTime = 0 }], "missing parent and unreadable child", "unobserved-orphan");

        store = new FakeOwnedStore { Value = [new(old, OwnedProcessKind.Desktop, "fixture", "")] };
        system = new FakeProcessSystem([old, child]);
        scope = new(system, store);
        check(scope.Selected(system.Items).Select(item => item.Pid).Order().SequenceEqual(new[] { old.Pid, child.Pid }.Order()),
            "verified original parent still captures its real child");
        system.Items.Remove(old);
        check(scope.Selected(system.Items).Single() == child, "previously recorded child remains owned after parent exits");

        store = new FakeOwnedStore { Value = [new(old with { CreationTime = 0 }, OwnedProcessKind.Desktop, "fixture", "")] };
        system = new FakeProcessSystem([]);
        lifecycle = new(system, new FakeClock(), store, new(new FakeTimezoneSystem(), new FakeTimezoneStore()), cloneScope: new(system, store));
        check(!(await lifecycle.AdoptAsync()).Success && store.Writes == 0, "legacy incomplete ownership cannot become empty success");

        foreach (var (error, expectedType, hresult) in new (Exception, string, string)[]
        {
            (new IOException(@"private C:\account\secret.json", unchecked((int)0x80070020)), "IOException", "0x80070020"),
            (new UnauthorizedAccessException("private account label"), "UnauthorizedAccessException", "0x80070005"),
            (new JsonException("private record contents"), "JsonException", "0x80131500")
        })
        {
            store = new FakeOwnedStore { ReadFailure = error, Value = [new(old, OwnedProcessKind.Desktop, "fixture", "")] };
            system = new FakeProcessSystem([]);
            timezone = new FakeTimezoneSystem();
            lifecycle = new(system, new FakeClock(), store, new(timezone, new FakeTimezoneStore()), cloneScope: new(system, store));
            var failed = await lifecycle.AdoptAsync();
            var diagnostic = string.Join(";", failed.Warnings);
            check(!failed.Success && failed.DesktopAlive && diagnostic.Contains("阶段=ownership-read")
                && diagnostic.Contains("类型=" + expectedType) && diagnostic.Contains("HResult=" + hresult)
                && diagnostic.Contains("数据根已隐藏") && !diagnostic.Contains("private") && !diagnostic.Contains("secret.json"),
                expectedType + " retains stage/HResult without private contents");
            check(!(await lifecycle.PollAsync()).Success && !(await lifecycle.CloseTrackedAsync()).Success
                && store.Writes == 0 && system.StopCalls.Count == 0 && timezone.SetCalls == 0,
                expectedType + " cannot authorize writes, stopping or timezone restore");
        }
        store = new FakeOwnedStore(); system = new FakeProcessSystem([]) { ThrowSnapshot = true };
        timezone = new FakeTimezoneSystem();
        lifecycle = new(system, new FakeClock(), store, new(timezone, new FakeTimezoneStore()));
        check(!(await lifecycle.AdoptAsync()).Success && (await lifecycle.PollAsync()).Warnings.Single().Contains("阶段=system-snapshot")
            && store.Writes == 0 && timezone.SetCalls == 0, "snapshot failure is distinct from missing ownership and stays blocking");

        foreach (var withScope in new[] { false, true })
        {
            store = new FakeOwnedStore { Value = [new(old, OwnedProcessKind.Desktop, "fixture", "")], FailWrite = true };
            system = new FakeProcessSystem([old]); timezone = new FakeTimezoneSystem();
            lifecycle = new(system, new FakeClock(), store, new(timezone, new FakeTimezoneStore()), cloneScope: withScope ? new(system, store) : null);
            var failed = await lifecycle.AdoptAsync();
            check(!failed.Success && failed.Warnings.Single().Contains("ownership-update-failed") && store.Value.Count == 1
                && !(await lifecycle.PollAsync()).Success && system.StopCalls.Count == 0 && timezone.SetCalls == 0,
                "failed persistence cannot report successful adoption; scope=" + withScope);
        }
        var cancelled = new CancellationToken(true);
        var cancellationPassed = false;
        try { await lifecycle.AdoptAsync(cancelled); } catch (OperationCanceledException) { cancellationPassed = true; }
        check(cancellationPassed, "cancellation remains cancellation rather than an ownership failure");
    }

    public static async Task InspectAsync(string ownershipPath, string reportPath)
    {
        // Explicit one-shot metadata collection. Never acquire a real store lease,
        // call real lifecycle operations, or persist a real process selection.
        if (Path.GetFileName(ownershipPath) != "managed-owned-processes.json") throw new ArgumentException("Expected explicit ownership record path");
        PhysicalFileStorage.RejectLinks(reportPath);
        using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var phase = "ownership-read";
        object report;
        try
        {
            var files = new PhysicalFileStorage();
            var beforeHash = files.Hash(ownershipPath);
            var records = new JsonOwnedProcessStore(ownershipPath).Read();
            phase = "system-snapshot";
            var snapshot = new WindowsProcessSystem().Snapshot();
            phase = "ownership-consistency";
            var afterHash = files.Hash(ownershipPath);
            if (beforeHash != afterHash) throw new IOException("Ownership changed during collection; no replay");
            var memoryStore = new FakeOwnedStore { Value = records };
            var memorySystem = new FakeProcessSystem(snapshot.ToList());
            var scope = new ManagedCloneProcessScope(memorySystem, memoryStore);
            var lifecycle = new OwnedProcessLifecycleService(memorySystem, new FakeClock(), memoryStore,
                new(new FakeTimezoneSystem(), new FakeTimezoneStore()), cloneScope: scope);
            var replay = await lifecycle.AdoptAsync();
            var pids = records.Select(record => record.Identity.Pid).ToHashSet();
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            object Metadata(ProcessIdentity identity) => new { identity.Pid, identity.ParentPid, identity.Name, identity.CreationTime,
                executable = profile.Length > 0 ? identity.Executable.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase) : identity.Executable };
            report = new { observedAt = DateTimeOffset.UtcNow, phase = "completed", ownershipPath = "%LOCALAPPDATA%/ChunsonSwitch/managed-owned-processes.json (logical alias)",
                beforeHash, afterHash, currentObservationOnly = true, originalIncidentStack = (string?)null,
                originalIncidentStage = (string?)null, originalIncidentHResult = (string?)null,
                records = records.Select(record => new { kind = record.Kind.ToString(), identity = Metadata(record.Identity) }),
                relatedProcesses = snapshot.Where(process => pids.Contains(process.Pid) || pids.Contains(process.ParentPid)).Select(Metadata),
                replayWithMemoryStore = replay, actualOwnershipWrites = 0, actualProcessActions = 0, actualTimezoneActions = 0 };
        }
        catch (Exception error)
        {
            report = new { observedAt = DateTimeOffset.UtcNow, phase, exceptionType = error.GetType().Name,
                hresult = $"0x{error.HResult:X8}", currentObservationOnly = true, completed = false,
                actualOwnershipWrites = 0, actualProcessActions = 0, actualTimezoneActions = 0 };
        }
        await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions { WriteIndented = true });
    }
}
