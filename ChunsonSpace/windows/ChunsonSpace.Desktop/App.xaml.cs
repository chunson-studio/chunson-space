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
            || (e.Args.Length == 3 && e.Args[0] == "--verify-visual-runtime"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { if (e.Args[0] == "--verify-visual-runtime") await VisualRuntimeVerification.RunAsync(Path.GetFullPath(e.Args[1]), e.Args.Length == 3 ? Path.GetFullPath(e.Args[2]) : null); else if (e.Args[0] == "--verify-ui") UiVerification.Run(Path.GetFullPath(e.Args[1])); else if (e.Args[0] == "--verify-home-motion") await HomeMotionVerification.RunAsync(Path.GetFullPath(e.Args[1])); else await StorageUiVerification.RunAsync(Path.GetFullPath(e.Args[1])); Shutdown(0); }
            catch (Exception exception) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), exception.ToString()); File.WriteAllText(Path.Combine(e.Args[1], "ui-verification.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = false, error = exception.Message })); Shutdown(1); }
            return;
        }
        var cliResult = await CliHost.TryRunAsync(e.Args);
        if (cliResult is not null) { Shutdown(cliResult.Value); return; }
        try
        {
            var workspace = await DesktopWorkspace.FromArgumentsAsync(e.Args);
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
                    bool completed = false;
                    await StopWatcherAsync();
                    try
                    {
                    // Tests and authorization have drained before any owned process is closed.
                    using var guard = await owned.BeginLaunchAsync();
                    var closed = await owned.CloseTrackedAsync();
                    if (!closed.Success) { model.ReportLifecycleFailure("退出未完成", closed.Message); return false; }
                    var restored = await workspace.NativeTimezone!.RestoreAsync();
                    if (!restored.Success) { model.ReportLifecycleFailure("时区恢复未完成", restored.Message); return false; }
                    if (workspace.Accounts is not null) await workspace.Accounts.DisposeAsync();
                    await model.ProxyPanel.NodeTesting.StopAsync();
                    completed = true;
                    return true;
                    }
                    finally { if (!completed && !stopped.IsCancellationRequested) StartWatcher(owned); }
                });
                var adopted = await owned.AdoptAsync(stopped.Token);
                var observed = await owned.PollAsync(stopped.Token);
                model.ApplyOwnedState(observed);
                owned.StatusChanged += (_, status) => Dispatcher.BeginInvoke(() => model.ApplyOwnedState(status));
                if (!adopted.Success) model.ReportLifecycleFailure("进程记录未确认", adopted.Message + "。" + string.Join("；", adopted.Warnings));
                StartWatcher(owned);
                MainWindow.Activated += async (_, _) => { try { model.ApplyOwnedState(await owned.PollAsync(stopped.Token)); } catch (OperationCanceledException) { } };
            }
            else model.ExitRequested += (_, _) => Shutdown();
            MainWindow.Show();
            if (workspace is { IsFixture: false })
            {
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
