using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

public interface IBrowserTimezoneController
{
    Task EnsureAsync(string accountRoot, BrowserIdentity browser, string timezone, CancellationToken token);
}

public sealed record BrowserTimezoneRequest(string Id, string AccountRoot, BrowserIdentity Browser, string Timezone);
public sealed record BrowserTimezoneState(string Id, int HelperPid, long HelperCreated, string State, string Timezone, DateTimeOffset Updated, string? Error = null);

// One small controller lives for exactly one browser process, independently of
// the manager window. It reads no cookies, credentials or page contents.
public sealed class BrowserTimezoneController(string helperExecutable) : IBrowserTimezoneController
{
    public const string RequestFile = "chunson-browser-timezone-request.json";
    public const string StateFile = "chunson-browser-timezone-state.json";
    public static bool IsAlive(int pid, long created, string? executable = null)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == created
            && (executable is null || process.MainModule?.FileName?.Equals(executable, StringComparison.OrdinalIgnoreCase) == true); }
        catch (Exception) { return false; }
    }
    public async Task EnsureAsync(string accountRoot, BrowserIdentity browser, string timezone, CancellationToken token)
    {
        Validate(new("validation", accountRoot, browser, timezone));
        var files = new PhysicalFileStorage();
        var requestPath = Path.Combine(browser.ProfileDirectory, RequestFile);
        var statePath = Path.Combine(browser.ProfileDirectory, StateFile);
        BrowserTimezoneRequest? previous = null;
        BrowserTimezoneState? state = null;
        if (files.FileExists(requestPath)) previous = JsonSerializer.Deserialize<BrowserTimezoneRequest>(files.Read(requestPath));
        if (files.FileExists(statePath)) state = JsonSerializer.Deserialize<BrowserTimezoneState>(files.Read(statePath));
        if (previous is not null && state is not null && state.Id == previous.Id && IsAlive(state.HelperPid, state.HelperCreated, helperExecutable))
        {
            if (previous.Browser != browser || previous.Timezone != timezone)
                throw new AccountException(AccountError.Browser, "档案浏览器仍使用上次的出口时区，请关闭此账号浏览器后重开；网页登录资料保留。");
            if (state.State == "ready" && DateTimeOffset.UtcNow - state.Updated < TimeSpan.FromSeconds(6)) return;
            throw new AccountException(AccountError.Browser, "档案浏览器时区尚未确认，请关闭此账号浏览器后重开。");
        }
        var request = new BrowserTimezoneRequest(Guid.NewGuid().ToString("N"), accountRoot, browser, timezone);
        files.WriteAtomic(requestPath, JsonSerializer.SerializeToUtf8Bytes(request));
        var start = new ProcessStartInfo(helperExecutable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--browser-timezone"); start.ArgumentList.Add(requestPath);
        using var helper = Process.Start(start) ?? throw new AccountException(AccountError.Browser, "浏览器时区服务未启动。");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        do
        {
            token.ThrowIfCancellationRequested();
            if (files.FileExists(statePath))
            {
                state = JsonSerializer.Deserialize<BrowserTimezoneState>(files.Read(statePath));
                if (state?.Id == request.Id)
                {
                    if (state.State == "ready" && state.Timezone == timezone) return;
                    if (state.State == "failed") throw new AccountException(AccountError.Browser, "浏览器时区未确认（" + state.Error + "），未打开登录页。");
                }
            }
            if (helper.HasExited || !IsAlive(browser.Pid, browser.CreationTime, browser.Executable)) break;
            await Task.Delay(100, token);
        } while (DateTimeOffset.UtcNow < deadline);
        throw new AccountException(AccountError.Browser, "浏览器时区服务未就绪，未打开登录页。请关闭此账号浏览器后重开。");
    }
    internal static void Validate(BrowserTimezoneRequest request)
    {
        _ = new AccountBrowserProfiles(request.AccountRoot).Validate(request.Browser.ProfileDirectory);
        if (!ProfileRules.ValidTimezone(request.Timezone) || !TimeZoneInfo.TryConvertIanaIdToWindowsId(request.Timezone, out _))
            throw new AccountException(AccountError.Browser, "出口时区无效。");
        if (!InstalledBrowsers.SupportedExecutable(request.Browser.Executable) || !IsAlive(request.Browser.Pid, request.Browser.CreationTime, request.Browser.Executable))
            throw new AccountException(AccountError.Browser, "浏览器进程身份未确认。");
    }
    public static async Task<int> RunHelperAsync(string requestPath)
    {
        if (Path.GetFileName(requestPath) != RequestFile) return 2;
        var files = new PhysicalFileStorage();
        var request = JsonSerializer.Deserialize<BrowserTimezoneRequest>(files.Read(requestPath)) ?? throw new IOException("Invalid timezone request.");
        Validate(request);
        if (!Path.GetFullPath(requestPath).Equals(Path.Combine(request.Browser.ProfileDirectory, RequestFile), StringComparison.OrdinalIgnoreCase)) return 2;
        using var self = Process.GetCurrentProcess();
        var statePath = Path.Combine(request.Browser.ProfileDirectory, StateFile);
        var state = new BrowserTimezoneState(request.Id, self.Id, self.StartTime.ToUniversalTime().ToFileTimeUtc(), "starting", request.Timezone, DateTimeOffset.UtcNow);
        void Save(string status, string? error = null)
        {
            if (!Directory.Exists(request.Browser.ProfileDirectory)) return;
            state = state with { State = status, Updated = DateTimeOffset.UtcNow, Error = error };
            files.WriteAtomic(statePath, JsonSerializer.SerializeToUtf8Bytes(state));
        }
        Save("starting");
        using var stopped = new CancellationTokenSource();
        try
        {
            var portPath = Path.Combine(request.Browser.ProfileDirectory, "DevToolsActivePort");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
            Uri? endpoint = null;
            while (endpoint is null && DateTimeOffset.UtcNow < deadline && IsAlive(request.Browser.Pid, request.Browser.CreationTime, request.Browser.Executable))
            {
                if (files.FileExists(portPath))
                {
                    var lines = System.Text.Encoding.UTF8.GetString(files.Read(portPath)).Split('\n', StringSplitOptions.TrimEntries);
                    if (lines.Length >= 2 && int.TryParse(lines[0], out var port) && port is > 0 and <= 65535 && Regex.IsMatch(lines[1], "\\A/devtools/browser/[A-Za-z0-9-]+\\z"))
                        endpoint = new($"ws://127.0.0.1:{port}{lines[1]}");
                }
                if (endpoint is null) await Task.Delay(100);
            }
            if (endpoint is null) throw new IOException("BrowserDebugEndpointUnavailable");
            await using var connection = await new CdpConnectionFactory().ConnectAsync(endpoint, stopped.Token);
            var firstPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var eventTask = ApplyTargetsAsync(connection, request.Timezone, firstPage, stopped.Token);
            await connection.CallAsync("Target.setAutoAttach", AutoAttach(), null, stopped.Token);
            var startup = await Task.WhenAny(firstPage.Task, eventTask, Task.Delay(12000, stopped.Token));
            if (startup != firstPage.Task) throw new IOException("BrowserPageTimezoneUnconfirmed");
            await firstPage.Task;
            Save("ready");
            while (IsAlive(request.Browser.Pid, request.Browser.CreationTime, request.Browser.Executable))
            {
                if (eventTask.IsCompleted) { await eventTask; throw new IOException("BrowserControlDisconnected"); }
                Save("ready");
                await Task.Delay(500);
            }
            stopped.Cancel();
            try { await eventTask; } catch (OperationCanceledException) { }
            Save("closed");
            return 0;
        }
        catch (Exception error)
        {
            stopped.Cancel();
            Save(IsAlive(request.Browser.Pid, request.Browser.CreationTime, request.Browser.Executable) ? "failed" : "closed", error.GetType().Name);
            return 1;
        }
    }
    private static JsonObject AutoAttach() => new() { ["autoAttach"] = true, ["waitForDebuggerOnStart"] = true, ["flatten"] = true,
        ["filter"] = new JsonArray(new JsonObject { ["type"] = "page", ["exclude"] = false }, new JsonObject { ["type"] = "iframe", ["exclude"] = false },
            new JsonObject { ["type"] = "worker", ["exclude"] = false }, new JsonObject { ["type"] = "shared_worker", ["exclude"] = false }, new JsonObject { ["exclude"] = true }) };
    internal static async Task ApplyTargetsAsync(ICdpConnection connection, string timezone, TaskCompletionSource firstPage, CancellationToken token)
    {
        await foreach (var packet in connection.Events(token))
        {
            if (AccountJson.Text(packet, "method") != "Target.attachedToTarget" || packet["params"] is not JsonObject parameters) continue;
            var session = AccountJson.Text(parameters, "sessionId");
            var type = AccountJson.Text(parameters["targetInfo"] as JsonObject, "type");
            var target = AccountJson.Text(parameters["targetInfo"] as JsonObject, "targetId");
            try
            {
                // Same-process frames may share an override. Only accept a rejected
                // override if the engine's actual timezone is already correct.
                try { await connection.CallAsync("Emulation.setTimezoneOverride", new() { ["timezoneId"] = timezone }, session, token); }
                catch (AccountException) { }
                var actual = await connection.CallAsync("Runtime.evaluate", new() { ["expression"] = "Intl.DateTimeFormat().resolvedOptions().timeZone", ["returnByValue"] = true }, session, token);
                if (AccountJson.Text(actual["result"] as JsonObject, "value") != timezone) throw new IOException("BrowserTimezoneMismatch");
                await connection.CallAsync("Target.setAutoAttach", AutoAttach(), session, token);
                if (type == "page") firstPage.TrySetResult();
            }
            catch (AccountException)
            {
                token.ThrowIfCancellationRequested();
                // A queued attach can outlive a short-lived tab or worker. Only
                // ignore its protocol failure after the browser confirms removal.
                // Live targets, real timezone mismatches and disconnects still fail.
                var current = await connection.CallAsync("Target.getTargets", new(), null, token);
                if (target.Length == 0 || current["targetInfos"] is not JsonArray targets
                    || targets.Any(item => item is not JsonObject || AccountJson.Text(item as JsonObject, "targetId").Length == 0)
                    || targets.OfType<JsonObject>().Any(item => AccountJson.Text(item, "targetId") == target)) throw;
            }
            finally
            {
                // Never strand a page or worker paused if applying the setting fails.
                try { await connection.CallAsync("Runtime.runIfWaitingForDebugger", new(), session, CancellationToken.None); } catch (Exception) { }
            }
        }
    }
}
