namespace FAswitch.Desktop;

// One test sequence per account, shared by all model-test panels.
public sealed class AccountTestQueue
{
    private readonly Dictionary<string, Entry> accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();
    private sealed class Entry { public readonly SemaphoreSlim Gate = new(1, 1); public int Users; }
    public event Action? Changed;
    private bool accepting = true;
    public bool Accepting { get => accepting; set { accepting = value; Changed?.Invoke(); } }
    public bool IsBusy(string? account) { lock (sync) return account is not null && accounts.ContainsKey(account); }
    public async Task<T> RunAsync<T>(string account, Func<Task<T>> run, CancellationToken token)
    {
        if (!Accepting) throw new InvalidOperationException("正在退出，未开始新测试。");
        Entry entry;
        lock (sync)
        {
            if (!accounts.TryGetValue(account, out entry!)) accounts[account] = entry = new();
            entry.Users++;
        }
        Changed?.Invoke();
        var entered = false;
        try { await entry.Gate.WaitAsync(token); entered = true; token.ThrowIfCancellationRequested(); return await run(); }
        finally
        {
            if (entered) entry.Gate.Release();
            lock (sync) { if (--entry.Users == 0) { accounts.Remove(account); entry.Gate.Dispose(); } }
            Changed?.Invoke();
        }
    }
}
