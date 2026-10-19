using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChunsonSpace.Application;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

// Focused production-path checks. All paths and identities are generated here;
// no normal startup, real transport, native process inventory or clock launcher.
internal static class MaintenanceV2Verification
{
    internal static async Task RunAsync(string directory, bool finalLayoutOnly = false)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>(); var screenshots = new List<string>(); bool passed = false;
        var listener = new UiVerification.BindingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        void Check(string name, bool value)
        { checks.Add(new { name, passed = value }); if (!value) throw new InvalidOperationException(name); }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var root = Path.Combine(directory, "accounts-fixture"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var workspace = DesktopWorkspace.CreateManagedFixture(root);
        workspace.Accounts!.OfflineTransport!.EgressTimezone = TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : TimeZoneInfo.Local.Id;
        var runtime = workspace.OfflineRuntime!; var storage = (ManagedCloneStorage)workspace.SwitchStorage;
        foreach (var name in new[] { "A", "B", "C" })
        {
            workspace.Profiles.CreateBoundAccount(name, new("", "Asia/Shanghai", "zh-CN", FollowSystemProxy: true));
            var profile = workspace.Profiles.ProfileDirectory(new(IdentityKind.Account, name));
            File.WriteAllText(Path.Combine(profile, "auth.json"), JsonSerializer.Serialize(new { auth_mode = "chatgpt", tokens = new { account_id = "synthetic-" + name, access_token = "synthetic-token-" + name } }));
            File.WriteAllText(Path.Combine(profile, "installation_id"), "synthetic-installation-" + name);
            File.WriteAllText(Path.Combine(profile, "login-metadata.json"), JsonSerializer.Serialize(new { firstCredentialAt = DateTimeOffset.UtcNow.AddHours(-8).ToString("O") }));
        }
        var a = new IdentityRef(IdentityKind.Account, "A");
        storage.ApplyTarget(a, null, true);
        var aPath = workspace.Profiles.ProfileDirectory(a); var auth = Hash(Path.Combine(aPath, "auth.json"));
        var installed = Hash(Path.Combine(aPath, "installation_id"));
        var model = new ShellViewModel(workspace);
        MainWindow? window = null;
        try
        {
            if (!finalLayoutOnly)
            {
            // Make the active credential deliberately unreadable. Rename/delete must not sync it.
            File.WriteAllText(Path.Combine(workspace.ActiveHome, "auth.json"), "{"); runtime.HasPendingLaunch = true;
            model.Selected = model.FilteredItems.Single(i => i.Name == "A");
            model.RenameSelectedCommand.Execute(null); var editor = model.Editor!;
            editor.Name = "当前账号 · 新显示名"; editor.SaveCommand.Execute(null); await editor.CurrentOperation;
            Check("current rename changes metadata without credentials, stable directory or process changes", !editor.HasError
                && Directory.Exists(aPath) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(aPath)!, editor.Name))
                && Hash(Path.Combine(aPath, "auth.json")) == auth && Hash(Path.Combine(aPath, "installation_id")) == installed
                && workspace.Profiles.ReadDisplayName("A") == editor.Name && runtime.StopCalls == 0 && runtime.PendingChecks == 0);
            var other = await workspace.Maintenance!.DeleteAccountAsync(workspace.ProfileMaintenance, "B", managed: storage);
            Check("unrelated deletion ignores pending and corrupt active credentials", other.Success && runtime.StopCalls == 0 && runtime.PendingChecks == 0
                && Directory.Exists(aPath) && other.RecoveryPaths.Count == 0 && !Directory.Exists(workspace.Profiles.ProfileDirectory(new(IdentityKind.Account, "B"))));
            var current = await workspace.Maintenance.DeleteAccountAsync(workspace.ProfileMaintenance, "A", managed: storage);
            Check("current deletion stops only managed runtime without switch or sync", current.Success && runtime.StopCalls == 1
                && runtime.MaintenanceCalls == 1 && runtime.PendingChecks == 0 && runtime.LastLaunchRequest is null
                && storage.ReadRuntimeReference() is null && Directory.Exists(workspace.Profiles.ProfileDirectory(new(IdentityKind.Account, "C"))));
            // Explicit failure releases UI busy; successful cleanup retains the entire account library.
            storage.ApplyTarget(new(IdentityKind.Account, "C"), null, true);
            model.RefreshCommand.Execute(null); runtime.StopSucceeds = false;
            model.ResetManagedCommand.Execute(null); await model.CurrentOperation;
            Check("failed shared cleanup releases busy and retains runtime", !model.IsBusy && model.ResetManagedCommand.CanExecute(null) && storage.ReadRuntimeReference()?.Name == "C");
            runtime.StopSucceeds = true; runtime.HasPendingLaunch = true;
            model.ResetManagedCommand.Execute(null); await model.CurrentOperation;
            File.WriteAllText(Path.Combine(directory, "rebuild-feedback.json"), JsonSerializer.Serialize(new { model.FeedbackTitle, model.Feedback, runtime.StopCalls, runtime.MaintenanceCalls, runtime.PendingChecks, target = runtime.LastLaunchRequest?.IdentityName }));
            Check("reset uses shared maintenance and leaves the clone closed", !model.IsBusy && runtime.LastLaunchRequest is null
                && storage.ReadRuntimeReference() is null && runtime.MaintenanceCalls >= 3);
            model.ResetManagedCommand.Execute(null); await model.CurrentOperation;
            Check("standalone cleanup keeps account auth", !model.IsBusy && storage.ReadRuntimeReference() is null
                && File.Exists(Path.Combine(workspace.Profiles.ProfileDirectory(new(IdentityKind.Account, "C")), "auth.json")));
            await CheckHandshakeAsync(Path.Combine(directory, "handshake"), Check);
            await CheckProcessesAsync(Path.Combine(directory, "processes"), Check);
            await CheckExitAsync(model, Check);
            Chunson.Widgets.ClockLauncherChecks.Run((ok, name) => Check(name, ok));

            }
            else await CheckProcessesAsync(Path.Combine(directory, "processes"), Check);

            // Populate local synthetic accounts for list, filtering and modes; no fabricated product state.
            foreach (var name in Enumerable.Range(1, 14).Select(n => "列表账号" + n.ToString("00")))
                workspace.Profiles.CreateBoundAccount(name, new("", "Asia/Shanghai", "zh-CN", FollowSystemProxy: true));
            model.RefreshCommand.Execute(null); model.InterfaceMode = "单账号";
            model.Selected = model.FilteredItems.Single(i => i.Name == "C");
            if (!finalLayoutOnly)
            {
            var requestsBefore = workspace.Accounts!.OfflineTransport!.Requests.Count;
            model.TestAllAccountsCommand.Execute(null); var health = model.AccountHealth!; await health.CurrentOperation;
            Check("single mode health dispatch contains only the displayed account", health.Rows.Count == 1 && health.Rows[0].Profile == "C"
                && workspace.Accounts.OfflineTransport.Requests.Skip(requestsBefore).Count(r => r.Url == AccountEndpoints.Responses) == 2
                && workspace.Accounts.OfflineTransport.Requests.Skip(requestsBefore).Where(r => r.Url == AccountEndpoints.Responses).All(r => r.Headers.GetValueOrDefault("chatgpt-account-id") == "synthetic-C"));
            health.CloseCommand.Execute(null);
            model.OpenVisualTestsCommand.Execute(null);
            Check("single drawing scope is one displayed profile and requires direct input", model.VisualTests is { } visual
                && visual.Profiles.SequenceEqual(new[] { "C" }) && visual.DrawingContent.Length == 0 && !visual.StartCommand.CanExecute(null));
            model.VisualTests!.CloseCommand.Execute(null);
            var art = await workspace.Accounts.VisualTests.RunAsync("C", "gpt-6.1-sol", VisualTestKind.Static, "一片叶子与一杯茶", effort: "high");
            Check("one existing static executor keeps direct input and writes a static artifact", art.Completed && art.Steps.Count == 1 && art.Steps[0].Prompt.Contains("一片叶子与一杯茶")
                && VisualTestArtifact.Extract("<svg><animate attributeName='x'/></svg>", false) is null
                && VisualTestArtifact.Extract("<html><body>legacy</body></html>", true) is null);
            Check("interface mode persists without switching identity", workspace.Profiles.ReadSettings().InterfaceMode == "single" && storage.ReadRuntimeReference() is null);

            }
            window = UiVerification.CreateWindow(model); window.Show();
            var layout = window.RenderRoot;
            T Control<T>(string name) where T : FrameworkElement => (T)(window.FindName(name) ?? ((Dashboard)window.FindName("DashboardPage")).FindName(name));
            void Capture(string name, int width, int height)
            {
                UiVerification.ArrangeForCapture(layout, width, height);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(layout);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.Combine(directory, name + ".png"); using (var stream = File.Create(path)) encoder.Save(stream); screenshots.Add(path);
            }
            foreach (var dark in new[] { false, true })
            {
                model.DarkTheme = dark;
                foreach (var size in new[] { (1040, 670), (1280, 850) })
                {
                    var suffix = (dark ? "night" : "paper") + "-" + size.Item1;
                    model.InterfaceMode = "多账号"; model.Navigate("Dashboard"); Capture("dashboard-" + suffix, size.Item1, size.Item2);
                    Check(suffix + " five routes, mode selector and live clock fit", new[] { "NavDashboard", "NavAccounts", "NavTests", "NavResults", "NavSettings", "BeijingTimeText", "InterfaceModePicker" }.All(n => UiVerification.FullyVisible(Control<FrameworkElement>(n), layout)));
                    var picker = Control<Button>("SingleModeButton");
                    var hit = layout.InputHitTest(picker.TranslatePoint(new Point(picker.ActualWidth / 2, picker.ActualHeight / 2), layout)) as DependencyObject;
                    Check(suffix + " mode selector is not covered by titlebar", hit == picker || hit is not null && picker.IsAncestorOf(hit));
                    if (!finalLayoutOnly)
                    {
                    model.Navigate("Accounts"); model.Search = "列表账号"; Capture("accounts-" + suffix, size.Item1, size.Item2);
                    Check(suffix + " search preserves usable account list", Control<ListBox>("ProfileList").ActualHeight >= 180 && model.FilteredItems.Any());
                    model.Search = ""; model.Navigate("Tests"); Capture("tests-" + suffix, size.Item1, size.Item2);
                    Check(suffix + " two tests use shared commands", Control<Button>("HealthTestEntry").Command == model.TestAllAccountsCommand && Control<Button>("StaticTestEntry").Command == model.OpenVisualTestsCommand);
                    model.Navigate("Settings"); Capture("settings-" + suffix, size.Item1, size.Item2);
                    Check(suffix + " settings contains one network and environment view", Control<TabControl>("SettingsGroups").Items.Count == 2 && window.FindName("BackupsPage") is null && window.FindName("NavProxy") is null);
                    if (size.Item1 == 1040)
                    {
                        Control<TabControl>("SettingsGroups").SelectedIndex = 1; Capture("network-" + suffix, size.Item1, size.Item2);
                        Control<TabControl>("SettingsGroups").SelectedIndex = 0;
                    }
                    }
                    picker.Command.Execute("单账号");
                    Check(suffix + " segment changes persisted mode", model.IsSingleAccountMode && workspace.Profiles.ReadSettings().InterfaceMode == "single");
                    model.Navigate("Dashboard"); Capture("single-" + suffix, size.Item1, size.Item2);
                    Check(suffix + " single account hides multi route", Control<Button>("NavAccounts").Visibility == Visibility.Collapsed && model.IsSingleAccountMode);
                }
                var theme = dark ? "night" : "paper";
                if (!finalLayoutOnly)
                {
                model.Selected = model.FilteredItems.Single(i => i.Name == "C");
                model.ManageDisplayedCommand.Execute(null); Capture("account-manage-" + theme, 1040, 670);
                model.CloseDetailsCommand.Execute(null);
                model.RenameSelectedCommand.Execute(null); Capture("rename-dialog-" + theme, 1040, 670); model.CloseEditor();
                model.OpenVisualTestsCommand.Execute(null); model.VisualTests!.DrawingContent = "一片叶子与一杯茶";
                Capture("drawing-input-" + theme, 1040, 670);
                Check(theme + " direct drawing input fits", UiVerification.FullyVisible(Control<TextBox>("DrawingContentInput"), layout));
                model.VisualTests.CloseCommand.Execute(null);
                }
                model.Navigate("Results");
                Check(theme + " single results scope uses displayed identity", model.ResultsGallery.Items.Cast<SavedResultRow>().All(r => r.Profile == model.DisplayedAccount?.Name));
                Capture("results-" + theme, 1040, 670);
            }
            model.RefreshBeijingTime(new DateTimeOffset(2026, 10, 19, 20, 1, 2, TimeSpan.Zero));
            Check("clock is explicit UTC+8 date rollover", model.BeijingTime == "04:01:02" && model.BeijingDate == "2026年10月20日");
            window.Close(); window = null;
            if (!finalLayoutOnly) await StorageUiVerification.RunFlClashDirectAsync(Path.Combine(directory, "flclash-direct"));
            if (!finalLayoutOnly) Check("FlClash retained direct script path", true);
            Check("no WPF binding errors in changed layouts", listener.Errors.Count == 0);
            passed = true;
        }
        finally
        {
            window?.Close(); await workspace.Accounts!.DisposeAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new { passed, finalLayoutOnly, checks, screenshots,
                syntheticOnly = true, actualAccountOperations = false, actualProxyChanges = false, actualSystemTimezoneChanges = false,
                actualClockStarts = 0, actualProcessStops = 0, bindingErrors = listener.Errors }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static async Task CheckHandshakeAsync(string root, Action<string, bool> check)
    {
        Directory.CreateDirectory(root);
        var system = new FakeProcesses(); var clock = new FastClock();
        var launch = new WindowsDesktopLaunchSystem(root, system, new NoCommands(), clock);
        string Fixture(string content)
        { var path = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); if (content != "missing") File.WriteAllText(Path.Combine(path, "state.json"), content.Replace("NONCE", Path.GetFileName(path))); return path; }
        var missing = Fixture("missing"); var receipt = await launch.CheckPendingAsync(default);
        check("missing receipt stays pending and identifies missing stage", receipt?.Status == LaunchStatus.Pending && launch.LastStateReadFailure?.ExceptionType.Contains("FileNotFound") == true);
        check("missing receipt can be explicitly cancelled without launch", launch.CancelPendingForMaintenance(default).Success);
        await launch.CheckPendingAsync(default);
        var damaged = Fixture("{\"nonce\":\"NONCE\",\"status\":"); receipt = await launch.CheckPendingAsync(default);
        // Invoke the previously installed reader against this synthetic receipt only.
        // No normal App startup and no process/command implementation is supplied.
        var baselinePath = Path.Combine(root, "legacy-reader", "ChunsonSpace.Windows.dll");
        if (File.Exists(baselinePath))
        {
            var context = new AssemblyLoadContext("legacy-receipt-" + Guid.NewGuid().ToString("N"), isCollectible: true);
            context.Resolving += (_, name) => name.Name == "ChunsonSpace.Core" ? context.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(baselinePath)!, name.Name + ".dll")) : null;
            try
            {
                var type = context.LoadFromAssemblyPath(baselinePath).GetType(typeof(WindowsDesktopLaunchSystem).FullName!)!;
                var instance = type.GetConstructors().Single().Invoke([root, null, null, null, null]);
                var task = (Task)type.GetMethod("CheckPendingAsync")!.Invoke(instance, [CancellationToken.None])!;
                await task;
                var oldReceipt = task.GetType().GetProperty("Result")!.GetValue(task)!;
                var message = oldReceipt.GetType().GetProperty("Message")!.GetValue(oldReceipt)?.ToString();
                File.WriteAllText(Path.Combine(root, "legacy-reader.json"), JsonSerializer.Serialize(new { baselineSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(baselinePath))), message, currentMessage = receipt?.Message, diagnostic = launch.LastStateReadFailure, syntheticOnly = true }));
                check("installed legacy reader reproduces the screenshot error on truncated receipt", message?.Contains("无法读取上次启动握手") == true);
            }
            finally { context.Unload(); }
        }
        check("actual reader reproduces truncated legacy JSON without false confirmation", receipt?.Status == LaunchStatus.Pending && launch.LastStateReadFailure?.ExceptionType.Contains("JsonException") == true);
        File.WriteAllText(Path.Combine(root, "damaged-receipt.sha256"), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(damaged, "state.json")))));
        check("damaged receipt explicit recovery preserves evidence", launch.CancelPendingForMaintenance(default).Success
            && Directory.GetFiles(Path.GetFullPath(Path.Combine(root, "..", "..", "recovery")), "launch-*.json").Length > 0);
        await launch.CheckPendingAsync(default);
        var pending = Fixture(JsonSerializer.Serialize(new { nonce = "NONCE", status = "pending", deadline_ms = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds() }));
        receipt = await launch.CheckPendingAsync(default);
        check("valid still-starting legacy state remains pending and intact", receipt?.Status == LaunchStatus.Pending && File.Exists(Path.Combine(pending, "state.json")));
        launch.CancelPendingForMaintenance(default); await launch.CheckPendingAsync(default);
        Fixture("{\"nonce\":\"NONCE\",\"status\":\"started\"}"); receipt = await launch.CheckPendingAsync(default);
        check("started text alone is never valid launch confirmation", receipt?.Status == LaunchStatus.Pending && system.Stopped.Count == 0);
        launch.CancelPendingForMaintenance(default);
        // Parse these exact production PowerShell sources later without executing them.
        var scripts = typeof(WindowsDesktopLaunchSystem).Assembly.GetType("ChunsonSpace.Windows.PackagedLaunchScripts")!;
        foreach (var field in scripts.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
            File.WriteAllText(Path.Combine(root, "launcher-" + field.Name + ".ps1"), (string)field.GetRawConstantValue()!);
    }

    private static async Task CheckProcessesAsync(string root, Action<string, bool> check)
    {
        Directory.CreateDirectory(root); var system = new FakeProcesses(); var clock = new FastClock();
        var store = new JsonOwnedProcessStore(Path.Combine(root, "owned.json"));
        var old = new ProcessIdentity(41, 0, "Codex.exe", "C:/fixture/OpenAI/Codex/Codex.exe", 100);
        var reused = old with { CreationTime = 300 }; var other = old with { Pid = 52, CreationTime = 120 };
        system.Live.AddRange([reused, other]); store.TryWrite([new(old, OwnedProcessKind.Desktop, "fixture", "")]);
        var scope = new ManagedCloneProcessScope(system, store, root);
        check("reused PID is not ownership", scope.Selected(system.Snapshot()).Count == 0);
        var stopped = await new CodexProcessService(system, clock, scope).StopAsync(ProcessScope.DesktopAndCli);
        check("original and unrelated desktop preserved", stopped.Success && system.Stopped.Count == 0 && system.Live.Count == 2);
        system.Fresh.Add(other); File.WriteAllText(Path.Combine(root, "owned.json"), "{");
        check("corrupt ownership recovers only fresh managed boundary evidence", scope.Selected(system.Snapshot()).Select(p => p.Pid).SequenceEqual(new[] { 52 }));
        var launch = new ForbiddenLaunch();
        var lifecycle = new OwnedProcessLifecycleService(system, clock, store, null!, launch, scope);
        stopped = await lifecycle.CloseForExplicitExitAsync(default);
        check("precise stop keeps reused original PID", stopped.Success && system.Stopped.SequenceEqual(new[] { 52 }) && system.Live.Single().Pid == 41);
        check("explicit process exit never verifies handshake or timezone", launch.Calls == 0);
        system.Fresh.Clear(); File.WriteAllText(Path.Combine(root, "owned.json"), "{");
        bool rejected = false; try { scope.Selected(system.Snapshot()); } catch (JsonException) { rejected = true; }
        check("unknown corrupt ownership never clears evidence to claim success", rejected && File.ReadAllText(Path.Combine(root, "owned.json")) == "{");
        await lifecycle.CloseForExplicitExitAsync(default);
        check("explicit exit with corrupt ownership preserves unrelated processes and records", system.Live.Single().Pid == 41 && File.ReadAllText(Path.Combine(root, "owned.json")) == "{" && launch.Calls == 0);
    }

    private static async Task CheckExitAsync(ShellViewModel model, Action<string, bool> check)
    {
        var window = UiVerification.CreateWindow(model); var tray = new FakeTray(); bool closed = false; int verify = 0, stops = 0;
        var lifetime = new DesktopLifetime(window, model, tray, new FakeInstance(), () => { verify++; throw new InvalidOperationException(); },
            () => { stops++; throw new InvalidDataException("synthetic damaged receipt"); }, () => { verify++; return false; }, () => closed = true);
        var sw = Stopwatch.StartNew(); var result = await lifetime.RequestExitAsync();
        check("explicit exit ignores broken verification and cleanup failure, disposes tray", result && closed && tray.Disposed && verify == 0 && stops == 1 && sw.Elapsed < TimeSpan.FromSeconds(3));
        model.ResumeAfterExitFailure();
        window = UiVerification.CreateWindow(model); closed = false; stops = 0; bool commit = false;
        lifetime = new(window, model, new FakeTray(), new FakeInstance(), () => Task.FromResult(false), () => { stops++; return Task.FromResult(true); }, shutdown: () => closed = true);
        result = await lifetime.RequestManagerExitAsync(() => { commit = true; return Task.CompletedTask; });
        check("update handoff never enters clone or clock stop callback", result && commit && closed && stops == 0);
        model.ResumeAfterExitFailure();
    }
    private sealed class FastClock : IWaitClock
    { public long Milliseconds { get; private set; } public Task DelayAsync(int milliseconds, CancellationToken token) { token.ThrowIfCancellationRequested(); Milliseconds += milliseconds; return Task.CompletedTask; } }
    private sealed class FakeProcesses : IProcessSystem
    {
        public List<ProcessIdentity> Live { get; } = []; public List<ProcessIdentity> Fresh { get; } = []; public List<int> Stopped { get; } = [];
        public IReadOnlyList<ProcessIdentity> Snapshot() => Live.ToArray();
        public IReadOnlyList<ProcessIdentity> ManagedRoots(string root) => Fresh.ToArray();
        public bool HasDesktopWindow(IReadOnlyList<ProcessIdentity> processes) => false;
        public bool TryStop(ProcessIdentity identity, bool force) { var found = Live.Find(p => p.Pid == identity.Pid && p.CreationTime == identity.CreationTime && p.Executable == identity.Executable); if (found is null) return false; Stopped.Add(found.Pid); Live.Remove(found); return true; }
    }
    private sealed class NoCommands : ICommandRunner
    { public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token) => throw new InvalidOperationException("No command is authorized in handshake fixture"); }
    private sealed class ForbiddenLaunch : IDesktopLaunchSystem
    {
        public int Calls;
        private Exception Forbidden() { Calls++; return new InvalidOperationException("Exit cannot verify launch"); }
        public Task<LaunchReceipt?> CheckPendingAsync(CancellationToken token) => throw Forbidden();
        public bool IsDesktopReady() => throw Forbidden();
        public Task<DesktopTarget?> FindTargetAsync(CancellationToken token) => throw Forbidden();
        public Task<LaunchReceipt> StartAsync(DesktopTarget target, DesktopLaunchRequest request, CancellationToken token) => throw Forbidden();
        public void Complete(LaunchReceipt receipt) => throw Forbidden();
    }
    private sealed class FakeTray : ITrayHost
    { public bool Disposed { get; private set; } public event EventHandler? RestoreRequested { add { } remove { } } public event EventHandler? ExitRequested { add { } remove { } } public void Show(string text) { } public void Hide() { } public void Dispose() => Disposed = true; }
    private sealed class FakeInstance : ISingleInstanceLease
    { public bool IsPrimary => true; public bool RequestActivation() => true; public Task ListenForActivationAsync(Action activate, CancellationToken token) => Task.CompletedTask; public void Dispose() { } }
}
