using System.ComponentModel;
using System.Windows;
using FAswitch.Windows;
using FAswitch.Accounts;
using FAswitch.Core;

namespace FAswitch.Desktop;

public sealed class DesktopLifetime : IDisposable, IAsyncDisposable
{
    private readonly MainWindow window;
    private readonly ShellViewModel model;
    private readonly ITrayHost tray;
    private readonly ISingleInstanceLease instance;
    private readonly Func<Task<bool>> trackedDesktopAlive;
    private readonly Func<Task<bool>> closeOwnedProcesses;
    private readonly Func<bool> confirmExit;
    private readonly Action shutdown;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task activationListener;
    private bool exiting;
    private bool disposed;
    private bool checkingExit;

    public DesktopLifetime(MainWindow window, ShellViewModel model, ITrayHost tray, ISingleInstanceLease instance,
        Func<Task<bool>> trackedDesktopAlive, Func<Task<bool>> closeOwnedProcesses, Func<bool>? confirmExit = null, Action? shutdown = null)
    {
        this.window = window; this.model = model; this.tray = tray; this.instance = instance;
        this.trackedDesktopAlive = trackedDesktopAlive; this.closeOwnedProcesses = closeOwnedProcesses;
        this.shutdown = shutdown ?? (() => System.Windows.Application.Current.Shutdown());
        this.confirmExit = confirmExit ?? (() => MessageBox.Show(window, "退出会关闭由 Chunson Space 启动并跟踪的 Codex，保留当前系统时区。是否退出？", "Chunson Space", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        window.Closing += Closing;
        tray.RestoreRequested += RestoreRequested;
        tray.ExitRequested += ExitRequested;
        model.ExitRequested += ExitRequested;
        tray.Show("Chunson Space"); model.SetTrayAvailable(true);
        activationListener = instance.ListenForActivationAsync(() => { if (!disposed) window.Dispatcher.BeginInvoke(Restore); }, lifetime.Token);
    }
    public bool TryHideOnClose()
    {
        if (exiting || disposed) return false;
        window.Hide(); return true;
    }
    private void Closing(object? sender, CancelEventArgs e)
    {
        if (!exiting) e.Cancel = TryHideOnClose();
    }
    private void RestoreRequested(object? sender, EventArgs e) => window.Dispatcher.BeginInvoke(Restore);
    private void ExitRequested(object? sender, EventArgs e) => window.Dispatcher.BeginInvoke(new Action(async () => await RequestExitAsync()));
    private void Restore()
    {
        if (exiting || disposed) return;
        window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (window.ShowActivated) window.Activate();
    }
    public async Task<bool> RequestExitAsync()
    {
        if (checkingExit || disposed) return false;
        checkingExit = true;
        try
        {
            if (model.QuitConfirm && await trackedDesktopAlive() && !confirmExit()) return false;
            if (!await model.DrainTasksForExitAsync()) { Restore(); return false; }
            if (!await closeOwnedProcesses()) { if (!model.IsError) model.ReportLifecycleFailure("退出未完成", "已跟踪的客户端退出尚未确认。窗口与账号资料保留，可重试退出。"); return false; }
            exiting = true; await DisposeAsync(); window.Close(); shutdown(); return true;
        }
        catch (Exception exception)
        {
            var detail = exception is AccountException or StorageException ? ErrorRedaction.Scrub(exception.Message) : exception.GetType().Name;
            model.ReportLifecycleFailure("退出未完成", "退出处理失败：" + detail + "。账号资料保留。"); return false;
        }
        finally { if (!exiting) { model.ResumeAfterExitFailure(); Restore(); } checkingExit = false; }
    }
    public void Dispose()
    {
        if (!BeginDisposal()) return;
        try { activationListener.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        finally { ReleaseResources(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (!BeginDisposal()) return;
        try { await activationListener; } catch (OperationCanceledException) { }
        finally { ReleaseResources(); }
    }
    private bool BeginDisposal()
    {
        if (disposed) return false; disposed = true; lifetime.Cancel();
        window.Closing -= Closing; tray.RestoreRequested -= RestoreRequested; tray.ExitRequested -= ExitRequested; model.ExitRequested -= ExitRequested;
        return true;
    }
    private void ReleaseResources()
    {
        tray.Dispose(); instance.Dispose(); lifetime.Dispose(); model.SetTrayAvailable(false);
    }
}
