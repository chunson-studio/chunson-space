using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChunsonSpace.Application;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

// Explicit --verify-ui unified-reset only: generated files, fake processes and offscreen WPF.
internal static class UnifiedResetVerification
{
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>(); var screenshots = new List<string>(); bool passed = false;
        void Check(string name, bool value)
        { checks.Add(new { name, passed = value }); if (!value) throw new InvalidOperationException(name); }
        string Write(string path, string text = "synthetic sentinel")
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path; }
        var listener = new UiVerification.BindingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var root = Path.Combine(directory, "fixture"); Write(Path.Combine(root, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var workspace = DesktopWorkspace.CreateManagedFixture(root);
        var storage = (ManagedCloneStorage)workspace.SwitchStorage; var runtime = workspace.OfflineRuntime!;
        var model = new ShellViewModel(workspace); MainWindow? window = null;
        using var release = new ManualResetEventSlim(false);
        try
        {
            Check("new and missing stored preference default to tray", new AppSettings().CloseToTray && workspace.Profiles.ReadSettings().CloseToTray && model.CloseToTray);
            var original = Write(Path.Combine(root, "original", "auth.json"));
            var project = Write(Path.Combine(root, "project", "keep"));
            var accountSentinel = Write(Path.Combine(workspace.AccountRoot, "keep"));
            var oldRecovery = Write(Path.Combine(workspace.ManagedPaths!.RecoveryRoot, "historical", "keep"));
            Check("empty account library and no selection can reset", model.Selected is null && model.Active is null && model.ResetManagedCommand.CanExecute(null));
            var cache = Write(Path.Combine(workspace.ActiveHome, "sessions", "cache"));
            runtime.HasPendingLaunch = true;
            // The button must not inspect an account identity or attempt to parse credentials.
            Write(Path.Combine(workspace.ManagedPaths.RuntimeRoot, ".chunson-active.json"), "{");
            Write(Path.Combine(workspace.ActiveHome, "auth.json"), "{");
            model.ResetManagedCommand.Execute(null); await model.CurrentOperation;
            Check("no account pending reset discards corrupt identity and remains closed", model.FeedbackTitle == "分身已重置" && !File.Exists(cache)
                && storage.ReadRuntimeReference() is null && runtime.LastLaunchRequest is null && runtime.PendingChecks == 0 && !runtime.DesktopAlive && !runtime.HasPendingLaunch);
            Check("exited state can reset again", model.ResetManagedCommand.CanExecute(null));
            runtime.StopSucceeds = false; cache = Write(Path.Combine(workspace.ActiveHome, "sessions", "retained"));
            model.ResetManagedCommand.Execute(null); await model.CurrentOperation;
            Check("stop failure is explicit and releases busy without discarding", model.FeedbackTitle == "重置未完成" && !model.IsBusy && File.Exists(cache) && model.ResetManagedCommand.CanExecute(null));
            runtime.StopSucceeds = true; runtime.ConfirmStopSucceeds = false;
            model.ResetManagedCommand.Execute(null); await model.CurrentOperation;
            Check("unconfirmed stop retains runtime and never reports success", model.FeedbackTitle == "重置未完成" && File.Exists(cache));
            runtime.ConfirmStopSucceeds = true;
            using (var locked = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                model.ResetManagedCommand.Execute(null); await model.CurrentOperation;
                Check("locked own runtime reports incomplete without a backup", model.FeedbackTitle == "重置未完成" && File.Exists(cache) && !model.IsBusy);
            }
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.BeforeStop = () => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(8)); };
            model.ResetManagedCommand.Execute(null); var operation = model.CurrentOperation;
            Check("immediate progress and busy gate", model.ResetManagedLabel == "正在重置…" && model.FeedbackTitle == "正在重置…" && !model.ResetManagedCommand.CanExecute(null));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var calls = runtime.StopCalls; model.ResetManagedCommand.Execute(null);
            bool responsive = false; await Dispatcher.CurrentDispatcher.InvokeAsync(() => responsive = true, DispatcherPriority.Background);
            Check("worker keeps dispatcher responsive and prevents duplicate dispatch", responsive && !operation.IsCompleted && runtime.StopCalls == calls && ReferenceEquals(operation, model.CurrentOperation));
            release.Set(); await operation; runtime.BeforeStop = null;
            Check("retry completes, restores label and preserves sentinels", model.FeedbackTitle == "分身已重置" && model.ResetManagedLabel == "重置分身"
                && !File.Exists(cache) && new[] { original, project, accountSentinel, oldRecovery }.All(File.Exists)
                && Directory.GetFiles(workspace.ManagedPaths.RecoveryRoot, "*", SearchOption.AllDirectories).SequenceEqual(new[] { oldRecovery }));

            await CheckForceAsync(Path.Combine(directory, "force"), Check);

            model.Navigate("Settings"); window = UiVerification.CreateWindow(model); window.Show();
            var reset = (Button)window.FindName("ResetManagedButton");
            Check("one maintenance action bound to the shared reset command", reset.Command == model.ResetManagedCommand
                && window.FindName("BottomRebuildButton") is null && window.FindName("ManagedCloneResetButton") is null
                && window.FindName("OneClickEnvironmentRebuildButton") is null
                && ((Dashboard)window.FindName("DashboardPage")).FindName("DashboardClean") is null);
            Check("normal start and close remain bound", ((Button)window.FindName("BottomLaunchButton")).Command == model.HomeLaunchCommand
                && ((Button)window.FindName("BottomCloseManagedButton")).Command == model.CloseManagedCommand);
            foreach (var dark in new[] { false, true })
            {
                model.DarkTheme = dark; UiVerification.ArrangeForCapture(window.RenderRoot, 1280, 850);
                Check((dark ? "night" : "paper") + " reset visible and enabled without accounts", UiVerification.FullyVisible(reset, window.RenderRoot) && reset.IsEnabled && reset.Content?.ToString() == "重置分身");
                var bitmap = new RenderTargetBitmap(1280, 850, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window.RenderRoot);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.Combine(directory, dark ? "reset-night.png" : "reset-paper.png"); using (var stream = File.Create(path)) encoder.Save(stream); screenshots.Add(path);
            }
            var preference = workspace.Profiles.ReadSettingsDocument(); preference["unrelated"] = new JsonObject { ["keep"] = 17 };
            new PhysicalFileStorage().WriteAtomic(Path.Combine(workspace.AccountRoot, "settings.json"), System.Text.Encoding.UTF8.GetBytes(preference.ToJsonString()));
            model.CloseToTray = false; model.SaveSettingsCommand.Execute(null);
            Check("optional false preference persists and other fields survive", !workspace.Profiles.ReadSettings().CloseToTray && workspace.Profiles.ReadSettingsDocument()["unrelated"]?["keep"]?.GetValue<int>() == 17);
            model.CloseToTray = true; model.SaveSettingsCommand.Execute(null);
            Check("tray preference saves through existing storage", workspace.Profiles.ReadSettings().CloseToTray);
            window.Close(); window = null;
            await CheckClosingAsync(model, Check);
            Check("no binding errors or fixture network requests", listener.Errors.Count == 0 && workspace.Accounts!.OfflineTransport!.Requests.Count == 0);
            passed = true;
        }
        finally
        {
            release.Set(); runtime.BeforeStop = null; window?.Close(); model.DisposeUpdates(); await workspace.Accounts!.DisposeAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new
            { passed, scope = "unified-reset", checks, bindingErrors = listener.Errors, screenshots, realRequests = 0, actualAccountOperations = false,
              actualCloneResets = 0, actualUserProcessStartsOrStops = 0, actualPreferenceWrites = 0, actualProxyChanges = false, actualFingerprintRequests = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static async Task CheckForceAsync(string root, Action<string, bool> check)
    {
        var paths = new ManagedClonePaths(root); var storage = new ManagedCloneStorage(paths);
        var system = new FakeProcesses(); var clock = new FastClock();
        var ownedPath = Path.Combine(root, "owned.json"); var store = new JsonOwnedProcessStore(ownedPath);
        var scope = new ManagedCloneProcessScope(system, store, paths.DesktopRoot);
        var processes = new CodexProcessService(system, clock, scope);
        var launch = new WindowsDesktopLaunchSystem(paths.LaunchStateRoot, system, new NoCommands(), clock);
        var ownership = new OwnedProcessLifecycleService(system, clock, store, null!, launch, scope);
        var runtime = new WindowsRuntimeOperations(processes, null!, launch, null!, ownership);
        var maintenance = new MaintenanceService(runtime);
        var own = new ProcessIdentity(41, 0, "Codex.exe", "C:/fixture/OpenAI/Codex/Codex.exe", 100);
        var child = own with { Pid = 42, ParentPid = 41, CreationTime = 110 };
        var original = own with { Pid = 55, CreationTime = 120 };
        var reused = own with { Pid = 66, CreationTime = 300 };
        system.Live.AddRange([own, child, original, reused]); system.Fresh.Add(own);
        store.TryWrite([new(reused with { CreationTime = 200 }, OwnedProcessKind.Desktop, "", "")]);
        var cache = Path.Combine(paths.CodexHome, "cache"); File.WriteAllText(cache, "synthetic runtime");
        var account = Path.Combine(paths.AccountRoot, "keep"); Directory.CreateDirectory(paths.AccountRoot); File.WriteAllText(account, "account sentinel");
        foreach (var content in new[] { "missing", "{" })
        {
            var directory = Path.Combine(paths.LaunchStateRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            if (content != "missing") File.WriteAllText(Path.Combine(directory, "state.json"), content);
        }
        var result = await maintenance.ResetManagedRuntimeAsync(storage, discard: true);
        check("native maintenance cancels missing/bad handshakes and force stops exact root/child", result.Success && system.Forced.Order().SequenceEqual(new[] { 41, 42 }) && !File.Exists(cache));
        check("same-name original and reused PID survive reset with account sentinel", system.Live.Select(p => p.Pid).Order().SequenceEqual(new[] { 55, 66 }) && File.Exists(account));
        check("reset creates no handshake or ownership backup", !Directory.Exists(paths.RecoveryRoot) && Directory.GetFiles(root, "*.recovery-*", SearchOption.AllDirectories).Length == 0);

        system.Live.Add(own); system.Fresh.Clear(); system.Fresh.Add(own); File.WriteAllText(ownedPath, "{");
        result = await maintenance.ResetManagedRuntimeAsync(storage, discard: true);
        check("corrupt ownership recovers from exact managed root without archive", result.Success && system.Live.All(p => p.Pid != own.Pid) && Directory.GetFiles(root, "*.recovery-*", SearchOption.AllDirectories).Length == 0);
        system.Fresh.Clear(); File.WriteAllText(ownedPath, "{"); File.WriteAllText(cache, "keep on unknown");
        result = await maintenance.ResetManagedRuntimeAsync(storage, discard: true);
        check("unresolvable corrupt ownership is retained and never claims cleanup", !result.Success && File.Exists(cache) && File.ReadAllText(ownedPath) == "{");

        var lostParent = own with { Pid = 71 }; var orphan = own with { Pid = 72, ParentPid = 71, CreationTime = 130 };
        system.Live.AddRange([own, orphan]); system.Fresh.Add(own);
        store.TryWrite([new(own, OwnedProcessKind.Desktop, "", ""), new(lostParent, OwnedProcessKind.Desktop, "", "")]);
        result = await maintenance.ResetManagedRuntimeAsync(storage, discard: true);
        check("unknown orphan does not prevent known target stop but blocks discard", !result.Success && system.Live.All(p => p.Pid != own.Pid) && system.Live.Any(p => p.Pid == orphan.Pid) && File.Exists(cache));
        system.Live.Remove(orphan); system.Fresh.Clear(); store.TryWrite([]);
        var markerPath = Path.Combine(paths.RuntimeRoot, ManagedCloneStorage.RuntimeMarker); var marker = File.ReadAllText(markerPath);
        File.WriteAllText(markerPath, "{}"); var stopCount = system.Forced.Count;
        result = await maintenance.ResetManagedRuntimeAsync(storage, discard: true);
        check("unowned runtime path is refused before process or data mutation", !result.Success && File.Exists(cache) && system.Forced.Count == stopCount && File.Exists(account));
        File.WriteAllText(markerPath, marker);
        result = await maintenance.ResetManagedRuntimeAsync(storage, discard: true);
        check("no owned processes needs no account or handshake to clear data", result.Success && !File.Exists(cache) && system.Live.Select(p => p.Pid).Order().SequenceEqual(new[] { 55, 66 }));
    }

    private static async Task CheckClosingAsync(ShellViewModel model, Action<string, bool> check)
    {
        int stops = 0, verify = 0, shutdown = 0, detached = 0;
        var window = UiVerification.CreateWindow(model); window.Show(); var tray = new FakeTray();
        using (var life = new DesktopLifetime(window, model, tray, new FakeInstance(), () => { verify++; throw new InvalidOperationException(); },
            () => { stops++; throw new IOException("fixture cleanup failure"); }, () => { verify++; return false; }, () => shutdown++, () => { detached++; return Task.CompletedTask; }))
        {
            ((Button)window.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check("actual X Closing hides to tray without stop detach or shutdown", !window.IsVisible && stops == 0 && verify == 0 && shutdown == 0 && detached == 0 && !tray.Disposed);
            tray.Exit(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check("explicit tray exit ignores environment and stop failure, closes owned callback once", stops == 1 && verify == 0 && shutdown == 1 && detached == 1 && tray.Disposed);
        }
        model.ResumeAfterExitFailure(); window = UiVerification.CreateWindow(model); bool committed = false; stops = 0; shutdown = 0;
        using (var life = new DesktopLifetime(window, model, new FakeTray(), new FakeInstance(), () => throw new InvalidOperationException(),
            () => { stops++; return Task.FromResult(true); }, shutdown: () => shutdown++))
        {
            check("update handoff bypasses tray and never stops clone or clock", await life.RequestManagerExitAsync(() => { committed = true; return Task.CompletedTask; }) && committed && stops == 0 && shutdown == 1);
        }
        model.ResumeAfterExitFailure(); model.CloseToTray = false; window = UiVerification.CreateWindow(model); window.Show(); stops = 0; shutdown = 0;
        using (var life = new DesktopLifetime(window, model, new FakeTray(), new FakeInstance(), () => Task.FromResult(false), () => { stops++; return Task.FromResult(true); }, shutdown: () => shutdown++))
        {
            ((Button)window.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check("optional false preference retains explicit close behavior", stops == 1 && shutdown == 1);
        }
        model.ResumeAfterExitFailure();
    }
    private sealed class FastClock : IWaitClock
    { public long Milliseconds { get; private set; } public Task DelayAsync(int milliseconds, CancellationToken token) { token.ThrowIfCancellationRequested(); Milliseconds += milliseconds; return Task.CompletedTask; } }
    private sealed class FakeProcesses : IProcessSystem
    {
        public List<ProcessIdentity> Live { get; } = []; public List<ProcessIdentity> Fresh { get; } = []; public List<int> Forced { get; } = [];
        public IReadOnlyList<ProcessIdentity> Snapshot() => Live.ToArray();
        public IReadOnlyList<ProcessIdentity> ManagedRoots(string root) => Fresh.Where(f => Live.Contains(f)).ToArray();
        public bool HasDesktopWindow(IReadOnlyList<ProcessIdentity> processes) => false;
        public bool TryStop(ProcessIdentity identity, bool force)
        { if (!force || !Live.Contains(identity)) return false; Forced.Add(identity.Pid); Live.Remove(identity); return true; }
    }
    private sealed class NoCommands : ICommandRunner
    { public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token) => throw new InvalidOperationException("No commands in reset fixture"); }
    private sealed class FakeTray : ITrayHost
    {
        public bool Disposed { get; private set; }
        public event EventHandler? RestoreRequested { add { } remove { } }
        public event EventHandler? ExitRequested;
        public void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);
        public void Show(string tooltip) { } public void Hide() { } public void Dispose() => Disposed = true;
    }
    private sealed class FakeInstance : ISingleInstanceLease
    { public bool IsPrimary => true; public bool RequestActivation() => true; public Task ListenForActivationAsync(Action activate, CancellationToken token) => Task.CompletedTask; public void Dispose() { } }
}
