using System.ComponentModel;
using System.Windows;
using ChunsonSpace.Windows;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

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
    private readonly Func<Task> detachManager;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task activationListener;
    private bool exiting;
    private bool disposed;
    private bool checkingExit;

    public DesktopLifetime(MainWindow window, ShellViewModel model, ITrayHost tray, ISingleInstanceLease instance,
        Func<Task<bool>> trackedDesktopAlive, Func<Task<bool>> closeOwnedProcesses, Func<bool>? confirmExit = null, Action? shutdown = null, Func<Task>? detachManager = null)
    {
        this.window = window; this.model = model; this.tray = tray; this.instance = instance;
        this.trackedDesktopAlive = trackedDesktopAlive; this.closeOwnedProcesses = closeOwnedProcesses;
        this.shutdown = shutdown ?? (() => System.Windows.Application.Current.Shutdown());
        this.detachManager = detachManager ?? (() => Task.CompletedTask);
        this.confirmExit = confirmExit ?? (() => MessageBox.Show(window, "退出会关闭由 Chunson Space 启动并跟踪的 Codex，保留当前系统时区。是否退出？", "Chunson Space", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        window.Closing += Closing;
        tray.RestoreRequested += RestoreRequested;
        tray.ExitRequested += ExitRequested;
        model.ExitRequested += ExitRequested;
        model.ExitManagerRequested += ManagerExitRequested;
        tray.Show("Chunson Space"); model.SetTrayAvailable(true);
        activationListener = instance.ListenForActivationAsync(() => { if (!disposed) window.Dispatcher.BeginInvoke(Restore); }, lifetime.Token);
    }
    public bool TryHideOnClose()
    {
        if (exiting || disposed || !model.CloseToTray) return false;
        window.Hide(); return true;
    }
    private void Closing(object? sender, CancelEventArgs e)
    {
        if (!exiting && !disposed) { e.Cancel = true; if (!TryHideOnClose()) ExitRequested(this, EventArgs.Empty); }
    }
    private void RestoreRequested(object? sender, EventArgs e) => window.Dispatcher.BeginInvoke(Restore);
    private void ExitRequested(object? sender, EventArgs e) => window.Dispatcher.BeginInvoke(new Action(async () => await RequestExitAsync()));
    private void ManagerExitRequested(object? sender, EventArgs e) => ExitRequested(sender, e);
    public async Task<bool> RequestManagerExitAsync(Func<Task>? beforeExit = null)
    {
        if (checkingExit || disposed) return false;
        checkingExit = true;
        try
        {
            if (!await model.DrainTasksForExitAsync()) return false;
            await detachManager(); // Stop only the manager's observation loop; do not read/repair ownership or close Codex.
            if (beforeExit is not null) await beforeExit();
            exiting = true; await DisposeAsync(); window.Close(); shutdown(); return true;
        }
        catch (Exception error) { model.ReportLifecycleFailure("退出未完成", "后台释放未确认：" + error.GetType().Name + "。可以稍后重试。"); return false; }
        finally { if (!exiting) { model.ResumeAfterExitFailure(); Restore(); } checkingExit = false; }
    }
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
            // Explicit exit is bounded. Uncertain ownership may leave an unknown process;
            // it must never trap the manager or authorize killing an unrelated process.
            try { await model.DrainTasksForExitAsync(explicitExit: true).WaitAsync(TimeSpan.FromSeconds(4)); } catch (Exception) { }
            try { await closeOwnedProcesses().WaitAsync(TimeSpan.FromSeconds(8)); } catch (Exception) { }
            try { await detachManager().WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { }
            exiting = true;
            await DisposeAsync(); window.Close(); shutdown(); return true;
        }
        finally { checkingExit = false; }
    }
    public void Dispose()
    {
        if (!BeginDisposal()) return;
        try { activationListener.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(); } catch (Exception) { }
        finally { ReleaseResources(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (!BeginDisposal()) return;
        try { await activationListener.WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { }
        finally { ReleaseResources(); }
    }
    private bool BeginDisposal()
    {
        if (disposed) return false; disposed = true; lifetime.Cancel();
        window.Closing -= Closing; tray.RestoreRequested -= RestoreRequested; tray.ExitRequested -= ExitRequested; model.ExitRequested -= ExitRequested;
        model.ExitManagerRequested -= ManagerExitRequested;
        return true;
    }
    private void ReleaseResources()
    {
        tray.Dispose(); instance.Dispose(); lifetime.Dispose(); model.SetTrayAvailable(false);
    }
}
