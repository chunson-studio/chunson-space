namespace ChunsonSpace.Desktop;

// One test sequence per account, shared by all model-test panels.
public sealed class AccountTestQueue
{
    private readonly Dictionary<string, Entry> accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();
    private readonly SynchronizationContext? notifications = SynchronizationContext.Current;
    private int notificationPending;
    private sealed class Entry { public readonly SemaphoreSlim Gate = new(1, 1); public int Users; }
    public event Action? Changed;
    private bool accepting = true;
    public bool Accepting { get { lock (sync) return accepting; } set { lock (sync) accepting = value; NotifyChanged(); } }
    private void NotifyChanged()
    {
        if (notifications is not null && SynchronizationContext.Current != notifications)
        {
            if (Interlocked.Exchange(ref notificationPending, 1) == 0)
                notifications.Post(_ => { Volatile.Write(ref notificationPending, 0); Changed?.Invoke(); }, null);
        }
        else Changed?.Invoke();
    }
    public bool IsBusy(string? account) { lock (sync) return account is not null && accounts.ContainsKey(account); }
    public bool HasPending { get { lock (sync) return accounts.Count > 0; } }
    internal bool PauseWhenIdle()
    {
        lock (sync)
        {
            if (!accepting || accounts.Count > 0) return false;
            accepting = false;
        }
        NotifyChanged();
        return true;
    }
    public async Task<T> RunAsync<T>(string account, Func<Task<T>> run, CancellationToken token)
    {
        Entry entry;
        lock (sync)
        {
            if (!accepting) throw new InvalidOperationException("正在退出，未开始新测试。");
            if (!accounts.TryGetValue(account, out entry!)) accounts[account] = entry = new();
            entry.Users++;
        }
        NotifyChanged();
        var entered = false;
        try
        {
            await entry.Gate.WaitAsync(token); entered = true; token.ThrowIfCancellationRequested();
            if (!Accepting) throw new InvalidOperationException("正在退出，未开始新测试。");
            return await run();
        }
        finally
        {
            if (entered) entry.Gate.Release();
            lock (sync) { if (--entry.Users == 0) { accounts.Remove(account); entry.Gate.Dispose(); } }
            NotifyChanged();
        }
    }
}
