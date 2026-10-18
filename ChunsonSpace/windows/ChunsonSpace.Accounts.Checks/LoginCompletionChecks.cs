using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;

internal static class LoginCompletionChecks
{
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        const string privateMessage = "DO_NOT_EXPORT code=one-use-code access_token=private-token account-a09ad6a9ce@example.invalid";
        var repository = new AccountRepository(Path.Combine(root, "accounts"));
        var clock = new FakeClock();
        var callbacks = new CompletionCallbackFactory { FailDispose = true };
        var events = new List<string>();
        var browser = new CompletionBrowser(events);
        var transport = new CompletionTransport(events)
        {
            Failure = new HttpRequestException(HttpRequestError.ProxyTunnelError, privateMessage,
                new SocketException((int)SocketError.ConnectionRefused), HttpStatusCode.BadGateway)
        };
        var admissions = 0;
        var login = new LoginService(repository, transport, callbacks, browser, clock,
            verifiedAdmission: (_, _) => { admissions++; events.Add("admission"); return Task.FromResult(new EgressResult(true, "203.0.113.9", Timezone: "America/Phoenix")); });
        var failed = await login.LoginNewAsync(browser.Proxy, browser);
        Check(!failed.Ok && failed.FailedStage == LoginStage.Token && failed.Callback == CallbackKind.Code && failed.Error == AccountError.Network,
            "received authorization callback followed by token transport failure retains exact failed stage");
        Check(failed.Message.Contains("网页授权回调已收到", StringComparison.Ordinal) && failed.Message.Contains("未保存新授权", StringComparison.Ordinal),
            "token failure distinguishes browser authorization from saved application credentials");
        Check(failed.TransportFailure is { HttpRequestError: "ProxyTunnelError", HttpStatus: 502, SocketErrorCode: "ConnectionRefused" },
            "login failure preserves allowlisted transport diagnostic categories");
        Check(failed.Warnings?.Any(message => message.Contains("回调会话结束处理未完成", StringComparison.Ordinal)) == true,
            "callback disposal failure warns separately without replacing original token transport failure");
        Check(!JsonSerializer.Serialize(failed).Contains("DO_NOT_EXPORT", StringComparison.Ordinal)
            && !JsonSerializer.Serialize(failed).Contains("one-use-code", StringComparison.Ordinal)
            && !JsonSerializer.Serialize(failed).Contains("private-token", StringComparison.Ordinal), "login failure does not expose raw exception or credentials");
        Check(transport.Calls == 1 && callbacks.Starts == 1 && admissions == 1 && events.Count(value => value == "prepare") == 1
            && events.IndexOf("admission") < events.IndexOf("open") && events.IndexOf("open") < events.IndexOf("token"),
            "admission occurs before browser and code exchange happens once without a second admission check");
        Check(repository.Profiles.ListAccounts().Count == 0 && failed.Profile.Length == 0 && failed.Save is null,
            "failed token exchange leaves no new account or credential commit");
        Check(login.Status is { Stage: LoginStage.Failed, Active: false } && login.Status.Result?.FailedStage == LoginStage.Token,
            "terminal login status retains original failure stage");

        transport.Failure = null; callbacks.FailDispose = false;
        var success = await login.LoginNewAsync(browser.Proxy, browser);
        var authPath = Path.Combine(repository.AccountPath(success.Profile), "auth.json");
        var committedBytes = File.ReadAllBytes(authPath);
        Check(!repository.Profiles.ReadAccountMarks(success.Profile).Recovered && repository.Profiles.ReadAccountMarks(success.Profile).Quality == ChunsonSpace.Core.AccountQuality.Unmarked, "direct successful authorization does not invent recovery or normal quality");
        Check(success.Ok && success.Save?.Saved == true && success.FailedStage is null && success.TransportFailure is null,
            "successful token exchange commits an account and has no failure state");
        transport.Failure = new HttpRequestException(HttpRequestError.ConnectionError, privateMessage);
        var before = transport.Calls;
        var existingFailure = await login.LoginAsync(success.Profile);
        Check(!existingFailure.Ok && existingFailure.FailedStage == LoginStage.Token && existingFailure.Profile == success.Profile
            && File.ReadAllBytes(authPath).SequenceEqual(committedBytes) && transport.Calls == before + 1,
            "failed reauthorization preserves prior credentials and never retries one-use code");

        transport.Failure = null;
        browser.FailKeep = true; browser.FailCleanup = true; callbacks.FailDispose = true;
        var postSaveFailure = await login.LoginAsync(success.Profile);
        Check(postSaveFailure.Ok && postSaveFailure.Save?.Saved == true && postSaveFailure.FailedStage is null
            && login.Status.Stage == LoginStage.Done && File.Exists(authPath),
            "callback disposal and browser cleanup failures cannot turn committed authorization into login failure");
        Check(postSaveFailure.Warnings?.Any(message => message.Contains("网页档案保留未完成", StringComparison.Ordinal)) == true
            && postSaveFailure.Warnings.Any(message => message.Contains("会话结束处理未完成", StringComparison.Ordinal))
            && postSaveFailure.Warnings.Any(message => message.Contains("清理未确认", StringComparison.Ordinal)),
            "post-save retention disposal and cleanup failures remain separate warnings");
        Check(admissions == 4 && transport.Calls == 4, "no network verification is performed after saving login credentials");

        foreach (var (target, kind) in new[]
        {
            ("/auth/callback?state=fixture-state&code=fixture-code", CallbackKind.Code),
            ("/auth/callback?state=wrong&code=fixture-code", CallbackKind.StateMismatch),
            ("/cancel", CallbackKind.Cancelled)
        })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var callback = new LoopbackLoginCallback(listener, port, "fixture-state");
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            var page = client.GetStringAsync($"http://127.0.0.1:{port}" + target);
            LoginCallback received;
            try { received = await callback.WaitAsync(TimeSpan.FromSeconds(1), default); }
            finally { await callback.DisposeAsync(); }
            var text = await page;
            Check(received.Kind == kind, "local callback provides exact result and immediate disposal still returns full page");
            Check(!text.Contains("fixture-code", StringComparison.Ordinal) && !text.Contains("fixture-state", StringComparison.Ordinal),
                "callback page does not echo the authorization code or state");
            Check(kind == CallbackKind.Code ? text.Contains("此页面不代表账号已保存", StringComparison.Ordinal)
                : kind == CallbackKind.Cancelled ? text.Contains("登录已取消", StringComparison.Ordinal)
                : text.Contains("未收到有效授权回调", StringComparison.Ordinal), "callback page distinguishes pending credential save from rejected or cancelled callback");
        }
        // Exercise the accept/stop race with real loopback sockets. These are
        // fixed local lifecycle trials, not authorization requests or retries.
        for (var trial = 0; trial < 64; trial++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var callback = new LoopbackLoginCallback(listener, port, "close-race-state");
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            var page = client.GetStringAsync($"http://127.0.0.1:{port}/auth/callback?state=close-race-state&code=local-fixture-code");
            LoginCallback received;
            try { received = await callback.WaitAsync(TimeSpan.FromSeconds(5), default); }
            finally { await callback.DisposeAsync(); }
            var text = await page;
            if (received.Kind != CallbackKind.Code || !text.Contains("此页面不代表账号已保存", StringComparison.Ordinal))
                throw new InvalidOperationException("Immediate callback shutdown lost the completed result page");
        }
        Check(true, "64 immediate callback shutdown trials preserve results and complete without disposed-socket failures");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new
        { passed = true, checks = count, requests = transport.Calls, admissions, scope = "isolated fixtures and loopback only; no real OAuth, browser, account or proxy changes" }, new JsonSerializerOptions { WriteIndented = true }));
        return count;
    }

    private sealed class CompletionTransport(List<string> events) : IAccountTransport
    {
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken token)
        {
            Calls++; events.Add("token");
            if (Failure is { } failure) return Task.FromException<AccountHttpResponse>(failure);
            var id = "fixture." + OAuthPkce.Base64Url(Encoding.UTF8.GetBytes(new JsonObject
            { ["email"] = "account-b72f1d402c@example.invalid", ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = "fixture-completion-id" } }.ToJsonString())) + ".unsigned";
            var body = new JsonObject { ["id_token"] = id, ["access_token"] = "fixture-at", ["refresh_token"] = "fixture-rt" };
            return Task.FromResult(new AccountHttpResponse(200, new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString()))));
        }
    }
    private sealed class CompletionBrowser(List<string> events) : IProxyBoundLoginBrowser, IVerifiedProxyLoginBrowser, IPersistentLoginBrowser, IBrowserSessionCleanup
    {
        public string Proxy => "http://127.0.0.1:7897";
        public bool FailKeep { get; set; }
        public bool FailCleanup { get; set; }
        public Task PrepareAsync(string proxy, CancellationToken token) { events.Add("prepare"); return Task.CompletedTask; }
        public Task<BrowserLaunch> OpenAsync(string profile, Uri authorize, CancellationToken token)
        { events.Add("open"); return Task.FromResult(new BrowserLaunch("fixture-browser", "fixture-browser-profile", [])); }
        public Task<BrowserLaunch> OpenVerifiedAsync(string profile, Uri authorize, string proxy, EgressResult egress, CancellationToken token) => OpenAsync(profile, authorize, token);
        public Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(BrowserLaunch launch, CancellationToken token) => Task.FromResult<IReadOnlyList<BrowserIdentity>?>(null);
        public Task KeepAccountProfileAsync(string profile, BrowserLaunch launch, CancellationToken token)
            => FailKeep ? Task.FromException(new IOException("fixture retention failure")) : Task.CompletedTask;
        public Task<BrowserSessionCleanupResult> CleanupAsync(BrowserLaunch? launch, CancellationToken token)
        { events.Add("cleanup"); return Task.FromResult(new BrowserSessionCleanupResult(!FailCleanup, "fixture cleanup")); }
    }
    private sealed class CompletionCallbackFactory : ILoginCallbackFactory
    {
        public int Starts { get; private set; }
        public bool FailDispose { get; set; }
        public Task<ILoginCallback> StartAsync(string state, CancellationToken token)
        { Starts++; return Task.FromResult<ILoginCallback>(new CompletionCallback(FailDispose)); }
    }
    private sealed class CompletionCallback(bool failDispose) : ILoginCallback
    {
        public int Port => 1455;
        public bool Cancel() => false;
        public Task<LoginCallback> WaitAsync(TimeSpan duration, CancellationToken token) => Task.FromResult(new LoginCallback(CallbackKind.Code, "one-use-code"));
        public ValueTask DisposeAsync() => failDispose ? ValueTask.FromException(new IOException("fixture callback disposal failure")) : ValueTask.CompletedTask;
    }
}
