using System.Text;
using System.Text.Json;
using FAswitch.Core;

namespace FAswitch.Accounts;

public sealed record InstalledBrowser(string Name, string Executable);

public static class InstalledBrowsers
{
    public static readonly IReadOnlyDictionary<string, string> Downloads = new Dictionary<string, string>
    {
        ["Chrome"] = "https://www.google.com/chrome/",
        ["Edge"] = "https://www.microsoft.com/edge/download",
        ["Brave"] = "https://brave.com/download/"
    };
    public static bool SupportedExecutable(string path) => new[] { "chrome.exe", "msedge.exe", "brave.exe" }
        .Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<InstalledBrowser> Find()
    {
        var found = new List<InstalledBrowser>();
        foreach (var (name, relative) in new[] {
            ("Chrome", @"Google\Chrome\Application\chrome.exe"),
            ("Edge", @"Microsoft\Edge\Application\msedge.exe"),
            ("Brave", @"BraveSoftware\Brave-Browser\Application\brave.exe") })
        {
            var path = new[] { "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" }
                .Select(Environment.GetEnvironmentVariable).Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => Path.Combine(root!, relative)).FirstOrDefault(File.Exists);
            if (path is not null) found.Add(new(name, Path.GetFullPath(path)));
        }
        return found;
    }
}

// Native mode keeps one local browser profile per account without cookie copying.
public sealed partial class ChromeLoginBrowser
{
    private sealed record SavedBrowserRoute(string Executable, string Proxy, IReadOnlyList<BrowserIdentity> Roots, bool? AlignTimezone = null, string? Timezone = null);
    private string? preparedAccount;
    private static string RoutePath(string profile) => Path.Combine(profile, "chunson-browser-route.json");
    public async Task<bool> CloseForNetworkChangeAsync(string profileName, CancellationToken token)
    {
        await cleanupGate.WaitAsync(token);
        try
        {
            var profile = new AccountBrowserProfiles(repository.Root).Read(profileName);
            if (profile is null) return false;
            var files = new PhysicalFileStorage();
            if (!files.FileExists(RoutePath(profile))) throw new AccountException(AccountError.Browser, "档案浏览器归属记录缺失，未切换网络。");
            var route = JsonSerializer.Deserialize<SavedBrowserRoute>(files.Read(RoutePath(profile)))
                ?? throw new AccountException(AccountError.Browser, "档案浏览器归属记录无效。");
            var current = await system.ObserveAsync(route.Executable, profile, token);
            if (current is null || current.Any(root => !route.Roots.Contains(root)))
                throw new AccountException(AccountError.Browser, "无法确认此账号浏览器归属，未关闭窗口或释放端口。");
            if (current.Count > 0 && !await system.CloseOwnedAsync(current, token))
                throw new AccountException(AccountError.Browser, "此账号浏览器关闭未确认，网络配置保留。");
            var remaining = await system.ObserveAsync(route.Executable, profile, token);
            if (remaining is null || remaining.Count > 0) throw new AccountException(AccountError.Browser, "此账号浏览器仍在运行，未切换网络。");
            if (sessions.TryGetValue(profile, out var session)) ReleaseSession(session);
            // Wait for the old controller's final state write before reusing this profile.
            var statePath = Path.Combine(profile, BrowserTimezoneController.StateFile);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
            while (files.FileExists(statePath))
            {
                var state = JsonSerializer.Deserialize<BrowserTimezoneState>(files.Read(statePath));
                if (state is null || !BrowserTimezoneController.IsAlive(state.HelperPid, state.HelperCreated)) break;
                if (DateTimeOffset.UtcNow >= deadline) throw new AccountException(AccountError.Browser, "此浏览器时区服务仍在退出，请稍后重试；资料保留。");
                await Task.Delay(100, token);
            }
            return current.Count > 0;
        }
        finally { cleanupGate.Release(); }
    }
    private static void FinishInstalledPreparation(AuthorizationSession session)
    {
        if (session.Persistent)
            new PhysicalFileStorage().WriteAtomic(Path.Combine(session.Launch.ProfileDirectory, "preparing.html"),
                Encoding.UTF8.GetBytes("<!doctype html><meta charset='utf-8'><title>Sign-in ended</title><p>This authorization has ended. Your saved browser profile is kept.</p>"));
    }
    public Task KeepAccountProfileAsync(string profileName, BrowserLaunch launch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!installedBrowser) return Task.CompletedTask;
        if (!repository.Exists(profileName) || !sessions.TryGetValue(launch.ProfileDirectory, out var session)
            || !session.Launch.Executable.Equals(launch.Executable, StringComparison.OrdinalIgnoreCase))
            throw new AccountException(AccountError.Browser, "未找到此账号的浏览器档案。");
        new AccountBrowserProfiles(repository.Root).Bind(profileName, launch.ProfileDirectory);
        var route = JsonSerializer.Deserialize<SavedBrowserRoute>(new PhysicalFileStorage().Read(RoutePath(launch.ProfileDirectory)));
        if (route is { AlignTimezone: true, Timezone: not null }) repository.Profiles.UpdateEnvironment(profileName, timezone: route.Timezone);
        session.Persistent = true;
        FinishInstalledPreparation(session);
        // Detach only after binding succeeds. Release/dispose must not close the
        // authenticated window or erase cookies; subsequent opens rediscover this profile.
        sessions.TryRemove(launch.ProfileDirectory, out _);
        if (previewSession == session) { previewSession = null; preparedProxy = null; preparedExecutable = null; }
        return Task.CompletedTask;
    }
    private async Task PrepareInstalledAsync(string proxy, CancellationToken token, string profileName = LoginService.NewAccountRequestProfile, bool disposablePreparation = false)
    {
        if (Volatile.Read(ref disposed) != 0) throw new AccountException(AccountError.Browser, "登录浏览器会话已关闭。");
        proxy = ChromeArguments.RequireLoopbackProxy(HttpAccountTransport.RequireHttpProxy(proxy));
        if (previewSession is not null)
        {
            if (preparedProxy != proxy || preparedAccount != profileName) throw new AccountException(AccountError.ExitUnavailable, "正在准备另一个账号或出口，请先结束本次登录。");
            return;
        }
        var profiles = new AccountBrowserProfiles(repository.Root);
        var bound = !disposablePreparation && repository.Exists(profileName) ? profiles.Read(profileName) : null;
        var profile = bound ?? profiles.Create();
        var files = new PhysicalFileStorage();
        SavedBrowserRoute? saved = null;
        if (files.FileExists(RoutePath(profile)))
            saved = JsonSerializer.Deserialize<SavedBrowserRoute>(files.Read(RoutePath(profile))) ?? throw new IOException("浏览器出口记录无效。");
        var alignTimezone = alignInstalledTimezone ?? (repository.Exists(profileName)
            ? !repository.Profiles.ReadEnvironment(profileName).FollowSystemProxy && installedTimezone is not null
            : saved?.AlignTimezone ?? (installedTimezone is not null));
        if (alignTimezone && installedTimezone is null) throw new AccountException(AccountError.Browser, "浏览器时区服务尚未连接。");
        var executable = saved is not null && File.Exists(saved.Executable) ? saved.Executable : system.FindChrome() ?? throw new AccountException(AccountError.Browser,
            "未找到本机 Chrome、Edge 或 Brave。请安装后重试：https://www.google.com/chrome/ 或 https://www.microsoft.com/edge/download");
        var existing = bound is null ? Array.Empty<BrowserIdentity>() : await system.ObserveAsync(saved?.Executable ?? executable, profile, token)
            ?? throw new AccountException(AccountError.Browser, "无法确认档案浏览器状态，请关闭该账号的浏览器后重试。");
        if (existing.Count > 0 && (saved is null || saved.Proxy != proxy || saved.AlignTimezone != alignTimezone || !saved.Executable.Equals(executable, StringComparison.OrdinalIgnoreCase)
            || existing.Any(root => !saved.Roots.Contains(root))))
            throw new AccountException(AccountError.Browser, "该账号的浏览器仍使用另一个出口或窗口状态已变化。请先关闭该账号的档案浏览器，再重新打开；网页登录状态会保留。");
        if (existing.Count == 0) WriteEnglishPreferences(profile);
        if (!disposablePreparation && bound is null && repository.Exists(profileName)) { profiles.Bind(profileName, profile); bound = profile; }
        var page = Path.Combine(profile, "preparing.html");
        // A local page reloads until admission and the browser timezone readback finish.
        new PhysicalFileStorage().WriteAtomic(page, Encoding.UTF8.GetBytes("<!doctype html><meta charset='utf-8'><meta http-equiv='refresh' content='1'><title>Preparing sign-in</title><style>body{background:#f3f1e8;color:#202820;font:18px system-ui;display:grid;place-content:center;min-height:90vh}h1{font-weight:600;color:#466332}p{color:#56634e;max-width:560px;line-height:1.7}</style><h1>Preparing sign-in…</h1><p>Setting up your selected connection and timezone.</p><p>The sign-in page will open automatically.</p>"));
        var arguments = ChromeArguments.Build(profile, new(proxy, Locale: "en-US"), new Uri(page), false).ToList();
        if (alignTimezone)
        {
            if (existing.Count == 0 && files.FileExists(Path.Combine(profile, "DevToolsActivePort"))) files.DeleteFile(Path.Combine(profile, "DevToolsActivePort"));
            arguments.Insert(0, "--remote-debugging-port=0");
        }
        // HTTP proxy resolves destination names. Do not install the test resolver
        // override: Chrome explicitly flags it as an unsafe startup argument.
        arguments.Insert(0, "--disable-background-networking");
        var launch = new BrowserLaunch(executable, profile, [], existing);
        previewSession = new(launch) { Persistent = bound is not null, DisposablePreparation = disposablePreparation }; sessions[profile] = previewSession;
        preparedProxy = proxy; preparedExecutable = executable; preparedAccount = profileName;
        IReadOnlyList<BrowserIdentity> roots;
        if (existing.Count > 0)
        {
            await system.StartAsync(executable, arguments, token);
            roots = existing;
        }
        else
        {
            var identity = await system.StartTrackedAsync(executable, arguments, token)
                ?? throw new AccountException(AccountError.Browser, "无法确认本次浏览器窗口，未打开授权网站。");
            previewSession.Roots.Add(identity); roots = [identity];
        }
        files.WriteAtomic(RoutePath(profile), JsonSerializer.SerializeToUtf8Bytes(new SavedBrowserRoute(executable, proxy, roots, alignTimezone, existing.Count > 0 ? saved?.Timezone : null)));
    }

    private async Task<BrowserLaunch> OpenInstalledAsync(string name, Uri url, string proxy, EgressResult? admitted, CancellationToken token, bool disposablePreparation = false)
    {
        if (!url.IsAbsoluteUri || url.Scheme != "https" || url.UserInfo.Length != 0
            || !(url.Host == AccountEndpoints.Authorize.Host || url.Host == "chatgpt.com"))
            throw new AccountException(AccountError.Browser, "登录网站地址无效。");
        await PrepareInstalledAsync(proxy, token, name, disposablePreparation);
        var exit = admitted ?? (egressProbe is null ? null : await egressProbe(proxy, token));
        if (exit is not { Ok: true } || !System.Net.IPAddress.TryParse(exit.Ip, out _))
            throw new AccountException(AccountError.ExitUnavailable, "当前出口未确认，尚未打开授权网站。");
        var session = previewSession!;
        var roots = await ObserveAsync(session.Launch, token);
        if (roots is not { Count: > 0 }) throw new AccountException(AccountError.Browser, "准备窗口已关闭，请重新登录。");
        var files = new PhysicalFileStorage();
        var route = JsonSerializer.Deserialize<SavedBrowserRoute>(files.Read(RoutePath(session.Launch.ProfileDirectory)))!;
        if (route.AlignTimezone == true)
        {
            if (roots.Count != 1 || !ProfileRules.ValidTimezone(exit.Timezone)) throw new AccountException(AccountError.ExitUnavailable, "此浏览器的出口时区或独立进程未确认。");
            if (route.Timezone is not null && route.Timezone != exit.Timezone) throw new AccountException(AccountError.Browser, "此档案出口时区已变化，请关闭此账号浏览器后重开；网页登录资料保留。");
            await installedTimezone!.EnsureAsync(repository.Root, roots[0], exit.Timezone, token);
            route = route with { Timezone = exit.Timezone };
            files.WriteAtomic(RoutePath(session.Launch.ProfileDirectory), JsonSerializer.SerializeToUtf8Bytes(route));
        }
        else if (installedTimezone is not null && !(systemTimezoneMatches?.Invoke(exit.Timezone) ?? CurrentTimezoneMatches(exit.Timezone)))
            throw new AccountException(AccountError.ExitUnavailable, "本机默认出口与 Windows 当前时区不一致，请先在环境设置中按本机出口匹配；未打开登录页。");
        token.ThrowIfCancellationRequested();
        var target = JsonSerializer.Serialize(url.AbsoluteUri);
        session.AuthorizationUrl = url.Host == AccountEndpoints.Authorize.Host && url.AbsolutePath == AccountEndpoints.Authorize.AbsolutePath ? url : null;
        new PhysicalFileStorage().WriteAtomic(Path.Combine(session.Launch.ProfileDirectory, "preparing.html"),
            Encoding.UTF8.GetBytes("<!doctype html><meta charset='utf-8'><title>Opening sign-in</title><script>location.replace(" + target + ");</script>Opening sign-in…"));
        if (session.AuthorizationUrl is not null)
        {
            // The prepared tab keeps this exact OAuth session. Open ChatGPT in
            // the same native profile so the user can sign in before refreshing it.
            var loginArguments = ChromeArguments.Build(session.Launch.ProfileDirectory, new(proxy, Locale: "en-US"), new("https://chatgpt.com/"), false).ToList();
            loginArguments.Insert(0, "--disable-background-networking");
            await system.StartAsync(session.Launch.Executable, loginArguments, token);
        }
        session.Launch = session.Launch with { Warnings = [$"已使用本机 {Path.GetFileNameWithoutExtension(session.Launch.Executable)} 的英文独立配置；代理出口 {exit.Ip}。" + (route.AlignTimezone == true ? $"浏览器时区已读回确认：{exit.Timezone}；Windows 时区保持不变。" : "浏览器沿用 Windows 当前时区。") ] };
        previewSession = null; preparedProxy = null; preparedExecutable = null;
        if (!disposablePreparation && url.Host == "chatgpt.com" && repository.Exists(name))
            await KeepAccountProfileAsync(name, session.Launch, token);
        return session.Launch;
    }

    public Task<BrowserLaunch> OpenAccountPreparationAsync(string name, string proxy, EgressResult? egress, CancellationToken token)
        => installedBrowser ? OpenInstalledAsync(name, new Uri("https://chatgpt.com/auth/login"), proxy, egress, token, disposablePreparation: true)
            : throw new AccountException(AccountError.Browser, "分步登录需要本机浏览器的独立资料目录。");

    public async Task<bool> IsPreparationClosedAsync(BrowserLaunch launch, CancellationToken token)
    {
        if (!sessions.TryGetValue(launch.ProfileDirectory, out var session) || session.Launch != launch || !session.DisposablePreparation)
            throw new AccountException(AccountError.Browser, "账号准备会话已结束，请重新开始。");
        var visible = await system.HasOpenWindowsAsync(launch.Executable, launch.ProfileDirectory, session.Roots, token);
        // Never interpret unknown ownership/window visibility as a manual close.
        if (visible is true) session.PreparationWindowSeen = true;
        return visible is false && (session.PreparationWindowSeen || (await ObserveAsync(launch, token)) is { Count: 0 });
    }

    public async Task ResumeAsync(BrowserLaunch launch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!installedBrowser || Volatile.Read(ref disposed) != 0
            || !sessions.TryGetValue(launch.ProfileDirectory, out var session) || session.Launch != launch
            || session.AuthorizationUrl is not { } url)
            throw new AccountException(AccountError.Browser, "本次授权已结束，请重新点击登录账号。");
        var saved = JsonSerializer.Deserialize<SavedBrowserRoute>(new PhysicalFileStorage().Read(RoutePath(launch.ProfileDirectory)));
        var roots = await ObserveAsync(launch, token);
        if (saved is null || roots is not { Count: > 0 } || roots.Any(root => !saved.Roots.Contains(root))
            || !saved.Executable.Equals(launch.Executable, StringComparison.OrdinalIgnoreCase))
            throw new AccountException(AccountError.Browser, "本次授权浏览器已关闭或发生变化，请结束本次登录后重试。");
        var arguments = ChromeArguments.Build(launch.ProfileDirectory, new(saved.Proxy, Locale: "en-US"), url, false).ToList();
        if (saved.AlignTimezone == true)
        {
            if (roots.Count != 1 || saved.Timezone is null) throw new AccountException(AccountError.Browser, "浏览器时区记录未确认。");
            await installedTimezone!.EnsureAsync(repository.Root, roots[0], saved.Timezone, token);
        }
        arguments.Insert(0, "--disable-background-networking");
        token.ThrowIfCancellationRequested();
        await system.StartAsync(launch.Executable, arguments, token);
    }
}
