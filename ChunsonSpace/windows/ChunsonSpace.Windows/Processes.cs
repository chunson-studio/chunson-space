namespace ChunsonSpace.Windows;

public enum ProcessScope { DesktopOnly, DesktopAndCli }
public sealed record ProcessIdentity(int Pid, int ParentPid, string Name, string Executable, long CreationTime, bool RegisteredDesktop = false);
public sealed record StopResult(bool Success, bool WasRunning, string Message, IReadOnlyList<int> RemainingPids);

public interface IWaitClock
{
    long Milliseconds { get; }
    Task DelayAsync(int milliseconds, CancellationToken cancellationToken);
}

public sealed class SystemWaitClock : IWaitClock
{
    public long Milliseconds => Environment.TickCount64;
    public Task DelayAsync(int milliseconds, CancellationToken cancellationToken) => Task.Delay(milliseconds, cancellationToken);
}

public interface IProcessSystem
{
    IReadOnlyList<ProcessIdentity> ManagedRoots(string desktopRoot) => [];
    IReadOnlyList<ProcessIdentity> Snapshot();
    // Must recheck creation time and executable on the SAME handle used to terminate.
    bool TryStop(ProcessIdentity process, bool force);
    bool HasDesktopWindow(IReadOnlyList<ProcessIdentity> desktopProcesses);
}

public sealed class CodexProcessService(IProcessSystem system, IWaitClock clock, ManagedCloneProcessScope? cloneScope = null, bool excludeManagedClone = false)
{
    public static bool IsDesktop(ProcessIdentity process)
    {
        var path = process.Executable.Replace('\\', '/').ToLowerInvariant();
        return (process.Name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase) || process.Name.Equals("chatgpt.exe", StringComparison.OrdinalIgnoreCase))
            && (process.RegisteredDesktop || path.Contains("/windowsapps/openai.codex_", StringComparison.Ordinal) || path.Contains("/openai/codex/app/", StringComparison.Ordinal) || path.EndsWith("/openai/codex/codex.exe", StringComparison.Ordinal));
    }

    public IReadOnlyList<ProcessIdentity> Discover(ProcessScope scope)
    {
        var snapshot = system.Snapshot();
        if (cloneScope is not null)
        {
            var owned = cloneScope.Selected(snapshot);
            if (!excludeManagedClone) return owned;
            var selected = snapshot.Where(IsDesktop).Where(process => !owned.Any(clone => clone.Pid == process.Pid)).ToDictionary(process => process.Pid);
            bool added;
            do
            {
                added = false;
                foreach (var process in snapshot)
                    if (!selected.ContainsKey(process.Pid) && selected.TryGetValue(process.ParentPid, out var parent) && process.CreationTime >= parent.CreationTime)
                    { selected.Add(process.Pid, process); added = true; }
            } while (added);
            return selected.Values.ToArray();
        }
        return snapshot.Where(p => IsDesktop(p) || scope == ProcessScope.DesktopAndCli && p.Name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public bool IsDesktopReady() => system.HasDesktopWindow(Discover(ProcessScope.DesktopOnly));

    public StopResult ConfirmStopped(ProcessScope scope, bool force = false)
    {
        try
        {
            if (force && cloneScope is null) return new(false, false, "未连接受管分身范围，运行资料未清理。", []);
            var unresolved = new List<string>();
            var remaining = force ? cloneScope!.Selected(system.Snapshot(), unresolved) : Discover(scope);
            if (unresolved.Count > 0) return new(false, remaining.Count > 0, "部分分身进程归属未确认，运行资料未清理。", remaining.Select(p => p.Pid).ToArray());
            return new(remaining.Count == 0, remaining.Count > 0, remaining.Count == 0 ? "Codex 已退出" : "Codex 被重新打开，活动数据未修改", remaining.Select(p => p.Pid).ToArray());
        }
        catch (Exception ex) { return new(false, false, "无法复查 Codex 是否退出：" + ex.GetType().Name, []); }
    }

    public async Task<StopResult> StopAsync(ProcessScope scope, CancellationToken cancellationToken = default, bool force = false)
    {
        if (force && cloneScope is null) return new(false, false, "未连接受管分身范围，运行资料未清理。", []);
        if (cloneScope is null) return await StopCoreAsync(p => IsDesktop(p) || scope == ProcessScope.DesktopAndCli && p.Name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase), cancellationToken);
        try
        {
            var unresolved = new List<string>();
            var targets = force ? cloneScope.Selected(system.Snapshot(), unresolved) : Discover(scope);
            var stopped = await StopIdentitiesAsync(targets, cancellationToken);
            return unresolved.Count == 0 ? stopped : stopped with { Success = false, Message = "已处理可确认的分身进程；仍有归属未确认，运行资料未清理。" };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, false, "分身停止未完成（" + ex.GetType().Name + "），运行资料未清理。", []); }
    }

    public Task<StopResult> StopIdentitiesAsync(IReadOnlyList<ProcessIdentity> identities, CancellationToken cancellationToken = default)
        => StopCoreAsync(p => identities.Any(old => p.Pid == old.Pid && (p.CreationTime <= 0 || p.Executable.Length == 0 || p.CreationTime == old.CreationTime && p.Executable.Equals(old.Executable, StringComparison.OrdinalIgnoreCase))), cancellationToken);

    private async Task<StopResult> StopCoreAsync(Func<ProcessIdentity, bool> selected, CancellationToken cancellationToken)
    {
        bool wasRunning = false;
        var captured = new Dictionary<int, ProcessIdentity>();
        try
        {
            var initial = system.Snapshot().Where(selected).ToArray();
            wasRunning = initial.Length > 0;
            if (!wasRunning) return new(true, false, "Codex 已关闭", []);
            StopSnapshot(initial, false, captured);
            var deadline = clock.Milliseconds + 800;
            while (clock.Milliseconds < deadline)
            {
                if (Remaining(selected, captured).Count == 0) return new(true, true, "Codex 已关闭", []);
                await clock.DelayAsync(100, cancellationToken);
            }
            deadline = clock.Milliseconds + 5000;
            while (clock.Milliseconds < deadline)
            {
                var remaining = Remaining(selected, captured);
                if (remaining.Count == 0) return new(true, true, "Codex 已关闭", []);
                StopSnapshot(remaining, true, captured);
                await clock.DelayAsync(150, cancellationToken);
            }
            var final = Remaining(selected, captured);
            return new(final.Count == 0, true, final.Count == 0 ? "Codex 已关闭" : "Codex 仍在运行，登录态未修改", final.Select(p => p.Pid).ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, wasRunning, $"无法确认 Codex 已退出，登录态未修改：{ex.Message}", []); }
    }

    private IReadOnlyList<ProcessIdentity> Remaining(Func<ProcessIdentity, bool> selected, Dictionary<int, ProcessIdentity> captured)
    {
        var all = system.Snapshot();
        return all.Where(p => selected(p)
            || captured.TryGetValue(p.Pid, out var old) && (p.CreationTime <= 0 || p.Executable.Length == 0 || p.CreationTime == old.CreationTime && p.Executable.Equals(old.Executable, StringComparison.OrdinalIgnoreCase)))
            .Select(p => captured.TryGetValue(p.Pid, out var old) && (p.CreationTime <= 0 || p.Executable.Length == 0) ? old : p).ToArray();
    }

    private void StopSnapshot(IReadOnlyList<ProcessIdentity> selected, bool force, Dictionary<int, ProcessIdentity> captured)
    {
        // Include captured descendants, but never issue taskkill /T against an unchecked PID.
        var all = system.Snapshot();
        var targets = selected.Where(p => all.Any(current => current.Pid == p.Pid && current.CreationTime == p.CreationTime && current.Executable.Equals(p.Executable, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(p => p.Pid);
        bool added;
        do
        {
            added = false;
            foreach (var p in all)
                if (!targets.ContainsKey(p.Pid) && targets.TryGetValue(p.ParentPid, out var parent) && p.CreationTime >= parent.CreationTime)
                { targets.Add(p.Pid, p); added = true; }
        } while (added);
        foreach (var p in targets.Values) captured[p.Pid] = p;
        foreach (var p in targets.Values.OrderByDescending(p => p.CreationTime)) system.TryStop(p, force);
    }
}
