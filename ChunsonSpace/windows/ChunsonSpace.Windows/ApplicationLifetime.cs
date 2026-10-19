namespace ChunsonSpace.Windows;

public interface ISingleInstanceLease : IDisposable
{
    bool IsPrimary { get; }
    bool RequestActivation();
    Task ListenForActivationAsync(Action activate, CancellationToken cancellationToken);
}

// Window dispatching belongs to the WPF host; activation callbacks run on a worker.
public sealed class SingleInstanceLease : ISingleInstanceLease
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle activation;
    private bool disposed;
    public bool IsPrimary { get; }
    public SingleInstanceLease(string applicationId = "ChunsonSpace_Windows_v1")
    {
        if (applicationId.Length == 0 || applicationId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))) throw new ArgumentException("单实例名称无效", nameof(applicationId));
        // Presence of the named mutex, rather than thread-affine ownership, defines lifetime.
        mutex = new Mutex(false, "Local\\" + applicationId, out var created);
        IsPrimary = created;
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + applicationId + "_Activate");
    }
    public bool RequestActivation() => !disposed && activation.Set();
    public Task ListenForActivationAsync(Action activate, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsPrimary) throw new InvalidOperationException("只有主实例监听激活请求");
        return Task.Run(() =>
        {
            var handles = new WaitHandle[] { activation, cancellationToken.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0) activate();
        }, cancellationToken);
    }
    public void Dispose() { if (disposed) return; disposed = true; activation.Dispose(); mutex.Dispose(); }
}

// Tray icon resource, UI dispatch and close interception are supplied by the WPF host.
public interface ITrayHost : IDisposable
{
    event EventHandler? RestoreRequested;
    event EventHandler? ExitRequested;
    void Show(string tooltip);
    void Hide();
}

public sealed record DesktopLifecyclePolicy(bool CloseToTray, bool MinimizeToTray)
{
    public bool ShouldHideOnClose(bool explicitExit, bool trayAvailable) => CloseToTray && trayAvailable && !explicitExit;
    public bool ShouldHideOnMinimize(bool trayAvailable) => MinimizeToTray && trayAvailable;
}
