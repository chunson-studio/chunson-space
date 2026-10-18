using ChunsonSpace.Core;

namespace ChunsonSpace.Windows;

public sealed record ManagedCloneLaunchOptions(string CodexHome, string DesktopRoot, ManagedCloneProcessScope ProcessScope,
    Func<DesktopTarget, DesktopLaunchRequest, CancellationToken, Task<string?>>? ProtectionPreflight = null,
    IManagedCodexRuntimeInspector? RuntimeInspector = null, string? RuntimeDiagnosticPath = null)
{
    public DesktopLaunchRequest Bind(DesktopLaunchRequest request, bool createDirectories = true)
    {
        var home = Path.GetFullPath(CodexHome);
        var desktop = Path.GetFullPath(DesktopRoot);
        PhysicalFileStorage.RejectLinks(home); PhysicalFileStorage.RejectLinks(desktop);
        if (request.Environment is null || !request.Environment.TryGetValue("CODEX_HOME", out var requestedHome)
            || !Path.GetFullPath(requestedHome).Equals(home, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("分身启动必须使用受管 CODEX_HOME");
        if (!request.Environment.TryGetValue("HTTP_PROXY", out var proxy) || !Uri.TryCreate(proxy, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0)
            throw new InvalidDataException("分身启动缺少明确 HTTP 代理，不会直连");
        if (createDirectories) { Directory.CreateDirectory(home); Directory.CreateDirectory(desktop); }
        var environment = new Dictionary<string, string>(request.Environment, StringComparer.OrdinalIgnoreCase)
        {
            ["CODEX_HOME"] = home,
            ["CODEX_SQLITE_HOME"] = home,
            ["CODEX_ELECTRON_USER_DATA_PATH"] = desktop,
            ["HTTPS_PROXY"] = proxy,
            ["NO_PROXY"] = "localhost,127.0.0.1,::1"
        };
        environment.Remove("ALL_PROXY");
        environment.Remove("CODEX_CHROME_USER_DATA_DIR");
        var args = new List<string>();
        for (var index = 0; index < request.Arguments.Count; index++)
        {
            var argument = request.Arguments[index];
            if (argument.Equals("--user-data-dir", StringComparison.OrdinalIgnoreCase)) { index++; continue; }
            if (argument.StartsWith("--user-data-dir=", StringComparison.OrdinalIgnoreCase)) continue;
            args.Add(argument);
        }
        args.Add("--user-data-dir=" + desktop);
        args.RemoveAll(arg => arg.StartsWith("--remote-debugging-port", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("--remote-debugging-address", StringComparison.OrdinalIgnoreCase));
        args.Add("--remote-debugging-port=0");
        args.Add("--remote-debugging-address=127.0.0.1");
        return request with { Environment = environment, Arguments = args };
    }
}

/// <summary>PID reuse cannot transfer ownership; descendants require a verified live parent.</summary>
public sealed class ManagedCloneProcessScope(IProcessSystem system, IOwnedProcessStore store)
{
    public bool OwnsPid(int pid) => Selected(system.Snapshot()).Any(process => process.Pid == pid);
    public Task<StopResult> StopOwnedAsync(CancellationToken token) => new CodexProcessService(system, new SystemWaitClock(), this).StopAsync(ProcessScope.DesktopAndCli, token);
    public bool ConfirmStartedRootExited(ProcessIdentity root)
    {
        if (root.Pid <= 0 || root.CreationTime <= 0 || root.Executable.Length == 0) return false;
        var snapshot = system.Snapshot();
        var liveRoot = snapshot.FirstOrDefault(process => process.Pid == root.Pid);
        if (liveRoot is not null && (liveRoot.CreationTime <= 0 || liveRoot.Executable.Length == 0 || Same(liveRoot, root))) return false;
        var records = store.Read().Where(record => record.Kind is OwnedProcessKind.Desktop or OwnedProcessKind.DesktopChild).ToArray();
        // The process can exit before root registration. Retain its receipt as
        // ancestry evidence, including descendants whose identity is unreadable.
        var parents = records.Select(record => record.Identity).Append(root).ToArray();
        foreach (var child in snapshot)
            if (parents.Any(parent => parent.Pid == child.ParentPid
                && (child.CreationTime <= 0 || child.Executable.Length == 0 || child.CreationTime >= parent.CreationTime)))
                return false;
        // This also rejects unreadable ownership and surviving recorded children
        // whose immediate parents have already exited. PID reuse is not ownership.
        return Selected(snapshot).Count == 0;
    }
    public IReadOnlyList<ProcessIdentity> Selected(IReadOnlyList<ProcessIdentity> snapshot)
    {
        var records = store.Read().Where(record => record.Kind is OwnedProcessKind.Desktop or OwnedProcessKind.DesktopChild).ToArray();
        if (records.Any(record => record.Identity.CreationTime <= 0 || record.Identity.Executable.Length == 0))
            throw new InvalidDataException("分身所有权记录缺少创建时间或路径");
        var selected = new Dictionary<int, ProcessIdentity>();
        foreach (var record in records)
        {
            var live = snapshot.FirstOrDefault(process => process.Pid == record.Identity.Pid);
            if (live is null) continue;
            if (live.CreationTime <= 0 || live.Executable.Length == 0) throw new IOException("分身进程身份不可读，停止清理");
            if (Same(live, record.Identity)) selected[live.Pid] = live;
        }
        foreach (var child in snapshot)
            if (!selected.ContainsKey(child.Pid) && records.Any(record => record.Identity.Pid == child.ParentPid && child.CreationTime >= record.Identity.CreationTime)
                && !records.Any(record => Same(record.Identity, child)) && !selected.ContainsKey(child.ParentPid))
                throw new IOException("发现尚未观测所有权的孤儿子进程，停止清理运行数据");
        bool added;
        do
        {
            added = false;
            foreach (var child in snapshot)
                if (!selected.ContainsKey(child.Pid) && selected.TryGetValue(child.ParentPid, out var parent))
                {
                    if (child.CreationTime <= 0 || child.Executable.Length == 0) throw new IOException("分身子进程身份不可读，停止清理");
                    if (child.CreationTime >= parent.CreationTime) { selected.Add(child.Pid, child); added = true; }
                }
        } while (added);
        // Persist children while the parent is alive, so later parent exit does not orphan ownership.
        if (selected.Count > 0)
        {
            var update = store.Update(latest => latest.Where(record => !selected.ContainsKey(record.Identity.Pid)).Concat(selected.Values.Select(identity =>
                records.FirstOrDefault(record => Same(record.Identity, identity)) ?? new(identity, OwnedProcessKind.DesktopChild, "受管分身", DateTimeOffset.UtcNow.ToString("O")))).ToArray());
            if (!update.Success) throw new IOException("分身进程所有权保存失败");
        }
        return selected.Values.ToArray();
    }

    public bool RegisterRoot(ProcessIdentity identity, string name)
    {
        if (identity.CreationTime <= 0 || identity.Executable.Length == 0) return false;
        var snapshot = system.Snapshot();
        if (!snapshot.Any(process => Same(process, identity))) return false;
        var tree = new Dictionary<int, ProcessIdentity> { [identity.Pid] = identity };
        bool added;
        do
        {
            added = false;
            foreach (var child in snapshot)
                if (!tree.ContainsKey(child.Pid) && tree.TryGetValue(child.ParentPid, out var parent))
                {
                    if (child.CreationTime <= 0 || child.Executable.Length == 0) return false;
                    if (child.CreationTime >= parent.CreationTime) { tree[child.Pid] = child; added = true; }
                }
        } while (added);
        return store.Update(latest => latest.Where(record => !tree.ContainsKey(record.Identity.Pid)).Concat(tree.Values.Select(process =>
            new OwnedProcessRecord(process, process.Pid == identity.Pid ? OwnedProcessKind.Desktop : OwnedProcessKind.DesktopChild, name, DateTimeOffset.UtcNow.ToString("O")))).ToArray()).Success;
    }
    private static bool Same(ProcessIdentity left, ProcessIdentity right) => left.Pid == right.Pid && left.CreationTime > 0
        && left.CreationTime == right.CreationTime && left.Executable.Equals(right.Executable, StringComparison.OrdinalIgnoreCase);
}
