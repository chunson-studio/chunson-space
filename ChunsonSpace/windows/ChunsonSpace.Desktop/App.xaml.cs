using System.IO;
using System.Windows;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

public partial class App : System.Windows.Application
{
    private DesktopLifetime? lifetime;
    private readonly CancellationTokenSource stopped = new();
    private CancellationTokenSource? watcherStopped;
    private Task? watcher;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 3 && e.Args[0] == "--verify-ui" && e.Args[2] == "drawing-presets")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await VisualTestVerification.RunPresetsAsync(Path.GetFullPath(e.Args[1])); Shutdown(0); }
            catch (Exception error) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--verify-ui" && e.Args[2] is "navigation-performance" or "navigation-refresh")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args[2] == "navigation-performance") await NavigationPerformanceVerification.RunAsync(Path.GetFullPath(e.Args[1]));
                else await NavigationRefreshVerification.RunAsync(Path.GetFullPath(e.Args[1]));
                Shutdown(0);
            }
            catch (Exception error) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--verify-ui" && e.Args[2] is "online-update" or "online-update-in-use-plan")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args[2] == "online-update-in-use-plan") await OnlineUpdateVerification.PrepareInUseAsync(Path.GetFullPath(e.Args[1]));
                else await OnlineUpdateVerification.RunAsync(Path.GetFullPath(e.Args[1]));
                Shutdown(0);
            }
            catch (Exception error) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-ip-separation")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await IpSeparationVerification.RunAsync(Path.GetFullPath(e.Args[1])); Shutdown(0); }
            catch (Exception error) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-timezone-match")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await LocalEnvironmentVerification.RunTimezoneMatchAsync(Path.GetFullPath(e.Args[1])); Shutdown(0); }
            catch (Exception error) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.FirstOrDefault() == "--inspect-account-trace")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args.Length != 8 || File.Exists(e.Args[7])) throw new InvalidDataException("需显式来源、身份与新报告路径。");
                var result = ChunsonSpace.Accounts.AccountTraceImport.Inspect(e.Args[1], e.Args[2], e.Args[3], e.Args[4], e.Args[5]);
                // Sixth parameter pins the requested mode to offline, preventing accidental import use.
                if (e.Args[6] != "offline") throw new InvalidDataException("仅允许离线重解析。");
                using var report = new FileStream(e.Args[7], FileMode.CreateNew);
                System.Text.Json.JsonSerializer.Serialize(report, result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                Shutdown(0);
            }
            catch (Exception) { Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-concise-status")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await SafetyBufferingVerification.RunStandalone(Path.GetFullPath(e.Args[1])); Shutdown(0); }
            catch (Exception error) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.FirstOrDefault() == "--import-account-trace")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args.Length != 8 || File.Exists(e.Args[7]) || !Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(e.Args[7])))) throw new InvalidDataException("导入参数或新报告路径无效。");
                var result = ChunsonSpace.Accounts.AccountTraceImport.Import(new ChunsonSpace.Core.FileProfileStore(e.Args[1]), e.Args[2], e.Args[3], e.Args[4], e.Args[5], e.Args[6]);
                using var report = new FileStream(e.Args[7], FileMode.CreateNew);
                System.Text.Json.JsonSerializer.Serialize(report, result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                Shutdown(0);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException or ChunsonSpace.Core.StorageException)
            {
                if (e.Args.Length == 8 && !File.Exists(e.Args[7]))
                    try { using var report = new FileStream(e.Args[7], FileMode.CreateNew); System.Text.Json.JsonSerializer.Serialize(report, new { completed = false, applied = false, message = error is InvalidDataException ? error.Message : "测试文件不可读或记录格式无效；未导入。" }); }
                    catch (Exception reportError) when (reportError is IOException or UnauthorizedAccessException or ArgumentException) { }
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-clear-test-history")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await TestHistoryClearVerification.RunStandalone(Path.GetFullPath(e.Args[1])); Shutdown(0); }
            catch (Exception error) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-account-health")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await AccountHealthVerification.RunStandalone(Path.GetFullPath(e.Args[1])); Shutdown(0); }
            catch (Exception error) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-account-marks")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await AccountMarksVerification.RunStandalone(Path.GetFullPath(e.Args[1])); Shutdown(0); }
            catch (Exception error) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-results-gallery")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await ResultsGalleryVerification.RunStandalone(Path.GetFullPath(e.Args[1])); Shutdown(0); }
            catch (Exception error) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--browser-timezone")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { Shutdown(await ChunsonSpace.Accounts.BrowserTimezoneController.RunHelperAsync(e.Args[1])); }
            catch (Exception) { Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--write-brand-icon")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            File.WriteAllBytes(Path.GetFullPath(e.Args[1]), BrandIcon.CreateIco()); Shutdown(); return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-safety-buffering-ui")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await SafetyBufferingVerification.RunStandalone(Path.GetFullPath(e.Args[1])); Shutdown(0); }
            catch (Exception error) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        if ((e.Args.Length == 2 && e.Args[0] is "--verify-ui" or "--verify-storage-ui" or "--verify-home-motion" or "--verify-visual-runtime")
            || (e.Args.Length == 3 && (e.Args[0] == "--verify-visual-runtime" || e.Args[0] == "--verify-ui" && e.Args[2] is "environment-actions" or "account-login-time" or "flclash-direct" or "maintenance-v2" or "maintenance-v2-final" or "delete-overlay" or "unified-reset")))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { if (e.Args.Length == 3 && e.Args[0] == "--verify-ui" && e.Args[2] == "unified-reset") await UnifiedResetVerification.RunAsync(Path.GetFullPath(e.Args[1])); else if (e.Args.Length == 3 && e.Args[0] == "--verify-ui" && e.Args[2] == "delete-overlay") await DeleteOverlayVerification.RunAsync(Path.GetFullPath(e.Args[1])); else if (e.Args.Length == 3 && e.Args[0] == "--verify-ui" && e.Args[2] is "maintenance-v2" or "maintenance-v2-final") await MaintenanceV2Verification.RunAsync(Path.GetFullPath(e.Args[1]), e.Args[2] == "maintenance-v2-final"); else if (e.Args[0] == "--verify-visual-runtime") await VisualRuntimeVerification.RunAsync(Path.GetFullPath(e.Args[1]), e.Args.Length == 3 ? Path.GetFullPath(e.Args[2]) : null); else if (e.Args[0] == "--verify-ui" && e.Args.Length == 3 && e.Args[2] == "flclash-direct") await StorageUiVerification.RunFlClashDirectAsync(Path.GetFullPath(e.Args[1])); else if (e.Args[0] == "--verify-ui" && e.Args.Length == 3 && e.Args[2] == "account-login-time") await AccountLoginTimeVerification.RunAsync(Path.GetFullPath(e.Args[1])); else if (e.Args[0] == "--verify-ui" && e.Args.Length == 3) await UiVerification.RunEnvironmentActionsAsync(Path.GetFullPath(e.Args[1])); else if (e.Args[0] == "--verify-ui") UiVerification.Run(Path.GetFullPath(e.Args[1])); else if (e.Args[0] == "--verify-home-motion") await HomeMotionVerification.RunAsync(Path.GetFullPath(e.Args[1])); else await StorageUiVerification.RunAsync(Path.GetFullPath(e.Args[1])); Shutdown(0); }
            catch (Exception exception) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), exception.ToString()); if (!File.Exists(Path.Combine(e.Args[1], "ui-verification.json"))) File.WriteAllText(Path.Combine(e.Args[1], "ui-verification.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = false, error = exception.Message })); Shutdown(1); }
            return;
        }
        var startupArgs = e.Args.Where(a => a is not ("--update-complete" or "--update-failed")).ToArray();
        var cliResult = await CliHost.TryRunAsync(startupArgs);
        if (cliResult is not null) { Shutdown(cliResult.Value); return; }
        try
        {
            var workspace = await DesktopWorkspace.FromArgumentsAsync(startupArgs);
            ISingleInstanceLease? instance = null;
            if (workspace?.Ownership is not null)
            {
                instance = new SingleInstanceLease();
                if (!instance.IsPrimary) { instance.RequestActivation(); instance.Dispose(); Shutdown(); return; }
            }
            if (workspace is { IsFixture: false })
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                if (!await Chunson.Setup.FlClashSetupWindow.EnsureAsync(stopped.Token))
                { instance?.Dispose(); Shutdown(); return; }
            }
            var model = new ShellViewModel(workspace);
            MainWindow = new MainWindow { DataContext = model };
            if (workspace?.Ownership is not null)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var owned = workspace.Ownership;
                lifetime = new((MainWindow)MainWindow, model, new WindowsTrayHost(), instance!, () => owned.TrackedDesktopAliveAsync(), async () =>
                {
                    using var exitBudget = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                    var token = exitBudget.Token;
                    try { await StopWatcherAsync().WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { }
                    var operations = new List<Task>();
                    operations.Add(Task.Run(async () =>
                    {
                        try
                        {
                            using var receiptBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                            receiptBudget.CancelAfter(TimeSpan.FromSeconds(1));
                            using var guard = await owned.BeginLaunchAsync(receiptBudget.Token);
                            try { workspace.NativeLaunch!.CancelPendingForMaintenance(receiptBudget.Token); } catch (Exception) { }
                        }
                        catch (Exception) { }
                        // A busy or damaged receipt cannot veto stopping identities we already hold.
                        try { await owned.CloseForExplicitExitAsync(token); } catch (Exception) { }
                    }));
                    operations.Add(Task.Run(() => Chunson.Widgets.ClockLauncher.StopManaged()));
                    if (workspace.Accounts is not null) operations.Add(workspace.Accounts.DisposeAsync().AsTask());
                    operations.Add(model.ProxyPanel.NodeTesting.StopAsync());
                    try { await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(7)); } catch (Exception) { }
                    return true;
                }, detachManager: StopWatcherAsync);
                model.ExitManagerForUpdate = commit => lifetime.RequestManagerExitAsync(commit);
                var adopted = await owned.AdoptAsync(stopped.Token);
                var observed = await owned.PollAsync(stopped.Token);
                model.ApplyOwnedState(observed);
                owned.StatusChanged += (_, status) => Dispatcher.BeginInvoke(() => model.ApplyOwnedState(status));
                if (!adopted.Success) model.ReportLifecycleFailure("进程记录未确认", adopted.Message + "。" + string.Join("；", adopted.Warnings));
                StartWatcher(owned);
                MainWindow.Activated += async (_, _) => { try { model.ApplyOwnedState(await owned.PollAsync(stopped.Token)); } catch (OperationCanceledException) { } };
            }
            else { model.ExitRequested += (_, _) => Shutdown(); model.ExitManagerRequested += (_, _) => Shutdown(); }
            MainWindow.Show();
            if (workspace is { IsFixture: false })
            {
                if (e.Args.Contains("--update-complete")) model.UpdateCompleted();
                if (e.Args.Contains("--update-failed")) model.UpdateFailed();
                _ = model.CheckForUpdatesAsync(false);
                model.ShowClock();
                _ = model.ProxyPanel.NodeTesting.StartBackgroundAsync();
                _ = model.ProxyPanel.RefreshDnsStatusAsync();
                _ = model.RefreshLocalEnvironmentAsync();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ChunsonSpace.Core.StorageException)
        { MessageBox.Show(exception.Message, "Chunson Space", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    private void StartWatcher(OwnedProcessLifecycleService owned)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
        watcherStopped = cancellation;
        watcher = Task.Run(() => owned.WatchAsync(cancellation.Token));
    }
    private async Task StopWatcherAsync()
    {
        watcherStopped?.Cancel();
        if (watcher is not null) { try { await watcher; } catch (OperationCanceledException) { } }
        watcherStopped?.Dispose(); watcherStopped = null; watcher = null;
    }
    protected override void OnExit(ExitEventArgs e)
    {
        ChunsonSpace.Accounts.LocalArtworkSession.StopAll();
        Environment.ExitCode = e.ApplicationExitCode;
        stopped.Cancel(); watcherStopped?.Cancel();
        if (watcher is not null) { try { watcher.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }
        watcherStopped?.Dispose(); lifetime?.Dispose(); stopped.Dispose(); base.OnExit(e);
    }
}
