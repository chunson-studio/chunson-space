using FAswitch.Accounts;
using FAswitch.Core;
using FAswitch.Windows;
using System.IO;
using FAswitch.NodeTesting;

namespace FAswitch.Desktop;

public sealed class DesktopAccountServices : IAsyncDisposable
{
    private DesktopAccountServices(DesktopWorkspace workspace, AccountRepository repository, IAccountTransport transport, ILoginCallbackFactory callbacks, ILoginBrowser browser, ILoginBrowser? newAccountBrowser, Func<string?>? systemProxyProvider = null, EgressService? egressService = null, ManagedNodeRoutes? routes = null)
    {
        Repository = repository; Browser = browser; Latency = new(transport); LocalEgress = new(repository, transport);
        Accounts = new(repository, transport); Egress = egressService ?? new(repository, transport, workspace.ProxyServices.NodeTesting!.ProbeAccountExitAsync);
        Admission = new(async (proxy, token) => (routes is null ? null : await routes.ProbeAdmissionAsync(proxy, token))
            ?? await workspace.ProxyServices.NodeTesting!.ProbeCachedExitAsync(proxy, token)); Routes = routes;
        Login = new(repository, transport, callbacks, browser, systemProxyProvider: systemProxyProvider, newAccountBrowser: newAccountBrowser, verifiedAdmission: RequireBrowserEgressAsync);
        if (workspace.IsFixture) { VisualRuntimeFixture = new(); VisualTests = new(repository, VisualRuntimeFixture); }
        else VisualTests = new(repository, new ResponsesVisualTestRuntime(Accounts,
            () => InstalledBrowsers.Find().FirstOrDefault()?.Executable ?? throw new IOException("请先安装 Chrome、Edge 或 Brave，以便预览作品。")));
    }
    public VisualTestService VisualTests { get; }
    public OfflineVisualTestRuntime? VisualRuntimeFixture { get; }
    public SystemRegionService Region { get; private init; } = null!;
    public OfflineRegionSystem? OfflineRegion { get; private init; }
    public AccountRepository Repository { get; }
    public AccountService Accounts { get; }
    public EgressService Egress { get; }
    public EgressService LocalEgress { get; }
    public IpAdmissionService Admission { get; }
    public ManagedNodeRoutes? Routes { get; }
    public ProxyLatencyService Latency { get; }
    public LoginService Login { get; }
    public ILoginBrowser Browser { get; }
    public OfflineAccountTransport? OfflineTransport { get; private init; }
    public OfflineLoginCallbackFactory? OfflineCallbacks { get; private init; }
    public OfflineDefaultLoginBrowser? OfflineNewAccountBrowser { get; private init; }
    public List<LocalProxyListener>? OfflineProxyListeners { get; private init; }
    public ISystemProxyResolver ProxyResolver { get; private init; } = new SystemProxyResolver(() => new(false, ""));
    public Func<CancellationToken, Task<LoginProxySelection>> ResolveLoginProxyAsync { get; private init; } = null!;
    private readonly Dictionary<ILoginBrowser, string> authorizationSessions = new(ReferenceEqualityComparer.Instance);
    private Func<string, bool, ILoginBrowser> AuthorizationBrowserFactory { get; init; } = (_, _) => throw new AccountException(AccountError.Browser, "授权浏览器尚未连接。");
    public ILoginBrowser CreateAuthorizationBrowser(string proxy, bool independentTimezone = false)
    {
        var browser = AuthorizationBrowserFactory(proxy, independentTimezone);
        authorizationSessions[browser] = proxy;
        return browser;
    }
    public async Task<BrowserLaunch> OpenProfileBrowserAsync(string name, CancellationToken token = default)
    {
        var proxy = Repository.Proxy(name);
        var check = RequireBrowserEgressAsync(proxy, token);
        var prepare = Browser is IVerifiedProxyLoginBrowser verified ? verified.PrepareAsync(name, proxy, token) : Task.CompletedTask;
        await Task.WhenAll(check, prepare);
        return Browser is IVerifiedProxyLoginBrowser ready
            ? await ready.OpenVerifiedAsync(name, new Uri("https://chatgpt.com/"), proxy, await check, token)
            : await Browser.OpenAsync(name, new Uri("https://chatgpt.com/"), token);
    }
    private async Task<EgressResult> RequireBrowserEgressAsync(string proxy, CancellationToken token)
    {
        var egress = await LocalEgress.ProbeProxyAsync(proxy, token);
        if (!egress.Ok) throw new AccountException(AccountError.ExitUnavailable, "无法确认真实出口 IP 与对应时区，请检查所选线路后重试。");
        return egress;
    }
    public async Task<BrowserSessionCleanupResult> ReleaseAuthorizationBrowserAsync(ILoginBrowser browser)
    {
        var cleanup = browser is IBrowserSessionCleanup session
            ? await session.CleanupAsync(null, CancellationToken.None)
            : new BrowserSessionCleanupResult(true, "离线授权替身没有真实窗口或缓存。");
        if (cleanup.Success)
        {
            if (authorizationSessions.TryGetValue(browser, out var proxy)) await ReleaseUnusedAuthorizationRouteAsync(proxy, browser);
            if (browser is IAsyncDisposable disposable) await disposable.DisposeAsync();
            authorizationSessions.Remove(browser);
        }
        return cleanup;
    }
    internal async Task ReleaseUnusedAuthorizationRouteAsync(string proxy, ILoginBrowser? completedBrowser = null)
    {
        if (Routes is null) return;
        var retained = Repository.Profiles.ListAccounts().Where(account => !account.Environment.FollowSystemProxy)
            .Select(account => account.Environment.Proxy)
            .Concat(authorizationSessions.Where(pair => !ReferenceEquals(pair.Key, completedBrowser)).Select(pair => pair.Value)).ToArray();
        await Routes.ReleaseUnusedAsync(proxy, retained, CancellationToken.None);
    }
    public static DesktopAccountServices CreateNormal(DesktopWorkspace workspace)
    {
        var resolver = new SystemProxyResolver();
        var discovery = AutomaticLoginProxy.ForFlClash(workspace.ProxyServices.FlClashRoot, token => LocalProxyListeners.ReadAsync(token));
        Func<string?> proxyReader = () => Task.Run(() => discovery.ResolveAsync(default)).GetAwaiter().GetResult().Proxy;
        var repository = new AccountRepository(workspace.AccountRoot, workspace.ManagedPaths is null ? workspace.ActiveHome : null, new RuntimePendingGuard(workspace), systemProxyProvider: proxyReader);
        var rawTransport = new HttpAccountTransport();
        var routes = workspace.ProxyServices.NodeTesting!.CreateRoutes(rawTransport);
        var transport = new ManagedRouteTransport(rawTransport, routes); var egress = new EgressService(repository, transport, workspace.ProxyServices.NodeTesting.ProbeAccountExitAsync);
        var chrome = new WindowsChromeSystem(finder: () => InstalledBrowsers.Find().FirstOrDefault()?.Executable);
        var timezone = new BrowserTimezoneController(DesktopWorkspace.ApplicationExecutable);
        var browserEgress = new EgressService(repository, transport);
        var browser = new ChromeLoginBrowser(repository, chrome, new CdpConnectionFactory(), egressProbe: browserEgress.ProbeProxyAsync, installedBrowser: true, installedTimezone: timezone);
        return new(workspace, repository, transport, new LoopbackCallbackFactory(), browser, null, proxyReader, egress, routes)
        {
            Region = new(new WindowsRegionSystem(new WindowsCommandRunner())),
            ProxyResolver = resolver,
            ResolveLoginProxyAsync = discovery.ResolveAsync,
            AuthorizationBrowserFactory = (proxy, independent) => new OnboardingProxyBrowser(workspace.AccountRoot, proxy, chrome, installedBrowser: true, installedTimezone: timezone, alignInstalledTimezone: independent)
        };
    }
    public static DesktopAccountServices CreateFixture(DesktopWorkspace workspace)
    {
        var newAccountBrowser = new OfflineDefaultLoginBrowser();
        var listeners = new List<LocalProxyListener>();
        var discovery = AutomaticLoginProxy.ForClients(() => newAccountBrowser.SystemProxy, workspace.ProxyServices.FlClashRoot,
            workspace.ProxyServices.ClashVergeRoot, _ => Task.FromResult<IReadOnlyList<LocalProxyListener>>(listeners));
        Func<string?> proxyReader = () => Task.Run(() => discovery.ResolveAsync(default)).GetAwaiter().GetResult().Proxy;
        var resolver = new SystemProxyResolver(() => new(newAccountBrowser.SystemProxy is not null, newAccountBrowser.SystemProxy ?? ""));
        var repository = new AccountRepository(workspace.AccountRoot, workspace.ManagedPaths is null ? workspace.ActiveHome : null, new RuntimePendingGuard(workspace), systemProxyProvider: proxyReader);
        var browser = new OfflineLoginBrowser(repository);
        var transport = new OfflineAccountTransport(repository, browser, newAccountBrowser);
        var routes = workspace.ProxyServices.NodeTesting!.CreateRoutes(transport);
        var callbacks = new OfflineLoginCallbackFactory(browser, newAccountBrowser);
        var region = new OfflineRegionSystem();
        return new(workspace, repository, new ManagedRouteTransport(transport, routes), callbacks, browser, newAccountBrowser, systemProxyProvider: proxyReader, routes: routes) { Region = new(region), OfflineRegion = region, ProxyResolver = resolver, OfflineTransport = transport, OfflineCallbacks = callbacks, OfflineNewAccountBrowser = newAccountBrowser, OfflineProxyListeners = listeners, ResolveLoginProxyAsync = discovery.ResolveAsync, AuthorizationBrowserFactory = (proxy, _) => { newAccountBrowser.Proxy = proxy; return newAccountBrowser; } };
    }
    public async ValueTask DisposeAsync()
    {
        Login.Cancel();
        foreach (var browser in authorizationSessions.Keys.ToArray())
        {
            var cleanup = await ReleaseAuthorizationBrowserAsync(browser);
            if (!cleanup.Success) throw new AccountException(AccountError.Browser, cleanup.Message);
        }
        if (Browser is IBrowserSessionCleanup activeSession)
        {
            var cleanup = await activeSession.CleanupAsync(null, CancellationToken.None);
            if (!cleanup.Success) throw new AccountException(AccountError.Browser, cleanup.Message);
        }
        if (Browser is IAsyncDisposable disposable) await disposable.DisposeAsync();
        if (Routes is not null) await Routes.DisposeAsync();
    }
    private sealed class RuntimePendingGuard(DesktopWorkspace workspace) : IPendingAccountGuard
    {
        public bool IsPending
        {
            get
            {
                if (workspace.OfflineRuntime is { } offline) return offline.HasPendingLaunch;
                if (workspace.Runtime is null) return true;
                try { return Task.Run(() => workspace.Runtime.CheckPendingAsync(CancellationToken.None)).GetAwaiter().GetResult() is not null; }
                catch (Exception) { return true; }
            }
        }
    }
}

public sealed class DesktopChromeLaunchCoordinator(DesktopWorkspace workspace) : IChromeLaunchCoordinator
{
    public async Task<IChromeLaunchLease> BeginAsync(string profileName, EnvironmentSettings environment, CancellationToken cancellationToken)
    {
        if (workspace.Ownership is null || workspace.NativeTimezone is null) throw new AccountException(AccountError.Browser, "浏览器启动服务尚未连接。");
        var scope = await workspace.Ownership.BeginLaunchAsync(cancellationToken);
        try
        {
            var enabled = workspace.ManagedPaths is null && workspace.Profiles.ReadSettings().AutoSystemTimezone;
            var timezone = await workspace.NativeTimezone.SwitchAsync(profileName, environment.Timezone, enabled, cancellationToken);
            return new Lease(workspace, scope, profileName, timezone);
        }
        catch { scope.Dispose(); throw; }
    }
    private sealed class Lease(DesktopWorkspace workspace, IDisposable scope, string name, TimezoneResult timezone) : IChromeLaunchLease
    {
        private readonly List<string> warnings = timezone.Success ? [] : [timezone.Message];
        private readonly List<BrowserIdentity> roots = [];
        private bool disposed;
        public IReadOnlyList<string> Warnings => warnings;
        public void TrackRoot(BrowserIdentity identity)
        {
            if (identity.CreationTime > 0 && Path.IsPathFullyQualified(identity.Executable)) roots.Add(identity);
            if (!workspace.Ownership!.Track(new(identity.Pid, 0, "chrome.exe", identity.Executable, identity.CreationTime), OwnedProcessKind.LoginBrowser, name)) warnings.Add("浏览器已打开，但进程所有权记录未确认；不会关闭未确认的外部进程。");
        }
        public async Task FailureAsync(CancellationToken cancellationToken)
        {
            if (roots.Count > 0 && !await new WindowsChromeSystem().CloseOwnedAsync(roots, cancellationToken))
                warnings.Add("本次授权浏览器退出未确认，会话缓存已保留；其他浏览器不受影响。");
            if (timezone.Changed)
            {
                var restored = await workspace.NativeTimezone!.RestoreAsync(cancellationToken);
                if (!restored.Success) warnings.Add(restored.Message);
            }
        }
        public ValueTask DisposeAsync() { if (!disposed) { disposed = true; scope.Dispose(); } return ValueTask.CompletedTask; }
    }
}
