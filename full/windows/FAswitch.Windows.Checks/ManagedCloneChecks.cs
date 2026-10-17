using System.Text;
using System.Text.Json;
using FAswitch.Windows;

internal static class ManagedCloneChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var main = new ProcessIdentity(101, 1, "Codex.exe", @"C:\OpenAI\Codex\Codex.exe", 100, true);
        var clone = main with { Pid = 102, CreationTime = 200 };
        var child = new ProcessIdentity(103, 102, "codex.exe", @"C:\OpenAI\Codex\bin\hash\codex.exe", 201);
        var externalCli = child with { Pid = 104, ParentPid = 1, CreationTime = 202 };
        var system = new FakeProcessSystem([main, clone, child, externalCli]) { Ready = true, AllowStop = true };
        var store = new FakeOwnedStore();
        var scope = new ManagedCloneProcessScope(system, store);
        var processes = new CodexProcessService(system, new FakeClock(), scope);
        check(!processes.IsDesktopReady(), "unowned main window cannot confirm managed clone readiness");
        check(scope.RegisterRoot(clone, "fixture"), "clone root ownership requires PID creation and executable");
        check(store.Value.Any(record => record.Identity.Pid == child.Pid), "clone root registration captures the same verified process tree snapshot");
        check(processes.Discover(ProcessScope.DesktopAndCli).Select(item => item.Pid).Order().SequenceEqual([102, 103]), "clone scope includes only owned root and child CLI");
        check(processes.IsDesktopReady(), "owned clone window can confirm readiness independently of main");
        check(store.Value.Any(record => record.Identity.Pid == child.Pid), "child ownership persists before root exit");
        var originals = new CodexProcessService(system, new FakeClock(), scope, excludeManagedClone: true);
        check(originals.Discover(ProcessScope.DesktopAndCli).Select(item => item.Pid).SequenceEqual([101]), "explicit original maintenance excludes clone and unrelated CLI");
        system.Items.Remove(clone);
        check(processes.Discover(ProcessScope.DesktopAndCli).Select(item => item.Pid).SequenceEqual([103]), "owned child remains selected after root exits");
        var stopped = await processes.StopAsync(ProcessScope.DesktopAndCli);
        check(stopped.Success && system.Items.Contains(main) && system.Items.Contains(externalCli), "clone stop preserves main and external CLI");
        system.Items.Add(clone with { CreationTime = 999 });
        check(processes.ConfirmStopped(ProcessScope.DesktopAndCli).Success, "reused PID cannot transfer clone ownership");
        system.Items.RemoveAll(item => item.Pid == clone.Pid);
        system.Items.Add(clone with { CreationTime = 0 });
        check(!processes.ConfirmStopped(ProcessScope.DesktopAndCli).Success, "unreadable owned PID blocks runtime cleanup");
        var externalOnly = new FakeProcessSystem([main]);
        var emptyStore = new FakeOwnedStore();
        var emptyScope = new ManagedCloneProcessScope(externalOnly, emptyStore);
        var lifecycle = new OwnedProcessLifecycleService(externalOnly, new FakeClock(), emptyStore, new SystemTimezoneService(new FakeTimezoneSystem(), new FakeTimezoneStore()), cloneScope: emptyScope);
        check(!lifecycle.TrackConfirmedDesktop(new(LaunchStatus.Confirmed, false, main.Pid, "fixture", ""), [], 0, "fixture") && emptyStore.Value.Count == 0,
            "managed launch ownership never adopts a newly opened original desktop fallback");
        var orphanSystem = new FakeProcessSystem([main, child]);
        var orphanStore = new FakeOwnedStore { Value = [new(clone, OwnedProcessKind.Desktop, "fixture", "")] };
        var orphanScope = new ManagedCloneProcessScope(orphanSystem, orphanStore);
        check(!new CodexProcessService(orphanSystem, new FakeClock(), orphanScope).ConfirmStopped(ProcessScope.DesktopAndCli).Success,
            "unobserved orphan child blocks clean runtime confirmation");
        var orphanLifecycle = new OwnedProcessLifecycleService(orphanSystem, new FakeClock(), orphanStore, new SystemTimezoneService(new FakeTimezoneSystem(), new FakeTimezoneStore()), cloneScope: orphanScope);
        check(!(await orphanLifecycle.AdoptAsync()).Success && orphanStore.Value.Count == 1,
            "adoption preserves dead-parent evidence when new orphan ownership is unknown");

        var root = Path.Combine(Path.GetTempPath(), "ChunsonSwitch-CloneChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var home = Path.Combine(root, "runtime", "codex-home");
            var desktop = Path.Combine(root, "runtime", "electron");
            var options = new ManagedCloneLaunchOptions(home, desktop, scope);
            var request = new DesktopLaunchRequest(null, ["--proxy-server=http://127.0.0.1:7890", "--user-data-dir=old"],
                new Dictionary<string, string> { ["CODEX_HOME"] = home, ["HTTP_PROXY"] = "http://127.0.0.1:7890", ["CODEX_CHROME_USER_DATA_DIR"] = "old-main" });
            var bound = options.Bind(request);
            check(bound.Environment!["CODEX_HOME"] == home && bound.Environment["CODEX_SQLITE_HOME"] == home && bound.Environment["CODEX_ELECTRON_USER_DATA_PATH"] == desktop,
                "clone binds independent Codex SQLite and Electron roots");
            check(bound.Arguments.Count(arg => arg.StartsWith("--user-data-dir=")) == 1 && bound.Arguments.Contains("--user-data-dir=" + desktop), "clone binds one stable user data directory");
            var separate = options.Bind(request with { Arguments = ["--user-data-dir", "old-separated", "--accept-lang=en-US"] });
            check(!separate.Arguments.Contains("old-separated") && separate.Arguments.Contains("--accept-lang=en-US"), "clone removes split-form user data override and its value");
            check(!bound.Environment.ContainsKey("CODEX_CHROME_USER_DATA_DIR"), "clone removes inherited Chrome data redirect");
            bool rejected = false;
            try { options.Bind(request with { Environment = new Dictionary<string, string> { ["CODEX_HOME"] = root, ["HTTP_PROXY"] = "http://127.0.0.1:7890" } }); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "clone refuses a caller home outside managed path");
            rejected = false;
            try { options.Bind(request with { Environment = new Dictionary<string, string> { ["CODEX_HOME"] = home } }); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "clone refuses empty proxy without DIRECT fallback");
            var preflightCalls = 0;
            var launchSystem = new WindowsDesktopLaunchSystem(Path.Combine(root, "launch-state"), system, new FakeCommands(), new FakeClock(),
                options with { ProtectionPreflight = (_, _, _) => { preflightCalls++; return Task.FromResult<string?>("fixture protection stale"); } });
            var receipt = await launchSystem.StartAsync(new(Path.Combine(root, "never-launched.exe"), ""), request, default);
            check(receipt.Status == LaunchStatus.Failed && preflightCalls == 1 && !Directory.Exists(Path.Combine(root, "launch-state")), "stale protection rejects before creating process or launch intent");
            var failingLaunch = new WindowsDesktopLaunchSystem(Path.Combine(root, "launch-state"), system, new FakeCommands(), new FakeClock(),
                options with { ProtectionPreflight = (_, _, _) => throw new IOException("fixture failed policy read") });
            receipt = await failingLaunch.StartAsync(new(Path.Combine(root, "never-launched.exe"), ""), request, default);
            check(receipt.Status == LaunchStatus.Failed && !Directory.Exists(Path.Combine(root, "launch-state")), "unreadable protection fails closed before dispatch");
            var earlyHome = Path.Combine(root, "early", "home"); var earlyDesktop = Path.Combine(root, "early", "desktop");
            var earlyRequest = request with { Environment = new Dictionary<string, string> { ["CODEX_HOME"] = earlyHome, ["HTTP_PROXY"] = "http://127.0.0.1:7890" } };
            var protectionReads = 0; var rejectProtection = false;
            var earlyOptions = options with { CodexHome = earlyHome, DesktopRoot = earlyDesktop,
                ProtectionPreflight = (_, _, _) => { protectionReads++; return Task.FromResult<string?>(rejectProtection ? "fixture stale" : null); } };
            var earlyLaunch = new WindowsDesktopLaunchSystem(Path.Combine(root, "early-state"), system, new FakeCommands(), new FakeClock(), earlyOptions);
            var fixtureExecutable = Path.Combine(root, "Codex.exe"); File.WriteAllText(fixtureExecutable, "inert fixture; never execute");
            var fixtureTarget = new DesktopTarget(fixtureExecutable, "OpenAI.Codex_fixture!App");
            var ready = await earlyLaunch.PrepareAsync(fixtureTarget, earlyRequest, default);
            check(ready.Success && protectionReads == 1 && !Directory.Exists(earlyHome) && !Directory.Exists(earlyDesktop)
                && !Directory.Exists(Path.Combine(root, "early-state")), "native early preflight is read-only and does not create runtime or launch state");
            // Prevent any process dispatch while exercising reuse of the exact prepared request.
            File.Delete(fixtureExecutable);
            receipt = await earlyLaunch.StartAsync(fixtureTarget, ready.Request, default);
            check(receipt.Status == LaunchStatus.Failed && protectionReads == 1, "matching early check is consumed without a second protection query");
            rejectProtection = true;
            receipt = await earlyLaunch.StartAsync(fixtureTarget, ready.Request, default);
            check(receipt.Status == LaunchStatus.Failed && protectionReads == 2, "a prepared check cannot be reused for a second launch");
            rejectProtection = false; File.WriteAllText(fixtureExecutable, "inert fixture; never execute");
            ready = await earlyLaunch.PrepareAsync(fixtureTarget, earlyRequest, default);
            rejectProtection = true; var readsBeforeChange = protectionReads;
            var changedEnvironment = new Dictionary<string, string>(ready.Request.Environment!) { ["HTTP_PROXY"] = "http://127.0.0.1:7900" };
            receipt = await earlyLaunch.StartAsync(fixtureTarget, ready.Request with { Environment = changedEnvironment }, default);
            check(receipt.Status == LaunchStatus.Failed && protectionReads == readsBeforeChange + 1, "changed proxy cannot reuse an earlier protection check");
            rejectProtection = false; ready = await earlyLaunch.PrepareAsync(fixtureTarget, earlyRequest, default);
            rejectProtection = true; readsBeforeChange = protectionReads;
            var otherSystem = new WindowsDesktopLaunchSystem(Path.Combine(root, "other-state"), system, new FakeCommands(), new FakeClock(), earlyOptions);
            receipt = await otherSystem.StartAsync(fixtureTarget, ready.Request, default);
            check(receipt.Status == LaunchStatus.Failed && protectionReads == readsBeforeChange + 1, "prepared evidence cannot cross native launch systems");
            rejectProtection = false; ready = await earlyLaunch.PrepareAsync(fixtureTarget, earlyRequest, default);
            rejectProtection = true; readsBeforeChange = protectionReads;
            receipt = await earlyLaunch.StartAsync(fixtureTarget with { Executable = fixtureExecutable + ".changed" }, ready.Request, default);
            check(receipt.Status == LaunchStatus.Failed && protectionReads == readsBeforeChange + 1, "changed executable cannot reuse an earlier protection check");
            var managedSystem = new FakeProcessSystem([main, clone]) { AllowStop = true };
            var managedStore = new FakeOwnedStore(); var managedScope = new ManagedCloneProcessScope(managedSystem, managedStore); managedScope.RegisterRoot(clone, "fixture");
            var failedInspector = new FixtureInspector(false);
            var verifiedLaunch = new WindowsDesktopLaunchSystem(Path.Combine(root, "verified-state"), managedSystem, new FakeCommands(), new FakeClock(), options with { ProcessScope = managedScope, RuntimeInspector = failedInspector });
            receipt = await verifiedLaunch.VerifyRuntimeAsync(new(LaunchStatus.Confirmed, clone.Pid, "fixture", ""), request, default);
            check(receipt.Status == LaunchStatus.Failed && managedSystem.Items.Contains(main) && managedSystem.Items.All(item => item.Pid != clone.Pid), "unverified actual Codex environment stops only managed clone and never confirms main");
            managedSystem.Items.Add(clone); managedScope.RegisterRoot(clone, "fixture"); managedSystem.AllowStop = false;
            receipt = await verifiedLaunch.VerifyRuntimeAsync(new(LaunchStatus.Confirmed, clone.Pid, "fixture", ""), request, default);
            check(receipt.Status == LaunchStatus.Pending, "unverified runtime with unconfirmed clone exit remains pending");
            var successfulInspector = new FixtureInspector(true);
            verifiedLaunch = new WindowsDesktopLaunchSystem(Path.Combine(root, "verified-state"), managedSystem, new FakeCommands(), new FakeClock(), options with { ProcessScope = managedScope, RuntimeInspector = successfulInspector });
            receipt = await verifiedLaunch.VerifyRuntimeAsync(new(LaunchStatus.Confirmed, clone.Pid, "fixture", ""), request, default);
            check(receipt.Status == LaunchStatus.Confirmed && successfulInspector.Calls == 1, "managed launch confirmation requires actual runtime inspector evidence");
            var portFile = Path.Combine(desktop, "DevToolsActivePort"); File.WriteAllText(portFile, "12345\n/devtools/browser/fixture\n");
            var ownerCommands = new FakeCommands { Response = new(0, "[101]") };
            var ownerInspection = await new ManagedCodexRuntimeInspector(ownerCommands).InspectAsync(options with { ProcessScope = managedScope },
                request with { Environment = new Dictionary<string, string> { ["TZ"] = "Asia/Tokyo" }, ExpectedEgressIp = "203.0.113.7" }, default);
            check(!ownerInspection.Verified && ownerCommands.Calls == 1, "CDP port owned by main is rejected before any websocket connection");
            managedSystem.AllowStop = true; managedSystem.Ready = true;
            var pendingRoot = Path.Combine(root, "pending-state"); var nonce = Guid.NewGuid().ToString("N"); var pendingDirectory = Path.Combine(pendingRoot, nonce); Directory.CreateDirectory(pendingDirectory);
            File.WriteAllText(Path.Combine(pendingDirectory, "state.json"), JsonSerializer.Serialize(new { nonce, status = "started", pid = clone.Pid, creation_time = clone.CreationTime, executable = clone.Executable, timezone = "Asia/Tokyo", expected_ip = "203.0.113.7" }));
            var recoveredLaunch = new WindowsDesktopLaunchSystem(pendingRoot, managedSystem, new FakeCommands(), new FakeClock(), options with { ProcessScope = managedScope, RuntimeInspector = failedInspector });
            var recovered = await recoveredLaunch.CheckPendingAsync(default);
            check(recovered?.Status == LaunchStatus.Failed && managedSystem.Items.Contains(main), "recovered pending clone must pass actual runtime verification before completion");
            managedSystem.Items.Add(clone); managedScope.RegisterRoot(clone, "fixture");
            var lostState = await recoveredLaunch.VerifyRuntimeAsync(new(LaunchStatus.Confirmed, clone.Pid, "fixture", "", Path.Combine(pendingRoot, Guid.NewGuid().ToString("N"))), request, default);
            check(lostState.Status == LaunchStatus.Pending && lostState.Message.Contains("fixture actual runtime evidence") && lostState.Message.Contains("分身已停止")
                && lostState.Message.Contains("启动记录未能更新") && managedSystem.Items.Contains(main), "missing launch record after runtime rejection preserves the real failure and confirmed stop instead of throwing a window error");
            var externalState = Path.Combine(root, "external-state"); var externalNonce = Guid.NewGuid().ToString("N"); var externalStateDirectory = Path.Combine(externalState, externalNonce); Directory.CreateDirectory(externalStateDirectory);
            var externalStateFile = Path.Combine(externalStateDirectory, "state.json"); File.WriteAllText(externalStateFile, JsonSerializer.Serialize(new { nonce = externalNonce, status = "complete" }));
            var linkedStateRoot = Path.Combine(root, "linked-state-root"); LinkFixtures.CreateDirectory(root, linkedStateRoot, externalState, "linked launch state root");
            var linkedLaunch = new WindowsDesktopLaunchSystem(linkedStateRoot, managedSystem, new FakeCommands(), new FakeClock());
            check((await linkedLaunch.CheckPendingAsync(default))?.Status == LaunchStatus.Pending && File.Exists(externalStateFile), "linked launch state root is rejected before reading or recursively deleting external state");
            Directory.Delete(linkedStateRoot);
            var stateRoot = Path.Combine(root, "state-links"); Directory.CreateDirectory(stateRoot);
            var nonceLink = Path.Combine(stateRoot, externalNonce); LinkFixtures.CreateDirectory(root, nonceLink, externalStateDirectory, "linked launch nonce directory");
            linkedLaunch = new WindowsDesktopLaunchSystem(stateRoot, managedSystem, new FakeCommands(), new FakeClock());
            check((await linkedLaunch.CheckPendingAsync(default))?.Status == LaunchStatus.Pending && File.Exists(externalStateFile), "linked nonce directory cannot escape explicit launch state scope");
            Directory.Delete(nonceLink); Directory.CreateDirectory(nonceLink);
            if (LinkFixtures.TryCreateFile(root, Path.Combine(nonceLink, "state.json"), externalStateFile, "linked state JSON is rejected before reading or deleting evidence"))
                check((await linkedLaunch.CheckPendingAsync(default))?.Status == LaunchStatus.Pending && File.Exists(externalStateFile), "linked state JSON is rejected before reading or deleting evidence");
            File.Delete(Path.Combine(nonceLink, "state.json")); File.WriteAllText(Path.Combine(nonceLink, "state.json"), JsonSerializer.Serialize(new { nonce = externalNonce, status = "started" }));
            var nestedLink = Path.Combine(nonceLink, "nested-link"); LinkFixtures.CreateDirectory(root, nestedLink, externalStateDirectory, "nested launch state link");
            var deleteRejected = false;
            try { linkedLaunch.Complete(new(LaunchStatus.Confirmed, null, "fixture", "", nonceLink)); }
            catch (FAswitch.Core.StorageException) { deleteRejected = true; }
            check(deleteRejected && File.Exists(externalStateFile), "recursive state cleanup rejects nested links and preserves external data");
            Directory.Delete(nestedLink);
            var local = Path.Combine(root, "OpenAI", "Codex"); Directory.CreateDirectory(local);
            var executable = Path.Combine(local, "Codex.exe"); File.WriteAllText(executable, "fixture-exe");
            check(WindowsDesktopLaunchSystem.FindUnpackagedTarget(root)?.Executable == executable, "unpackaged Codex discovery has no hardcoded version");
            var cliRoot = Path.Combine(local, "bin", "changed-version"); Directory.CreateDirectory(cliRoot); var cliPath = Path.Combine(cliRoot, "codex.exe"); File.WriteAllText(cliPath, "fixture-cli");
            var manager = Path.Combine(root, "ChunsonSwitch.exe"); File.WriteAllText(manager, "fixture-manager");
            var targets = DirectConnectionProtectionTargets.Discover(new(executable, ""), manager, root);
            check(targets.Contains(cliPath) && targets.Contains(manager) && targets.Contains(executable), "protection discovers exact desktop CLI and manager targets");
            var runner = new ProtectionFakeRunner(); var service = new DirectConnectionProtectionService(runner);
            var protectionRequest = new DirectConnectionProtectionRequest(targets, "http://127.0.0.1:7890");
            runner.Status = DirectConnectionProtectionStatus.PreviewReady;
            var result = await service.PreviewAsync(protectionRequest);
            check(result.Success && result.CanConfigure && !result.RulesVerified && !result.NetworkVerified && runner.Actions.SequenceEqual([DirectConnectionProtectionAction.Preview]), "protection preview permits configuration without applying or claiming actual blocking");
            runner.Status = DirectConnectionProtectionStatus.Configured; runner.Verified = true;
            var applyCount = runner.Actions.Count;
            result = await service.ApplyAsync(protectionRequest);
            check(!result.Success && !result.RulesVerified && runner.Actions.Count == applyCount, "retired firewall apply never invokes runner or requests elevation");
            var count = runner.Actions.Count;
            result = await service.ApplyAsync(protectionRequest with { LocalProxy = "http://192.168.1.1:7890" });
            check(result.Status == DirectConnectionProtectionStatus.Failed && runner.Actions.Count == count, "protection rejects remote proxy before requesting elevation");
            runner.Status = DirectConnectionProtectionStatus.ProtectionStale; runner.Verified = false;
            result = await service.PreviewAsync(protectionRequest);
            check(result.Status == DirectConnectionProtectionStatus.ProtectionStale && !result.Success && result.CanConfigure, "stale protection permits explicit repair without claiming success");
            foreach (var rejectedStatus in new[] { DirectConnectionProtectionStatus.Failed, DirectConnectionProtectionStatus.Uncertain, DirectConnectionProtectionStatus.Restored })
                check(!new DirectConnectionProtectionResult(rejectedStatus, "fixture", targets).CanConfigure, "unconfirmed or restored result cannot authorize apply " + rejectedStatus);
            check(!new DirectConnectionProtectionResult(DirectConnectionProtectionStatus.Configured, "fixture", targets).CanConfigure,
                "unverified configured result cannot authorize apply");
            runner.NetworkVerified = true;
            result = await service.PreviewAsync(protectionRequest);
            check(result.Status == DirectConnectionProtectionStatus.Uncertain, "service refuses a report claiming unperformed network verification");
            runner.NetworkVerified = false; runner.Status = DirectConnectionProtectionStatus.Restored;
            result = await service.RestoreAsync(new([], ""));
            check(result.Success && runner.Actions.Last() == DirectConnectionProtectionAction.Restore, "own firewall rules can restore without current proxy or installed path");
            runner.Status = DirectConnectionProtectionStatus.Disabled; runner.Verified = false;
            result = await service.InspectAsync(new([], ""));
            check(result.Success && result.Status == DirectConnectionProtectionStatus.Disabled && !result.RulesVerified
                && runner.Actions.Last() == DirectConnectionProtectionAction.Inspect, "read-only disabled state needs no proxy or installed client");
            runner.Verified = true;
            result = await service.InspectAsync(new([], ""));
            check(result.Status == DirectConnectionProtectionStatus.Uncertain, "disabled report cannot also claim configured rules");
            runner.Status = DirectConnectionProtectionStatus.Configured;
            result = await service.InspectAsync(protectionRequest with { LocalProxy = "" });
            check(result.Success && result.RulesVerified, "inspection validates configured rules without resolving a proxy");
            var encoded = WindowsDirectConnectionProtectionRunner.BuildEncodedCommand(DirectConnectionProtectionAction.Apply, protectionRequest, Path.Combine(root, "quote'fixture", "report.json"));
            var source = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
            check(source.Contains("quote''fixture") && source.Contains("Get-FileHash") && source.Length < 4096, "elevated command escapes paths verifies payload and avoids command length limit");
            var scriptDirectory = Path.Combine(AppContext.BaseDirectory, "script-fixtures"); Directory.CreateDirectory(scriptDirectory);
            File.WriteAllText(Path.Combine(scriptDirectory, "direct-connection-protection.ps1"), WindowsDirectConnectionProtectionRunner.Script);
            check(!WindowsDirectConnectionProtectionRunner.Script.Contains("New-NetFirewallRule") && WindowsDirectConnectionProtectionRunner.Script.Contains("feature-retired"), "legacy protection script cannot create firewall rules");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ProtectionFakeRunner : IDirectConnectionProtectionRunner
    {
        public DirectConnectionProtectionStatus Status { get; set; }
        public bool Verified { get; set; }
        public bool NetworkVerified { get; set; }
        public List<DirectConnectionProtectionAction> Actions { get; } = [];
        public Task<DirectConnectionProtectionExecution> ExecuteAsync(DirectConnectionProtectionAction action, DirectConnectionProtectionRequest request, CancellationToken token)
        {
            Actions.Add(action);
            return Task.FromResult(new DirectConnectionProtectionExecution(0, JsonSerializer.Serialize(new { schema = 1, action = action.ToString(), status = Status.ToString(), message = "fixture", programPaths = request.ProgramPaths, rulesVerified = Verified, networkVerified = NetworkVerified }), "fixture-report"));
        }
    }
    private sealed class FixtureInspector(bool verified) : IManagedCodexRuntimeInspector
    {
        public int Calls { get; private set; }
        public Task<ManagedRuntimeInspection> InspectAsync(ManagedCloneLaunchOptions options, DesktopLaunchRequest request, CancellationToken token)
        { Calls++; return Task.FromResult(new ManagedRuntimeInspection(verified, "fixture actual runtime evidence")); }
    }
}
