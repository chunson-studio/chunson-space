using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using ChunsonSpace.Core;

namespace ChunsonSpace.Windows;

public sealed record ManagedRuntimeInspection(bool Verified, string Message);
public sealed class ManagedRuntimeEnvironmentMismatchException() : IOException("Actual page environment does not match the selected profile.");
public interface IManagedCodexRuntimeInspector
{
    Task<ManagedRuntimeInspection> InspectAsync(ManagedCloneLaunchOptions options, DesktopLaunchRequest request, CancellationToken token);
}
public interface IManagedRuntimeCdp : IAsyncDisposable
{
    Task<JsonObject> CallAsync(string method, JsonObject parameters, string? session, CancellationToken token);
    IAsyncEnumerable<JsonObject> Events(CancellationToken token);
}
public interface IManagedRuntimeCdpFactory { Task<IManagedRuntimeCdp> ConnectAsync(Uri endpoint, CancellationToken token); }

public sealed class ManagedCodexRuntimeInspector(ICommandRunner? commands = null, IManagedRuntimeCdpFactory? connections = null) : IManagedCodexRuntimeInspector, IAsyncDisposable
{
    private RuntimeSession? active;
    public async Task<ManagedRuntimeInspection> InspectAsync(ManagedCloneLaunchOptions options, DesktopLaunchRequest request, CancellationToken cancellationToken)
    {
        var timezone = request.Environment?.GetValueOrDefault("TZ") ?? "";
        if (!System.Net.IPAddress.TryParse(request.ExpectedEgressIp, out _) || !ProfileRules.ValidTimezone(timezone)) return new(false, "分身缺少启动前已验证的出口 IP 或 IANA 时区。");
        var stage = "连接独立调试入口";
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(20000); var token = deadline.Token;
            var portFile = Path.Combine(Path.GetFullPath(options.DesktopRoot), "DevToolsActivePort"); PhysicalFileStorage.RejectLinks(portFile);
            string[] lines = [];
            for (var attempt = 0; attempt < 40; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(portFile) && new FileInfo(portFile).Length <= 4096) { lines = await File.ReadAllLinesAsync(portFile, token); if (lines.Length >= 2) break; }
                await Task.Delay(150, token);
            }
            if (lines.Length < 2 || !int.TryParse(lines[0], out var port) || port is < 1 or > 65535 || !Regex.IsMatch(lines[1], "\\A/devtools/browser/[A-Za-z0-9-]+\\z")) return new(false, "分身独立调试入口未确认。");
            var script = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);$ErrorActionPreference='Stop';@((Get-NetTCPConnection -LocalPort " + port + " -State Listen -ErrorAction Stop).OwningProcess | Sort-Object -Unique)|ConvertTo-Json -Compress";
            var ownership = await (commands ?? new WindowsCommandRunner()).RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], token);
            if (ownership.ExitCode != 0) return new(false, "分身调试端口所属进程未确认。");
            var ownerJson = JsonNode.Parse(ownership.Output);
            var owners = ownerJson is JsonArray array ? array.Select(value => value?.GetValue<int>() ?? 0).ToArray() : ownerJson is JsonValue number ? [number.GetValue<int>()] : Array.Empty<int>();
            if (owners.Length == 0 || owners.Any(pid => pid <= 0 || !options.ProcessScope.OwnsPid(pid))) return new(false, "调试端口不属于已拥有的分身；未连接本体页面。");
            var endpoint = new Uri($"ws://127.0.0.1:{port}{lines[1]}");
            if (active is null || !active.Matches(endpoint, timezone, request.ExpectedEgressIp))
            {
                if (active is not null) await active.DisposeAsync();
                active = new(await (connections ?? new ManagedRuntimeCdpFactory()).ConnectAsync(endpoint, token), options, request, owners, endpoint);
            }
            stage = "检查页面时区和英语";
            await active.VerifyAsync(token);
            var message = $"Codex 页面实际时区：{timezone}，语言：en-US；启动前已核对出口 IP：{request.ExpectedEgressIp}。启动后不重复查询出口或评分，Rust 后端出口未单独实测。";
            SaveDiagnostic(options, true, message);
            return new(true, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var reason = active?.FailureReason;
            var message = "分身运行验证未完成：" + (string.IsNullOrEmpty(reason) ? stage : reason) + (error is OperationCanceledException ? "（等待超时）" : "") + "。";
            SaveDiagnostic(options, false, message, error);
            if (active is not null) { await active.DisposeAsync(); active = null; }
            return new(false, message);
        }
    }
    public async ValueTask DisposeAsync() { if (active is not null) { await active.DisposeAsync(); active = null; } }
    private static string Text(JsonObject? value, string name) => value?[name] is JsonValue text && text.TryGetValue<string>(out var result) ? result : "";
    private static void SaveDiagnostic(ManagedCloneLaunchOptions options, bool verified, string message, Exception? error = null)
    {
        if (options.RuntimeDiagnosticPath is not { Length: > 0 } path) return;
        try
        {
            new PhysicalFileStorage().WriteAtomic(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                checkedAt = DateTimeOffset.UtcNow, verified, exitCheck = "prelaunch-only", message,
                errorType = error?.GetType().Name, method = error is ManagedRuntimeCdpTimeoutException timeout ? timeout.Method : (error as ManagedRuntimeCdpException)?.Method, code = (error as ManagedRuntimeCdpException)?.Code
            }));
        }
        catch (Exception writeError) when (writeError is StorageException or IOException or UnauthorizedAccessException) { /* Diagnostics cannot change the launch outcome. */ }
    }

    private sealed class RuntimeSession(IManagedRuntimeCdp rpc, ManagedCloneLaunchOptions options, DesktopLaunchRequest request, int[] owners, Uri endpoint) : IAsyncDisposable
    {
        private readonly string timezone = request.Environment!["TZ"];
        private readonly CancellationTokenSource stopped = new();
        private Task? eventWorker;
        private Task? ownerWorker;
        private string userAgent = "";
        private int faulted;
        private bool verified;
        private int disposed;
        public string FailureReason { get; private set; } = "读取分身页面";
        private readonly ConcurrentDictionary<string, Lazy<Task<string>>> configuredTargets = new(StringComparer.Ordinal);
        public bool Matches(Uri other, string zone, string ip) => Volatile.Read(ref disposed) == 0 && Volatile.Read(ref faulted) == 0 && endpoint == other && timezone == zone && request.ExpectedEgressIp == ip;
        public async Task VerifyAsync(CancellationToken token)
        {
            if (Volatile.Read(ref faulted) != 0) throw new IOException();
            userAgent = Text(await rpc.CallAsync("Browser.getVersion", new(), null, token), "userAgent"); if (userAgent.Length == 0) throw new IOException();
            var targets = await rpc.CallAsync("Target.getTargets", new(), null, token);
            var pages = (targets["targetInfos"] as JsonArray)?.OfType<JsonObject>().Where(target => Text(target, "type") == "page").ToArray() ?? [];
            if (pages.Length == 0) throw new IOException();
            foreach (var page in pages)
            {
                var targetId = Text(page, "targetId");
                try { await ConfigureTargetAsync(targetId, null, token); }
                catch (IOException) { if (await TargetExistsAsync(targetId, token)) throw; configuredTargets.TryRemove(targetId, out _); }
            }
            eventWorker ??= ObservePagesAsync();
            await rpc.CallAsync("Target.setAutoAttach", new() { ["autoAttach"] = true, ["flatten"] = true, ["waitForDebuggerOnStart"] = false }, null, token);
            if (Volatile.Read(ref faulted) != 0) throw new IOException();
            FailureReason = "独立调试连接或页面跟踪已断开";
            verified = true; ownerWorker ??= ObserveOwnershipAsync();
        }
        private async Task<string> ConfigureTargetAsync(string targetId, string? existingSession, CancellationToken token)
        {
            if (targetId.Length == 0) throw new IOException();
            var configured = configuredTargets.GetOrAdd(targetId, _ => new Lazy<Task<string>>(async () =>
            {
                var session = existingSession;
                if (session is null) session = Text(await rpc.CallAsync("Target.attachToTarget", new() { ["targetId"] = targetId, ["flatten"] = true }, null, stopped.Token), "sessionId");
                await ConfigurePageAsync(session, stopped.Token);
                return session;
            }, LazyThreadSafetyMode.ExecutionAndPublication));
            var configuredSession = await configured.Value.WaitAsync(token);
            if (existingSession is { Length: > 0 } && existingSession != configuredSession)
                await rpc.CallAsync("Runtime.runIfWaitingForDebugger", new(), existingSession, token);
            return configuredSession;
        }
        private async Task ConfigurePageAsync(string session, CancellationToken token)
        {
            if (session.Length == 0) throw new IOException();
            FailureReason = "设置分身页面时区和英语";
            try { await rpc.CallAsync("Emulation.setTimezoneOverride", new() { ["timezoneId"] = timezone }, session, token); }
            catch (ManagedRuntimeCdpException error) when (error.SharedOverride) { /* Verify the inherited value below. */ }
            try { await rpc.CallAsync("Emulation.setLocaleOverride", new() { ["locale"] = "en-US" }, session, token); }
            catch (ManagedRuntimeCdpException error) when (error.SharedOverride) { /* Verify the inherited value below. */ }
            await rpc.CallAsync("Emulation.setUserAgentOverride", new() { ["userAgent"] = userAgent, ["acceptLanguage"] = "en-US,en;q=0.9" }, session, token);
            await rpc.CallAsync("Network.enable", new(), session, token);
            await rpc.CallAsync("Network.setExtraHTTPHeaders", new() { ["headers"] = new JsonObject { ["Accept-Language"] = "en-US,en;q=0.9" } }, session, token);
            await rpc.CallAsync("Runtime.runIfWaitingForDebugger", new(), session, token);
            FailureReason = "读取分身页面实际时区和语言";
            JsonObject? actual = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var observed = await rpc.CallAsync("Runtime.evaluate", new() { ["expression"] = BrowserEnvironmentVerification.ReadExpression(timezone), ["returnByValue"] = true }, session, token);
                    actual = JsonNode.Parse(Text(observed["result"] as JsonObject, "value")) as JsonObject;
                    break;
                }
                catch (ManagedRuntimeCdpException error) when (error.ContextChanged && attempt < 2) { await Task.Delay(100, token); }
            }
            FailureReason = "分身页面实际时区或语言不匹配";
            if (!BrowserEnvironmentVerification.EnglishEnvironmentMatches(actual) || !BrowserEnvironmentVerification.EnglishLocaleMatches(actual)) throw new ManagedRuntimeEnvironmentMismatchException();
            await rpc.CallAsync("Target.setAutoAttach", new() { ["autoAttach"] = true, ["flatten"] = true, ["waitForDebuggerOnStart"] = false }, session, token);
        }
        private async Task ObservePagesAsync()
        {
            try
            {
                await foreach (var packet in rpc.Events(stopped.Token))
                {
                    if (Text(packet, "method") != "Target.attachedToTarget" || packet["params"] is not JsonObject parameters) continue;
                    var session = Text(parameters, "sessionId");
                    if (Text(parameters["targetInfo"] as JsonObject, "type") == "page")
                    {
                        var targetId = Text(parameters["targetInfo"] as JsonObject, "targetId");
                        try { await ConfigureTargetAsync(targetId, session, stopped.Token); }
                        catch (IOException error)
                        {
                            configuredTargets.TryRemove(targetId, out _);
                            if (await TargetExistsAsync(targetId, stopped.Token))
                            {
                                if (error is ManagedRuntimeEnvironmentMismatchException) throw;
                                RecordIncomplete(error);
                            }
                        }
                        catch (OperationCanceledException error) when (!stopped.IsCancellationRequested)
                        { configuredTargets.TryRemove(targetId, out _); RecordIncomplete(error); }
                        FailureReason = "独立调试连接或页面跟踪已断开";
                    }
                    else if (session.Length > 0) await rpc.CallAsync("Runtime.runIfWaitingForDebugger", new(), session, stopped.Token);
                }
                if (!stopped.IsCancellationRequested) await FailAsync();
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
            catch (Exception error) { if (!stopped.IsCancellationRequested) await FailAsync(error); }
        }
        private async Task<bool> TargetExistsAsync(string targetId, CancellationToken token)
        {
            var targets = await rpc.CallAsync("Target.getTargets", new(), null, token);
            return (targets["targetInfos"] as JsonArray)?.OfType<JsonObject>().Any(target => Text(target, "targetId") == targetId) == true;
        }
        private async Task ObserveOwnershipAsync()
        {
            try
            {
                while (!stopped.IsCancellationRequested)
                {
                    await Task.Delay(1000, stopped.Token);
                    if (!owners.Any(options.ProcessScope.OwnsPid)) { stopped.Cancel(); await rpc.DisposeAsync(); return; }
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
            catch (Exception error) { if (!stopped.IsCancellationRequested) await FailAsync(error); }
        }
        private async Task FailAsync(Exception? error = null)
        {
            if (Interlocked.Exchange(ref faulted, 1) != 0) return;
            var mismatch = error is ManagedRuntimeEnvironmentMismatchException;
            SaveDiagnostic(options, false, "运行期间检查中断：" + FailureReason + "。" + (verified && mismatch ? "实际页面环境不匹配，项目将停止受管分身。" : verified ? "检查未完成，保留已启动分身；不把检查中断当作配置不符。" : "启动尚未确认。"), error);
            if (verified && mismatch) await options.ProcessScope.StopOwnedAsync(CancellationToken.None);
            stopped.Cancel(); await rpc.DisposeAsync();
        }
        private void RecordIncomplete(Exception error) => SaveDiagnostic(options, false, "运行期间页面检查未完成：" + FailureReason + "。分身保持运行；未将超时或协议错误当作实际环境不匹配。", error);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stopped.Cancel(); await rpc.DisposeAsync(); if (eventWorker is not null) await eventWorker; if (ownerWorker is not null) await ownerWorker; stopped.Dispose();
        }
    }
}
