using System.Text.Json.Nodes;
using ChunsonSpace.Application;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;

int count = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); count++; Console.WriteLine("PASS " + name); }
var original = new IdentityRef(IdentityKind.Account, "Original");
var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
try
{
    foreach (var kind in Enum.GetValues<IdentityKind>())
    foreach (var isolated in new[] { false, true })
    foreach (var launch in new[] { false, true })
    {
        var trace = new List<string>();
        var storage = new FakeStorage(fixtureRoot, trace) { Active = original };
        var runtime = new FakeRuntime(trace);
        var service = new SwitchService(storage, runtime);
        var target = new IdentityRef(kind, "Target");
        var request = new SwitchRequest(target, launch, isolated, true, new("localhost:1234", "Asia/Tokyo", "ja-JP"), "fixture-home");
        var result = await service.SwitchAsync(request);
        Check(result.Success && result.ActiveIdentity == target && result.RunningIdentity == (launch ? target : null), $"{kind}/{isolated}/{launch} success identities");
        if (launch) Check(result.Message.Contains("fixture-confirmed"), "confirmed switch preserves launch verification details for the UI");
        Check(trace.IndexOf("stop:DesktopAndCli") < trace.IndexOf("snapshot") && trace.IndexOf("snapshot") < trace.IndexOf("archive") && trace.IndexOf("archive") < trace.IndexOf("confirm:DesktopAndCli") && trace.IndexOf("confirm:DesktopAndCli") < trace.IndexOf("apply"), $"{kind}/{isolated}/{launch} sequence");
        Check(storage.LastIsolated == isolated && storage.CompleteCalls == 1, $"{kind}/{isolated}/{launch} storage parameters");
        if (launch) Check(trace.IndexOf("ensure-timezone") < trace.IndexOf("launch") && trace.IndexOf("launch") < trace.IndexOf("complete"), $"{kind}/{isolated} launch and cleanup sequence");
        runtime = new FakeRuntime([]) { StopFailsAt = 1 };
        storage = new FakeStorage(fixtureRoot, []) { Active = original };
        result = await new SwitchService(storage, runtime).SwitchAsync(request);
        Check(result.Outcome == OperationOutcome.Failed && storage.Mutations == 0 && storage.Active == original, $"{kind}/{isolated}/{launch} stop failure does not mutate");
        storage = new FakeStorage(fixtureRoot, []) { Active = original, ThrowSnapshot = true };
        result = await new SwitchService(storage, new FakeRuntime([])).SwitchAsync(request);
        Check(result.Outcome == OperationOutcome.Failed && storage.ApplyCalls == 0 && storage.RestoreCalls == 0, $"{kind}/{isolated}/{launch} snapshot failure precedes apply");
        storage = new FakeStorage(fixtureRoot, []) { Active = original, ThrowApply = true };
        runtime = new FakeRuntime([]);
        result = await new SwitchService(storage, runtime).SwitchAsync(request);
        Check(result.Outcome == OperationOutcome.RolledBack && storage.Active == original && storage.RestoreCalls == 1 && runtime.RestoreTimezoneCalls == 1 && storage.CompleteCalls == 0, $"{kind}/{isolated}/{launch} partial apply rollback");
        storage = new FakeStorage(fixtureRoot, []) { Active = original, ThrowApply = true, ThrowRestore = true };
        result = await new SwitchService(storage, new FakeRuntime([])).SwitchAsync(request);
        Check(result.Outcome == OperationOutcome.RecoveryFailed && result.RecoveryPaths.Count > 0 && storage.CompleteCalls == 0, $"{kind}/{isolated}/{launch} rollback failure retains snapshot");
        storage = new FakeStorage(fixtureRoot, []) { Active = original, ThrowApply = true };
        runtime = new FakeRuntime([]) { StopFailsAt = 2 };
        result = await new SwitchService(storage, runtime).SwitchAsync(request);
        Check(result.Outcome == OperationOutcome.RecoveryFailed && storage.RestoreCalls == 0 && runtime.RestoreTimezoneCalls == 0, $"{kind}/{isolated}/{launch} rollback stop failure never writes snapshot");
        if (!launch) continue;
        storage = new FakeStorage(fixtureRoot, []) { Active = original };
        runtime = new FakeRuntime([]) { LaunchResult = new(LaunchStatus.Pending, false, null, "fake", "pending", "launch-evidence") };
        result = await new SwitchService(storage, runtime).SwitchAsync(request);
        Check(result.PendingLaunch && !result.Success && result.ActiveIdentity == target && storage.RestoreCalls == 0 && storage.CompleteCalls == 0 && runtime.RestoreTimezoneCalls == 0 && result.RecoveryPaths.Contains("launch-evidence"), $"{kind}/{isolated} pending keeps active and recovery");
        storage = new FakeStorage(fixtureRoot, []) { Active = original };
        runtime = new FakeRuntime([]) { LaunchResult = new(LaunchStatus.Failed, false, null, "fake", "creation-rejected") };
        result = await new SwitchService(storage, runtime).SwitchAsync(request);
        Check(result.Outcome == OperationOutcome.RolledBack && runtime.RestoreTimezoneCalls == 1 && storage.Active == original, $"{kind}/{isolated} launch failure restores timezone and identity");
        storage = new FakeStorage(fixtureRoot, []) { Active = original };
        runtime = new FakeRuntime([]) { EnsureTimezoneFails = true };
        result = await new SwitchService(storage, runtime).SwitchAsync(request);
        Check(result.Outcome == OperationOutcome.RolledBack && runtime.LaunchCalls == 0 && runtime.RestoreTimezoneCalls == 1, $"{kind}/{isolated} timezone gate prevents launch and restores");
        storage = new FakeStorage(fixtureRoot, []) { Active = original };
        runtime = new FakeRuntime([]) { ThrowLaunch = true };
        result = await new SwitchService(storage, runtime).SwitchAsync(request);
        Check(result.PendingLaunch && storage.RestoreCalls == 0 && storage.CompleteCalls == 0, $"{kind}/{isolated} unknown dispatch exception cannot rollback");
    }
    var accountRequest = new SwitchRequest(new(IdentityKind.Account, "Target"), true, true, true, new("localhost:1234", "Asia/Tokyo"), "fixture-home");
    var fakeStorage = new FakeStorage(fixtureRoot, []) { Active = original };
    var fakeRuntime = new FakeRuntime([]) { Pending = new(LaunchStatus.Pending, false, null, "fake", "unresolved") };
    var operation = await new SwitchService(fakeStorage, fakeRuntime).SwitchAsync(accountRequest);
    Check(operation.PendingLaunch && fakeStorage.Mutations == 0 && fakeRuntime.StopCalls == 0, "persisted pending launch guards all file mutations");
    fakeRuntime = new FakeRuntime([]) { ThrowPending = true };
    operation = await new SwitchService(fakeStorage, fakeRuntime).SwitchAsync(accountRequest);
    Check(operation.PendingLaunch && fakeStorage.Mutations == 0, "pending-state read failure blocks switching");
    fakeRuntime = new FakeRuntime([]);
    operation = await new SwitchService(fakeStorage, fakeRuntime).SwitchAsync(accountRequest with { Environment = new() });
    Check(operation.Outcome == OperationOutcome.Failed && fakeRuntime.StopCalls == 0 && fakeStorage.Mutations == 0, "invalid launch spec stops before process and file mutation");
    fakeStorage = new FakeStorage(fixtureRoot, []) { Active = original };
    operation = await new SwitchService(fakeStorage, new FakeRuntime([])).SwitchAsync(accountRequest with { Target = original, Launch = false });
    Check(operation.Outcome == OperationOutcome.Unchanged && fakeStorage.Mutations == 0, "same identity without launch remains unchanged");
    fakeStorage = new FakeStorage(fixtureRoot, []) { Active = original, Archived = false };
    operation = await new SwitchService(fakeStorage, new FakeRuntime([])).SwitchAsync(accountRequest);
    Check(operation.Success && operation.RecoveryPaths.Contains(fakeStorage.SnapshotPath), "unarchived source retains sole recovery snapshot");
    fakeStorage = new FakeStorage(fixtureRoot, []) { Active = original, ThrowComplete = true };
    operation = await new SwitchService(fakeStorage, new FakeRuntime([])).SwitchAsync(accountRequest);
    Check(operation.Success && fakeStorage.RestoreCalls == 0 && operation.Warnings.Count > 0 && operation.RecoveryPaths.Contains(fakeStorage.SnapshotPath), "cleanup failure does not rollback confirmed launch");
    fakeStorage = new FakeStorage(fixtureRoot, []) { Active = original, ThrowApply = true };
    fakeRuntime = new FakeRuntime([]);
    operation = await new SwitchService(fakeStorage, fakeRuntime).SwitchAsync(accountRequest);
    Check(operation.Outcome == OperationOutcome.RolledBack && fakeRuntime.LeaseHeldDuringRestore && !fakeRuntime.LeaseActive, "exception rollback holds launch lease through timezone recovery then releases it");
    var tracePrepare = new List<string>();
    fakeStorage = new FakeStorage(fixtureRoot, tracePrepare) { Active = original };
    var prepare = new FakePreparation(tracePrepare) { Throw = true };
    operation = await new SwitchService(fakeStorage, new FakeRuntime(tracePrepare), prepare).SwitchAsync(accountRequest);
    Check(operation.Success && operation.Warnings.Count > 0 && tracePrepare.IndexOf("archive") < tracePrepare.IndexOf("prepare") && tracePrepare.IndexOf("prepare") < tracePrepare.IndexOf("confirm:DesktopAndCli") && tracePrepare.IndexOf("prepare") < tracePrepare.IndexOf("apply"), "profile preparation warns after archive and before stopped recheck and credential copy");
    fakeStorage = new FakeStorage(fixtureRoot, []) { Active = original };
    fakeRuntime = new FakeRuntime([]) { SwitchTimezoneFails = true };
    operation = await new SwitchService(fakeStorage, fakeRuntime).SwitchAsync(accountRequest with { Launch = false });
    Check(operation.Success && fakeRuntime.SwitchTimezoneCalls == 0 && fakeRuntime.RestoreTimezoneCalls == 0, "switch-only does not change system timezone");
    fakeStorage = new FakeStorage(fixtureRoot, []) { Active = original, ThrowArchive = true };
    operation = await new SwitchService(fakeStorage, new FakeRuntime([])).SwitchAsync(accountRequest);
    Check(operation.Outcome == OperationOutcome.Failed && fakeStorage.RestoreCalls == 0 && fakeStorage.ApplyCalls == 0 && operation.RecoveryPaths.Count > 0, "archive failure retains snapshot without overwriting untouched active data");
    fakeStorage = new FakeStorage(fixtureRoot, []) { Active = original };
    fakeRuntime = new FakeRuntime([]) { ConfirmFails = true };
    operation = await new SwitchService(fakeStorage, fakeRuntime).SwitchAsync(accountRequest);
    Check(operation.Outcome == OperationOutcome.Failed && fakeStorage.ApplyCalls == 0 && fakeStorage.RestoreCalls == 0 && fakeStorage.Active == original, "Codex reopened during snapshot blocks apply without restoring over external changes");
    fakeStorage = new FakeStorage(fixtureRoot, []) { Active = original, NeedLogin = true };
    fakeRuntime = new FakeRuntime([]);
    operation = await new SwitchService(fakeStorage, fakeRuntime).SwitchAsync(accountRequest);
    Check(operation.NeedsLogin && operation.LoginTarget == accountRequest.Target && fakeStorage.Mutations == 0 && fakeRuntime.StopCalls == 0, "missing account credentials returns typed original login target");
    fakeStorage = new FakeStorage(fixtureRoot, []) { Active = original };
    fakeRuntime = new FakeRuntime([]);
    var environmentSource = new FakeEnvironmentSource();
    operation = await new SwitchService(fakeStorage, fakeRuntime, environments: environmentSource).SwitchAsync(accountRequest with { Target = original });
    Check(operation.Success && fakeStorage.Mutations == 0 && fakeRuntime.LastScope == ProcessScope.DesktopOnly && environmentSource.LastIdentity == original, "same-identity switch and launch bypasses archiving and preserves CLI");
    Check(fakeRuntime.LeaseCalls == 1 && !fakeRuntime.LeaseActive, "same identity launch uses exactly one ownership lease");

    foreach (var kind in Enum.GetValues<IdentityKind>())
    {
        var active = new IdentityRef(kind, "Current");
        var launchRequest = new LaunchCurrentRequest(true, "fixture-home");
        fakeStorage = new FakeStorage(fixtureRoot, []) { Active = active };
        fakeRuntime = new FakeRuntime([]);
        operation = await new SwitchService(fakeStorage, fakeRuntime, environments: new FakeEnvironmentSource()).LaunchCurrentAsync(launchRequest);
        Check(operation.Success && fakeStorage.Mutations == 0 && fakeRuntime.LastScope == ProcessScope.DesktopOnly, $"{kind} launch-current stops desktop only and changes no identity files");
        fakeRuntime = new FakeRuntime([]) { LaunchResult = new(LaunchStatus.Failed, false, null, "fake", "failed") };
        operation = await new SwitchService(fakeStorage, fakeRuntime, environments: new FakeEnvironmentSource()).LaunchCurrentAsync(launchRequest);
        Check(operation.Outcome == OperationOutcome.Failed && fakeRuntime.RestoreTimezoneCalls == 1 && operation.ActiveIdentity == active, $"{kind} launch-current failure restores timezone");
        fakeRuntime = new FakeRuntime([]) { LaunchResult = new(LaunchStatus.Pending, false, null, "fake", "pending") };
        operation = await new SwitchService(fakeStorage, fakeRuntime, environments: new FakeEnvironmentSource()).LaunchCurrentAsync(launchRequest);
        Check(operation.PendingLaunch && fakeRuntime.RestoreTimezoneCalls == 0, $"{kind} launch-current pending preserves timezone");
    }
    fakeStorage = new FakeStorage(fixtureRoot, []) { Active = null };
    fakeRuntime = new FakeRuntime([]);
    operation = await new SwitchService(fakeStorage, fakeRuntime).LaunchCurrentAsync(new(true, "fixture-home"));
    Check(!operation.Success && fakeRuntime.StopCalls == 0, "unrecognized current identity cannot launch");
    fakeStorage = new FakeStorage(fixtureRoot, []) { Active = original };
    fakeRuntime = new FakeRuntime([]) { LaunchResult = new(LaunchStatus.Confirmed, true, null, "fake", "already running; no environment reapplied") };
    operation = await new SwitchService(fakeStorage, fakeRuntime, environments: new FakeEnvironmentSource()).LaunchCurrentAsync(new(true, "fixture-home"));
    Check(operation.Success && operation.RunningIdentity is null && operation.Warnings.Contains("already running; no environment reapplied"), "already-running launch receipt retains warning and unknown runtime identity");
    fakeRuntime = new FakeRuntime([]) { LaunchResult = new(LaunchStatus.Confirmed, false, 77, "fake", "ownership unconfirmed", RuntimeIdentityConfirmed: false) };
    operation = await new SwitchService(fakeStorage, fakeRuntime, environments: new FakeEnvironmentSource()).LaunchCurrentAsync(new(true, "fixture-home"));
    Check(operation.Success && operation.RunningIdentity is null && operation.Warnings.Contains("ownership unconfirmed"), "unconfirmed launched-process identity stays unknown despite a visible window");
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    fakeStorage = new FakeStorage(fixtureRoot, []) { Active = original };
    operation = await new SwitchService(fakeStorage, new FakeRuntime([])).SwitchAsync(accountRequest, cancellationToken: cancellation.Token);
    Check(operation.Outcome == OperationOutcome.Failed && fakeStorage.Mutations == 0, "cancelled preflight does not mutate");

    // Actual storage migration is exercised only in this newly created project fixture tree.
    foreach (var kind in Enum.GetValues<IdentityKind>())
    foreach (var isolated in new[] { false, true })
    {
        var directory = Path.Combine(fixtureRoot, "actual-" + kind + "-" + isolated);
        var root = Path.Combine(directory, "store"); var home = Path.Combine(directory, "active");
        Directory.CreateDirectory(root); Directory.CreateDirectory(home);
        var profileStore = new FileProfileStore(root);
        var sourceAuth = new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "fixture-original" } };
        profileStore.SaveAccount("Original", sourceAuth);
        File.WriteAllText(Path.Combine(home, "auth.json"), sourceAuth.ToJsonString());
        var target = new IdentityRef(kind, "Target");
        if (kind == IdentityKind.Account) profileStore.SaveAccount("Target", new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "fixture-target" } });
        else profileStore.SaveProvider("Target", "https://fixture.invalid/v1", "fixture-model", "fixture-key");
        var actualStorage = new FileSwitchStorage(new(root, home));
        fakeRuntime = new FakeRuntime([]) { LaunchResult = new(LaunchStatus.Failed, false, null, "fake", "failure") };
        operation = await new SwitchService(actualStorage, fakeRuntime).SwitchAsync(new(target, true, isolated, true, new("localhost:1234", "Asia/Tokyo"), home));
        Check(operation.Outcome == OperationOutcome.RolledBack && actualStorage.ReadActiveIdentity() == original && operation.RecoveryPaths.Any(Directory.Exists), $"actual {kind}/{isolated} launch failure restores file-backed original");
        operation = await new SwitchService(actualStorage, new FakeRuntime([])).SwitchAsync(new(target, false, isolated, false, new(), home));
        Check(operation.Success && actualStorage.ReadActiveIdentity() == target, $"actual {kind}/{isolated} switching writes target files with fake systems");
    }
    count += await SystemProxyApplicationChecks.Run(fixtureRoot);
    count += await MaintenanceChecks.RunAsync(fixtureRoot);
    count += await ManagedCloneApplicationChecks.Run(fixtureRoot);
}
finally { Directory.Delete(fixtureRoot, true); }
Console.WriteLine($"{count} Application isolated checks passed. Only fake processes, fake timezone, fake launch and project fixtures were used.");

sealed class FakeStorage(string root, List<string> trace) : ISwitchStorage
{
    public IdentityRef? Active { get; set; }
    private IdentityRef? before;
    public string SnapshotPath => Path.Combine(root, "fake-snapshot");
    public bool Archived { get; set; } = true;
    public bool ThrowSnapshot { get; set; }
    public bool ThrowApply { get; set; }
    public bool ThrowArchive { get; set; }
    public bool NeedLogin { get; set; }
    public bool ThrowRestore { get; set; }
    public bool ThrowComplete { get; set; }
    public bool LastIsolated { get; private set; }
    public int Mutations { get; private set; }
    public int ApplyCalls { get; private set; }
    public int RestoreCalls { get; private set; }
    public int CompleteCalls { get; private set; }
    public IdentityRef? ReadActiveIdentity() => Active;
    public void Preflight(IdentityRef target, bool isolated) { LastIsolated = isolated; trace.Add("preflight"); if (NeedLogin) throw new StorageException(StorageError.LoginRequired, root, "fixture needs login"); }
    public SwitchSnapshot Snapshot(string action, bool isolated)
    {
        trace.Add("snapshot"); if (ThrowSnapshot) throw new StorageException(StorageError.Io, SnapshotPath, "fixture snapshot failure");
        Mutations++; before = Active; return new(SnapshotPath, new(root, Path.Combine(root, "active")));
    }
    public bool ArchiveCurrent(SwitchSnapshot snapshot, IdentityRef? current, bool isolated) { trace.Add("archive"); Mutations++; if (ThrowArchive) throw new IOException("fixture archive"); return Archived; }
    public SwitchApplyResult ApplyTarget(IdentityRef target, IdentityRef? current, bool isolated)
    {
        trace.Add("apply"); Mutations++; ApplyCalls++; Active = target;
        if (ThrowApply) throw new StorageException(StorageError.Io, root, "fixture partial write");
        return new(target, [], []);
    }
    public void Restore(SwitchSnapshot snapshot, bool isolated) { trace.Add("restore"); RestoreCalls++; if (ThrowRestore) throw new IOException("fixture restore failed"); Active = before; }
    public SwitchCompleteResult Complete(SwitchSnapshot snapshot, bool archived, IReadOnlyList<string>? recoveryPaths = null) { trace.Add("complete"); CompleteCalls++; if (ThrowComplete) throw new IOException("fixture cleanup failed"); return new(!archived, []); }
}
sealed class FakeRuntime(List<string> trace) : IRuntimeOperations
{
    public bool PreflightFails { get; set; }
    public bool PreflightThrows { get; set; }
    public int PreflightCalls { get; private set; }
    public DesktopLaunchRequest? PreparedRequest { get; private set; }
    public Task<DesktopLaunchPreparation> PrepareLaunchAsync(DesktopLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); PreflightCalls++; trace.Add("launch-preflight");
        if (PreflightThrows) throw new IOException("fixture protection unavailable");
        PreparedRequest = request with { Target = new("fixture-Codex.exe", "fixture") };
        return Task.FromResult(new DesktopLaunchPreparation(!PreflightFails, PreparedRequest, PreflightFails ? "fixture protection stale" : ""));
    }
    public bool LeaseActive { get; private set; }
    public int LeaseCalls { get; private set; }
    public bool LeaseHeldDuringRestore { get; private set; }
    public Task<IDisposable?> BeginLaunchAsync(CancellationToken cancellationToken) { LeaseCalls++; LeaseActive = true; return Task.FromResult<IDisposable?>(new FakeLease(this)); }
    public DesktopLaunchResult? Pending { get; set; }
    public bool ThrowPending { get; set; }
    public bool ConfirmFails { get; set; }
    public int? StopFailsAt { get; set; }
    public int StopCalls { get; private set; }
    public int RestoreTimezoneCalls { get; private set; }
    public int LaunchCalls { get; private set; }
    public DesktopLaunchRequest? LastLaunch { get; private set; }
    public int SwitchTimezoneCalls { get; private set; }
    public ProcessScope? LastScope { get; private set; }
    public bool EnsureTimezoneFails { get; set; }
    public bool SwitchTimezoneFails { get; set; }
    public bool ThrowLaunch { get; set; }
    public DesktopLaunchResult LaunchResult { get; set; } = new(LaunchStatus.Confirmed, false, 77, "fake", "fixture-confirmed");
    public Task<DesktopLaunchResult?> CheckPendingAsync(CancellationToken cancellationToken) { if (ThrowPending) throw new IOException("fixture pending"); return Task.FromResult(Pending); }
    public Task<StopResult> StopAsync(ProcessScope scope, CancellationToken cancellationToken) { StopCalls++; LastScope = scope; trace.Add("stop:" + scope); return Task.FromResult(new StopResult(StopCalls != StopFailsAt, true, "fixture stop", [])); }
    public Task<StopResult> ConfirmStoppedAsync(ProcessScope scope, CancellationToken cancellationToken) { trace.Add("confirm:" + scope); return Task.FromResult(new StopResult(!ConfirmFails, ConfirmFails, "fixture recheck", [])); }
    public Task<TimezoneResult> SwitchTimezoneAsync(IdentityRef identity, EnvironmentSettings environment, bool enabled, CancellationToken cancellationToken) { SwitchTimezoneCalls++; trace.Add("switch-timezone"); return Task.FromResult(new TimezoneResult(!SwitchTimezoneFails, true, false, "fixture", "fixture timezone")); }
    public Task<TimezoneResult> EnsureTimezoneForLaunchAsync(IdentityRef identity, EnvironmentSettings environment, bool enabled, CancellationToken cancellationToken) { trace.Add("ensure-timezone"); return Task.FromResult(new TimezoneResult(!EnsureTimezoneFails, true, false, "fixture", "fixture timezone")); }
    public Task<TimezoneResult> RestoreTimezoneAsync(CancellationToken cancellationToken) { RestoreTimezoneCalls++; LeaseHeldDuringRestore = LeaseActive; trace.Add("restore-timezone"); return Task.FromResult(new TimezoneResult(true, true, false, "original", "fixture restored")); }
    public Task<TimezoneResult> ReassertTimezoneAfterLaunchAsync(IdentityRef identity, EnvironmentSettings environment, bool enabled, CancellationToken cancellationToken) { trace.Add("reassert-timezone"); return Task.FromResult(new TimezoneResult(true, false, true, "fixture", "fixture same")); }
    public Task<DesktopLaunchResult> LaunchAsync(DesktopLaunchRequest request, CancellationToken cancellationToken) { LastLaunch = request; LaunchCalls++; trace.Add("launch"); if (ThrowLaunch) throw new IOException("fixture dispatch unknown"); return Task.FromResult(LaunchResult); }
    private sealed class FakeLease(FakeRuntime owner) : IDisposable { public void Dispose() => owner.LeaseActive = false; }
}
sealed class FakePreparation(List<string> trace) : IProfilePreparation
{
    public bool Throw { get; set; }
    public Task<IReadOnlyList<string>> PrepareAsync(IdentityRef target, CancellationToken cancellationToken) { trace.Add("prepare"); if (Throw) throw new IOException("fixture preparation"); return Task.FromResult<IReadOnlyList<string>>([]); }
}
sealed class FakeEnvironmentSource : IIdentityEnvironmentSource
{
    public IdentityRef? LastIdentity { get; private set; }
    public EnvironmentSettings Read(IdentityRef identity) { LastIdentity = identity; return new("localhost:1234", "Asia/Tokyo"); }
}
