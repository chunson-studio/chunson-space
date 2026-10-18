using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;
using System.Collections.Concurrent;

namespace ChunsonSpace.Accounts;

public interface IChromeSystem
{
    string? FindChrome();
    Task StartAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
    Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(string executable, string profileDirectory, CancellationToken cancellationToken);
    Task<IReadOnlyList<BrowserIdentity>?> ObserveKnownAsync(string executable, string profileDirectory, IReadOnlyList<BrowserIdentity> known, CancellationToken token)
        => ObserveAsync(executable, profileDirectory, token);
    async Task<BrowserIdentity?> StartTrackedAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    { await StartAsync(executable, arguments, token); return null; }
    Task<bool> CloseOwnedAsync(IReadOnlyList<BrowserIdentity> identities, CancellationToken token) => Task.FromResult(false);
    async Task<bool?> HasOpenWindowsAsync(string executable, string profile, IReadOnlyList<BrowserIdentity> known, CancellationToken token)
    { var roots = await ObserveAsync(executable, profile, token); return roots is null ? null : roots.Count > 0; }
}
public sealed record BrowserSessionCleanupResult(bool Success, string Message, IReadOnlyList<string>? Warnings = null);
public interface IBrowserSessionCleanup { Task<BrowserSessionCleanupResult> CleanupAsync(BrowserLaunch? launch, CancellationToken token); }
public interface IAccountBrowserNetwork { Task<bool> CloseForNetworkChangeAsync(string profileName, CancellationToken token); }
public interface ICdpConnection : IAsyncDisposable
{
    Task<JsonObject> CallAsync(string method, JsonObject parameters, string? sessionId, CancellationToken cancellationToken);
    IAsyncEnumerable<JsonObject> Events(CancellationToken cancellationToken);
}
public interface ICdpConnectionFactory { Task<ICdpConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken); }
public interface IChromeLaunchCoordinator
{
    Task<IChromeLaunchLease> BeginAsync(string profileName, EnvironmentSettings environment, CancellationToken cancellationToken);
}
public interface IChromeLaunchLease : IAsyncDisposable
{
    IReadOnlyList<string> Warnings { get; }
    void TrackRoot(BrowserIdentity identity);
    Task FailureAsync(CancellationToken cancellationToken);
}

public static class ChromeArguments
{
    public static string RequireLoopbackProxy(string proxy)
    {
        proxy = Proxy(proxy);
        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var endpoint)
            || !(endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || System.Net.IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address)))
            throw new AccountException(AccountError.ExitUnavailable, "专用授权浏览器需要本机代理地址。");
        return proxy;
    }
    public static string RestrictedDns(string proxy)
    {
        var endpoint = new Uri(RequireLoopbackProxy(proxy));
        // Chromium applies MAP to numeric proxy hosts too. Keep the selected loopback reachable.
        return "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE " + endpoint.Host;
    }
    public static string Proxy(string value)
    {
        string proxy;
        try { proxy = ProfileRules.NormalizeProxy(value); }
        catch (StorageException) { throw new AccountException(AccountError.ExitUnavailable, "浏览器代理写法不合法。"); }
        if (proxy.Length == 0) throw new AccountException(AccountError.ExitUnavailable, "此档案未绑定代理，浏览器不会改用系统出口或直连。");
        return proxy;
    }
    public static IReadOnlyList<string> Build(string profile, EnvironmentSettings environment, Uri url, bool defer)
    {
        var proxy = Proxy(environment.Proxy);
        var args = new List<string> { "--user-data-dir=" + Path.GetFullPath(profile), "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-features=Translate", "--disable-crash-reporter",
            "--proxy-server=" + proxy, "--disable-quic", "--webrtc-ip-handling-policy=disable_non_proxied_udp" };
        if (environment.Locale.Length > 0 && ProfileRules.ValidLocale(environment.Locale)) { args.Add("--lang=" + environment.Locale); args.Add("--accept-lang=" + environment.Locale); }
        if (defer) args.Add("--remote-debugging-port=0");
        args.Add(defer ? "about:blank" : url.AbsoluteUri);
        return args;
    }
    public static string? RootProfile(IReadOnlyList<string> arguments)
    {
        string? result = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            var item = arguments[i];
            if (item == "--type" || item.StartsWith("--type=", StringComparison.Ordinal)) return null;
            if (item == "--user-data-dir") { if (result is not null || i + 1 >= arguments.Count) return null; result = arguments[++i]; }
            else if (item.StartsWith("--user-data-dir=", StringComparison.Ordinal)) { if (result is not null) return null; result = item[16..]; }
        }
        return result is not null && Path.IsPathFullyQualified(result) ? Path.GetFullPath(result) : null;
    }
}

public sealed class WindowsChromeSystem(Func<string?>? finder = null) : IChromeSystem
{
    public async Task<bool?> HasOpenWindowsAsync(string executable, string profile, IReadOnlyList<BrowserIdentity> known, CancellationToken token)
    {
        var roots = await ObserveAsync(executable, profile, token);
        if (roots is null || roots.Any(root => !known.Contains(root))) return null;
        if (roots.Count == 0) return false;
        var visible = false;
        if (!EnumWindows((window, _) => { GetWindowThreadProcessId(window, out var pid); if (roots.Any(root => root.Pid == pid) && IsWindowVisible(window)) visible = true; return true; }, 0)) return null;
        return visible;
    }
    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    public Task<IReadOnlyList<BrowserIdentity>?> ObserveKnownAsync(string executable, string profileDirectory, IReadOnlyList<BrowserIdentity> known, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var snapshot = new WindowsProcessSystem().Snapshot();
            return Task.FromResult<IReadOnlyList<BrowserIdentity>?>(known.Where(identity => identity.Executable.Equals(executable, StringComparison.OrdinalIgnoreCase)
                && identity.ProfileDirectory.Equals(profileDirectory, StringComparison.OrdinalIgnoreCase)
                && snapshot.Any(process => process.Pid == identity.Pid && process.CreationTime == identity.CreationTime
                    && process.Executable.Equals(identity.Executable, StringComparison.OrdinalIgnoreCase))).ToArray());
        }
        catch (Exception) { return Task.FromResult<IReadOnlyList<BrowserIdentity>?>(null); }
    }
    public string? FindChrome()
    {
        if (finder is not null) return finder();
        foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (root is null) continue;
            var candidate = Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
    public Task StartAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AccountException(AccountError.Browser, "未能启动此档案的 Chrome。");
        return Task.CompletedTask;
    }
    public Task<BrowserIdentity?> StartTrackedAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var profile = ChromeArguments.RootProfile(arguments) ?? throw new AccountException(AccountError.Browser, "专用浏览器目录无法确认。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new AccountException(AccountError.Browser, "专用浏览器未创建进程。");
        return Task.FromResult<BrowserIdentity?>(new(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(), Path.GetFullPath(executable), profile));
    }
    public async Task<bool> CloseOwnedAsync(IReadOnlyList<BrowserIdentity> identities, CancellationToken token)
    {
        if (identities.Count == 0 || identities.Any(identity => identity.Pid <= 0 || identity.CreationTime <= 0 || identity.Executable.Length == 0)) return false;
        var system = new WindowsProcessSystem();
        try
        {
            var before = system.Snapshot();
            bool Orphan(IReadOnlyList<ProcessIdentity> snapshot) => snapshot.Any(child => identities.Any(root => child.ParentPid == root.Pid && child.CreationTime >= root.CreationTime)
                && !identities.Any(root => root.Pid == child.Pid && root.CreationTime == child.CreationTime && root.Executable.Equals(child.Executable, StringComparison.OrdinalIgnoreCase))
                && !snapshot.Any(parent => identities.Any(root => parent.Pid == root.Pid && parent.CreationTime == root.CreationTime && parent.Executable.Equals(root.Executable, StringComparison.OrdinalIgnoreCase))));
            if (Orphan(before)) return false;
            var selected = identities.Select(identity => new ProcessIdentity(identity.Pid, before.FirstOrDefault(process => process.Pid == identity.Pid)?.ParentPid ?? 0, Path.GetFileName(identity.Executable), identity.Executable, identity.CreationTime)).ToArray();
            var result = await new CodexProcessService(system, new SystemWaitClock()).StopIdentitiesAsync(selected, token);
            return result.Success && !Orphan(system.Snapshot());
        }
        catch (Exception) { return false; }
    }
    public async Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(string executable, string profileDirectory, CancellationToken cancellationToken)
    {
        if (!InstalledBrowsers.SupportedExecutable(executable)) return null;
        var escaped = Path.GetFullPath(executable).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        var filter = "Name='" + Path.GetFileName(executable) + "' AND ExecutablePath='" + escaped + "'";
        var script = "$ErrorActionPreference='Stop'; $OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_Process -Filter '" + filter.Replace("'", "''", StringComparison.Ordinal) + "' | ForEach-Object { @{pid=[int]$_.ProcessId;exe=$_.ExecutablePath;command=$_.CommandLine;created=$_.CreationDate.ToFileTimeUtc()} })";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", WindowsPowerShellCommand.Encode(script) }) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(deadline.Token);
            _ = await errors;
            if (process.ExitCode != 0) return null;
            var parsed = JsonNode.Parse(await output);
            var items = parsed is JsonArray array ? array.OfType<JsonObject>() : parsed is JsonObject singleton ? [singleton] : [];
            var nativeProcesses = new WindowsProcessSystem().Snapshot();
            var result = new List<BrowserIdentity>();
            var unreadable = false;
            foreach (var item in items)
            {
                var command = AccountJson.Text(item, "command");
                if (command.Length == 0) { unreadable = true; continue; }
                var profile = ChromeArguments.RootProfile(ParseCommandLine(command));
                if (profile is null || !profile.Equals(Path.GetFullPath(profileDirectory), StringComparison.OrdinalIgnoreCase)) continue;
                var pid = (int)(AccountJson.Integer(item["pid"]) ?? 0);
                var created = AccountJson.Integer(item["created"]) ?? 0;
                // CIM dates lose sub-microsecond ticks; retain native precision for ownership checks.
                var native = nativeProcesses.FirstOrDefault(process => process.Pid == pid && process.CreationTime > 0 && created > 0
                    && process.CreationTime / 10 == created / 10 && process.Executable.Equals(executable, StringComparison.OrdinalIgnoreCase));
                if (native is null) { unreadable = true; continue; }
                result.Add(new(pid, native.CreationTime, native.Executable, profile));
            }
            return result.Count > 0 || !unreadable ? result : null;
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or OperationCanceledException or System.ComponentModel.Win32Exception) { return null; }
    }
    private static string[] ParseCommandLine(string command)
    {
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero) return [];
        try { return Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size)) ?? "").ToArray(); }
        finally { _ = LocalFree(pointer); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}

public sealed partial class ChromeLoginBrowser(AccountRepository repository, IChromeSystem system, ICdpConnectionFactory cdp, IAccountClock? clock = null, Func<string, bool>? systemTimezoneMatches = null, IChromeLaunchCoordinator? coordinator = null,
    Func<string, CancellationToken, Task<EgressResult>>? egressProbe = null, Func<string, string, CancellationToken, Task<bool>>? protectionVerifier = null,
    Func<string, EnvironmentSettings>? environmentReader = null,
    Func<string, CancellationToken, Task<string>>? prepareBrowser = null, bool previewDuringPreparation = false, bool installedBrowser = false,
    IBrowserTimezoneController? installedTimezone = null, bool? alignInstalledTimezone = null) : IResolvedProxyLoginBrowser, IVerifiedProxyLoginBrowser, IPersistentLoginBrowser, IResumableLoginBrowser, IAccountPreparationBrowser, IBrowserSessionCleanup, IAccountBrowserNetwork, IAsyncDisposable
{
    private string? preparedProxy;
    private string? preparedExecutable;
    private AuthorizationSession? previewSession;
    public Task PrepareAsync(string profileName, string proxy, CancellationToken token)
        => installedBrowser ? PrepareInstalledAsync(proxy, token, profileName) : PrepareAsync(proxy, token);
    public async Task PrepareAsync(string proxy, CancellationToken token)
    {
        if (installedBrowser) { await PrepareInstalledAsync(proxy, token); return; }
        preparedProxy = null; preparedExecutable = null;
        if (prepareBrowser is null) return;
        var normalized = HttpAccountTransport.RequireHttpProxy(proxy);
        var executable = await prepareBrowser(normalized, token);
        token.ThrowIfCancellationRequested();
        preparedExecutable = executable; preparedProxy = normalized;
        if (previewDuringPreparation && previewSession is null)
        {
            var profile = NewProfile(LoginService.NewAccountRequestProfile);
            WriteEnglishPreferences(profile);
            var page = Path.Combine(profile, "preparing.html");
            await File.WriteAllTextAsync(page, "<!doctype html><meta charset='utf-8'><title>Preparing sign-in</title><style>body{background:#1b1e24;color:#f4f2fa;font:18px system-ui;display:grid;place-content:center;min-height:90vh}p{color:#b8b5c3}</style><h1>Preparing sign-in…</h1><p>Checking your connection and IP quality.</p><p>The sign-in page will open automatically.</p>", token);
            var arguments = RestrictedArguments(profile, new(normalized, Locale: "en-US"), new Uri("about:blank"));
            arguments[^1] = new Uri(page).AbsoluteUri;
            var opened = new BrowserLaunch(executable, profile, [], []);
            previewSession = new(opened); sessions[profile] = previewSession;
            var identity = await system.StartTrackedAsync(executable, arguments, token)
                ?? throw new AccountException(AccountError.Browser, "英文准备窗口的进程身份未确认。");
            previewSession.Roots.Add(identity);
        }
    }
    public Task<BrowserLaunch> OpenVerifiedAsync(string profileName, Uri authorizeUrl, string proxy, EgressResult egress, CancellationToken token)
        => OpenCoreAsync(profileName, authorizeUrl, proxy, token, egress);
    private readonly IAccountClock clock = clock ?? new SystemAccountClock();
    private readonly List<ICdpConnection> connections = [];
    private readonly CancellationTokenSource stopped = new();
    private readonly HashSet<string> trackedRoots = new(StringComparer.OrdinalIgnoreCase);
    private sealed class AuthorizationSession(BrowserLaunch launch)
    { public BrowserLaunch Launch { get; set; } = launch; public List<BrowserIdentity> Roots { get; } = []; public ICdpConnection? Connection { get; set; } public bool Persistent { get; set; } public Uri? AuthorizationUrl { get; set; } public bool DisposablePreparation { get; init; } public bool PreparationWindowSeen { get; set; } }
    private readonly ConcurrentDictionary<string, AuthorizationSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim cleanupGate = new(1, 1);
    private int disposed;
    public Task<BrowserLaunch> OpenAsync(string profileName, Uri authorizeUrl, CancellationToken cancellationToken)
        => OpenWithProxyAsync(profileName, authorizeUrl, repository.Proxy(profileName), cancellationToken);
    public Task<BrowserLaunch> OpenWithProxyAsync(string profileName, Uri authorizeUrl, string resolvedProxy, CancellationToken cancellationToken)
        => OpenCoreAsync(profileName, authorizeUrl, resolvedProxy, cancellationToken, null);
    private async Task<BrowserLaunch> OpenCoreAsync(string profileName, Uri authorizeUrl, string resolvedProxy, CancellationToken cancellationToken, EgressResult? admittedEgress)
    {
        if (installedBrowser) return await OpenInstalledAsync(profileName, authorizeUrl, resolvedProxy, admittedEgress, cancellationToken);
        if (Volatile.Read(ref disposed) != 0) throw new AccountException(AccountError.Browser, "授权浏览器会话已关闭。");
        resolvedProxy = HttpAccountTransport.RequireHttpProxy(resolvedProxy);
        if (prepareBrowser is not null && (preparedExecutable is null || preparedProxy != resolvedProxy)) await PrepareAsync(resolvedProxy, cancellationToken);
        var wasPrepared = preparedExecutable is not null && preparedProxy == resolvedProxy;
        var executable = (wasPrepared ? preparedExecutable : system.FindChrome()) ?? throw new AccountException(AccountError.Browser, "专用授权浏览器尚未准备或版本未确认，请先一键配置 DNS 与保护。");
        preparedProxy = null; preparedExecutable = null;
        var environment = environmentReader?.Invoke(profileName) ?? repository.Profiles.ReadEnvironment(profileName);
        environment = environment with { Proxy = HttpAccountTransport.RequireHttpProxy(resolvedProxy), FollowSystemProxy = false };
        _ = ChromeArguments.Proxy(environment.Proxy);
        EgressResult? verifiedEgress = null;
        if (egressProbe is not null || admittedEgress is not null)
        {
            if (!wasPrepared && (protectionVerifier is null || !await protectionVerifier(executable, environment.Proxy, cancellationToken)))
                throw new AccountException(AccountError.ExitUnavailable, "专用浏览器出口保护未确认，未打开授权网站。");
            verifiedEgress = admittedEgress ?? await egressProbe!(environment.Proxy, cancellationToken);
            if (!verifiedEgress.Ok || !ChunsonSpace.Application.LaunchEgressRules.Verified(new(true, verifiedEgress.Ip, verifiedEgress.Timezone, "")))
                throw new AccountException(AccountError.ExitUnavailable, "无法确认真实出口 IP 与对应时区，未打开授权网站。");
            environment = environment with { Timezone = verifiedEgress.Timezone, Locale = "en-US", LocaleAuto = false };
        }
        var warming = previewSession;
        var profile = warming?.Launch.ProfileDirectory ?? NewProfile(profileName);
        if (warming is null && verifiedEgress is not null) WriteEnglishPreferences(profile);
        // Strict sessions always use a newly created random profile, with no prior roots.
        var previous = verifiedEgress is not null ? Array.Empty<BrowserIdentity>() : await system.ObserveAsync(executable, profile, cancellationToken);
        var reuseExternal = previous is { Count: > 0 } && previous.Any(identity => !trackedRoots.Contains(RootKey(identity)));
        await using var lease = coordinator is null || reuseExternal ? null : await coordinator.BeginAsync(profileName, environment, cancellationToken);
        try
        {
        var timezone = environment.Timezone;
        var viaCdp = verifiedEgress is not null || ProfileRules.ValidTimezone(timezone) && (previous is null || reuseExternal && coordinator is not null || !(systemTimezoneMatches?.Invoke(timezone) ?? CurrentTimezoneMatches(timezone)));
        var arguments = verifiedEgress is not null ? RestrictedArguments(profile, environment, authorizeUrl)
            : ChromeArguments.Build(profile, environment, authorizeUrl, viaCdp).ToList();
        var warnings = lease?.Warnings.ToList() ?? [];
        var opened = new BrowserLaunch(executable, profile, warnings, previous, lease);
        var session = warming ?? new AuthorizationSession(opened); session.Launch = opened; sessions[profile] = session;
        previewSession = null;
        var startedIdentity = verifiedEgress is null || warming is not null ? null : await system.StartTrackedAsync(executable, arguments, cancellationToken);
        if (verifiedEgress is null && warming is null) await system.StartAsync(executable, arguments, cancellationToken);
        if (startedIdentity is not null) session.Roots.Add(startedIdentity);
        if (verifiedEgress is not null)
        {
            var deadline = this.clock.UtcNow + TimeSpan.FromSeconds(6);
            IReadOnlyList<BrowserIdentity>? roots = null;
            do
            {
                roots = await ObserveAsync(opened, cancellationToken);
                if (roots is { Count: > 0 }) break;
                await this.clock.DelayAsync(TimeSpan.FromMilliseconds(150), cancellationToken);
            } while (this.clock.UtcNow < deadline);
            if (roots is null || roots.Count == 0 || session.Roots.Count == 0) throw new AccountException(AccountError.Browser, "专用浏览器进程所有权未确认，授权页未打开。");
        }
        if (verifiedEgress is not null) warnings.Add($"已确认本次代理出口 IP：{verifiedEgress.Ip}；时区：{verifiedEgress.Timezone}；浏览器使用 en-US 与专用程序保护。授权页仅在浏览器内出口和环境复核后打开。");
        if (timezone.Length > 0 && !ProfileRules.ValidTimezone(timezone)) warnings.Add("浏览器时区写法不合法，此次页面跟随系统时区。");
        if (reuseExternal && coordinator is not null) warnings.Add("此档案已有外部浏览器，已沿用该窗口；未认领或修改其系统时区，页面使用 CDP 时区对齐。");
        if (viaCdp)
        {
            var result = await OpenWithTimezone(profile, authorizeUrl, timezone, cancellationToken, verifiedEgress, warming is not null);
            if (verifiedEgress is not null && (!result.PageOpened || !result.Confirmed))
                throw new AccountException(AccountError.ExitUnavailable, "专用浏览器的出口 IP、时区或英文环境未确认，授权网站未获准打开。");
            if (!result.PageOpened)
            {
                await system.StartAsync(executable, ChromeArguments.Build(profile, environment, authorizeUrl, false), cancellationToken);
                warnings.Add("浏览器页面已按普通方式打开，CDP 时区未生效。");
            }
            else if (!result.Confirmed) warnings.Add("登录页已打开，页面时区未得到确认。");
        }
        var observationDeadline = this.clock.UtcNow + TimeSpan.FromSeconds(5);
        if (lease is not null && verifiedEgress is null)
        {
            IReadOnlyList<BrowserIdentity>? roots;
            do
            {
                roots = await ObserveAsync(opened, cancellationToken);
                if (roots is { Count: > 0 }) break;
                await this.clock.DelayAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
            } while (this.clock.UtcNow < observationDeadline);
            if (previous is null || roots is null || roots.Count == 0) warnings.Add("浏览器已请求启动，但实际新窗口所有权未确认，未认领其他浏览器。");
        }
        if (lease is not null) warnings.AddRange(lease.Warnings.Where(warning => !warnings.Contains(warning, StringComparer.Ordinal)));
        return opened;
        }
        catch
        {
            if (verifiedEgress is not null) await CleanupAsync(sessions.TryGetValue(profile, out var session) ? session.Launch : null, CancellationToken.None);
            if (lease is not null) await lease.FailureAsync(CancellationToken.None);
            throw;
        }
    }
    public async Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(BrowserLaunch launch, CancellationToken cancellationToken)
    {
        var roots = sessions.TryGetValue(launch.ProfileDirectory, out var observedSession) && observedSession.Roots.Count > 0
            ? await system.ObserveKnownAsync(launch.Executable, launch.ProfileDirectory, observedSession.Roots, cancellationToken)
            : await system.ObserveAsync(launch.Executable, launch.ProfileDirectory, cancellationToken);
        if (roots is not null && launch.PreviousRoots is not null && sessions.TryGetValue(launch.ProfileDirectory, out var session))
            foreach (var identity in roots.Where(identity => identity.Pid > 0 && identity.CreationTime > 0 && identity.Executable.Equals(launch.Executable, StringComparison.OrdinalIgnoreCase)
                && identity.ProfileDirectory.Equals(launch.ProfileDirectory, StringComparison.OrdinalIgnoreCase)))
                if (!launch.PreviousRoots.Any(previous => previous.Pid == identity.Pid && previous.CreationTime == identity.CreationTime && previous.Executable.Equals(identity.Executable, StringComparison.OrdinalIgnoreCase))
                    && !session.Roots.Any(previous => previous.Pid == identity.Pid && previous.CreationTime == identity.CreationTime)) session.Roots.Add(identity);
        if (roots is not null && launch.PreviousRoots is not null && launch.LaunchLease is not null)
            foreach (var identity in roots)
            {
                if (launch.PreviousRoots.Any(previous => previous.Pid == identity.Pid && previous.CreationTime == identity.CreationTime && previous.Executable.Equals(identity.Executable, StringComparison.OrdinalIgnoreCase))) continue;
                var key = RootKey(identity);
                if (trackedRoots.Add(key)) launch.LaunchLease.TrackRoot(identity);
            }
        return roots;
    }
    private static string RootKey(BrowserIdentity identity) => identity.Pid + "|" + identity.CreationTime + "|" + identity.Executable;
    private static bool CurrentTimezoneMatches(string timezone)
    {
        TimeZoneInfo.ClearCachedData();
        return TimeZoneInfo.TryConvertIanaIdToWindowsId(timezone, out var windows) && TimeZoneInfo.Local.Id.Equals(windows, StringComparison.OrdinalIgnoreCase);
    }
    private string NewProfile(string profileName)
    {
        var profile = Path.Combine(repository.Root, "browser-envs", ProfileRules.Name(profileName), "auth-" + Guid.NewGuid().ToString("N"));
        PhysicalFileStorage.RejectLinks(profile); Directory.CreateDirectory(profile);
        profile = WindowsDesktopLaunchSystem.ResolveRealPath(profile); PhysicalFileStorage.RejectLinks(profile);
        return profile;
    }
    private static void WriteEnglishPreferences(string profile)
    {
        PhysicalFileStorage.RejectLinks(profile);
        Directory.CreateDirectory(Path.Combine(profile, "Default"));
        var files = new PhysicalFileStorage();
        foreach (var (relative, changes) in new[] {
            ("Local State", "{\"intl\":{\"app_locale\":\"en-US\"}}"),
            (Path.Combine("Default", "Preferences"), "{\"intl\":{\"accept_languages\":\"en-US,en\",\"selected_languages\":\"en-US,en\"},\"translate\":{\"enabled\":false},\"net\":{\"network_prediction_options\":2}}") })
        {
            var path = Path.Combine(profile, relative);
            var data = files.FileExists(path) ? JsonNode.Parse(files.Read(path)) as JsonObject ?? throw new IOException("浏览器偏好无效，已保留原文件。") : new JsonObject();
            foreach (var group in JsonNode.Parse(changes)!.AsObject())
            {
                var target = data[group.Key] as JsonObject;
                if (target is null) data[group.Key] = target = new JsonObject();
                foreach (var value in group.Value!.AsObject()) target[value.Key] = value.Value?.DeepClone();
            }
            files.WriteAtomic(path, Encoding.UTF8.GetBytes(data.ToJsonString()));
        }
    }
    private static List<string> RestrictedArguments(string profile, EnvironmentSettings environment, Uri url)
    {
        var arguments = ChromeArguments.Build(profile, environment, url, true).ToList();
        arguments.InsertRange(0, [ChromeArguments.RestrictedDns(environment.Proxy), "--disable-background-networking", "--disable-component-update", "--disable-domain-reliability"]);
        return arguments;
    }
    private async Task<(bool PageOpened, bool Confirmed)> OpenWithTimezone(string profile, Uri url, string timezone, CancellationToken token, EgressResult? verifiedEgress = null, bool reusePreview = false)
    {
        var deadline = this.clock.UtcNow + TimeSpan.FromSeconds(12);
        ICdpConnection? connection = null;
        var retained = false;
        var navigated = false;
        var stage = "连接专用浏览器";
        (bool, bool) NotConfirmed(string reason)
        {
            if (verifiedEgress is not null) throw new AccountException(AccountError.Browser, "授权前检查未通过：" + reason + "。未打开登录页。");
            return (false, false);
        }
        string userAgent = "";
        try
        {
            while (this.clock.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                var activePort = Path.Combine(profile, "DevToolsActivePort");
                PhysicalFileStorage.RejectLinks(activePort);
                if (File.Exists(activePort))
                {
                    var lines = await File.ReadAllLinesAsync(activePort, token);
                    if (lines.Length >= 2 && int.TryParse(lines[0], out var port) && port is > 0 and <= 65535 && Regex.IsMatch(lines[1], "\\A/devtools/browser/[A-Za-z0-9-]+\\z"))
                    {
                        try { connection = await cdp.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{lines[1]}"), token); break; }
                        catch (AccountException) { }
                    }
                }
                await this.clock.DelayAsync(TimeSpan.FromMilliseconds(200), token);
            }
            if (connection is null) return NotConfirmed("未找到本次浏览器调试端口");
            stage = "建立授权标签页";
            await connection.CallAsync("Target.setAutoAttach", new JsonObject { ["autoAttach"] = true, ["flatten"] = true, ["waitForDebuggerOnStart"] = true }, null, token);
            var target = "";
            if (reusePreview)
            {
                var targets = await connection.CallAsync("Target.getTargets", new(), null, token);
                target = (targets["targetInfos"] as JsonArray)?.OfType<JsonObject>().Where(item => AccountJson.Text(item, "type") == "page"
                    && AccountJson.Text(item, "url") == new Uri(Path.Combine(profile, "preparing.html")).AbsoluteUri).Select(item => AccountJson.Text(item, "targetId")).FirstOrDefault() ?? "";
                if (target.Length == 0) return NotConfirmed("准备窗口已关闭，请重新登录");
            }
            else
            {
                var created = await connection.CallAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null, token);
                target = AccountJson.Text(created, "targetId");
            }
            if (target.Length == 0) return NotConfirmed("浏览器没有返回标签页标识");
            var attached = await connection.CallAsync("Target.attachToTarget", new JsonObject { ["targetId"] = target, ["flatten"] = true }, null, token);
            var session = AccountJson.Text(attached, "sessionId");
            if (session.Length == 0) return NotConfirmed("浏览器没有返回会话标识");
            stage = "设置标签页地区环境";
            await connection.CallAsync("Emulation.setTimezoneOverride", new JsonObject { ["timezoneId"] = timezone }, session, token);
            if (verifiedEgress is not null)
            {
                var browserVersion = await connection.CallAsync("Browser.getVersion", new(), null, token);
                userAgent = AccountJson.Text(browserVersion, "userAgent");
                if (userAgent.Length == 0) return NotConfirmed("浏览器版本信息缺失");
                await connection.CallAsync("Emulation.setLocaleOverride", new JsonObject { ["locale"] = "en-US" }, session, token);
                // Language comes from the isolated profile. Preserve Chrome's native
                // User-Agent and Client Hints instead of overriding half of that pair.
            }
            await connection.CallAsync("Runtime.runIfWaitingForDebugger", new(), session, token);
            if (verifiedEgress is not null)
            {
                stage = "读回标签页地区环境";
                var environmentRead = await connection.CallAsync("Runtime.evaluate", new JsonObject { ["expression"] = BrowserEnvironmentVerification.ReadExpression(timezone), ["returnByValue"] = true }, session, token);
                var actualEnvironment = JsonNode.Parse(AccountJson.Text(environmentRead["result"] as JsonObject, "value")) as JsonObject;
                if (!BrowserEnvironmentVerification.EnglishEnvironmentMatches(actualEnvironment)) return NotConfirmed("浏览器时区或语言读回不一致");
                // Consume auto-attach events before navigating. Site isolation can create
                // a waiting renderer during the probe, before the OAuth page is opened.
                connections.Add(connection);
                if (sessions.TryGetValue(profile, out var opening)) opening.Connection = connection;
                retained = true;
                _ = KeepTimezone(connection, timezone, profile, true, userAgent, target, stopped.Token);
                stage = "核对浏览器实际出口";
                var actualEgress = await ReadBrowserEgressAsync(connection, session, verifiedEgress, timezone, token);
                if (!actualEgress.Ok) return NotConfirmed(actualEgress.Message);
            }
            stage = "打开登录页";
            var navigation = await connection.CallAsync("Page.navigate", new JsonObject { ["url"] = url.AbsoluteUri }, session, token);
            if (verifiedEgress is not null && (AccountJson.Text(navigation, "errorText").Length > 0 || AccountJson.Text(navigation, "frameId").Length == 0)) return (false, false);
            navigated = true;
            var observed = await connection.CallAsync("Runtime.evaluate", new JsonObject { ["expression"] = BrowserEnvironmentVerification.ReadExpression(timezone), ["returnByValue"] = true }, session, token);
            var actual = JsonNode.Parse(AccountJson.Text(observed["result"] as JsonObject, "value")) as JsonObject;
            if (!retained)
            {
                connections.Add(connection);
                if (sessions.TryGetValue(profile, out var authorization)) authorization.Connection = connection;
                _ = KeepTimezone(connection, timezone, profile, false, userAgent, target, stopped.Token);
            }
            connection = null;
            return (true, verifiedEgress is null ? BrowserEnvironmentVerification.TimezoneMatches(actual) : BrowserEnvironmentVerification.EnglishEnvironmentMatches(actual));
        }
        catch (AccountException error) when (verifiedEgress is not null)
        { throw new AccountException(AccountError.Browser, "授权浏览器检查失败：" + stage + "；" + error.Message); }
        catch (Exception error) when (error is AccountException or IOException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException)
        {
            if (verifiedEgress is not null) throw new AccountException(AccountError.Browser, "授权浏览器检查失败：" + stage + "（" + error.GetType().Name + "）。");
            return (navigated, false);
        }
        finally { if (connection is not null && !retained) await connection.DisposeAsync(); }
    }
    private async Task<EgressResult> ReadBrowserEgressAsync(ICdpConnection connection, string session, EgressResult expected, string timezone, CancellationToken token)
    {
        var failures = new List<string>();
        foreach (var source in EgressSources.Locations)
        {
            token.ThrowIfCancellationRequested();
            var endpoint = EgressSources.Endpoint(source);
            EgressResult? measured = null;
            if (source == EgressLocationSource.NetCoffee)
            {
                var trace = await ReadBrowserEndpointAsync(connection, session, endpoint, json: false, token);
                measured = EgressSources.Trace(trace, EgressSources.Name(source));
                if (!measured.Ok) { failures.Add(measured.Message); continue; }
                if (!EgressSources.SameIp(measured.Ip, expected.Ip)) return measured with { Ok = false, Message = "浏览器 Net.Coffee 出口与已验证的代理出口不一致" };
                endpoint = new Uri(NetCoffeeService.LookupEndpoint, Uri.EscapeDataString(measured.Ip));
            }
            var text = await ReadBrowserEndpointAsync(connection, session, endpoint, json: true, token);
            JsonObject? data;
            try { data = JsonNode.Parse(text) as JsonObject; }
            catch (System.Text.Json.JsonException) { data = null; }
            if (data is null) { failures.Add(EgressSources.Name(source) + " 未返回有效 JSON"); continue; }
            var actual = EgressSources.Parse(source, data, measured);
            if (measured is not null && EgressSources.LookupConflicts(data, measured)) return actual with { Ok = false };
            if (EgressSources.ValidIp(actual.Ip) && !EgressSources.SameIp(actual.Ip, expected.Ip))
                return actual with { Ok = false, Message = "浏览器出口与已验证的代理出口不一致" };
            if (EgressSources.HasCountry(expected) && EgressSources.HasCountry(actual) && expected.CountryCode != actual.CountryCode)
                return actual with { Ok = false, Message = "浏览器出口国家与已验证的代理出口不一致" };
            if (!actual.Ok) { failures.Add(actual.Message); continue; }
            if (!EgressSources.SameZone(actual.Timezone, timezone)) return actual with { Ok = false, Message = "浏览器出口时区与已验证的代理出口不一致" };
            return actual;
        }
        return new(false, Message: "浏览器主查询与备用查询均未完成：" + string.Join("；", failures));
    }
    private async Task<string> ReadBrowserEndpointAsync(ICdpConnection connection, string session, Uri endpoint, bool json, CancellationToken token)
    {
        // Every fetch/navigation runs inside the same already-bound browser tab.
        // No host HTTP request or direct route is substituted for a renderer failure.
        var url = System.Text.Json.JsonSerializer.Serialize(endpoint.AbsoluteUri);
        var expression = "(async()=>{const c=new AbortController();const t=setTimeout(()=>c.abort(),8000);try{const r=await fetch(" + url
            + ",{cache:'no-store',credentials:'omit',signal:c.signal});if(!r.ok)return '';const s=await r.text();return s.length<=65536?s:'';}catch{return '';}finally{clearTimeout(t);}})()";
        var response = await connection.CallAsync("Runtime.evaluate", new() { ["expression"] = expression, ["awaitPromise"] = true, ["returnByValue"] = true }, session, token);
        var text = AccountJson.Text(response["result"] as JsonObject, "value");
        if (text.Length > 0) return text.Length <= 65536 ? text : "";
        // Same public endpoint as a top-level document covers opaque-origin CORS.
        var navigation = await connection.CallAsync("Page.navigate", new() { ["url"] = endpoint.AbsoluteUri }, session, token);
        if (AccountJson.Text(navigation, "errorText").Length > 0 || AccountJson.Text(navigation, "frameId").Length == 0) return "";
        var read = "location.href === " + url + " && document.readyState === 'complete' && document.contentType === "
            + (json ? "'application/json'" : "'text/plain'") + " ? document.body.innerText : ''";
        var deadline = this.clock.UtcNow + TimeSpan.FromSeconds(8);
        do
        {
            var document = await connection.CallAsync("Runtime.evaluate", new() { ["expression"] = read, ["returnByValue"] = true }, session, token);
            text = AccountJson.Text(document["result"] as JsonObject, "value");
            if (text.Length > 0) return text.Length <= 65536 ? text : "";
            await this.clock.DelayAsync(TimeSpan.FromMilliseconds(200), token);
        } while (this.clock.UtcNow < deadline);
        return "";
    }
    private async Task KeepTimezone(ICdpConnection connection, string timezone, string profile, bool strict, string userAgent, string primaryTarget, CancellationToken token)
    {
        try
        {
            await foreach (var entry in connection.Events(token))
            {
                if (AccountJson.Text(entry, "method") != "Target.attachedToTarget" || entry["params"] is not JsonObject parameters) continue;
                var session = AccountJson.Text(parameters, "sessionId");
                if (session.Length == 0) continue;
                // Auto-attach may also pause workers/iframes. They must never remain
                // suspended just because only top-level pages need environment setup.
                if (AccountJson.Text(parameters["targetInfo"] as JsonObject, "type") != "page")
                {
                    await connection.CallAsync("Runtime.runIfWaitingForDebugger", new(), session, token);
                    continue;
                }
                if (AccountJson.Text(parameters["targetInfo"] as JsonObject, "targetId") == primaryTarget)
                {
                    await connection.CallAsync("Runtime.runIfWaitingForDebugger", new(), session, token);
                    continue;
                }
                await connection.CallAsync("Emulation.setTimezoneOverride", new JsonObject { ["timezoneId"] = timezone }, session, token);
                if (strict)
                {
                    await connection.CallAsync("Emulation.setLocaleOverride", new JsonObject { ["locale"] = "en-US" }, session, token);
                }
                await connection.CallAsync("Runtime.runIfWaitingForDebugger", new(), session, token);
                if (strict)
                {
                    var observed = await connection.CallAsync("Runtime.evaluate", new() { ["expression"] = BrowserEnvironmentVerification.ReadExpression(timezone), ["returnByValue"] = true }, session, token);
                    var actual = JsonNode.Parse(AccountJson.Text(observed["result"] as JsonObject, "value")) as JsonObject;
                    if (!BrowserEnvironmentVerification.EnglishEnvironmentMatches(actual)) throw new AccountException(AccountError.Browser, "授权新页面环境未确认。");
                }
            }
            if (strict && !token.IsCancellationRequested && sessions.TryGetValue(profile, out var ended)) await CleanupAsync(ended.Launch, CancellationToken.None);
        }
        catch (Exception error) when (error is AccountException or OperationCanceledException or IOException or System.Text.Json.JsonException)
        { if (strict && !token.IsCancellationRequested && sessions.TryGetValue(profile, out var failed)) await CleanupAsync(failed.Launch, CancellationToken.None); }
    }
    public async Task<BrowserSessionCleanupResult> CleanupAsync(BrowserLaunch? launch, CancellationToken token)
    {
        await cleanupGate.WaitAsync(token);
        try
        {
        var warnings = new List<string>();
        var selected = launch is null ? sessions.Values.ToArray() : sessions.TryGetValue(launch.ProfileDirectory, out var found) && found.Launch.Executable.Equals(launch.Executable, StringComparison.OrdinalIgnoreCase) ? [found] : Array.Empty<AuthorizationSession>();
        foreach (var session in selected)
        {
            try
            {
                // Reauthorization in an already-open account browser does not own
                // its other tabs. Cancellation leaves that browser and its data alone.
                if (session.Persistent && session.Launch.PreviousRoots is { Count: > 0 })
                {
                    try { FinishInstalledPreparation(session); }
                    catch (Exception error) { warnings.Add("授权会话已结束，档案浏览器准备页未更新（" + error.GetType().Name + "）；网页登录资料保留。"); }
                    ReleaseSession(session);
                    continue;
                }
                var latest = await system.ObserveAsync(session.Launch.Executable, session.Launch.ProfileDirectory, token);
                if (latest is null) return new(false, "授权资料独立保留；专用浏览器退出状态未知，临时目录未删除。");
                foreach (var identity in latest)
                    if (!session.Roots.Any(owned => owned.Pid == identity.Pid && owned.CreationTime == identity.CreationTime && owned.Executable.Equals(identity.Executable, StringComparison.OrdinalIgnoreCase)))
                        return new(false, "发现未确认所有权的授权窗口，未关闭或删除临时目录。");
                if ((latest.Count > 0 || session.DisposablePreparation) && !await system.CloseOwnedAsync(session.DisposablePreparation ? session.Roots : latest, token)) return new(false, "专用授权窗口或后台进程退出尚未确认，临时目录保留。");
                var remaining = await system.ObserveAsync(session.Launch.Executable, session.Launch.ProfileDirectory, token);
                if (remaining is null || remaining.Count > 0) return new(false, "授权窗口仍在运行或状态未知，临时目录保留。");
                // A confirmed ended browser releases authorization even if its cache
                // is locked. Directory cleanup is separate from process ownership.
                try
                {
                    if (session.Connection is not null)
                    {
                        try { await session.Connection.DisposeAsync(); }
                        finally { connections.Remove(session.Connection); }
                    }
                    var profile = ResolveCleanupProfileDirectory(repository.Root, session.Launch.ProfileDirectory);
                    if (session.DisposablePreparation)
                    {
                        var statePath = Path.Combine(profile, BrowserTimezoneController.StateFile);
                        var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
                        while (File.Exists(statePath))
                        {
                            var state = System.Text.Json.JsonSerializer.Deserialize<BrowserTimezoneState>(new PhysicalFileStorage().Read(statePath));
                            if (state is null || !BrowserTimezoneController.IsAlive(state.HelperPid, state.HelperCreated)) break;
                            if (DateTimeOffset.UtcNow >= deadline) throw new IOException("Preparation timezone helper is still exiting.");
                            await Task.Delay(100, token);
                        }
                    }
                    if (!session.Persistent && Directory.Exists(profile))
                    {
                        var pending = new Stack<string>(); pending.Push(profile);
                        while (pending.Count > 0) foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop())) { PhysicalFileStorage.RejectLinks(entry); if (Directory.Exists(entry)) pending.Push(entry); }
                        Directory.Delete(profile, true);
                    }
                }
                catch (Exception error)
                {
                    if (session.DisposablePreparation) return new(false, "第一步临时资料尚未删除（" + error.GetType().Name + "），不能进入第二步；关闭占用后重试。");
                    warnings.Add("授权会话已结束，临时浏览器资料暂未清理（" + error.GetType().Name + "）；不会阻止下次登录。");
                }
                ReleaseSession(session);
            }
            catch (Exception) { return new(false, "授权资料保留；专用浏览器与临时目录清理未确认。"); }
        }
        return new(true, warnings.Count == 0 ? "本次授权会话已结束；已绑定账号的网页资料保留，未绑定的临时资料已清理。" : "本次授权会话已释放；部分浏览器资料保留待清理。", warnings);
        }
        finally { cleanupGate.Release(); }
    }
    private void ReleaseSession(AuthorizationSession session)
    {
        session.AuthorizationUrl = null;
        sessions.TryRemove(session.Launch.ProfileDirectory, out _);
        if (previewSession == session) { previewSession = null; preparedProxy = null; preparedExecutable = null; preparedAccount = null; }
    }
    public static string ResolveCleanupProfileDirectory(string accountRoot, string profileDirectory, Func<string, string>? resolveRealPath = null)
    {
        PhysicalFileStorage.RejectLinks(profileDirectory);
        var resolve = resolveRealPath ?? WindowsDesktopLaunchSystem.ResolveRealPath;
        var browserRoot = Path.GetFullPath(resolve(Path.GetFullPath(Path.Combine(accountRoot, "browser-envs"))));
        var profile = Path.GetFullPath(resolve(Path.GetFullPath(profileDirectory)));
        if (!profile.StartsWith(browserRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(Path.GetFileName(profile), "\\Aauth-[a-f0-9]{32}\\z"))
            throw new IOException("授权临时目录范围未确认，未删除。");
        PhysicalFileStorage.RejectLinks(profile);
        return profile;
    }
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var cleanup = await CleanupAsync(null, CancellationToken.None);
        if (egressProbe is not null && !cleanup.Success) throw new AccountException(AccountError.Browser, cleanup.Message);
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopped.Cancel(); foreach (var connection in connections) await connection.DisposeAsync(); stopped.Dispose();
    }
}
