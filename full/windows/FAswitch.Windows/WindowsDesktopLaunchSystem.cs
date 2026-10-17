using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FAswitch.Core;

namespace FAswitch.Windows;

public sealed partial class WindowsDesktopLaunchSystem(string stateRoot, IProcessSystem processes, ICommandRunner commands, IWaitClock clock, ManagedCloneLaunchOptions? clone = null) : IDesktopLaunchSystem
{
    [GeneratedRegex("\\AOpenAI\\.Codex_[A-Za-z0-9]+!App\\z")] private static partial Regex AumidPattern();
    private const string PackageMethod = "Windows 包内兼容启动";
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private sealed record State(string nonce, string status, long deadline_ms = 0, int? pid = null, long creation_time = 0, string executable = "", string timezone = "", string expected_ip = "");
    private readonly CodexProcessService discovery = new(processes, clock, clone?.ProcessScope);
    private readonly IManagedCodexRuntimeInspector runtimeInspector = clone?.RuntimeInspector ?? new ManagedCodexRuntimeInspector(commands);
    public bool IsDesktopReady() => discovery.IsDesktopReady();

    public async Task<DesktopTarget?> FindTargetAsync(CancellationToken cancellationToken)
    {
        var local = FindUnpackagedTarget(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (local is not null)
        {
            if (processes is WindowsProcessSystem localNative) localNative.RegisterDesktopExecutable(local.Executable);
            return local;
        }
        const string script = """
            $ErrorActionPreference='Stop'
            [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
            $p=Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | Select-Object -First 1
            if($p) {
                $m=Get-AppxPackageManifest -Package $p.PackageFullName
                $a=$m.Package.Applications.Application | Where-Object {$_.Id -eq 'App'} | Select-Object -First 1
                if($a) { @{Executable=(Join-Path $p.InstallLocation $a.Executable); Aumid=($p.PackageFamilyName+'!'+$a.Id)} | ConvertTo-Json -Compress; return }
            }
            $a=Get-StartApps | Where-Object {$_.AppID -like 'OpenAI.Codex_*!App'} | Select-Object -First 1
            if($a){@{Executable=''; Aumid=$a.AppID} | ConvertTo-Json -Compress}
            """;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var result = await commands.RunAsync(PowerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Encode(script)], timeout.Token);
        if (result.ExitCode != 0 || result.Output.Length == 0) return null;
        var target = JsonSerializer.Deserialize<DesktopTarget>(result.Output);
        if (target is null || !AumidPattern().IsMatch(target.Aumid)) return null;
        target = target with { Executable = File.Exists(target.Executable) ? ResolveRealPath(target.Executable) : "" };
        if (target.Executable.Length > 0 && processes is WindowsProcessSystem native) native.RegisterDesktopExecutable(target.Executable);
        return target;
    }

    public static DesktopTarget? FindUnpackagedTarget(string localAppData)
    {
        foreach (var relative in new[] { Path.Combine("OpenAI", "Codex", "Codex.exe"), Path.Combine("OpenAI", "Codex", "ChatGPT.exe"), Path.Combine("OpenAI", "Codex", "app", "Codex.exe"), Path.Combine("OpenAI", "Codex", "app", "ChatGPT.exe") })
        {
            var executable = Path.Combine(localAppData, relative);
            if (File.Exists(executable)) return new(ResolveRealPath(executable), "");
        }
        return null;
    }

    public async Task<LaunchReceipt?> CheckPendingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { PhysicalFileStorage.RejectLinks(Path.GetFullPath(stateRoot)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageException) { return Pending(stateRoot, "启动状态目录未通过链接检查，禁止访问或清理"); }
        if (!Directory.Exists(stateRoot)) return null;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(stateRoot))
            {
                var nonce = Path.GetFileName(directory);
                if (!Guid.TryParseExact(nonce, "N", out _)) continue;
                using var mutex = new Mutex(false, "Local\\FAswitchLaunch_" + nonce);
                if (!Acquire(mutex, 100)) return Pending(directory, "启动握手正在使用");
                State state;
                try
                {
                    state = ReadState(directory, nonce);
                    if (state.status == "pending" && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= state.deadline_ms)
                    {
                        WriteState(directory, state with { status = "cancelled" });
                        state = state with { status = "cancelled" };
                    }
                    if (state.status == "started" && clone is not null && state.pid is { } startedPid
                        && clone.ProcessScope.ConfirmStartedRootExited(new(startedPid, 0, "", state.executable, state.creation_time)))
                    {
                        // A started receipt identifies an already-created process;
                        // unlike a pending dispatch, it cannot start a delayed root.
                        state = state with { status = "failed" };
                        WriteState(directory, state);
                    }
                    if (state.status is "failed" or "cancelled" or "complete") { DeleteState(directory); continue; }
                }
                finally { mutex.ReleaseMutex(); }
                if (state.status == "started" && RegisterCloneState(state) && IsDesktopReady())
                {
                    // Never await while owning a thread-affine mutex.
                    await clock.DelayAsync(500, cancellationToken);
                    if (IsDesktopReady())
                    {
                        var receipt = new LaunchReceipt(LaunchStatus.Confirmed, state.pid, PackageMethod, "", directory);
                        if (clone is not null)
                        {
                            var verified = await VerifyRuntimeAsync(receipt, new(null, [], new Dictionary<string, string> { ["TZ"] = state.timezone }, ExpectedEgressIp: state.expected_ip), cancellationToken);
                            if (verified.Status != LaunchStatus.Confirmed) return verified;
                        }
                        Complete(receipt);
                        continue;
                    }
                }
                // A dispatch receipt cannot prove that the delayed child will not start.
                return Pending(directory, "上次桌面启动尚未确认；禁止更换身份与恢复旧档案", state.pid);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or WaitHandleCannotBeOpenedException or StorageException)
        { return Pending(stateRoot, "无法读取上次启动握手；保留恢复材料"); }
    }

    public async Task<LaunchReceipt> StartAsync(DesktopTarget target, DesktopLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clone is not null)
        {
            try { request = clone.Bind(request, createDirectories: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { return new(LaunchStatus.Failed, null, "分身启动检查", "受管分身环境或 HTTP 代理不符合要求"); }
            if (request.Preparation is not PreparedLaunch prepared || !prepared.Take(this, target, request))
            {
                var checkedRequest = await PrepareAsync(target, request, cancellationToken);
                if (!checkedRequest.Success) return new(LaunchStatus.Failed, null, "启动条件检查", checkedRequest.Message);
                request = checkedRequest.Request;
                if (request.Preparation is not PreparedLaunch fresh || !fresh.Take(this, target, request))
                    return new(LaunchStatus.Failed, null, "启动条件检查", "启动条件未确认，未创建分身。");
            }
            try { request = clone.Bind(request); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { return new(LaunchStatus.Failed, null, "分身目录检查", "受管分身目录不可用，未创建进程。"); }
        }
        bool dispatched = false;
        string directory = "";
        try
        {
            // Persist direct/registered launch intent too: a timeout must survive app restart.
            string nonce = Guid.NewGuid().ToString("N");
            directory = Path.Combine(stateRoot, nonce);
            GuardStateDirectory(directory);
            Directory.CreateDirectory(directory);
            WriteState(directory, new State(nonce, "launching"));
            if (clone is null && request.Environment is null && AumidPattern().IsMatch(target.Aumid))
            {
                var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("shell:appsFolder\\" + target.Aumid);
                using var registered = Process.Start(info) ?? throw new IOException("注册启动未创建进程");
                dispatched = true;
                WriteState(directory, new State(nonce, "started"));
                return new(LaunchStatus.Confirmed, null, "Windows 注册应用入口", "启动请求已提交，等待桌面窗口", directory);
            }
            if (!LaunchableTarget(target))
            { DeleteState(directory); return new(LaunchStatus.Failed, null, "桌面程序", "Codex 桌面程序路径不可用"); }
            if (processes is WindowsProcessSystem native) native.RegisterDesktopExecutable(ResolveRealPath(target.Executable));
            var direct = new ProcessStartInfo(ResolveRealPath(target.Executable)) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in request.Arguments) direct.ArgumentList.Add(argument);
            if (request.Environment is not null)
            {
                var inherited = direct.Environment.Where(p => p.Value is not null).Select(p => new KeyValuePair<string, string>(p.Key, p.Value!));
                var clean = DesktopLaunchSpec.SanitizeEnvironment(inherited);
                direct.Environment.Clear();
                foreach (var pair in clean) direct.Environment[pair.Key] = pair.Value;
                if (request.Environment.ContainsKey("HTTP_PROXY"))
                    foreach (var key in new[] { "ALL_PROXY", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY" }) direct.Environment.Remove(key);
                foreach (var pair in DesktopLaunchSpec.SanitizeEnvironment(request.Environment)) direct.Environment[pair.Key] = pair.Value;
            }
            try
            {
                using var child = Process.Start(direct) ?? throw new IOException("桌面启动未创建进程");
                dispatched = true;
                var state = new State(nonce, "started", pid: child.Id, creation_time: child.StartTime.ToUniversalTime().ToFileTimeUtc(), executable: direct.FileName,
                    timezone: request.Environment?.GetValueOrDefault("TZ") ?? "", expected_ip: request.ExpectedEgressIp);
                WriteState(directory, state);
                if (!RegisterCloneState(state)) return Pending(directory, "分身创建后所有权未确认，保留启动记录", child.Id);
                return new(LaunchStatus.Confirmed, child.Id, "桌面程序", "进程创建已确认，等待桌面窗口", directory);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5 && request.Environment is not null && AumidPattern().IsMatch(target.Aumid))
            {
                DeleteState(directory);
                return await StartPackagedAsync(target, request, cancellationToken);
            }
        }
        catch (Win32Exception) when (!dispatched) { DeleteState(directory); return new(LaunchStatus.Failed, null, "桌面程序", "系统拒绝创建桌面进程"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            if (dispatched) return Pending(directory, "请求已经提交，启动状态写入或确认失败");
            // Process.Start may throw after dispatch for a non-Win32 exception. Keep intent conservatively.
            return directory.Length > 0 ? Pending(directory, "桌面进程创建结果无法确认") : new(LaunchStatus.Failed, null, "准备启动", "无法准备启动记录");
        }
    }

    private async Task<LaunchReceipt> StartPackagedAsync(DesktopTarget target, DesktopLaunchRequest request, CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(stateRoot, nonce);
        bool dispatched = false;
        try
        {
            GuardStateDirectory(directory);
            Directory.CreateDirectory(directory);
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 15000;
            using var mutex = new Mutex(false, "Local\\FAswitchLaunch_" + nonce);
            if (!Acquire(mutex, 1000)) { DeleteState(directory); return new(LaunchStatus.Failed, null, PackageMethod, "启动握手锁不可用"); }
            try { WriteState(directory, new State(nonce, "pending", deadline)); }
            finally { mutex.ReleaseMutex(); }
            var parts = target.Aumid.Split('!');
            var allow = new[] { "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "TZ", "CODEX_HOME", "CODEX_SQLITE_HOME", "CODEX_ELECTRON_USER_DATA_PATH" };
            var env = request.Environment!.Where(pair => allow.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(pair => pair.Key.ToUpperInvariant(), pair => pair.Value);
            var payload = new { family = parts[0], app_id = parts[1], nonce, mutex = "Local\\FAswitchLaunch_" + nonce, state = Path.Combine(directory, "state.json"), deadline_ms = deadline, arguments = JoinArguments(request.Arguments), env, expected_ip = request.ExpectedEgressIp };
            var info = new ProcessStartInfo(PowerShell) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Encode(PackagedLaunchScripts.Build(payload)) }) info.ArgumentList.Add(arg);
            var inherited = info.Environment.Where(p => p.Value is not null).Select(p => new KeyValuePair<string, string>(p.Key, p.Value!));
            var clean = DesktopLaunchSpec.SanitizeEnvironment(inherited);
            info.Environment.Clear();
            foreach (var pair in clean.Where(p => !new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" }.Contains(p.Key, StringComparer.OrdinalIgnoreCase))) info.Environment[pair.Key] = pair.Value;
            using var bootstrap = Process.Start(info) ?? throw new IOException("包内启动未创建引导进程");
            dispatched = true;
            var monotonicDeadline = clock.Milliseconds + 15000;
            while (clock.Milliseconds < monotonicDeadline)
            {
                if (Acquire(mutex, 100))
                {
                    try
                    {
                        var state = ReadState(directory, nonce);
                        if (state.status is "started" or "failed" or "cancelled") return Receipt(state, directory);
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                await clock.DelayAsync(50, cancellationToken);
            }
            if (!Acquire(mutex, 1000)) return Pending(directory, "包内启动握手尚未确认");
            try
            {
                var state = ReadState(directory, nonce);
                if (state.status == "pending") { state = state with { status = "cancelled" }; WriteState(directory, state); }
                return Receipt(state, directory);
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Win32Exception) when (!dispatched) { DeleteState(directory); return new(LaunchStatus.Failed, null, PackageMethod, "未提交包内启动请求"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        { return Pending(directory, dispatched ? "包内启动状态无法确认" : "包内启动准备状态无法确认"); }
    }

    public void Complete(LaunchReceipt receipt)
    {
        if (receipt.StateDirectory is not { } directory) return;
        var nonce = Path.GetFileName(directory);
        using var mutex = new Mutex(false, "Local\\FAswitchLaunch_" + nonce);
        if (!Acquire(mutex, 1000)) throw new IOException("无法确认启动记录完成");
        try
        {
            var state = ReadState(directory, nonce);
            if (state.status != "started") throw new InvalidDataException("启动握手状态不允许完成");
            WriteState(directory, state with { status = "complete" });
            DeleteState(directory);
        }
        finally { mutex.ReleaseMutex(); }
    }

    public async Task<LaunchReceipt> VerifyRuntimeAsync(LaunchReceipt receipt, DesktopLaunchRequest request, CancellationToken cancellationToken)
    {
        if (clone is null) return receipt;
        ManagedRuntimeInspection inspection;
        try { inspection = await runtimeInspector.InspectAsync(clone, request, cancellationToken); }
        catch (Exception) { inspection = new(false, "受管分身的实际运行环境未验证。"); }
        if (inspection.Verified)
        {
            try
            {
                if (processes is WindowsProcessSystem)
                {
                    var snapshot = processes.Snapshot();
                    var ownedPids = clone.ProcessScope.Selected(snapshot).Select(process => process.Pid).ToHashSet();
                    _ = new ManagedWindowIcon().Apply(snapshot, ownedPids.Contains);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { /* An icon must not block a healthy launch. */ }
            return receipt with { Message = inspection.Message };
        }
        var stopped = await discovery.StopAsync(ProcessScope.DesktopAndCli, CancellationToken.None);
        var confirmed = discovery.ConfirmStopped(ProcessScope.DesktopAndCli);
        if (!stopped.Success || !confirmed.Success) return receipt with { Status = LaunchStatus.Pending, Message = inspection.Message + " 分身退出尚未确认，保留恢复材料。" };
        if (receipt.StateDirectory is { } directory)
        {
            try
            {
                var nonce = Path.GetFileName(directory);
                var state = ReadState(directory, nonce);
                WriteState(directory, state with { status = "failed" });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or StorageException)
            {
                return receipt with { Status = LaunchStatus.Pending, Message = inspection.Message + " 受管分身已停止，但启动记录未能更新（" + error.GetType().Name + "）；保留恢复材料。" };
            }
        }
        return receipt with { Status = LaunchStatus.Failed, Message = inspection.Message + " 仅本项目拥有的分身已停止，本体保持运行。" };
    }

    private LaunchReceipt Receipt(State state, string directory)
    {
        if (state.status == "started" && state.pid > 0 && RegisterCloneState(state)) return new(LaunchStatus.Confirmed, state.pid, PackageMethod, "包内进程创建已确认，等待窗口", directory);
        if (state.status is "failed" or "cancelled") { DeleteState(directory); return new(LaunchStatus.Failed, null, PackageMethod, "包内启动失败或已取消，已确认没有待执行请求"); }
        return Pending(directory, "包内进程创建尚未确认", state.pid);
    }
    private bool RegisterCloneState(State state)
    {
        if (clone is null) return true;
        if (state.pid is not { } pid || state.creation_time <= 0 || state.executable.Length == 0) return false;
        var snapshot = processes.Snapshot();
        var identity = snapshot.FirstOrDefault(process => process.Pid == pid && process.CreationTime == state.creation_time
            && process.Executable.Equals(state.executable, StringComparison.OrdinalIgnoreCase));
        return identity is not null && clone.ProcessScope.RegisterRoot(identity, "受管分身");
    }
    private static LaunchReceipt Pending(string directory, string message, int? pid = null) => new(LaunchStatus.Pending, pid, PackageMethod, message, directory);
    private static bool Acquire(Mutex mutex, int milliseconds) { try { return mutex.WaitOne(milliseconds); } catch (AbandonedMutexException) { return true; } }
    private State ReadState(string directory, string nonce)
    {
        directory = GuardStateDirectory(directory);
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(Path.Combine(directory, "state.json")));
        return state is not null && state.nonce == nonce ? state : throw new InvalidDataException("启动状态标识不匹配");
    }
    private void WriteState(string directory, State state)
    {
        directory = GuardStateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, state); stream.Flush(true);
    }
    private void DeleteState(string directory)
    {
        if (directory.Length == 0) return;
        directory = GuardStateDirectory(directory);
        if (!Directory.Exists(directory)) return;
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.Count > 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                PhysicalFileStorage.RejectLinks(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private string GuardStateDirectory(string directory)
    {
        var root = Path.GetFullPath(stateRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new InvalidDataException("启动状态路径不在已明确的受管目录内");
        PhysicalFileStorage.RejectLinks(root); PhysicalFileStorage.RejectLinks(target); PhysicalFileStorage.RejectLinks(Path.Combine(target, "state.json"));
        return target;
    }
    private static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    public static string JoinArguments(IEnumerable<string> args) => string.Join(" ", args.Select(QuoteArgument));
    private static string QuoteArgument(string argument)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static string ResolveRealPath(string path)
    {
        // BACKUP_SEMANTICS also opens directories. MSIX may redirect LocalAppData;
        // external programs and firewall rules need the path actually on disk.
        using var handle = NativeMethods.CreateFileW(path, 0, 7, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid) return path;
        var buffer = new StringBuilder(32768);
        var length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return path;
        var result = buffer.ToString();
        return result.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase) ? "\\\\" + result[8..] : result.StartsWith("\\\\?\\", StringComparison.Ordinal) ? result[4..] : result;
    }
}
