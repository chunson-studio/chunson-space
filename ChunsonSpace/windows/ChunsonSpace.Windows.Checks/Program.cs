using System.Text;
using System.Text.Json;
using ChunsonSpace.Windows;

var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks++; Console.WriteLine("PASS " + name); }
if (args is ["--launch-recovery-only"])
{
    await ManagedLaunchRecoveryChecks.RunAsync(Check);
    Console.WriteLine($"{checks} managed launch recovery checks passed. No real process, account, network, proxy, or timezone actions were performed.");
    return;
}
var desktop = new ProcessIdentity(10, 1, "Codex.exe", @"C:\Program Files\WindowsApps\OpenAI.Codex_1_x64__pub\app\Codex.exe", 100);
var cli = new ProcessIdentity(20, 1, "codex.exe", @"D:\cli\codex.exe", 101);
var unrelated = new ProcessIdentity(30, 1, "ChatGPT.exe", @"D:\ChatGPT\ChatGPT.exe", 102);
var child = new ProcessIdentity(11, 10, "helper.exe", @"D:\helper.exe", 110);
var system = new FakeProcessSystem([desktop, cli, unrelated, child]);
var clock = new FakeClock();
var processes = new CodexProcessService(system, clock);
Check(processes.Discover(ProcessScope.DesktopOnly).Select(p => p.Pid).SequenceEqual([10]), "desktop excludes CLI and unrelated ChatGPT");
Check(processes.Discover(ProcessScope.DesktopAndCli).Count == 2, "switch scope includes CLI");
Check(!CodexProcessService.IsDesktop(desktop with { Executable = @"D:\other\Codex.exe" }), "desktop requires known installation path");
Check(CodexProcessService.IsDesktop(desktop with { Executable = @"D:\relocated\app\Codex.exe", RegisteredDesktop = true }), "manifest-registered relocated desktop remains identifiable");
system.AllowStop = true;
var stop = await processes.StopAsync(ProcessScope.DesktopOnly);
Check(stop.Success && stop.WasRunning, "desktop close confirmed");
Check(system.Items.Contains(cli) && system.Items.Contains(unrelated) && !system.Items.Contains(child), "desktop descendants closed without CLI or unrelated application");
stop = await processes.StopAsync(ProcessScope.DesktopAndCli);
Check(stop.Success && !system.Items.Contains(cli), "switch close includes CLI");
stop = await processes.StopAsync(ProcessScope.DesktopAndCli);
Check(stop.Success && !stop.WasRunning, "already stopped result");
system = new FakeProcessSystem([desktop]) { AllowStop = false };
stop = await new CodexProcessService(system, new FakeClock()).StopAsync(ProcessScope.DesktopOnly);
Check(!stop.Success && stop.RemainingPids.SequenceEqual([10]), "stop failure never reports success");
Check(system.StopCalls.Any(p => p.Force), "graceful close escalates only captured identities");
system = new FakeProcessSystem([desktop, child]) { AllowStop = true, RefusePid = child.Pid };
stop = await new CodexProcessService(system, new FakeClock()).StopAsync(ProcessScope.DesktopOnly);
Check(!stop.Success && stop.RemainingPids.SequenceEqual([child.Pid]) && !system.Items.Contains(desktop), "surviving non-Codex child blocks switch after parent exits");
system = new FakeProcessSystem([desktop]) { ThrowSnapshot = true };
stop = await new CodexProcessService(system, new FakeClock()).StopAsync(ProcessScope.DesktopOnly);
Check(!stop.Success, "incomplete snapshot blocks identity mutation");
system = new FakeProcessSystem([desktop, child]) { AllowStop = true, ReplaceParentOnSecondSnapshot = true };
stop = await new CodexProcessService(system, new FakeClock()).StopAsync(ProcessScope.DesktopOnly);
Check(system.StopCalls.Count == 0 && system.Items.Contains(child), "PID identity change does not terminate replacement or its children");
system = new FakeProcessSystem([desktop with { CreationTime = 0 }]) { AllowStop = true };
stop = await new CodexProcessService(system, new FakeClock()).StopAsync(ProcessScope.DesktopOnly);
Check(!stop.Success, "unreadable process identity remains blocking");

var inherited = new Dictionary<string, string> { ["GM_TOKEN"] = "fixture", ["GPT Key"] = "fixture", ["API_KEY"] = "fixture", ["PATH"] = "fixture-path", ["ALL_PROXY"] = "socks5://old:1", ["HTTP_PROXY"] = "http://old:2" };
var spec = DesktopLaunchSpec.Build("127.0.0.1:7897", "UTC", "ja-JP", @"D:\隔离\home", true, inherited);
Check(spec.Success && spec.Environment!["TZ"] == "UTC", "UTC uses shared timezone validation");
Check(spec.Arguments.SequenceEqual(["--proxy-server=http://127.0.0.1:7897", "--accept-lang=ja-JP"]), "proxy and locale arguments");
Check(!spec.Environment!.ContainsKey("GM_TOKEN") && !spec.Environment.ContainsKey("GPT Key") && !spec.Environment.ContainsKey("API_KEY"), "credential environment removed");
Check(!spec.Environment.ContainsKey("ALL_PROXY") && spec.Environment["HTTPS_PROXY"] == "http://127.0.0.1:7897", "bound proxy replaces inherited proxy");
Check(!DesktopLaunchSpec.Build("", "", "", "home", true, inherited).Success, "account desktop requires proxy");
Check(!DesktopLaunchSpec.Build("socks5://localhost:7897", "", "", "home", true, inherited).Success, "desktop rejects SOCKS");
spec = DesktopLaunchSpec.Build("", "", "", "fixture-home", false, inherited);
Check(spec.Success && spec.Environment!["CODEX_HOME"] == "fixture-home" && !spec.Environment.ContainsKey("GM_TOKEN"), "provider launch always carries explicit home and sanitized environment");
spec = DesktopLaunchSpec.Build("localhost:1234", "UTC", "bad en-US", "home", true, inherited);
Check(!spec.Arguments.Any(p => p.StartsWith("--accept-lang=")), "locale cannot match only a suffix");
Check(WindowsDesktopLaunchSystem.JoinArguments(["a b", "x\"y", "end\\"]) == "\"a b\" \"x\\\"y\" \"end\\\\\"", "Windows argument quoting preserves whitespace quotes and trailing slash");

var request = new DesktopLaunchRequest(new("fixture", "fixture"), [], inherited, 1200);
var launch = new FakeLaunchSystem { Ready = true };
var launched = await new DesktopLaunchService(launch, new FakeClock()).LaunchAsync(request);
Check(launched.Success && launched.AlreadyRunning && launch.Starts == 0, "existing window does not reapply environment");
launch = new FakeLaunchSystem { Receipt = new(LaunchStatus.Failed, null, "fake", "rejected") };
launched = await new DesktopLaunchService(launch, new FakeClock()).LaunchAsync(request);
Check(launched.Status == LaunchStatus.Failed && launch.Completes == 0, "confirmed process rejection is failure");
launch = new FakeLaunchSystem { Receipt = new(LaunchStatus.Pending, null, "fake", "pending", "fixture-state") };
launched = await new DesktopLaunchService(launch, new FakeClock()).LaunchAsync(request);
Check(launched.PendingLaunch && !launched.Success && launched.StateDirectory == "fixture-state", "package pending retains recovery state");
launch = new FakeLaunchSystem { Receipt = new(LaunchStatus.Confirmed, 77, "fake", "started") };
launched = await new DesktopLaunchService(launch, new FakeClock()).LaunchAsync(request);
Check(launched.PendingLaunch && launch.Completes == 0, "created background process does not imply desktop success");
launch = new FakeLaunchSystem { ShowWindowAfterStart = true };
clock = new FakeClock();
launched = await new DesktopLaunchService(launch, clock).LaunchAsync(request);
Check(launched.Success && clock.Milliseconds >= 500 && launch.Completes == 1, "visible desktop window must remain stable");
launch = new FakeLaunchSystem { ThrowAfterDispatch = true };
launched = await new DesktopLaunchService(launch, new FakeClock()).LaunchAsync(request);
Check(launched.PendingLaunch && launch.Starts == 1, "exception after dispatch never becomes confirmed failure");
launch = new FakeLaunchSystem { Pending = new(LaunchStatus.Pending, null, "fake", "previous") };
launched = await new DesktopLaunchService(launch, new FakeClock()).LaunchAsync(request);
Check(launched.PendingLaunch && launch.Starts == 0, "persisted uncertain request blocks another launch");
launch = new FakeLaunchSystem { ThrowWindowAfterDispatch = true };
launched = await new DesktopLaunchService(launch, new FakeClock()).LaunchAsync(request);
Check(launched.PendingLaunch, "window enumeration failure after dispatch keeps recovery");
Check(launched.Message.Contains("确认桌面窗口") && launched.Message.Contains("IOException"), "window enumeration error reports its actual stage and safe exception type");
launch = new FakeLaunchSystem { ShowWindowAfterStart = true, ThrowRuntime = true };
launched = await new DesktopLaunchService(launch, new FakeClock()).LaunchAsync(request);
Check(launched.PendingLaunch && launched.Message.Contains("验证分身运行环境") && !launched.Message.Contains("无法确认桌面窗口"), "runtime error after a stable window is not mislabeled as a missing window");
launch = new FakeLaunchSystem { ShowWindowAfterStart = true, ThrowComplete = true, Receipt = new(LaunchStatus.Confirmed, 77, "fake", "started", "fixture-state") };
launched = await new DesktopLaunchService(launch, new FakeClock()).LaunchAsync(request);
Check(launched.PendingLaunch && launched.Message.Contains("桌面窗口和运行环境已确认") && launched.StateDirectory == "fixture-state" && launch.Starts == 1,
    "completion record failure preserves evidence and the confirmed window without another start");

var timezoneSystem = new FakeTimezoneSystem { Current = "Original" };
var store = new FakeTimezoneStore { FailWrite = true };
var timezone = new SystemTimezoneService(timezoneSystem, store);
var tz = await timezone.SwitchAsync("fixture", "Asia/Tokyo", true);
Check(!tz.Success && timezoneSystem.SetCalls == 0, "recovery record write failure prevents system timezone change");
store.FailWrite = false;
tz = await timezone.EnsureForLaunchAsync("fixture", "Asia/Tokyo", true);
Check(tz.Success && tz.Changed && timezoneSystem.Current == "Tokyo" && store.Value!.Saved == "Original", "timezone launch gate rereads target");
tz = await timezone.SwitchAsync("second", "America/Chicago", true);
Check(tz.Success && store.Value!.Saved == "Original" && store.Value.Target == "Chicago", "second managed timezone preserves original baseline");
tz = await timezone.RestoreAsync();
Check(tz.Success && tz.Skipped && timezoneSystem.Current == "Chicago", "legacy restore preserves selected timezone without resetting baseline");
await timezone.SwitchAsync("fixture", "Asia/Tokyo", true);
timezoneSystem.Current = "UserChoice";
var previousSets = timezoneSystem.SetCalls;
tz = await timezone.RestoreAsync();
Check(tz.Success && tz.Skipped && timezoneSystem.SetCalls == previousSets && timezoneSystem.Current == "UserChoice", "restore preserves manual user timezone");
await timezone.SwitchAsync("fixture", "Asia/Tokyo", true);
timezoneSystem.ReadFails = true;
tz = await timezone.RestoreAsync();
Check(tz.Success && tz.Skipped && store.Value!.Target == "Tokyo", "disabled restoration never needs to read or change current timezone");
timezoneSystem.ReadFails = false;
timezoneSystem.ApplySet = false;
tz = await timezone.RestoreAsync();
Check(tz.Success && tz.Skipped && store.Value!.Target == "Tokyo", "legacy restoration record is inert even when system writes are unavailable");
timezoneSystem.Current = "Original";
store.Value = null;
tz = await timezone.EnsureForLaunchAsync("fixture", "Asia/Tokyo", true);
Check(!tz.Success && timezoneSystem.Current == "Original", "command exit success without changed timezone cannot pass launch gate");
timezoneSystem.ApplySet = true;
store.Value = new("Original", "Tokyo", "fixture", "Asia/Tokyo", "fixture");
timezoneSystem.Current = "UserChoice";
previousSets = timezoneSystem.SetCalls;
tz = await timezone.ReassertAfterLaunchAsync("fixture", "Asia/Tokyo", true);
Check(tz.Success && timezoneSystem.SetCalls == previousSets, "post-launch reassert does not overwrite third timezone");
timezoneSystem.Current = "Original";
tz = await timezone.ReassertAfterLaunchAsync("fixture", "Asia/Tokyo", true);
Check(tz.Success && timezoneSystem.Current == "Original" && timezoneSystem.SetCalls == previousSets, "post-launch cannot automatically reapply any previous timezone");
previousSets = timezoneSystem.SetCalls;
tz = await timezone.RecoverAtStartupAsync(false, true);
Check(tz.Skipped && timezoneSystem.SetCalls == previousSets, "pending launch suppresses startup timezone recovery");
tz = await timezone.SwitchAsync("fixture", "Asia/Tokyo", false);
Check(tz.Skipped && timezoneSystem.SetCalls == previousSets, "disabled timezone option performs no writes");
store.ThrowRead = true;
timezoneSystem.Current = "Original";
tz = await timezone.SwitchAsync("fixture", "Asia/Tokyo", true);
Check(!tz.Success && timezoneSystem.SetCalls == previousSets, "corrupt recovery record blocks timezone mutation");
store.Value = new("China Standard Time", "Tokyo", "legacy", "Asia/Tokyo", "old");
timezoneSystem.Current = "Tokyo";
tz = await timezone.RestoreAsync();
var startupTz = await timezone.RecoverAtStartupAsync(false, false);
Check(tz.Success && startupTz.Success && timezoneSystem.SetCalls == previousSets && timezoneSystem.Current == "Tokyo", "exit and startup never restore Beijing even with a corrupt stale Beijing baseline");

// File evidence remains inside the project; never uses the user's credential or runtime directories.
var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
try
{
    var file = new JsonTimezoneRestoreStore(Path.Combine(fixtureRoot, "中文记录", "tz-restore.json"));
    Check(file.Read() is null, "missing recovery file is absence");
    var value = new TimezoneRestoreRecord("Original", "Tokyo", "隔离档案", "Asia/Tokyo", "fixture");
    Check(file.TryWrite(value) && file.Read() == value, "Unicode atomic recovery record roundtrip");
    var recordPath = Path.Combine(fixtureRoot, "中文记录", "tz-restore.json");
    File.WriteAllText(recordPath, "broken");
    bool threw = false; try { file.Read(); } catch (JsonException) { threw = true; }
    Check(threw, "corrupt recovery file is not absence");
    using (var held = new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.None))
        Check(!file.TryWrite(value), "locked recovery destination fails without partial replacement");
    Check(Directory.GetFiles(Path.GetDirectoryName(recordPath)!, "*.tmp").Length == 0, "failed atomic write cleans only its temporary file");

    var fakeCommands = new FakeCommands { Response = new(0, "Tokyo") };
    var tzAdapter = new WindowsTimezoneSystem(fakeCommands);
    var read = await tzAdapter.ReadAsync(default);
    Check(read.Success && read.Id == "Tokyo" && fakeCommands.LastArguments!.SequenceEqual(["/g"]), "native timezone read uses injected command");
    Check(tzAdapter.ResolveWindowsId("Asia/Tokyo") == "Tokyo Standard Time", "runtime IANA to Windows mapping");
    fakeCommands.Response = new(0, "");
    Check(await tzAdapter.SetAsync("Tokyo Standard Time", default) && fakeCommands.LastArguments!.SequenceEqual(["/s", "Tokyo Standard Time"]), "native timezone set arguments verified without executing command");
    var count = fakeCommands.Calls;
    Check(!await tzAdapter.SetAsync("Unknown_invalid", default) && fakeCommands.Calls == count, "invalid timezone cannot dispatch system command");
    fakeCommands.Response = new(0, "{\"Executable\":\"\",\"Aumid\":\"OpenAI.Codex_pub!App\"}");
    var adapter = new WindowsDesktopLaunchSystem(Path.Combine(fixtureRoot, "launch"), new FakeProcessSystem([]), fakeCommands, new FakeClock());
    var target = await adapter.FindTargetAsync(default);
    Check(target?.Aumid == "OpenAI.Codex_pub!App", "registered target discovery uses injected command");
    var targetScript = Encoding.Unicode.GetString(Convert.FromBase64String(fakeCommands.LastArguments![^1]));
    Check(targetScript.Contains("Get-AppxPackageManifest") && targetScript.Contains("Get-StartApps"), "target discovery includes manifest and StartApps fallback");

    var pendingRoot = Path.Combine(fixtureRoot, "pending");
    var nonce = Guid.NewGuid().ToString("N");
    var pendingDirectory = Path.Combine(pendingRoot, nonce);
    Directory.CreateDirectory(pendingDirectory);
    void WriteLaunch(string status, long deadline = 0, string? token = null) => File.WriteAllText(Path.Combine(pendingDirectory, "state.json"), JsonSerializer.Serialize(new { nonce = token ?? nonce, status, deadline_ms = deadline, pid = 77 }));
    var fakeProcesses = new FakeProcessSystem([]);
    adapter = new WindowsDesktopLaunchSystem(pendingRoot, fakeProcesses, fakeCommands, new FakeClock());
    WriteLaunch("launching");
    Check((await adapter.CheckPendingAsync(default))?.Status == LaunchStatus.Pending, "persisted launching state cannot be cancelled");
    WriteLaunch("pending", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1);
    Check(await adapter.CheckPendingAsync(default) is null && !Directory.Exists(pendingDirectory), "unstarted expired request cancelled under same mutex");
    Directory.CreateDirectory(pendingDirectory);
    WriteLaunch("started");
    Check((await adapter.CheckPendingAsync(default))?.Status == LaunchStatus.Pending, "persisted process receipt still waits for desktop window");
    fakeProcesses.Items.Add(desktop); fakeProcesses.Ready = true;
    Check(await adapter.CheckPendingAsync(default) is null && !Directory.Exists(pendingDirectory), "recovered launch completes after stable desktop window");
    Directory.CreateDirectory(pendingDirectory);
    WriteLaunch("pending", token: "wrong");
    Check((await adapter.CheckPendingAsync(default))?.Status == LaunchStatus.Pending && Directory.Exists(pendingDirectory), "nonce mismatch keeps evidence and blocks next launch");
    var script = PackagedLaunchScripts.Build(new { family = "fixture", app_id = "App", nonce = "fixture" });
    Check(script.Contains("Invoke-CommandInDesktopPackage") && script.Contains("Windows.FullTrustApplication") && script.Contains("-PreventBreakaway"), "packaged launcher preserves full trust package activation");
    Check(script.Contains("$s.status -eq 'pending'") && script.Contains("$mutex.ReleaseMutex()"), "outer package failure can resolve only unstarted request");
}
finally { Directory.Delete(fixtureRoot, true); }
var policy = new DesktopLifecyclePolicy(true, true);
Check(policy.ShouldHideOnClose(false, true) && !policy.ShouldHideOnClose(true, true) && !policy.ShouldHideOnClose(false, false), "tray close policy preserves explicit exit and unavailable tray");
var pathProbe = new FakePathProbe();
var inputs = new WindowsPathInputs(@"D:\portable", @"C:\Users\fixture", @"D:\working", new Dictionary<string, string> { ["APPDATA"] = @"C:\Users\fixture\Roaming", ["LOCALAPPDATA"] = @"C:\Users\fixture\Local" });
var dataPaths = WindowsDataPaths.Resolve(inputs, pathProbe);
Check(dataPaths.Root == @"D:\portable" && dataPaths.ActiveHome == @"C:\Users\fixture\.codex", "default portable root and same Codex client home");
Check(dataPaths.WebDesktopRoot == @"C:\Users\fixture\Roaming\Codex\web\Codex" && dataPaths.PackagedDesktopRoot is null, "web desktop path does not invent package root");
pathProbe.Writable = false;
dataPaths = WindowsDataPaths.Resolve(inputs, pathProbe);
Check(dataPaths.Root == @"C:\Users\fixture\Roaming\ChunsonSpace", "unwritable executable directory falls back to original roaming semantics");
pathProbe.Existing.Add(@"C:\Users\fixture\Local\Packages");
pathProbe.PackageDirectories.AddRange([@"C:\Users\fixture\Local\Packages\OpenAI.Codex_aaa", @"C:\Users\fixture\Local\Packages\OpenAI.Codex_bbb"]);
foreach (var directory in pathProbe.PackageDirectories) pathProbe.Existing.Add(Path.Combine(directory, "LocalCache", "Roaming", "Codex", "web", "Codex"));
dataPaths = WindowsDataPaths.Resolve(inputs with { RegisteredPackageFamily = "OpenAI.Codex_bbb" }, pathProbe);
Check(dataPaths.PackagedDesktopRoot!.Contains("OpenAI.Codex_bbb"), "registered package family takes precedence over directory sorting");
dataPaths = WindowsDataPaths.Resolve(inputs with { Environment = new Dictionary<string, string> { ["ChunsonSpace_DATA_DIR"] = @"D:\chosen-store", ["CODEX_HOME"] = @"D:\chosen-client" } }, pathProbe);
Check(dataPaths.Root == @"D:\chosen-store" && dataPaths.ActiveHome == @"D:\chosen-client" && dataPaths.WebDesktopRoot is null, "explicit roots preserved without independent client creation");
bool invalidOverride = false;
try { WindowsDataPaths.Resolve(inputs with { Environment = new Dictionary<string, string> { ["ChunsonSpace_DATA_DIR"] = "relative" } }, pathProbe); }
catch (ArgumentException) { invalidOverride = true; }
Check(invalidOverride, "relative ChunsonSpace_DATA_DIR rejected");
var ownedDesktop = desktop with { Pid = 100, ParentPid = 1, CreationTime = 1000 };
var externalDesktop = desktop with { Pid = 200, ParentPid = 1, CreationTime = 2000 };
var browser = new ProcessIdentity(300, 1, "chrome.exe", @"D:\fixture\chrome.exe", 3000);
var ownedChild = child with { Pid = 101, ParentPid = 100, CreationTime = 1100 };
var ownedSystem = new FakeProcessSystem([ownedDesktop, externalDesktop, browser, ownedChild]) { AllowStop = true };
var ownedStore = new FakeOwnedStore();
var ownedTimezoneSystem = new FakeTimezoneSystem { Current = "Tokyo" };
var ownedTimezoneStore = new FakeTimezoneStore { Value = new("Original", "Tokyo", "fixture", "Asia/Tokyo", "fixture") };
var ownedTimezone = new SystemTimezoneService(ownedTimezoneSystem, ownedTimezoneStore);
var lifecycle = new OwnedProcessLifecycleService(ownedSystem, new FakeClock(), ownedStore, ownedTimezone);
Check(lifecycle.Track(ownedDesktop, OwnedProcessKind.Desktop, "fixture") && lifecycle.Track(browser, OwnedProcessKind.LoginBrowser, "fixture"), "lifecycle records exact tool-owned desktop and login browser");
Check(ownedStore.Value.Count == 3 && ownedStore.Value.Any(p => p.Kind == OwnedProcessKind.DesktopChild), "lifecycle records captured owned descendants");
Check(await lifecycle.TrackedDesktopAliveAsync(), "owned desktop alive is observable");
var ownClose = await lifecycle.CloseTrackedAsync();
Check(ownClose.Success && ownedSystem.Items.Contains(externalDesktop) && ownedSystem.Items.Contains(browser) && !ownedSystem.Items.Contains(ownedChild), "exit closes only tool-owned desktop tree and preserves outside Codex and browser");
var ownedStatus = await lifecycle.PollAsync();
Check(ownedStatus.BrowserAlive && ownedTimezoneSystem.SetCalls == 0, "owned login browser prevents early timezone restoration");
ownedSystem.Items.Remove(browser);
ownedStatus = await lifecycle.PollAsync();
Check(ownedStatus.Success && ownedTimezoneSystem.Current == "Tokyo", "last owned process exit retains selected timezone");
ownedSystem = new FakeProcessSystem([externalDesktop]) { AllowStop = true };
ownedStore.Value = [new(ownedDesktop, OwnedProcessKind.Desktop, "fixture", "fixture")];
ownedTimezoneSystem.Current = "Tokyo"; ownedTimezoneStore.Value = new("Original", "Tokyo", "fixture", "Asia/Tokyo", "fixture");
lifecycle = new(ownedSystem, new FakeClock(), ownedStore, ownedTimezone);
ownedStatus = await lifecycle.AdoptAsync();
Check(ownedStatus.Success && !ownedStatus.DesktopAlive && !await lifecycle.TrackedDesktopAliveAsync(), "adoption never claims arbitrary outside desktop by name");
await lifecycle.PollAsync();
Check(ownedTimezoneSystem.Current == "Tokyo", "tool restart cannot restore a previous timezone after an owned process exited");
ownedStore.Value = [new(ownedDesktop, OwnedProcessKind.Desktop, "fixture", "fixture")];
ownedSystem.Items.Add(ownedDesktop with { CreationTime = 9999 });
lifecycle = new(ownedSystem, new FakeClock(), ownedStore, ownedTimezone);
await lifecycle.AdoptAsync(); ownClose = await lifecycle.CloseTrackedAsync();
Check(ownClose.Success && ownedSystem.Items.Any(p => p.Pid == ownedDesktop.Pid) && ownedSystem.StopCalls.Count == 0, "PID reuse is not adopted or closed");
ownedStore = new FakeOwnedStore { ThrowRead = true };
lifecycle = new(ownedSystem, new FakeClock(), ownedStore, ownedTimezone);
ownedStatus = await lifecycle.AdoptAsync(); await lifecycle.PollAsync();
Check(!ownedStatus.Success && ownedStore.Writes == 0, "corrupt ownership evidence is retained and never silently cleared");
ownedStore = new FakeOwnedStore { Value = [new(ownedDesktop with { CreationTime = 0 }, OwnedProcessKind.Desktop, "legacy", "fixture")] };
lifecycle = new(ownedSystem, new FakeClock(), ownedStore, ownedTimezone);
ownedStatus = await lifecycle.AdoptAsync();
ownClose = await lifecycle.CloseTrackedAsync();
Check(!ownedStatus.Success && !ownClose.Success && ownedStore.Value.Count == 1 && ownedSystem.StopCalls.Count == 0, "legacy record without creation time cannot claim or terminate a live PID");
ownedSystem = new FakeProcessSystem([ownedDesktop, ownedChild, externalDesktop]) { AllowStop = true, RefusePid = ownedChild.Pid };
ownedStore = new FakeOwnedStore();
ownedTimezoneSystem.Current = "Tokyo"; ownedTimezoneStore.Value = new("Original", "Tokyo", "fixture", "Asia/Tokyo", "fixture");
lifecycle = new(ownedSystem, new FakeClock(), ownedStore, ownedTimezone);
lifecycle.Track(ownedDesktop, OwnedProcessKind.Desktop, "fixture");
ownClose = await lifecycle.CloseTrackedAsync(); ownedStatus = await lifecycle.PollAsync();
Check(!ownClose.Success && ownedStatus.DesktopAlive && ownedTimezoneSystem.Current == "Tokyo", "surviving owned child blocks shutdown and timezone restoration after parent exit");
ownedSystem.Items.Remove(ownedChild);
await lifecycle.PollAsync();
Check(ownedTimezoneSystem.Current == "Tokyo", "owned child final exit leaves the selected timezone unchanged");
ownedStore = new FakeOwnedStore(); ownedSystem = new FakeProcessSystem([ownedDesktop]);
lifecycle = new(ownedSystem, new FakeClock(), ownedStore, ownedTimezone);
lifecycle.Track(ownedDesktop, OwnedProcessKind.Desktop, "fixture");
ownedSystem.Items.Clear();
using (var scope = await lifecycle.BeginLaunchAsync())
{
    var poll = lifecycle.PollAsync();
    Check(!poll.IsCompleted, "launch registration scope serializes watcher restoration");
    scope.Dispose(); await poll;
}
var ownershipFixture = Path.Combine(AppContext.BaseDirectory, "ownership-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(ownershipFixture);
try
{
    var durable = new JsonOwnedProcessStore(Path.Combine(ownershipFixture, "owned-processes.json"));
    var record = new OwnedProcessRecord(ownedDesktop, OwnedProcessKind.Desktop, "隔离档案", "fixture");
    Check(durable.TryWrite([record]) && durable.Read().Single() == record, "owned-process ledger persists PID creation path and kind in compatible file");
    Check(durable.TryWrite([]) && durable.Read().Count == 0, "empty owned ledger removes only its own record");
}
finally { Directory.Delete(ownershipFixture, true); }
var sharedSystem = new FakeProcessSystem([ownedDesktop, browser]) { AllowStop = true };
var sharedStore = new FakeOwnedStore();
ownedTimezoneSystem.Current = "Tokyo"; ownedTimezoneStore.Value = new("Original", "Tokyo", "fixture", "Asia/Tokyo", "fixture");
var guiLifecycle = new OwnedProcessLifecycleService(sharedSystem, new FakeClock(), sharedStore, ownedTimezone);
var cliLifecycle = new OwnedProcessLifecycleService(sharedSystem, new FakeClock(), sharedStore, ownedTimezone);
guiLifecycle.Track(ownedDesktop, OwnedProcessKind.Desktop, "GUI");
cliLifecycle.Track(browser, OwnedProcessKind.LoginBrowser, "CLI");
Check(sharedStore.Value.Count == 2, "CLI registration merges live GUI ownership instead of replacing its cached ledger");
await guiLifecycle.PollAsync();
Check(sharedStore.Value.Count == 2 && sharedStore.Value.Any(record => record.Kind == OwnedProcessKind.LoginBrowser), "GUI watcher imports newly persisted CLI browser without overwriting it");
sharedSystem.Items.Remove(ownedDesktop);
await guiLifecycle.PollAsync();
Check(ownedTimezoneSystem.Current == "Tokyo", "GUI desktop exit does not restore timezone while shared CLI browser remains");
sharedSystem.Items.Remove(browser);
await guiLifecycle.PollAsync();
Check(ownedTimezoneSystem.Current == "Tokyo", "shared ledger final process exit never restores the original timezone");
var sharedFixture = Path.Combine(AppContext.BaseDirectory, "shared-ownership-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sharedFixture);
try
{
    sharedSystem = new FakeProcessSystem([ownedDesktop, browser]);
    var guiStore = new JsonOwnedProcessStore(Path.Combine(sharedFixture, "owned-processes.json"));
    var cliStore = new JsonOwnedProcessStore(Path.Combine(sharedFixture, "owned-processes.json"));
    guiLifecycle = new(sharedSystem, new FakeClock(), guiStore, ownedTimezone);
    cliLifecycle = new(sharedSystem, new FakeClock(), cliStore, ownedTimezone);
    Check(guiLifecycle.Track(ownedDesktop, OwnedProcessKind.Desktop, "GUI") && cliLifecycle.Track(browser, OwnedProcessKind.LoginBrowser, "CLI") && guiStore.Read().Count == 2, "independent JSON stores atomically merge exact live ownership records");
    using (var cliLease = await cliLifecycle.BeginLaunchAsync())
    {
        sharedSystem.Items.Remove(ownedDesktop);
        ownedTimezoneSystem.Current = "Tokyo"; ownedTimezoneStore.Value = new("Original", "Tokyo", "fixture", "Asia/Tokyo", "fixture");
        var crossPoll = await guiLifecycle.PollAsync();
        Check(crossPoll.Message.Contains("其他实例") && ownedTimezoneSystem.Current == "Tokyo", "cross-process launch file lease suppresses GUI watcher restoration before browser registration");
        Check(!guiStore.TryWrite([]) && cliStore.Read().Count == 2, "contending JSON writer cannot clear another process ledger");
    }
    var crossResult = await guiLifecycle.PollAsync();
    Check(crossResult.BrowserAlive && guiStore.Read().Count == 1, "file lease release lets GUI import CLI ownership and prune only exited identities");
}
finally { Directory.Delete(sharedFixture, true); }
var statusEvents = 0;
using (var watchCancellation = new CancellationTokenSource())
{
    var watchClock = new FakeClock();
    int ticks = 0;
    watchClock.OnDelay = () => { if (++ticks == 3) watchCancellation.Cancel(); };
    lifecycle = new(new FakeProcessSystem([]), watchClock, new FakeOwnedStore(), ownedTimezone);
    lifecycle.StatusChanged += (_, _) => statusEvents++;
    try { await lifecycle.WatchAsync(watchCancellation.Token); } catch (OperationCanceledException) { }
    Check(statusEvents == 1, "watcher emits only changed lifecycle status without duplicate UI polling");
}
var installSystem = new FakeInstallSystem { States = new Queue<DesktopInstallationState>([new(DesktopInstallationStatus.Installed)]) };
var installResult = await new CodexInstallService(installSystem).InstallAsync();
Check(installResult.Success && installResult.AlreadyInstalled && installSystem.InstallCalls == 0, "installed Codex does not initiate installation");
installSystem = new() { States = new Queue<DesktopInstallationState>([new(DesktopInstallationStatus.Unknown)]) };
installResult = await new CodexInstallService(installSystem).InstallAsync();
Check(installResult.Status == CodexInstallStatus.Unknown && installSystem.InstallCalls == 0 && installSystem.StoreCalls == 0, "unknown installation status does not infer missing or reinstall");
installSystem = new() { Winget = false, States = new Queue<DesktopInstallationState>([new(DesktopInstallationStatus.NotInstalled)]) };
installResult = await new CodexInstallService(installSystem).InstallAsync();
Check(!installResult.Success && installResult.StoreOpened && installResult.Status == CodexInstallStatus.StoreOpened, "store page dispatch is separate from installed confirmation");
installSystem = new() { States = new Queue<DesktopInstallationState>([new(DesktopInstallationStatus.NotInstalled), new(DesktopInstallationStatus.NotInstalled)]), Receipt = new(true, 0) };
installResult = await new CodexInstallService(installSystem).InstallAsync();
Check(!installResult.Success && installSystem.StoreCalls == 1, "winget exit zero does not establish registered package success");
installSystem = new() { States = new Queue<DesktopInstallationState>([new(DesktopInstallationStatus.NotInstalled), new(DesktopInstallationStatus.Installed, "fixture-version")]), Receipt = new(true, 1) };
installResult = await new CodexInstallService(installSystem).InstallAsync();
Check(installResult.Success && installResult.ExitCode == 1, "registered package reread is authoritative after installer completion");
installSystem = new() { States = new Queue<DesktopInstallationState>([new(DesktopInstallationStatus.NotInstalled), new(DesktopInstallationStatus.Unknown)]), Receipt = new(false, null) };
installResult = await new CodexInstallService(installSystem).InstallAsync();
Check(installResult.Status == CodexInstallStatus.Pending && installSystem.StoreCalls == 0, "unconfirmed installer timeout is pending");
var silentSystem = new FakeSilentSystem { States = new Queue<SilentTimezoneState>([new(SilentTimezoneStatus.Enabled, true)]) };
var enabled = await new SilentTimezoneChannelService(silentSystem).EnableAsync();
Check(enabled.Success && !enabled.Elevated && !enabled.Created && enabled.LastResult is null && silentSystem.SetupCalls == 0, "existing readable timezone task does not elevate and unknown last-result is not zero");
silentSystem = new() { States = new Queue<SilentTimezoneState>([new(SilentTimezoneStatus.Unknown, false)]) };
enabled = await new SilentTimezoneChannelService(silentSystem).EnableAsync();
Check(!enabled.Success && silentSystem.SetupCalls == 0, "unknown timezone channel refuses setup automatically");
silentSystem = new() { States = new Queue<SilentTimezoneState>([new(SilentTimezoneStatus.Disabled, false)]), SetupSuccess = false };
enabled = await new SilentTimezoneChannelService(silentSystem).EnableAsync();
Check(!enabled.Success && enabled.Elevated && !enabled.Enabled, "declined timezone elevation never reports enabled");
silentSystem = new() { States = new Queue<SilentTimezoneState>([new(SilentTimezoneStatus.Disabled, false), new(SilentTimezoneStatus.Enabled, false)]), SetupSuccess = true };
enabled = await new SilentTimezoneChannelService(silentSystem).EnableAsync();
Check(!enabled.Success && !enabled.Enabled, "registered task with unreadable helper is not accepted");
silentSystem = new() { States = new Queue<SilentTimezoneState>([new(SilentTimezoneStatus.Disabled, false), new(SilentTimezoneStatus.Enabled, true, 0)]), SetupSuccess = true };
enabled = await new SilentTimezoneChannelService(silentSystem).EnableAsync();
Check(enabled.Success && enabled.Created && enabled.Elevated && silentSystem.SetupCalls == 1, "explicit timezone enable confirms task and ordinary-user helper read");
var systemFixture = Path.Combine(AppContext.BaseDirectory, "system-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(systemFixture);
try
{
    var commands = new FakeCommands { Response = new(0, "{\"installed\":true,\"version\":\"fixture\",\"family\":\"OpenAI.Codex_pub\"}") };
    var nativeInstall = new WindowsCodexInstallSystem(commands, systemFixture);
    var installed = await nativeInstall.ReadStateAsync(default);
    Check(installed.Status == DesktopInstallationStatus.Installed && installed.PackageFamily == "OpenAI.Codex_pub", "native package install state uses fake command and validates JSON");
    commands.Response = new(0, "invalid");
    installed = await nativeInstall.ReadStateAsync(default);
    Check(installed.Status == DesktopInstallationStatus.Unknown, "invalid package state remains unknown");
    var nativeSilent = new WindowsSilentTimezoneSystem(commands, systemFixture, @"FIXTURE\user");
    Directory.CreateDirectory(Path.GetDirectoryName(nativeSilent.HelperPath)!);
    File.WriteAllText(nativeSilent.HelperPath, WindowsSilentTimezoneSystem.HelperScript);
    var xml = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("Task",
        new System.Xml.Linq.XElement("Actions", new System.Xml.Linq.XElement("Exec",
            new System.Xml.Linq.XElement("Command", "powershell.exe"),
            new System.Xml.Linq.XElement("Arguments", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + nativeSilent.HelperPath + "\""))))).ToString();
    commands.Responses.Enqueue(new(0, xml)); commands.Responses.Enqueue(new(0, "0"));
    var inspected = await nativeSilent.InspectAsync(default);
    Check(inspected.Status == SilentTimezoneStatus.Enabled && inspected.HelperReadable && inspected.LastResult == 0, "native timezone channel validates fixed task action and readable fixture helper");
    commands.Response = new(1, "Access is denied");
    inspected = await nativeSilent.InspectAsync(default);
    Check(inspected.Status == SilentTimezoneStatus.Unknown, "denied task query is not treated as absence");
    commands.Response = new(1, "The system cannot find the file specified");
    inspected = await nativeSilent.InspectAsync(default);
    Check(inspected.Status == SilentTimezoneStatus.Disabled, "confirmed missing task is disabled");
    commands.Response = new(1, "", "The system cannot find the file specified");
    inspected = await nativeSilent.InspectAsync(default);
    Check(inspected.Status == SilentTimezoneStatus.Disabled, "missing task reported only on stderr is still recognized without exposing raw diagnostics");
}
finally { Directory.Delete(systemFixture, true); }
var generatedScripts = Path.Combine(AppContext.BaseDirectory, "script-fixtures");
Directory.CreateDirectory(generatedScripts);
var packagedScript = PackagedLaunchScripts.Build(new { family = "OpenAI.Codex_fixture", app_id = "App", nonce = "fixture", mutex = "Local\\Fixture", state = @"D:\fixture\state.json", deadline_ms = 1, arguments = "", env = new { } });
File.WriteAllText(Path.Combine(generatedScripts, "packaged-launch.ps1"), packagedScript);
var encodedInner = System.Text.RegularExpressions.Regex.Matches(packagedScript, @"FromBase64String\('([^']+)'\)")[1].Groups[1].Value;
File.WriteAllText(Path.Combine(generatedScripts, "packaged-launch-inner.ps1"), Encoding.UTF8.GetString(Convert.FromBase64String(encodedInner)));
File.WriteAllText(Path.Combine(generatedScripts, "silent-timezone-setup.ps1"), WindowsSilentTimezoneSystem.BuildSetupScript(@"D:\fixture\ChunsonSpace\tz\set-tz.ps1", @"FIXTURE\user", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"));
File.WriteAllText(Path.Combine(generatedScripts, "silent-timezone-helper.ps1"), WindowsSilentTimezoneSystem.HelperScript);
Check(WindowsSilentTimezoneSystem.HelperScript.Contains(@"ChunsonSpace\tz\tz.txt") && WindowsSilentTimezoneSystem.HelperScript.Contains(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Time Zones"), "helper paths preserve literal backslashes and legacy data contract");
SystemProxyChecks.Run(Check);
await RegionChecks.Run(Check);
WindowIconChecks.Run(Check);
BrowserUdpChecks.Run(Check);
await ManagedCloneChecks.RunAsync(Check);
await ManagedLaunchRecoveryChecks.RunAsync(Check);
await ManagedBrowserProgramChecks.RunAsync(Check);
await ManagedRuntimeCdpChecks.RunAsync(Check);
await FlClashDnsChecks.RunAsync(Check);
LinkFixtures.Report();
Console.WriteLine($"{checks} Windows isolated checks passed. No real Codex, account, proxy, network, or timezone actions were performed.");

sealed class FakeClock : IWaitClock
{
    public long Milliseconds { get; private set; }
    public Action? OnDelay { get; set; }
    public Task DelayAsync(int milliseconds, CancellationToken cancellationToken) { OnDelay?.Invoke(); cancellationToken.ThrowIfCancellationRequested(); Milliseconds += milliseconds; return Task.CompletedTask; }
}
sealed class FakeProcessSystem(List<ProcessIdentity> items) : IProcessSystem
{
    public List<ProcessIdentity> Items { get; } = items;
    public List<(ProcessIdentity Identity, bool Force)> StopCalls { get; } = [];
    public bool AllowStop { get; set; }
    public bool Ready { get; set; }
    public bool ThrowSnapshot { get; set; }
    public bool ReplaceParentOnSecondSnapshot { get; set; }
    public int? RefusePid { get; set; }
    private int snapshots;
    public IReadOnlyList<ProcessIdentity> Snapshot()
    {
        if (ThrowSnapshot) throw new IOException("fixture failure");
        snapshots++;
        if (ReplaceParentOnSecondSnapshot && snapshots == 2) Items[0] = Items[0] with { Name = "replacement.exe", Executable = @"D:\other.exe", CreationTime = 999 };
        return Items.ToArray();
    }
    public bool TryStop(ProcessIdentity process, bool force)
    {
        StopCalls.Add((process, force));
        if (!AllowStop || RefusePid == process.Pid || process.CreationTime == 0 || !Items.Contains(process)) return false;
        Items.Remove(process); return true;
    }
    public bool HasDesktopWindow(IReadOnlyList<ProcessIdentity> desktopProcesses) => Ready && desktopProcesses.Count > 0;
}
sealed class FakeLaunchSystem : IDesktopLaunchSystem
{
    public bool Ready { get; set; }
    public bool ShowWindowAfterStart { get; set; }
    public bool ThrowAfterDispatch { get; set; }
    public bool ThrowWindowAfterDispatch { get; set; }
    public bool ThrowRuntime { get; set; }
    public bool ThrowComplete { get; set; }
    public int Starts { get; private set; }
    public int Completes { get; private set; }
    public LaunchReceipt Receipt { get; set; } = new(LaunchStatus.Confirmed, 77, "fake", "started");
    public LaunchReceipt? Pending { get; set; }
    public Task<LaunchReceipt?> CheckPendingAsync(CancellationToken cancellationToken) => Task.FromResult(Pending);
    public bool IsDesktopReady() { if (Starts > 0 && ThrowWindowAfterDispatch) throw new IOException("fixture"); return Ready; }
    public Task<DesktopTarget?> FindTargetAsync(CancellationToken cancellationToken) => Task.FromResult<DesktopTarget?>(new("fixture", "fixture"));
    public Task<LaunchReceipt> StartAsync(DesktopTarget target, DesktopLaunchRequest request, CancellationToken cancellationToken)
    {
        Starts++;
        if (ThrowAfterDispatch) throw new IOException("dispatched fixture");
        Ready = ShowWindowAfterStart;
        return Task.FromResult(Receipt);
    }
    public Task<LaunchReceipt> VerifyRuntimeAsync(LaunchReceipt receipt, DesktopLaunchRequest request, CancellationToken cancellationToken)
        => ThrowRuntime ? throw new IOException("fixture private detail") : Task.FromResult(receipt);
    public void Complete(LaunchReceipt receipt) { if (ThrowComplete) throw new IOException("fixture private detail"); Completes++; }
}
sealed class FakeTimezoneSystem : ITimezoneSystem
{
    public string Current { get; set; } = "Original";
    public bool ApplySet { get; set; } = true;
    public bool ReadFails { get; set; }
    public int SetCalls { get; private set; }
    public string? ResolveWindowsId(string value) => value switch { "Asia/Tokyo" => "Tokyo", "America/Chicago" => "Chicago", "Original" or "Tokyo" or "Chicago" or "UserChoice" => value, _ => null };
    public Task<TimezoneReadResult> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(new TimezoneReadResult(!ReadFails, ReadFails ? "" : Current));
    public Task<bool> SetAsync(string windowsId, CancellationToken cancellationToken) { SetCalls++; if (ApplySet) Current = windowsId; return Task.FromResult(true); }
}
sealed class FakeTimezoneStore : ITimezoneRestoreStore
{
    public TimezoneRestoreRecord? Value { get; set; }
    public bool FailWrite { get; set; }
    public bool ThrowRead { get; set; }
    public TimezoneRestoreRecord? Read() => ThrowRead ? throw new JsonException("fixture corrupt") : Value;
    public bool TryWrite(TimezoneRestoreRecord record) { if (FailWrite) return false; Value = record; return true; }
}
sealed class FakeCommands : ICommandRunner
{
    public CommandResult Response { get; set; } = new(0, "");
    public IReadOnlyList<string>? LastArguments { get; private set; }
    public int Calls { get; private set; }
    public Queue<CommandResult> Responses { get; } = [];
    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken) { Calls++; LastArguments = arguments; return Task.FromResult(Responses.Count > 0 ? Responses.Dequeue() : Response); }
}
sealed class FakePathProbe : IWindowsPathProbe
{
    public bool Writable { get; set; } = true;
    public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PackageDirectories { get; } = [];
    public bool DirectoryExists(string path) => Existing.Contains(path);
    public IReadOnlyList<string> Directories(string path) => PackageDirectories;
    public bool CanWriteDirectory(string path) => Writable;
}
sealed class FakeOwnedStore : IOwnedProcessStore
{
    public IReadOnlyList<OwnedProcessRecord> Value { get; set; } = [];
    public bool ThrowRead { get; set; }
    public int Writes { get; private set; }
    public IReadOnlyList<OwnedProcessRecord> Read() => ThrowRead ? throw new JsonException("fixture corrupt") : Value;
    public bool TryWrite(IReadOnlyList<OwnedProcessRecord> records) { Writes++; Value = records.ToArray(); return true; }
}
sealed class FakeInstallSystem : ICodexInstallSystem
{
    public Queue<DesktopInstallationState> States { get; set; } = [];
    public bool Winget { get; set; } = true;
    public InstallCommandReceipt Receipt { get; set; } = new(true, 0);
    public int InstallCalls { get; private set; }
    public int StoreCalls { get; private set; }
    public Task<DesktopInstallationState> ReadStateAsync(CancellationToken cancellationToken) => Task.FromResult(States.Dequeue());
    public bool HasWinget() => Winget;
    public Task<InstallCommandReceipt> InstallAsync(CancellationToken cancellationToken) { InstallCalls++; return Task.FromResult(Receipt); }
    public bool OpenStore() { StoreCalls++; return true; }
}
sealed class FakeSilentSystem : ISilentTimezoneSystem
{
    public Queue<SilentTimezoneState> States { get; set; } = [];
    public bool SetupSuccess { get; set; }
    public int SetupCalls { get; private set; }
    public Task<SilentTimezoneState> InspectAsync(CancellationToken cancellationToken) => Task.FromResult(States.Dequeue());
    public Task<bool> SetupExplicitlyAsync(CancellationToken cancellationToken) { SetupCalls++; return Task.FromResult(SetupSuccess); }
}
