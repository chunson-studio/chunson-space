using System.IO;
using System.Reflection;
using ChunsonSpace.Accounts;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

internal static class ReviewLifecycleVerification
{
    public static async Task RunAsync(string directory, Action<string, bool> check)
    {
        var workspace = BackgroundAccountTestsVerification.Fixture(Path.Combine(directory, "tasks"));
        var model = new ShellViewModel(workspace); var account = workspace.Accounts!;
        Directory.CreateDirectory(workspace.ProxyServices.FlClashRoot);
        File.WriteAllText(Path.Combine(workspace.ProxyServices.FlClashRoot, "config.yaml"), "proxies:\n  - {name: fixture, type: http, server: node.fixture.test, port: 443}\n");
        var nodes = model.ProxyPanel.NodeTesting; nodes.LoadCommand.Execute(null); await nodes.CurrentOperation;
        account.OfflineTransport!.HoldEgressResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        nodes.ScreenCommand.Execute(null);
        await BackgroundAccountTestsVerification.Until(() => nodes.IsScreening && workspace.ProxyServices.NodeTesting!.OfflineRuntime!.StartedIds.Count > 0);
        var modelReleased = false;
        account.VisualRuntimeFixture!.ReplyFactory = async (_, _, token) => { try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return ""; } finally { modelReleased = true; } };
        model.Selected = model.FilteredItems.Single(item => item.Name == "A");
        model.OpenVisualTestsCommand.Execute(null); var visual = model.VisualTests!; visual.StartCommand.Execute(null);
        await BackgroundAccountTestsVerification.Until(() => account.VisualRuntimeFixture.Requests.Count == 1);
        visual.CloseCommand.Execute(null);
        model.Selected = model.FilteredItems.Single(item => item.Name == "B");
        account.OfflineTransport.HoldResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.OpenCialloCommand.Execute(null); var ciallo = model.Ciallo!; ciallo.StartCommand.Execute(null);
        await BackgroundAccountTestsVerification.Until(() => account.OfflineTransport.Requests.Any(request => request.Url == AccountEndpoints.Responses));
        ciallo.CloseCommand.Execute(null);
        var window = UiVerification.CreateWindow(model); var tray = new Tray(); var lease = new Lease(); var closed = false; var shutdown = false;
        using (var lifecycle = new DesktopLifetime(window, model, tray, lease, () => Task.FromResult(false), async () =>
        {
            closed = modelReleased && !model.IsModelTestRunning && !nodes.IsBusy && visual.CurrentOperation.IsCompleted && ciallo.CurrentOperation.IsCompleted;
            await account.DisposeAsync(); return closed;
        }, shutdown: () => shutdown = true))
        {
            check("exit command remains actionable while model and IP tasks run", model.ExitCommand.CanExecute(null));
            check("exit cancels all tasks and waits for their cleanup before owned close", await lifecycle.RequestExitAsync() && closed && shutdown && tray.Disposed && visual.Result?.Cancelled == true);
            var nodeRuntime = workspace.ProxyServices.NodeTesting!.OfflineRuntime!;
            check("exit joins node core release and Ciallo cancellation record", nodeRuntime.StartedIds.Count == nodeRuntime.StopCalls && account.Accounts.CialloHistory("B").First().Result.Cancelled);
        }
        var loginWorkspace = BackgroundAccountTestsVerification.Fixture(Path.Combine(directory, "login"));
        var loginModel = new ShellViewModel(loginWorkspace); var loginServices = loginWorkspace.Accounts!;
        loginServices.OfflineCallbacks!.HoldCallback = true;
        loginModel.Selected = loginModel.FilteredItems.Single(item => item.Name == "A");
        loginModel.LoginCommand.Execute(null);
        await TwoStageLoginVerification.ContinueAsync(loginModel, loginServices, loginModel.CurrentOperation);
        await BackgroundAccountTestsVerification.Until(() => loginServices.Login.Status.Stage == LoginStage.Browser);
        var loginWindow = UiVerification.CreateWindow(loginModel); var loginClosed = false;
        using (var lifecycle = new DesktopLifetime(loginWindow, loginModel, new Tray(), new Lease(), () => Task.FromResult(false), async () =>
        {
            loginClosed = !loginModel.IsLoggingIn && loginModel.CurrentOperation.IsCompleted;
            await loginServices.DisposeAsync(); return loginClosed;
        }, shutdown: () => { }))
            check("exit cancels waiting authorization and joins its finally", await lifecycle.RequestExitAsync() && loginClosed);
        var newWorkspace = BackgroundAccountTestsVerification.Fixture(Path.Combine(directory, "new-login"));
        var newModel = new ShellViewModel(newWorkspace); var newServices = newWorkspace.Accounts!;
        newServices.OfflineCallbacks!.HoldCallback = true;
        newModel.NewAccountLoginCommand.Execute(null); var onboarding = newModel.Onboarding!;
        onboarding.AuthorizeCommand.Execute(null);
        await BackgroundAccountTestsVerification.Until(() => newServices.Login.Status.Stage == LoginStage.AccountPreparation);
        var newWindow = UiVerification.CreateWindow(newModel); var newClosed = false;
        using (var lifecycle = new DesktopLifetime(newWindow, newModel, new Tray(), new Lease(), () => Task.FromResult(false), async () =>
        {
            newClosed = !newModel.IsLoggingIn && newModel.CurrentOperation.IsCompleted && newServices.OfflineNewAccountBrowser!.CleanupCalls > 0;
            await newServices.DisposeAsync(); return newClosed;
        }, shutdown: () => { }))
            check("exit cancels account preparation before callback starts and releases its browser", await lifecycle.RequestExitAsync() && newClosed && newWorkspace.Profiles.ListAccounts().Count == 2 && newServices.OfflineCallbacks.Starts == 0);
        await onboarding.CurrentOperation;
        var retryWorkspace = BackgroundAccountTestsVerification.Fixture(Path.Combine(directory, "retry-exit"));
        var retryModel = new ShellViewModel(retryWorkspace); var retryNodes = retryModel.ProxyPanel.NodeTesting;
        Directory.CreateDirectory(retryWorkspace.ProxyServices.FlClashRoot);
        File.WriteAllText(Path.Combine(retryWorkspace.ProxyServices.FlClashRoot, "config.yaml"), "proxies:\n  - {name: fixture, type: http, server: node.fixture.test, port: 443}\n");
        retryNodes.LoadCommand.Execute(null); await retryNodes.CurrentOperation;
        var retryRuntime = retryWorkspace.ProxyServices.NodeTesting!.OfflineRuntime!; var retryWindow = UiVerification.CreateWindow(retryModel);
        var allowClose = false;
        using (var lifecycle = new DesktopLifetime(retryWindow, retryModel, new Tray(), new Lease(), () => Task.FromResult(false), () => Task.FromResult(allowClose), shutdown: () => { }))
        {
            check("failed owned close restores manual IP actions without restarting requests", !await lifecycle.RequestExitAsync() && retryNodes.SupplementCommand.CanExecute(null) && retryRuntime.StartedIds.Count == 0);
            retryNodes.SupplementCommand.Execute(null); await retryNodes.CurrentOperation;
            check("manual IP supplement really runs after failed exit", retryRuntime.StartedIds.Count > 0 && retryRuntime.StartedIds.Count == retryRuntime.StopCalls && !retryNodes.IsBusy);
            allowClose = true;
            check("exit remains retryable after manual IP supplement", await lifecycle.RequestExitAsync());
        }
        await retryWorkspace.Accounts!.DisposeAsync();
        var failedCoreWorkspace = BackgroundAccountTestsVerification.Fixture(Path.Combine(directory, "unconfirmed-core"));
        var failedCoreModel = new ShellViewModel(failedCoreWorkspace); var failedCoreNodes = failedCoreModel.ProxyPanel.NodeTesting;
        Directory.CreateDirectory(failedCoreWorkspace.ProxyServices.FlClashRoot);
        File.WriteAllText(Path.Combine(failedCoreWorkspace.ProxyServices.FlClashRoot, "config.yaml"), "proxies:\n  - {name: fixture, type: http, server: node.fixture.test, port: 443}\n");
        failedCoreNodes.LoadCommand.Execute(null); await failedCoreNodes.CurrentOperation;
        var failedCoreRuntime = failedCoreWorkspace.ProxyServices.NodeTesting!.OfflineRuntime!; failedCoreRuntime.FailStop = true;
        failedCoreNodes.ScreenCommand.Execute(null); await failedCoreNodes.CurrentOperation;
        var unconfirmedWindow = UiVerification.CreateWindow(failedCoreModel); var ownedCloseCalls = 0; var unconfirmedShutdown = false;
        using (var lifecycle = new DesktopLifetime(unconfirmedWindow, failedCoreModel, new Tray(), new Lease(), () => Task.FromResult(false), () => { ownedCloseCalls++; return Task.FromResult(true); }, shutdown: () => unconfirmedShutdown = true))
        {
            check("unconfirmed IP core cleanup blocks owned close and shutdown with its reason", !await lifecycle.RequestExitAsync() && ownedCloseCalls == 0 && !unconfirmedShutdown && failedCoreModel.Feedback.Contains("IP 筛选核心"));
            failedCoreRuntime.FailStop = false;
            failedCoreNodes.LoadCommand.Execute(null); await failedCoreNodes.CurrentOperation;
            failedCoreNodes.ScreenCommand.Execute(null); await failedCoreNodes.CurrentOperation;
            check("reload and later successful IP batches do not erase an unresolved core cleanup", failedCoreRuntime.StartedIds.Count == 2 && !await lifecycle.RequestExitAsync() && ownedCloseCalls == 0 && !unconfirmedShutdown);
            UiVerification.AssertWindowsOutsideDesktop();
            check("unconfirmed cleanup fixture cannot appear on the desktop or take focus", unconfirmedWindow.IsVisible && !unconfirmedWindow.IsActive && !unconfirmedWindow.ShowInTaskbar);
        }
        unconfirmedWindow.Close();
        await failedCoreWorkspace.Accounts!.DisposeAsync();
        var writing = new ShellViewModel(); var writingWindow = UiVerification.CreateWindow(writing); var writesClosed = 0;
        var setBusy = typeof(ShellViewModel).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setBusy.Invoke(writing, [true]);
        using (var lifecycle = new DesktopLifetime(writingWindow, writing, new Tray(), new Lease(), () => Task.FromResult(false), () => { writesClosed++; return Task.FromResult(true); }, shutdown: () => { }))
        {
            check("noninterruptible writes reject exit with visible reason", !await lifecycle.RequestExitAsync() && writesClosed == 0 && writing.FeedbackTitle == "暂不能退出" && writing.Feedback.Contains("写入"));
            setBusy.Invoke(writing, [false]);
            check("exit can be retried after the write finishes", await lifecycle.RequestExitAsync() && writesClosed == 1);
        }
    }
    private sealed class Tray : ITrayHost
    {
        public bool Disposed { get; private set; }
        public event EventHandler? RestoreRequested { add { } remove { } }
        public event EventHandler? ExitRequested { add { } remove { } }
        public void Show(string tooltip) { } public void Hide() { } public void Dispose() => Disposed = true;
    }
    private sealed class Lease : ISingleInstanceLease
    {
        public bool IsPrimary => true;
        public bool RequestActivation() => true;
        public Task ListenForActivationAsync(Action activate, CancellationToken token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        public void Dispose() { }
    }
}
