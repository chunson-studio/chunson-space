using System.Text.Json;
using System.Text.Json.Nodes;
using FAswitch.Core;

namespace FAswitch.Windows;

public enum OwnedProcessKind { Desktop, LoginBrowser, DesktopChild, LoginBrowserChild }
public sealed record OwnedProcessRecord(ProcessIdentity Identity, OwnedProcessKind Kind, string Name, string At);
public sealed record OwnedLifecycleResult(bool Success, bool DesktopAlive, bool BrowserAlive, string Message, IReadOnlyList<string> Warnings, bool LaunchPending = false);
public sealed record OwnedStoreUpdate(bool Success, IReadOnlyList<OwnedProcessRecord> Records);
public interface IOwnedProcessStore
{
    IReadOnlyList<OwnedProcessRecord> Read();
    bool TryWrite(IReadOnlyList<OwnedProcessRecord> records);
    OwnedStoreUpdate Update(Func<IReadOnlyList<OwnedProcessRecord>, IReadOnlyList<OwnedProcessRecord>> update)
    { var records = update(Read()); return new(TryWrite(records), records); }
    IDisposable? TryEnterLease() => new EmptyOwnedLease();
    Task<IDisposable> EnterLeaseAsync(IWaitClock clock, CancellationToken cancellationToken) => Task.FromResult<IDisposable>(new EmptyOwnedLease());
}
internal sealed class EmptyOwnedLease : IDisposable { public void Dispose() { } }

public sealed class JsonOwnedProcessStore(string path, IFileStorage? storage = null) : IOwnedProcessStore
{
    private readonly IFileStorage files = storage ?? new PhysicalFileStorage();
    private readonly object fileGate = new();
    private FileStream? heldLease;
    public IDisposable? TryEnterLease()
    {
        lock (fileGate)
        {
            if (heldLease is not null) return new EmptyOwnedLease();
            var lockPath = Path.GetFullPath(path) + ".lock";
            PhysicalFileStorage.RejectLinks(lockPath);
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            try { heldLease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return null; }
            return new FileLease(this, heldLease);
        }
    }
    public async Task<IDisposable> EnterLeaseAsync(IWaitClock clock, CancellationToken cancellationToken)
    {
        var deadline = clock.Milliseconds + 5000;
        while (clock.Milliseconds < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryEnterLease() is { } lease) return lease;
            await clock.DelayAsync(100, cancellationToken);
        }
        throw new IOException("其他实例正在更新进程所有权或执行启动，请稍后重试");
    }
    public OwnedStoreUpdate Update(Func<IReadOnlyList<OwnedProcessRecord>, IReadOnlyList<OwnedProcessRecord>> update)
    {
        using var lease = TryEnterLease();
        if (lease is null) return new(false, []);
        lock (fileGate)
        {
            var records = update(Read());
            return new(WriteUnlocked(records), records);
        }
    }
    public IReadOnlyList<OwnedProcessRecord> Read()
    {
        byte[] content;
        try { content = files.Read(path); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        var document = JsonNode.Parse(content) as JsonObject ?? throw new JsonException("进程所有权记录损坏");
        if (document["pids"] is not JsonArray entries) throw new JsonException("进程所有权记录缺少进程列表");
        var result = new List<OwnedProcessRecord>();
        foreach (var node in entries)
        {
            if (node is not JsonObject item) throw new JsonException("进程所有权条目损坏");
            int pid = Integer(item, "pid"), parent = Integer(item, "parent");
            long created = Long(item, "creation_time");
            if (created == 0) long.TryParse(Text(item, "identity"), out created);
            string kind = Text(item, "kind");
            if (pid <= 0 || kind is not ("桌面端" or "浏览器" or "Desktop" or "LoginBrowser" or "DesktopChild" or "LoginBrowserChild")) continue;
            var processKind = kind switch { "桌面端" or "Desktop" => OwnedProcessKind.Desktop, "浏览器" or "LoginBrowser" => OwnedProcessKind.LoginBrowser, "DesktopChild" => OwnedProcessKind.DesktopChild, _ => OwnedProcessKind.LoginBrowserChild };
            result.Add(new(new(pid, parent, Text(item, "process_name"), Text(item, "exe"), created), processKind, Text(item, "name"), Text(item, "at")));
        }
        return result;
    }
    public bool TryWrite(IReadOnlyList<OwnedProcessRecord> records)
    {
        using var lease = TryEnterLease();
        if (lease is null) return false;
        lock (fileGate) return WriteUnlocked(records);
    }
    private bool WriteUnlocked(IReadOnlyList<OwnedProcessRecord> records)
    {
        try
        {
            if (records.Count == 0) { files.DeleteFile(path); return true; }
            var pids = new JsonArray(records.Select(record => (JsonNode)new JsonObject
            {
                ["pid"] = record.Identity.Pid, ["parent"] = record.Identity.ParentPid, ["process_name"] = record.Identity.Name,
                ["kind"] = record.Kind switch { OwnedProcessKind.Desktop => "桌面端", OwnedProcessKind.LoginBrowser => "浏览器", _ => record.Kind.ToString() }, ["name"] = record.Name,
                ["exe"] = record.Identity.Executable, ["creation_time"] = record.Identity.CreationTime,
                ["identity"] = record.Identity.CreationTime.ToString(System.Globalization.CultureInfo.InvariantCulture), ["at"] = record.At
            }).ToArray());
            files.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["schema"] = 2, ["pids"] = pids }));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException) { return false; }
    }
    private static string Text(JsonObject item, string key) => item[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static int Integer(JsonObject item, string key) => item[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
    private static long Long(JsonObject item, string key) => item[key] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
    private sealed class FileLease(JsonOwnedProcessStore owner, FileStream stream) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.fileGate)
            {
                stream.Dispose();
                if (ReferenceEquals(owner.heldLease, stream)) owner.heldLease = null;
            }
        }
    }
}

public sealed class OwnedProcessLifecycleService(IProcessSystem system, IWaitClock clock, IOwnedProcessStore store, SystemTimezoneService timezone, IDesktopLaunchSystem? launchSystem = null, ManagedCloneProcessScope? cloneScope = null)
{
    private readonly Dictionary<int, OwnedProcessRecord> owned = [];
    private readonly object recordsGate = new();
    // Serializes timezone restoration with the entire launch/registration interval.
    private readonly SemaphoreSlim launchRestoreGate = new(1, 1);
    private bool uncertain;
    private bool storeUncertain;
    private bool recoveryNeeded;
    private OwnedLifecycleResult? lastObserved;
    public event EventHandler<OwnedLifecycleResult>? StatusChanged;

    public async Task<IDisposable> BeginLaunchAsync(CancellationToken cancellationToken = default)
    {
        await launchRestoreGate.WaitAsync(cancellationToken);
        try { return new LaunchLease(launchRestoreGate, await store.EnterLeaseAsync(clock, cancellationToken)); }
        catch { launchRestoreGate.Release(); throw; }
    }
    public Task<OwnedLifecycleResult> AdoptAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        try
        {
            using var fileLease = store.TryEnterLease();
            if (fileLease is null) { lock (recordsGate) return Task.FromResult(Status(false, "其他实例正在启动或更新所有权，保留原记录", [])); }
            var records = store.Read();
            if (system is WindowsProcessSystem native)
                foreach (var record in records.Where(record => record.Kind == OwnedProcessKind.Desktop && record.Identity.CreationTime > 0 && record.Identity.Executable.Length > 0)) native.RegisterDesktopExecutable(record.Identity.Executable);
            var current = system.Snapshot();
            if (cloneScope is not null) _ = cloneScope.Selected(current);
            lock (recordsGate)
            {
                owned.Clear(); uncertain = false; storeUncertain = false;
                foreach (var record in records)
                {
                    var process = current.FirstOrDefault(p => p.Pid == record.Identity.Pid);
                    if (process is null) continue;
                    if (record.Identity.CreationTime <= 0 || record.Identity.Executable.Length == 0) { uncertain = true; storeUncertain = true; warnings.Add("旧进程记录缺少创建时间或路径，未认领且保留恢复材料"); continue; }
                    if (Unknown(process)) { uncertain = true; owned[record.Identity.Pid] = record; warnings.Add("进程身份读不到，保留原记录且不认定已退出"); continue; }
                    if (!Same(process, record.Identity) || record.Kind == OwnedProcessKind.Desktop && !CodexProcessService.IsDesktop(process)) continue;
                    owned[process.Pid] = record with { Identity = process };
                }
                if (!uncertain && records.Any(record => record.Identity.CreationTime > 0 && record.Identity.Executable.Length > 0) && owned.Count == 0) recoveryNeeded = true;
                if (!storeUncertain && !MergeAndPersist(current)) warnings.Add("进程所有权记录更新失败");
                return Task.FromResult(Status(!uncertain, "已复核并接续本工具启动的进程", warnings));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            lock (recordsGate) { uncertain = true; storeUncertain = true; return Task.FromResult(Status(false, "进程所有权记录或系统快照无法确认，保留材料", [error.GetType().Name])); }
        }
    }

    public bool Track(ProcessIdentity identity, OwnedProcessKind kind, string name)
    {
        if (identity.CreationTime <= 0 || identity.Executable.Length == 0) return false;
        var all = system.Snapshot();
        var current = all.FirstOrDefault(p => Same(p, identity));
        if (current is null || kind == OwnedProcessKind.Desktop && !CodexProcessService.IsDesktop(current)) return false;
        lock (recordsGate)
        {
            owned[identity.Pid] = new(current, kind, name, DateTimeOffset.UtcNow.ToString("O"));
            var tree = new Dictionary<int, ProcessIdentity> { [current.Pid] = current };
            bool added;
            do
            {
                added = false;
                foreach (var child in all)
                    if (!tree.ContainsKey(child.Pid) && tree.TryGetValue(child.ParentPid, out var parent) && child.CreationTime >= parent.CreationTime)
                    { tree.Add(child.Pid, child); added = true; }
            } while (added);
            foreach (var child in tree.Values.Where(p => p.Pid != current.Pid)) owned[child.Pid] = new(child, kind == OwnedProcessKind.Desktop ? OwnedProcessKind.DesktopChild : OwnedProcessKind.LoginBrowserChild, name, DateTimeOffset.UtcNow.ToString("O"));
            return MergeAndPersist(all);
        }
    }

    public bool TrackConfirmedDesktop(DesktopLaunchResult result, IReadOnlyList<ProcessIdentity> before, long createdAfter, string name)
    {
        if (!result.Success || result.AlreadyRunning) return false;
        var snapshot = system.Snapshot();
        var all = (cloneScope is null ? snapshot : cloneScope.Selected(snapshot)).Where(CodexProcessService.IsDesktop).ToArray();
        var selected = result.Pid is { } pid ? all.FirstOrDefault(p => p.Pid == pid && p.CreationTime >= createdAfter) : null;
        if (cloneScope is not null && selected is null) return false;
        if (selected is null)
        {
            var pids = all.Select(p => p.Pid).ToHashSet();
            var roots = all.Where(p => !pids.Contains(p.ParentPid) && p.CreationTime >= createdAfter && !before.Any(old => Same(old, p))).ToArray();
            if (roots.Length != 1) return false;
            selected = roots[0];
        }
        return Track(selected, OwnedProcessKind.Desktop, name);
    }

    public Task<bool> TrackedDesktopAliveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var current = system.Snapshot();
            lock (recordsGate) return Task.FromResult(uncertain || owned.Values.Any(record => DesktopKind(record.Kind) && current.Any(p => p.Pid == record.Identity.Pid && (Unknown(p) || Same(p, record.Identity)))));
        }
        catch (Exception) { return Task.FromResult(true); }
    }

    public async Task<StopResult> CloseTrackedAsync(CancellationToken cancellationToken = default)
    {
        using var fileLease = store.TryEnterLease();
        if (fileLease is null) return new(false, false, "其他实例正在启动或更新所有权，本次未关闭进程", []);
        var refreshed = await AdoptAsync(cancellationToken);
        if (!refreshed.Success) return new(false, false, refreshed.Message, []);
        if (launchSystem is not null)
        {
            try
            {
                var pending = await launchSystem.CheckPendingAsync(cancellationToken);
                if (pending is not null) return new(false, false, "桌面启动结果待确认，未关闭其他 Codex", []);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return new(false, false, "无法确认桌面启动状态，未关闭其他 Codex", []); }
        }
        ProcessIdentity[] identities;
        lock (recordsGate)
        {
            if (uncertain) return new(false, false, "本工具进程身份尚未确认，未关闭其他 Codex", []);
            identities = owned.Values.Where(record => DesktopKind(record.Kind)).Select(record => record.Identity).ToArray();
        }
        var stopped = await new CodexProcessService(system, clock).StopIdentitiesAsync(identities, cancellationToken);
        if (stopped.Success)
        {
            lock (recordsGate) { foreach (var identity in identities) owned.Remove(identity.Pid); if (identities.Length > 0 && owned.Count == 0) recoveryNeeded = true; MergeAndPersist(system.Snapshot()); }
        }
        else
        {
            try
            {
                var remaining = system.Snapshot().Where(p => stopped.RemainingPids.Contains(p.Pid)).ToArray();
                lock (recordsGate)
                {
                    foreach (var process in remaining)
                    {
                        owned[process.Pid] = new(process, OwnedProcessKind.DesktopChild, "", DateTimeOffset.UtcNow.ToString("O"));
                        if (Unknown(process)) { uncertain = true; storeUncertain = true; }
                    }
                    MergeAndPersist(system.Snapshot());
                }
            }
            catch (Exception) { lock (recordsGate) { uncertain = true; storeUncertain = true; } }
        }
        return stopped with { Message = stopped.Success ? "Chunson Space 启动的桌面端已关闭，外部进程保持运行" : "Chunson Space 启动的桌面端未完全关闭，恢复材料已保留" };
    }

    public async Task<OwnedLifecycleResult> PollAsync(CancellationToken cancellationToken = default)
    {
        await launchRestoreGate.WaitAsync(cancellationToken);
        try
        {
            using var fileLease = store.TryEnterLease();
            if (fileLease is null) { lock (recordsGate) return Status(true, "其他实例正在启动或登记，暂不恢复时区", []) with { LaunchPending = true }; }
            bool retryAdopt;
            lock (recordsGate) retryAdopt = storeUncertain;
            if (retryAdopt)
            {
                var adopted = await AdoptAsync(cancellationToken);
                if (!adopted.Success) return adopted;
            }
            if (launchSystem is not null && await launchSystem.CheckPendingAsync(cancellationToken) is not null)
            { lock (recordsGate) return Status(true, "启动结果待确认，暂不恢复时区", []) with { LaunchPending = true }; }
            var current = system.Snapshot();
            if (cloneScope is not null) _ = cloneScope.Selected(current);
            bool hadRecords, hasRecords;
            var warnings = new List<string>();
            lock (recordsGate)
            {
                hadRecords = owned.Count > 0;
                if (!MergeAndPersist(current)) return Status(false, "共享进程所有权更新未确认，暂不恢复时区", []);
                if (storeUncertain) return Status(false, "进程所有权记录读不到，原记录保持且不恢复时区", warnings);
                uncertain = false;
                hadRecords |= owned.Count > 0;
                foreach (var pair in owned.ToArray())
                {
                    var process = current.FirstOrDefault(p => p.Pid == pair.Key);
                    if (process is not null && Unknown(process)) { uncertain = true; continue; }
                    if (process is null || !Same(process, pair.Value.Identity)) owned.Remove(pair.Key);
                }
                hasRecords = owned.Count > 0;
                if (hadRecords && !hasRecords) recoveryNeeded = true;
                if (!MergeAndPersist(current)) return Status(false, "进程所有权记录更新失败，暂不恢复时区", []);
                hasRecords = owned.Count > 0;
                if (uncertain) return Status(false, "进程身份尚未确认，保留时区恢复记录", warnings);
            }
            if (recoveryNeeded && !hasRecords)
            {
                var restored = await timezone.RestoreAsync(cancellationToken);
                if (!restored.Success) warnings.Add(restored.Message);
                else recoveryNeeded = false;
            }
            lock (recordsGate) return Status(warnings.Count == 0, hasRecords ? "本工具进程仍在运行" : "本工具进程已退出", warnings);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        { lock (recordsGate) return Status(false, "生命周期状态无法确认，未恢复系统时区", [error.GetType().Name]); }
        finally { launchRestoreGate.Release(); }
    }
    public async Task WatchAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await clock.DelayAsync(2000, cancellationToken);
            var status = await PollAsync(cancellationToken);
            Publish(status);
        }
    }
    private void Publish(OwnedLifecycleResult status)
    {
        var previous = lastObserved;
        if (previous is not null && previous.Success == status.Success && previous.DesktopAlive == status.DesktopAlive && previous.BrowserAlive == status.BrowserAlive && previous.LaunchPending == status.LaunchPending && previous.Message == status.Message && previous.Warnings.SequenceEqual(status.Warnings)) return;
        lastObserved = status;
        foreach (var handler in StatusChanged?.GetInvocationList() ?? [])
            try { ((EventHandler<OwnedLifecycleResult>)handler)(this, status); }
            catch (Exception) { /* UI observer failures cannot disable process cleanup or timezone recovery. */ }
    }
    private OwnedLifecycleResult Status(bool success, string message, IReadOnlyList<string> warnings) => new(success, uncertain || owned.Values.Any(record => DesktopKind(record.Kind)), owned.Values.Any(record => record.Kind is OwnedProcessKind.LoginBrowser or OwnedProcessKind.LoginBrowserChild), message, warnings);
    private static bool DesktopKind(OwnedProcessKind kind) => kind is OwnedProcessKind.Desktop or OwnedProcessKind.DesktopChild;
    private static bool Same(ProcessIdentity left, ProcessIdentity right) => left.Pid == right.Pid && left.CreationTime > 0 && left.CreationTime == right.CreationTime && left.Executable.Equals(right.Executable, StringComparison.OrdinalIgnoreCase);
    private static bool Unknown(ProcessIdentity process) => process.CreationTime <= 0 || process.Executable.Length == 0;
    private bool MergeAndPersist(IReadOnlyList<ProcessIdentity> current)
    {
        var updated = store.Update(latest =>
        {
            var combined = latest.GroupBy(record => record.Identity.Pid).ToDictionary(group => group.Key, group => group.Last());
            foreach (var record in owned.Values)
            {
                var process = current.FirstOrDefault(p => p.Pid == record.Identity.Pid);
                if (!combined.ContainsKey(record.Identity.Pid) || process is not null && Same(process, record.Identity)) combined[record.Identity.Pid] = record;
            }
            return combined.Values.Where(record => current.Any(process => process.Pid == record.Identity.Pid && (Unknown(process) || Unknown(record.Identity) || Same(process, record.Identity)))).ToArray();
        });
        if (!updated.Success) return false;
        owned.Clear();
        foreach (var record in updated.Records) owned[record.Identity.Pid] = record;
        uncertain |= updated.Records.Any(record => Unknown(record.Identity) || current.Any(process => process.Pid == record.Identity.Pid && Unknown(process)));
        return true;
    }
    private sealed class LaunchLease(SemaphoreSlim gate, IDisposable fileLease) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) try { fileLease.Dispose(); } finally { gate.Release(); } }
    }
}
