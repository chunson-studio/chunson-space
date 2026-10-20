using System.IO;
using System.Net.NetworkInformation;

namespace ChunsonSpace.Desktop;

// Only file names/change notifications are observed: no subscription, database or config contents.
// The timer belongs to this process and continues while the window is hidden in the tray.
internal sealed class LocalEnvironmentMonitor : IDisposable
{
    internal static readonly TimeSpan WatchdogInterval = TimeSpan.FromMinutes(5);
    private readonly TimeProvider clock;
    private readonly Action invalidate;
    private readonly Action probe;
    private readonly ITimer debounce;
    private readonly ITimer watchdog;
    private readonly FileSystemWatcher? watcher;
    private readonly bool native;
    private long lastProbeUtcTicks;
    private int disposed;
    internal LocalEnvironmentMonitor(string root, Action invalidate, Action probe, TimeProvider? clock = null, bool native = true)
    {
        this.clock = clock ?? TimeProvider.System; this.invalidate = invalidate; this.probe = probe; this.native = native;
        debounce = this.clock.CreateTimer(_ => Probe(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        watchdog = this.clock.CreateTimer(_ => Probe(), null, WatchdogInterval, WatchdogInterval);
        if (!native) return;
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        if (!Directory.Exists(root)) return; // Five-minute watchdog also covers a missing/recreated data directory.
        try
        {
            watcher = new(root) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            watcher.Changed += FileChanged; watcher.Created += FileChanged; watcher.Deleted += FileChanged;
            watcher.Renamed += Renamed; watcher.Error += WatchError; watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { watcher?.Dispose(); }
    }
    internal static bool Relevant(string? name) => name is not null &&
        new[] { "shared_preferences.json", "config.yaml", "cache.db", "cache.db-wal", "database.sqlite", "database.sqlite-wal" }
            .Contains(name, StringComparer.OrdinalIgnoreCase);
    private void FileChanged(object sender, FileSystemEventArgs e) { if (Relevant(e.Name)) SignalChange(); }
    private void Renamed(object sender, RenamedEventArgs e) { if (Relevant(e.Name) || Relevant(e.OldName)) SignalChange(); }
    private void WatchError(object sender, ErrorEventArgs e) => SignalChange();
    private void NetworkChanged(object? sender, EventArgs e) => SignalChange();
    internal void SignalChange()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        invalidate(); // Invalidate in-flight evidence immediately, before the debounced probe.
        var due = TimeSpan.FromSeconds(2);
        var remaining = TimeSpan.FromSeconds(15) - TimeSpan.FromTicks(clock.GetUtcNow().UtcTicks - Interlocked.Read(ref lastProbeUtcTicks));
        if (remaining > due) due = remaining;
        try { debounce.Change(due, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }
    private void Probe()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        Interlocked.Exchange(ref lastProbeUtcTicks, clock.GetUtcNow().UtcTicks);
        try { debounce.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { return; }
        if (Volatile.Read(ref disposed) == 0) probe();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        debounce.Dispose(); watchdog.Dispose(); watcher?.Dispose();
        if (native) NetworkChange.NetworkAddressChanged -= NetworkChanged;
    }
}
