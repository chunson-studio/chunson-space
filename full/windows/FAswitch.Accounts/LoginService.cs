using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FAswitch.Accounts;

public enum LoginStage { Idle, Checking, Browser, Token, Save, Done, Failed }
public enum CallbackKind { Waiting, Code, Cancelled, StateMismatch, ProviderError, MissingCode, BrowserClosed, Timeout }
public sealed record LoginCallback(CallbackKind Kind, string? Code = null);
public sealed record LoginResult(bool Ok, string Profile, string Message, CallbackKind? Callback = null, AccountError? Error = null, CredentialSaveResult? Save = null, IReadOnlyList<string>? Warnings = null);
public sealed record LoginStatus(string Profile, LoginStage Stage, bool Active, LoginResult? Result = null, bool BrowserOpened = false);
public interface ILoginCallback : IAsyncDisposable
{
    int Port { get; }
    Task<LoginCallback> WaitAsync(TimeSpan duration, CancellationToken cancellationToken);
    bool Cancel();
}
public interface ILoginCallbackFactory { Task<ILoginCallback> StartAsync(string state, CancellationToken cancellationToken); }
public sealed record BrowserLaunch(string Executable, string ProfileDirectory, IReadOnlyList<string> Warnings, IReadOnlyList<BrowserIdentity>? PreviousRoots = null, IChromeLaunchLease? LaunchLease = null);
public sealed record BrowserIdentity(int Pid, long CreationTime, string Executable, string ProfileDirectory);
public interface ILoginBrowser
{
    Task<BrowserLaunch> OpenAsync(string profileName, Uri authorizeUrl, CancellationToken cancellationToken);
    Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(BrowserLaunch launch, CancellationToken cancellationToken);
}
public interface IProxyBoundLoginBrowser : ILoginBrowser { string Proxy { get; } }
public interface IResolvedProxyLoginBrowser : ILoginBrowser
{
    Task<BrowserLaunch> OpenWithProxyAsync(string profileName, Uri authorizeUrl, string resolvedProxy, CancellationToken cancellationToken);
}
public interface IVerifiedProxyLoginBrowser : ILoginBrowser
{
    Task PrepareAsync(string proxy, CancellationToken token);
    Task PrepareAsync(string profileName, string proxy, CancellationToken token) => PrepareAsync(proxy, token);
    Task<BrowserLaunch> OpenVerifiedAsync(string profileName, Uri authorizeUrl, string proxy, EgressResult egress, CancellationToken token);
}
public interface IPersistentLoginBrowser
{
    Task KeepAccountProfileAsync(string profileName, BrowserLaunch launch, CancellationToken token);
}
public interface IResumableLoginBrowser
{
    Task ResumeAsync(BrowserLaunch launch, CancellationToken token);
}

public static class OAuthPkce
{
    public const string StateSuffix = ".onboarding_entrypoint=life_sciences";
    public static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static (string Verifier, string Challenge) Create()
    { var verifier = Base64Url(RandomNumberGenerator.GetBytes(64)); return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))); }
    public static string State() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Redirect(int port)
    {
        if (port is not (1455 or 1457)) throw new ArgumentOutOfRangeException(nameof(port));
        return $"http://127.0.0.1:{port}/auth/callback";
    }
    public static Uri Authorize(int port, string challenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = AccountEndpoints.ClientId, ["redirect_uri"] = Redirect(port),
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = state,
            ["scope"] = "openid profile email offline_access api.connectors.read api.connectors.invoke", ["id_token_add_organizations"] = "true", ["codex_cli_simplified_flow"] = "true", ["originator"] = "codex_cli_rs"
        };
        return new(AccountEndpoints.Authorize + "?" + string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
    }
}

public sealed class LoginService(AccountRepository repository, IAccountTransport transport, ILoginCallbackFactory callbacks, ILoginBrowser browser,
    IAccountClock? clock = null, Func<string?>? systemProxyProvider = null, ILoginBrowser? newAccountBrowser = null, Func<string, CancellationToken, Task>? admission = null,
    Func<string, CancellationToken, Task<EgressResult>>? verifiedAdmission = null,
    Func<EgressResult, CancellationToken, Task>? configureNewAccountEnvironment = null)
{
    public const string NewAccountRequestProfile = "new-account";
    private readonly IAccountClock clock = clock ?? new SystemAccountClock();
    private readonly AccountNetwork network = new(transport);
    private int running;
    private ILoginCallback? callback;
    private CancellationTokenSource? preparationCancellation;
    private IBrowserSessionCleanup? pendingCleanup;
    private BrowserLaunch? pendingCleanupLaunch;
    private sealed record PendingBrowser(IResumableLoginBrowser Browser, BrowserLaunch Launch, CancellationToken Token);
    private PendingBrowser? pendingBrowser;
    private int resumingBrowser;
    public LoginStatus Status { get; private set; } = new("", LoginStage.Idle, false);
    public bool CanResumeBrowser => Status is { Active: true, Stage: LoginStage.Browser, BrowserOpened: true }
        && pendingBrowser is not null && Volatile.Read(ref resumingBrowser) == 0;
    public async Task<bool> ResumeBrowserAsync()
    {
        var pending = pendingBrowser;
        if (!CanResumeBrowser || pending is null || Interlocked.CompareExchange(ref resumingBrowser, 1, 0) != 0) return false;
        try
        {
            pending.Token.ThrowIfCancellationRequested();
            await pending.Browser.ResumeAsync(pending.Launch, pending.Token);
            return true;
        }
        finally { Interlocked.Exchange(ref resumingBrowser, 0); }
    }
    public bool Cancel()
    {
        if (Status.Active && Status.Stage is LoginStage.Idle or LoginStage.Checking && preparationCancellation is { } pending)
        { pending.Cancel(); return true; }
        if (Status.Stage == LoginStage.Browser && callback?.Cancel() == true)
        { pendingBrowser = null; preparationCancellation?.Cancel(); return true; }
        return false;
    }
    public Task<LoginResult> LoginAsync(string profileName, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => LoginCoreAsync(profileName, false, null, false, browser, timeout, cancellationToken);
    public Task<LoginResult> LoginNewAsync(string? explicitProxy = null, ILoginBrowser? authorizationBrowser = null,
        bool followSystemProxy = false, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => LoginCoreAsync(NewAccountRequestProfile, true, explicitProxy, followSystemProxy, authorizationBrowser ?? newAccountBrowser, timeout, cancellationToken);
    private async Task<LoginResult> LoginCoreAsync(string profileName, bool newAccount, string? explicitProxy, bool followSystemProxy, ILoginBrowser? selectedBrowser,
        TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return new(false, newAccount ? "" : profileName, "已有登录正在进行。", Error: AccountError.Busy);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var browserContinuation = CancellationTokenSource.CreateLinkedTokenSource(operationCancellation.Token);
        preparationCancellation = operationCancellation; cancellationToken = operationCancellation.Token;
        Status = new(profileName, LoginStage.Idle, true);
        LoginResult result;
        var warnings = new List<string>();
        BrowserLaunch? openedBrowser = null;
        bool browserAttempted = false;
        try
        {
            if (pendingCleanup is not null)
            {
                BrowserSessionCleanupResult cleanup;
                try { cleanup = await pendingCleanup.CleanupAsync(pendingCleanupLaunch, CancellationToken.None); }
                catch (Exception) { throw new AccountException(AccountError.Busy, "无法确认上次专用登录浏览器清理状态，未打开新的授权窗口。"); }
                if (!cleanup.Success) throw new AccountException(AccountError.Busy, "上次专用登录浏览器退出未确认，未打开新的授权窗口：" + ErrorRedaction.Scrub(cleanup.Message));
                if (cleanup.Warnings is { } pendingWarnings) warnings.AddRange(pendingWarnings);
                pendingCleanup = null; pendingCleanupLaunch = null;
            }
            if (repository.IsPending) throw new AccountException(AccountError.Busy, "桌面启动结果尚未确认，请等待后再登录。");
            string proxy;
            if (newAccount)
            {
                if (followSystemProxy && explicitProxy is not null) throw new AccountException(AccountError.ExitUnavailable, "请明确选择专属出口或跟随系统代理，不能混用。");
                proxy = followSystemProxy ? HttpAccountTransport.ResolveSystemHttpProxy(systemProxyProvider) : HttpAccountTransport.RequireHttpProxy(explicitProxy);
            }
            else
            {
                profileName = FAswitch.Core.ProfileRules.Name(profileName);
                if (!repository.Exists(profileName)) throw new AccountException(AccountError.Storage, "请先创建并绑定代理的账号档案。");
                proxy = repository.Proxy(profileName);
            }
            if (selectedBrowser is null) throw new AccountException(AccountError.Browser, "授权需要对应出口的隔离浏览器；未改用系统默认浏览器。");
            if (newAccount && (selectedBrowser is not IProxyBoundLoginBrowser bound || HttpAccountTransport.RequireHttpProxy(bound.Proxy) != proxy))
                throw new AccountException(AccountError.Browser, "授权浏览器与所选代理不一致；未打开默认浏览器，未发起请求。");
            Status = new(profileName, LoginStage.Checking, true);
            EgressResult? admittedEgress = null;
            if (verifiedAdmission is not null)
            {
                using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                browserAttempted = selectedBrowser is IVerifiedProxyLoginBrowser;
                var prepare = selectedBrowser is IVerifiedProxyLoginBrowser preparedBrowser
                    ? preparedBrowser.PrepareAsync(profileName, proxy, preparation.Token) : Task.CompletedTask;
                var check = verifiedAdmission(proxy, preparation.Token);
                var first = await Task.WhenAny(check, prepare);
                if (!first.IsCompletedSuccessfully)
                {
                    await preparation.CancelAsync();
                    try { await Task.WhenAll(check, prepare); } catch { }
                    await first;
                }
                await Task.WhenAll(check, prepare);
                admittedEgress = await check;
            }
            else if (admission is not null) await admission(proxy, cancellationToken);
            if (newAccount && configureNewAccountEnvironment is not null)
            {
                if (admittedEgress is not { Ok: true }) throw new AccountException(AccountError.ExitUnavailable, "出口未确认，无法匹配系统时区。");
                await configureNewAccountEnvironment(admittedEgress, cancellationToken);
            }
            var pkce = OAuthPkce.Create();
            var state = OAuthPkce.State();
            callback = await callbacks.StartAsync(state, cancellationToken);
            await using var server = callback;
            Status = new(profileName, LoginStage.Browser, true);
            var authorizeUrl = OAuthPkce.Authorize(server.Port, pkce.Challenge, state);
            browserAttempted = true;
            var opened = admittedEgress is not null && selectedBrowser is IVerifiedProxyLoginBrowser verifiedBrowser
                ? await verifiedBrowser.OpenVerifiedAsync(profileName, authorizeUrl, proxy, admittedEgress, cancellationToken)
                : selectedBrowser is IResolvedProxyLoginBrowser resolvedBrowser
                ? await resolvedBrowser.OpenWithProxyAsync(profileName, authorizeUrl, proxy, cancellationToken)
                : await selectedBrowser.OpenAsync(profileName, authorizeUrl, cancellationToken);
            openedBrowser = opened;
            if (selectedBrowser is IResumableLoginBrowser resumable)
                pendingBrowser = new(resumable, opened, browserContinuation.Token);
            Status = new(profileName, LoginStage.Browser, true, BrowserOpened: true);
            warnings.AddRange(opened.Warnings);
            var started = clock.UtcNow;
            // Interactive authorization has no default time limit. A caller may
            // explicitly set a deadline (e.g. an isolated test); closing/cancelling still ends it.
            DateTimeOffset? deadline = timeout is { } limit ? started + limit : null;
            LoginCallback received;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline is { } until && clock.UtcNow >= until) { received = new(CallbackKind.Timeout); break; }
                received = await server.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
                if (received.Kind != CallbackKind.Waiting) break;
                var roots = await selectedBrowser.ObserveAsync(opened, cancellationToken);
                if (roots is { Count: 0 } && clock.UtcNow - started >= TimeSpan.FromSeconds(5)) { received = new(CallbackKind.BrowserClosed); break; }
            }
            pendingBrowser = null;
            await browserContinuation.CancelAsync();
            if (received.Kind != CallbackKind.Code) result = new(false, profileName, received.Kind switch
            {
                CallbackKind.Cancelled => "登录已取消。", CallbackKind.BrowserClosed => "此档案的登录浏览器已关闭，凭据未保存。",
                CallbackKind.Timeout => "登录超时，凭据未保存。", CallbackKind.StateMismatch => "登录回调 state 不匹配，已拒绝。",
                _ => "登录回调未提供有效授权码，凭据未保存。"
            }, received.Kind);
            else
            {
                Status = new(profileName, LoginStage.Token, true);
                var fields = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["client_id"] = AccountEndpoints.ClientId,
                    ["code"] = received.Code ?? "", ["redirect_uri"] = OAuthPkce.Redirect(server.Port), ["code_verifier"] = pkce.Verifier };
                var body = Encoding.UTF8.GetBytes(string.Join('&', fields.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
                // Authorization code is one-use: exactly one request, no retry on timeout or failure.
                var tokens = await network.JsonAsync(new(HttpMethod.Post, AccountEndpoints.Token, proxy, true, new Dictionary<string, string> { ["user-agent"] = "" }, body, "application/x-www-form-urlencoded"), TimeSpan.FromSeconds(30), cancellationToken, received.Code, pkce.Verifier, state);
                var auth = JwtClaims.ToAuth(tokens, clock.UtcNow);
                cancellationToken.ThrowIfCancellationRequested();
                Status = new(profileName, LoginStage.Save, true);
                if (newAccount)
                {
                    var imported = repository.ImportLogin(auth, followSystemProxy ? null : proxy, followSystemProxy);
                    profileName = imported.Profile;
                    warnings.AddRange(imported.Warnings);
                    result = new(true, profileName, imported.Created ? "授权已保存，账号已自动录入。" : "授权已保存，已更新同账号档案及所选出口。", Save: new(true, false, imported.Warnings));
                }
                else
                {
                    var saved = repository.SaveLogin(profileName, auth);
                    warnings.AddRange(saved.Warnings);
                    result = new(true, profileName, "登录凭据已保存。", Save: saved);
                }
                // Credentials are already committed. A browser retention problem must
                // never report that the successful authorization was lost.
                if (selectedBrowser is IPersistentLoginBrowser persistent)
                {
                    try { await persistent.KeepAccountProfileAsync(profileName, opened, CancellationToken.None); }
                    catch (Exception) { warnings.Add("账号授权已保存，但网页档案保留未完成；下次打开网页可能需要重新登录。"); }
                }
            }
        }
        catch (OperationCanceledException) { result = new(false, profileName, "登录已取消，未保存新凭据。", CallbackKind.Cancelled, AccountError.Cancelled); }
        catch (AccountException error) { result = new(false, profileName, error.Message, Error: error.Code); }
        catch (FAswitch.Core.StorageException) { result = new(false, profileName, "档案名称或存储配置无效。", Error: AccountError.Storage); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
        { result = new(false, profileName, "登录连接或存储失败，未保存新凭据。", Error: AccountError.Network); }
        catch (Exception)
        { result = new(false, profileName, "登录准备未完成，未保存新凭据。请重新选择线路后重试。", Error: AccountError.Network); }
        finally
        {
            pendingBrowser = null;
            await browserContinuation.CancelAsync();
            if (browserAttempted && selectedBrowser is IBrowserSessionCleanup cleanup)
            {
                try
                {
                    var cleaned = await cleanup.CleanupAsync(openedBrowser, CancellationToken.None);
                    if (cleaned.Warnings is { } cleanupWarnings) warnings.AddRange(cleanupWarnings);
                    if (!cleaned.Success)
                    {
                        pendingCleanup = cleanup; pendingCleanupLaunch = openedBrowser;
                        warnings.Add("专用登录浏览器退出或临时数据清理未确认；已保存的账号授权保持保留，未确认清理前禁止新授权。");
                    }
                }
                catch (Exception)
                {
                    pendingCleanup = cleanup; pendingCleanupLaunch = openedBrowser;
                    warnings.Add("专用登录浏览器清理异常；已保存的账号授权保持保留，未确认清理前禁止新授权。");
                }
            }
            callback = null; preparationCancellation = null; Interlocked.Exchange(ref running, 0);
        }
        if (openedBrowser?.LaunchLease is { } launchLease) warnings.AddRange(launchLease.Warnings.Where(warning => !warnings.Contains(warning, StringComparer.Ordinal)));
        result = result with { Warnings = warnings, Profile = newAccount && !result.Ok ? "" : result.Profile };
        Status = new(profileName, result.Ok ? LoginStage.Done : LoginStage.Failed, false, result);
        return result;
    }
}

public interface IStatefulLoginCallback : ILoginCallback { string State { get; } }
