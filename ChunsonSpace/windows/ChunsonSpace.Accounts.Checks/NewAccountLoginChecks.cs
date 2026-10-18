using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

internal static class NewAccountLoginChecks
{
    public static async Task<int> Run(string root)
    {
        int count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        JsonObject Response(string id, string email, string access = "fixture-access") => new()
        {
            ["id_token"] = "fixture." + OAuthPkce.Base64Url(Encoding.UTF8.GetBytes(new JsonObject { ["email"] = email, ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = id } }.ToJsonString())) + ".unsigned",
            ["access_token"] = access, ["refresh_token"] = "fixture-refresh"
        };
        var clock = new FakeClock(); var callbacks = new FakeCallbackFactory(clock); var network = new FakeTransport();
        var physical = new PhysicalFileStorage(); var files = new ImportFaultStorage(physical);
        var home = Path.Combine(root, "original-home"); Directory.CreateDirectory(home); File.WriteAllText(Path.Combine(home, "auth.json"), Response("original", "account-508289b1dc@example.invalid").ToJsonString());
        var original = File.ReadAllText(Path.Combine(home, "auth.json"));
        var repository = new AccountRepository(Path.Combine(root, "accounts"), home, files: files, systemProxyProvider: () => "http://127.0.0.1:7899");
        var browser = new ProxyFixtureBrowser("http://127.0.0.1:7897");
        var login = new LoginService(repository, network, callbacks, browser, clock, () => "http://127.0.0.1:7899");
        network.Json(200, Response("new-id", "account-cc6d96611c@example.invalid"));
        var first = await login.LoginNewAsync(browser.Proxy, browser);
        Check(first.Ok && repository.Profiles.ListAccounts().Count == 1 && first.Profile == "account-cc6d96611c@example.invalid", "login imports named identity without precreation");
        Check(network.Requests.Count == 1 && callbacks.Starts == 1 && browser.Opens == 1 && network.Requests[0].Proxy == browser.Proxy, "single PKCE callback and token exchange share explicit route");
        Check(File.ReadAllText(Path.Combine(home, "auth.json")) == original && first.Save?.ActiveSynced == false, "new import never mutates original active home");
        repository.Profiles.UpdateEnvironment(first.Profile, timezone: "Asia/Tokyo", locale: "zh-CN");
        var systemBrowser = new ProxyFixtureBrowser("http://127.0.0.1:7899");
        network.Json(200, Response("new-id", "account-cc6d96611c@example.invalid", "updated"));
        var updated = await login.LoginNewAsync(authorizationBrowser: systemBrowser, followSystemProxy: true);
        Check(updated.Ok && updated.Profile == first.Profile && repository.Profiles.ListAccounts().Count == 1, "same identity updates existing record");
        Check(repository.Profiles.ReadEnvironment(first.Profile) is { FollowSystemProxy: true, Timezone: "Asia/Tokyo", Locale: "zh-CN" } && repository.Proxy(first.Profile) == systemBrowser.Proxy, "same identity saves chosen route and preserves other environment fields");
        network.Json(200, Response("other-id", "account-cc6d96611c@example.invalid"));
        var other = await login.LoginNewAsync(browser.Proxy, browser);
        Check(other.Ok && other.Profile != first.Profile && repository.ReadAuth(first.Profile)["tokens"]?["account_id"]?.GetValue<string>() == "new-id", "another identity sharing email cannot overwrite");
        callbacks.Kind = CallbackKind.Cancelled;
        var beforeRequests = network.Requests.Count; var beforeAccounts = repository.Profiles.ListAccounts().Count;
        var cancelled = await login.LoginNewAsync(browser.Proxy, browser);
        Check(!cancelled.Ok && cancelled.Profile.Length == 0 && network.Requests.Count == beforeRequests && repository.Profiles.ListAccounts().Count == beforeAccounts, "cancel leaves no empty account and no token exchange");
        callbacks.Kind = CallbackKind.Code; network.Json(500, new());
        var failed = await login.LoginNewAsync(browser.Proxy, browser);
        Check(!failed.Ok && network.Requests.Count == beforeRequests + 1 && repository.Profiles.ListAccounts().Count == beforeAccounts, "failed authorization exchange occurs once without empty record");
        var starts = callbacks.Starts;
        var unbound = await login.LoginNewAsync(browser.Proxy, new FakeBrowser());
        Check(!unbound.Ok && callbacks.Starts == starts, "unbound default browser rejected before callback or network");
        var mismatch = await login.LoginNewAsync("http://127.0.0.1:7898", browser);
        Check(!mismatch.Ok && callbacks.Starts == starts, "mismatched browser proxy rejected before exchange");
        var noProxy = new LoginService(repository, network, callbacks, browser, clock, () => null);
        Check(!(await noProxy.LoginNewAsync(authorizationBrowser: browser, followSystemProxy: true)).Ok && callbacks.Starts == starts, "missing system HTTP proxy fails before request without direct fallback");
        int resolutions = 0;
        var dynamicRepository = new AccountRepository(repository.Root, systemProxyProvider: () => ++resolutions == 1 ? "http://127.0.0.1:7899" : "http://127.0.0.1:7900");
        var stableBrowser = new ProxyFixtureBrowser("http://127.0.0.1:7899");
        network.Json(200, Response("new-id", "account-cc6d96611c@example.invalid"));
        var stableLogin = await new LoginService(dynamicRepository, network, callbacks, stableBrowser, clock).LoginAsync(first.Profile);
        Check(stableLogin.Ok && resolutions == 1 && stableBrowser.ResolvedProxy == "http://127.0.0.1:7899" && network.Requests[^1].Proxy == stableBrowser.ResolvedProxy, "existing system-route login resolves once and binds browser/token to the same snapshot");
        network.Json(200, new() { ["access_token"] = "fixture-preheated" });
        await new AccountService(dynamicRepository, network, clock).PrepareWithProxyAsync(new(IdentityKind.Account, first.Profile), browser.Proxy, default);
        Check(resolutions == 1 && network.Requests[^1].Proxy == browser.Proxy && network.Requests[^1].RequireProxy, "profile refresh preparation uses final resolved proxy without consulting changed system route");
        var oldAuth = repository.ReadAuth(first.Profile).ToJsonString(); var oldEnvironment = repository.Profiles.ReadEnvironment(first.Profile);
        files.FailAuthOnce = Path.Combine(repository.AccountPath(first.Profile), "auth.json");
        try { repository.ImportLogin(JwtClaims.ToAuth(Response("new-id", "account-cc6d96611c@example.invalid", "failed-update"), clock.UtcNow), browser.Proxy); throw new InvalidOperationException("update failure accepted"); }
        catch (AccountException error) { Check(error.Code == AccountError.Storage, "failed repeated login reports storage failure"); }
        Check(repository.ReadAuth(first.Profile).ToJsonString() == oldAuth && repository.Profiles.ReadEnvironment(first.Profile) == oldEnvironment, "failed auth write rolls back both authorization and selected route");
        Check(File.ReadAllText(Path.Combine(home, "auth.json")) == original, "all login scenarios preserve original home");
        browser.FailCleanup = true; network.Json(200, Response("new-id", "account-cc6d96611c@example.invalid", "saved-before-cleanup"));
        var savedBeforeCleanup = await login.LoginNewAsync(browser.Proxy, browser);
        Check(savedBeforeCleanup.Ok && savedBeforeCleanup.Save?.Saved == true && savedBeforeCleanup.Warnings?.Any(message => message.Contains("清理未确认", StringComparison.Ordinal)) == true && repository.ReadAuth(first.Profile)["tokens"]?["access_token"]?.GetValue<string>() == "saved-before-cleanup", "cleanup failure retains committed login success and reports separate cleanup warning");
        var opens = browser.Opens; beforeRequests = network.Requests.Count;
        var blockedByCleanup = await login.LoginNewAsync(browser.Proxy, browser);
        Check(!blockedByCleanup.Ok && blockedByCleanup.Error == AccountError.Busy && browser.Opens == opens && network.Requests.Count == beforeRequests, "unconfirmed previous owned-window cleanup blocks new authorization and token requests");
        browser.FailCleanup = false; network.Json(200, Response("new-id", "account-cc6d96611c@example.invalid"));
        Check((await login.LoginNewAsync(browser.Proxy, browser)).Ok, "confirmed retry of previous cleanup allows a fresh authorization");
        callbacks.Kind = CallbackKind.Cancelled; var cleanups = browser.CleanupCalls;
        await login.LoginNewAsync(browser.Proxy, browser);
        Check(browser.CleanupCalls == cleanups + 1, "cancelled authorization still cleans its owned browser session");
        callbacks.Kind = CallbackKind.Code; browser.FailOpen = true; cleanups = browser.CleanupCalls;
        Check(!(await login.LoginNewAsync(browser.Proxy, browser)).Ok && browser.CleanupCalls == cleanups + 1 && browser.LastCleanupHadLaunch == false, "partial browser opening failure invokes internal-session cleanup without a returned launch");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparedBrowser = new ProxyFixtureBrowser(browser.Proxy) { PrepareGate = gate };
        var admissions = 0; var verifiedExit = new EgressResult(true, "203.0.113.7", Timezone: "America/Chicago");
        var fastLogin = new LoginService(repository, network, callbacks, preparedBrowser, clock, verifiedAdmission: async (route, token) =>
        { admissions++; await gate.Task.WaitAsync(token); return verifiedExit; });
        network.Json(200, Response("fast-id", "account-4c4bec884b@example.invalid")); var beforeFastCallbacks = callbacks.Starts;
        var fastTask = fastLogin.LoginNewAsync(browser.Proxy, preparedBrowser);
        Check(admissions == 1 && preparedBrowser.Prepares == 1 && callbacks.Starts == beforeFastCallbacks && preparedBrowser.Opens == 0, "prepare and admission start concurrently before any callback or browser");
        gate.SetResult(); var fastResult = await fastTask;
        Check(fastResult.Ok && preparedBrowser.VerifiedOpens == 1 && ReferenceEquals(preparedBrowser.Evidence, verifiedExit) && admissions == 1 && preparedBrowser.Prepares == 1, "one login carries one prepared browser and one admission result to verified opening");
        network.Json(200, Response("fast-id", "account-4c4bec884b@example.invalid")); await fastLogin.LoginNewAsync(browser.Proxy, preparedBrowser);
        Check(admissions == 2 && preparedBrowser.Prepares == 2 && preparedBrowser.VerifiedOpens == 2, "later login performs fresh preparation and admission instead of persistent bypass cache");
        var deniedLogin = new LoginService(repository, network, callbacks, preparedBrowser, clock, verifiedAdmission: (_, _) => Task.FromException<EgressResult>(new AccountException(AccountError.ExitUnavailable, "质量未知，不放行")));
        var deniedStarts = callbacks.Starts; var deniedOpens = preparedBrowser.Opens;
        var deniedResult = await deniedLogin.LoginNewAsync(browser.Proxy, preparedBrowser);
        Check(!deniedResult.Ok && callbacks.Starts == deniedStarts && preparedBrowser.Opens == deniedOpens, "failed admission never opens browser or callback even when preparation succeeds");
        var slowPreparation = new ProxyFixtureBrowser(browser.Proxy) { PrepareGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var failFast = new LoginService(repository, network, callbacks, slowPreparation, clock,
            verifiedAdmission: (_, _) => Task.FromException<EgressResult>(new AccountException(AccountError.ExitUnavailable, "质量未知，不放行")));
        var failedFast = await failFast.LoginNewAsync(browser.Proxy, slowPreparation).WaitAsync(TimeSpan.FromSeconds(3));
        Check(!failedFast.Ok && !failFast.Status.Active && slowPreparation.CleanupCalls == 1 && slowPreparation.Opens == 0,
            "admission failure cancels slow preparation and cleans preview without leaving login active");
        var cancellable = new LoginService(repository, network, callbacks, slowPreparation, clock,
            verifiedAdmission: async (_, token) => { await Task.Delay(Timeout.Infinite, token); return verifiedExit; });
        var pendingLogin = cancellable.LoginNewAsync(browser.Proxy, slowPreparation);
        Check(cancellable.Cancel(), "preparation can be cancelled before a callback or authorization page exists");
        Check(!(await pendingLogin.WaitAsync(TimeSpan.FromSeconds(3))).Ok && !cancellable.Status.Active, "cancelled preparation clears active login state");
        return count;
    }

    private sealed class ProxyFixtureBrowser(string proxy) : IProxyBoundLoginBrowser, IResolvedProxyLoginBrowser, IVerifiedProxyLoginBrowser, IBrowserSessionCleanup
    {
        public TaskCompletionSource? PrepareGate { get; init; }
        public int Prepares { get; private set; }
        public int VerifiedOpens { get; private set; }
        public EgressResult? Evidence { get; private set; }
        public async Task PrepareAsync(string route, CancellationToken token) { Prepares++; if (PrepareGate is not null) await PrepareGate.Task.WaitAsync(token); }
        public Task<BrowserLaunch> OpenVerifiedAsync(string name, Uri uri, string route, EgressResult evidence, CancellationToken token)
        { VerifiedOpens++; Evidence = evidence; return OpenWithProxyAsync(name, uri, route, token); }
        public string Proxy => proxy;
        public int Opens { get; private set; }
        public string? ResolvedProxy { get; private set; }
        public bool FailOpen { get; set; }
        public bool FailCleanup { get; set; }
        public int CleanupCalls { get; private set; }
        public bool LastCleanupHadLaunch { get; private set; }
        public Task<BrowserLaunch> OpenAsync(string name, Uri uri, CancellationToken token) { Opens++; if (FailOpen) throw new AccountException(AccountError.Browser, "fixture partial open failure"); return Task.FromResult(new BrowserLaunch("fixture-chrome", "fixture-profile", [])); }
        public Task<BrowserLaunch> OpenWithProxyAsync(string name, Uri uri, string resolvedProxy, CancellationToken token) { ResolvedProxy = resolvedProxy; return OpenAsync(name, uri, token); }
        public Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(BrowserLaunch launch, CancellationToken token) => Task.FromResult<IReadOnlyList<BrowserIdentity>?>(null);
        public Task<BrowserSessionCleanupResult> CleanupAsync(BrowserLaunch? launch, CancellationToken token) { CleanupCalls++; LastCleanupHadLaunch = launch is not null; return Task.FromResult(new BrowserSessionCleanupResult(!FailCleanup, "fixture cleanup")); }
    }
    private sealed class ImportFaultStorage(IFileStorage actual) : IFileStorage
    {
        public string? FailAuthOnce { get; set; }
        public bool FileExists(string path) => actual.FileExists(path);
        public bool DirectoryExists(string path) => actual.DirectoryExists(path);
        public byte[] Read(string path) => actual.Read(path);
        public void WriteAtomic(string path, byte[] content) { if (path == FailAuthOnce) { FailAuthOnce = null; throw new IOException("fixture commit failure"); } actual.WriteAtomic(path, content); }
        public void CreateDirectory(string path) => actual.CreateDirectory(path);
        public IReadOnlyList<string> Files(string path, bool recursive) => actual.Files(path, recursive);
        public IReadOnlyList<string> Directories(string path) => actual.Directories(path);
        public void MoveDirectory(string source, string destination) => actual.MoveDirectory(source, destination);
        public void DeleteFile(string path) => actual.DeleteFile(path);
        public void DeleteDirectory(string path, bool recursive) => actual.DeleteDirectory(path, recursive);
    }
}
