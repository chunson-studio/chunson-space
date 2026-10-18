using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

/// <summary>A new isolated Chrome session using the explicit, resolved login route.</summary>
public sealed class OnboardingProxyBrowser : IProxyBoundLoginBrowser, IVerifiedProxyLoginBrowser, IPersistentLoginBrowser, IResumableLoginBrowser, IAccountPreparationBrowser, IBrowserSessionCleanup, IAsyncDisposable
{
    private readonly string workspaceRoot;
    private readonly IChromeSystem system;
    private BrowserLaunch? launched;
    private readonly ChromeLoginBrowser? strictBrowser;
    private readonly bool? independentNetwork;
    public string Proxy { get; }

    public OnboardingProxyBrowser(string workspaceRoot, string proxy, IChromeSystem? system = null,
        Func<string, CancellationToken, Task<EgressResult>>? egressProbe = null, Func<string, string, CancellationToken, Task<bool>>? protectionVerifier = null, ICdpConnectionFactory? cdp = null,
        IChromeLaunchCoordinator? coordinator = null, Func<string, CancellationToken, Task<string>>? prepareBrowser = null, bool installedBrowser = false,
        IBrowserTimezoneController? installedTimezone = null, bool? alignInstalledTimezone = null)
    {
        Proxy = ChromeArguments.Proxy(proxy);
        if (!Uri.TryCreate(Proxy, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new AccountException(AccountError.ExitUnavailable, "首次登录需要明确的无认证 HTTP 代理，软件不会直连。");
        this.workspaceRoot = Path.GetFullPath(workspaceRoot);
        independentNetwork = alignInstalledTimezone;
        this.system = system ?? new WindowsChromeSystem();
        if (egressProbe is not null || installedBrowser)
            strictBrowser = new(new AccountRepository(this.workspaceRoot), this.system, cdp ?? new CdpConnectionFactory(),
                coordinator: coordinator, egressProbe: egressProbe, protectionVerifier: protectionVerifier, environmentReader: _ => new(Proxy), prepareBrowser: prepareBrowser, previewDuringPreparation: true, installedBrowser: installedBrowser,
                installedTimezone: installedTimezone, alignInstalledTimezone: alignInstalledTimezone);
    }

    public Task PrepareAsync(string proxy, CancellationToken token)
    {
        if (HttpAccountTransport.RequireHttpProxy(proxy) != Proxy) throw new AccountException(AccountError.ExitUnavailable, "授权出口与准备的浏览器不一致。");
        return strictBrowser?.PrepareAsync(proxy, token) ?? Task.CompletedTask;
    }
    public Task PrepareAsync(string profileName, string proxy, CancellationToken token) => PrepareAsync(proxy, token);
    public async Task KeepAccountProfileAsync(string profileName, BrowserLaunch launch, CancellationToken token)
    {
        if (strictBrowser is not null) await strictBrowser.KeepAccountProfileAsync(profileName, launch, token);
        if (independentNetwork is { } independent)
        {
            var profiles = new FileProfileStore(workspaceRoot);
            var environment = profiles.ReadEnvironment(profileName);
            TimeZoneInfo.ClearCachedData();
            var local = TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var zone) ? zone : TimeZoneInfo.Local.Id;
            profiles.SaveEnvironment(profileName, environment with { Proxy = independent ? Proxy : "", FollowSystemProxy = !independent, Timezone = independent ? environment.Timezone : local });
        }
    }
    public async Task<BrowserLaunch> OpenAccountPreparationAsync(string name, string proxy, EgressResult? egress, CancellationToken token)
    {
        if (strictBrowser is null || launched is not null || HttpAccountTransport.RequireHttpProxy(proxy) != Proxy)
            throw new AccountException(AccountError.Browser, "本次准备浏览器或出口无效，请重新开始。");
        return launched = await strictBrowser.OpenAccountPreparationAsync(name, proxy, egress, token);
    }
    public Task<bool> IsPreparationClosedAsync(BrowserLaunch launch, CancellationToken token)
        => strictBrowser is not null && launch == launched ? strictBrowser.IsPreparationClosedAsync(launch, token)
            : throw new AccountException(AccountError.Browser, "没有可继续的账号准备浏览器。");
    public Task ResumeAsync(BrowserLaunch launch, CancellationToken token)
        => strictBrowser is not null && launch == launched ? strictBrowser.ResumeAsync(launch, token)
            : throw new AccountException(AccountError.Browser, "没有可继续的本次授权浏览器。");
    public Task<BrowserLaunch> OpenVerifiedAsync(string profileName, Uri authorizeUrl, string proxy, EgressResult egress, CancellationToken token)
    {
        if (HttpAccountTransport.RequireHttpProxy(proxy) != Proxy) throw new AccountException(AccountError.ExitUnavailable, "授权出口不一致。");
        return OpenCoreAsync(profileName, authorizeUrl, token, egress);
    }
    public Task<BrowserLaunch> OpenAsync(string profileName, Uri authorizeUrl, CancellationToken cancellationToken)
        => OpenCoreAsync(profileName, authorizeUrl, cancellationToken, null);
    private async Task<BrowserLaunch> OpenCoreAsync(string profileName, Uri authorizeUrl, CancellationToken cancellationToken, EgressResult? egress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = AccountEndpoints.Authorize;
        if (authorizeUrl is null || !authorizeUrl.IsAbsoluteUri
            || !authorizeUrl.Scheme.Equals(endpoint.Scheme, StringComparison.OrdinalIgnoreCase)
            || !authorizeUrl.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) || authorizeUrl.Port != endpoint.Port
            || !authorizeUrl.AbsolutePath.Equals(endpoint.AbsolutePath, StringComparison.Ordinal)
            || authorizeUrl.UserInfo.Length != 0 || authorizeUrl.Fragment.Length != 0)
            throw new AccountException(AccountError.Browser, "授权地址不是指定的 OpenAI 登录入口，未打开浏览器。");
        if (launched is not null) throw new AccountException(AccountError.Browser, "此登录浏览器会话已经打开，请使用新的登录会话。");
        try
        {
            if (strictBrowser is not null) return launched = egress is not null
                ? await strictBrowser.OpenVerifiedAsync(profileName, authorizeUrl, Proxy, egress, cancellationToken)
                : await strictBrowser.OpenWithProxyAsync(profileName, authorizeUrl, Proxy, cancellationToken);
            var executable = system.FindChrome();
            if (string.IsNullOrWhiteSpace(executable)) throw new AccountException(AccountError.Browser, "未找到 Chrome，请安装 Chrome 后再登录。");
            var profile = Path.Combine(workspaceRoot, "browser-envs", "login-" + Guid.NewGuid().ToString("N"));
            PhysicalFileStorage.RejectLinks(profile);
            var arguments = ChromeArguments.Build(profile, new(Proxy), authorizeUrl, defer: false);
            Directory.CreateDirectory(profile);
            await system.StartAsync(executable, arguments, cancellationToken);
            return launched = new(executable, profile, ["授权页面使用本次选定的 HTTP 代理和独立浏览器会话；不会更改系统时区或关闭用户浏览器。"]);
        }
        catch (OperationCanceledException) { throw; }
        catch (AccountException) { throw; }
        catch (Exception error) { throw new AccountException(AccountError.Browser, "专用登录浏览器启动失败（" + error.GetType().Name + (error is System.ComponentModel.Win32Exception native ? ":" + native.NativeErrorCode : "") + "），授权未完成。"); }
    }

    public async Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(BrowserLaunch launch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (launched is null || launch != launched) return null;
        if (strictBrowser is not null) return await strictBrowser.ObserveAsync(launch, cancellationToken);
        try { return await system.ObserveAsync(launched.Executable, launched.ProfileDirectory, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }
    public ValueTask DisposeAsync() => strictBrowser?.DisposeAsync() ?? ValueTask.CompletedTask;
    public async Task<BrowserSessionCleanupResult> CleanupAsync(BrowserLaunch? launch, CancellationToken token)
    {
        var result = strictBrowser is null ? new BrowserSessionCleanupResult(false, "此兼容浏览器未连接严格会话清理服务。") : await strictBrowser.CleanupAsync(launch, token);
        if (result.Success && (launch is null || launch == launched)) launched = null;
        return result;
    }
}
