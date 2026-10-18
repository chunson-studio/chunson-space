using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

if (args.Length == 2 && args[0] == "--verify-login-completion")
{
    var checks = await LoginCompletionChecks.Run(args[1]);
    Console.WriteLine($"PASS {checks} isolated login completion checks; fake OAuth and loopback only; fixture root: {args[1]}");
    return 0;
}

if (args.Length == 4 && args[0] == "--verify-browser-timezones")
{
    await BrowserTimezoneVerification.RunAsync(args[1], args[2], args[3]);
    return 0;
}

if (args.Length == 5 && args[0] == "--inspect-renderer-egress")
{
    await RendererEgressProbe.RunAsync(args[1], args[2], args[3], args[4]);
    return 0;
}
if (args.Length == 5 && args[0] == "--inspect-browser-session")
{
    await BrowserSessionInspection.Run(args[1], args[2], args[3], args[4]);
    return 0;
}

if (args.Length == 3 && args[0] == "--verify-browser-persistence")
{
    await NativeBrowserOwnershipChecks.RunPersistence(args[1], args[2]);
    return 0;
}
if (args.Length == 3 && args[0] == "--verify-local-browser")
{
    await NativeBrowserOwnershipChecks.RunLocal(args[1], args[2]);
    return 0;
}
if (args.Length == 3 && args[0] == "--verify-installed-browser-owner")
{
    await NativeBrowserOwnershipChecks.Run(args[1], args[2]);
    return 0;
}
if (args.Length == 3 && args[0] == "--verify-installed-browser-preview")
{
    await NativeBrowserOwnershipChecks.RunPreview(args[1], args[2]);
    return 0;
}

var count = 0;
var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "账号-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Assert(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
async Task Expect(AccountError code, Func<Task> action, string label)
{
    try { await action(); throw new InvalidOperationException(label + ": missing expected failure"); }
    catch (AccountException error) { Assert(error.Code == code, label + ": actual " + error.Code); }
}
string Jwt(string account, string email = "account-cc6d96611c@example.invalid") => "fixture." + OAuthPkce.Base64Url(Encoding.UTF8.GetBytes(new JsonObject
{ ["email"] = email, ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = account } }.ToJsonString())) + ".unsigned";
JsonObject TokenResponse(string id, string access = "short-at", string refresh = "short-rt") => new()
{ ["id_token"] = Jwt(id), ["access_token"] = access, ["refresh_token"] = refresh, ["account_id"] = "wrong-response-field" };
try
{
    var clock = new FakeClock();
    var pending = new FakePending();
    var data = Path.Combine(root, "data");
    var home = Path.Combine(root, "home");
    var repository = new AccountRepository(data, home, pending);
    repository.Profiles.CreateBoundAccount("A", new("http://127.0.0.1:7897"));
    var pkce = OAuthPkce.Create();
    Assert(pkce.Verifier.Length == 86 && pkce.Challenge.Length == 43 && OAuthPkce.State().Length == 43, "PKCE entropy lengths");
    Assert(pkce.Challenge == OAuthPkce.Base64Url(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(pkce.Verifier))), "PKCE S256");
    Assert(OAuthPkce.Authorize(1455, pkce.Challenge, "state").Query.Contains("code_challenge_method=S256", StringComparison.Ordinal), "authorize parameters");
    Assert(OAuthPkce.Redirect(1457) == "http://127.0.0.1:1457/auth/callback", "loopback fixed ports");
    try { _ = OAuthPkce.Redirect(1456); throw new InvalidOperationException("random port allowed"); } catch (ArgumentOutOfRangeException) { count++; }
    Assert(LoopbackLoginCallback.Parse("/auth/callback?state=correct&code=c", "correct") == new LoginCallback(CallbackKind.Code, "c"), "valid callback");
    Assert(LoopbackLoginCallback.Parse("/auth/callback?state=correct" + OAuthPkce.StateSuffix + "&code=c", "correct").Kind == CallbackKind.Code, "onboarding suffix");
    Assert(LoopbackLoginCallback.Parse("/auth/callback?state=wrong&code=c", "correct").Kind == CallbackKind.StateMismatch, "state mismatch rejected");
    Assert(LoopbackLoginCallback.Parse("/auth/callback?state=correct&state=correct&code=c", "correct").Kind == CallbackKind.StateMismatch, "duplicate state rejected");
    Assert(LoopbackLoginCallback.Parse("/auth/callback?state=correct&error=denied", "correct").Kind == CallbackKind.ProviderError, "provider callback rejected");
    Assert(LoopbackLoginCallback.Parse("/auth/callback?state=correct", "correct").Kind == CallbackKind.MissingCode, "missing code rejected");
    Assert(LoopbackLoginCallback.Parse("/cancel", "correct").Kind == CallbackKind.Cancelled, "cancel route");
    var network = new FakeTransport();
    network.Json(200, TokenResponse("id-A"));
    var callbacks = new FakeCallbackFactory(clock);
    var browser = new FakeBrowser();
    var login = new LoginService(repository, network, callbacks, browser, clock);
    var loggedIn = await login.LoginAsync("A");
    Assert(loggedIn.Ok && login.Status.Stage == LoginStage.Done && !login.Status.Active, "login success status");
    Assert(AccountId(repository.ReadAuth("A")) == "id-A", "account ID comes from id_token");
    Assert(network.Requests.Count == 1 && network.Requests[0].RequireProxy && network.Requests[0].Proxy == "http://127.0.0.1:7897", "single code exchange uses bound proxy");
    Assert(Encoding.UTF8.GetString(network.Requests[0].Body!).Contains("grant_type=authorization_code", StringComparison.Ordinal) && network.Requests[0].ContentType == "application/x-www-form-urlencoded", "code request shape");
    Assert(!login.CanResumeBrowser && !await login.ResumeBrowserAsync(), "completed authorization cannot reopen a stale URL");
    var resumeStarts = callbacks.Starts; var resumeRequests = network.Requests.Count;
    callbacks.WaitsBeforeResult = 1;
    browser.OnObserve = () =>
    {
        Assert(login.CanResumeBrowser && login.ResumeBrowserAsync().GetAwaiter().GetResult(), "active authorization can explicitly resume its browser");
        Assert(browser.ResumeCount == 1 && callbacks.Starts == resumeStarts + 1 && network.Requests.Count == resumeRequests,
            "resuming reuses callback without token request or a new login");
        login.Cancel();
        Assert(!login.CanResumeBrowser, "cancel immediately disables continuation while the callback settles");
    };
    var resumedCancelled = await login.LoginAsync("A");
    Assert(resumedCancelled.Callback == CallbackKind.Cancelled && !await login.ResumeBrowserAsync(), "cancelled authorization cannot be resumed");
    browser.OnObserve = null; callbacks.WaitsBeforeResult = 0;
    var originalAuth = repository.ReadAuth("A").ToJsonString();
    network.Json(200, TokenResponse("other-account"));
    var mismatch = await login.LoginAsync("A");
    Assert(!mismatch.Ok && mismatch.Error == AccountError.IdentityMismatch && repository.ReadAuth("A").ToJsonString() == originalAuth, "different identity never overwrites");
    network.Json(500, new JsonObject { ["error"] = "code=one-use-code code_verifier=abc" });
    var beforeFailure = network.Requests.Count;
    var codeFailure = await login.LoginAsync("A");
    Assert(!codeFailure.Ok && network.Requests.Count == beforeFailure + 1 && !codeFailure.Message.Contains("one-use-code", StringComparison.Ordinal), "failed code exchange never retried or exposed");
    callbacks.Kind = CallbackKind.StateMismatch;
    var beforeCallbackFailure = network.Requests.Count;
    var badState = await login.LoginAsync("A");
    Assert(!badState.Ok && network.Requests.Count == beforeCallbackFailure, "bad callback sends no token request");
    callbacks.Kind = CallbackKind.Waiting; browser.Roots = [];
    var closed = await login.LoginAsync("A", TimeSpan.FromSeconds(20));
    Assert(!closed.Ok && closed.Callback == CallbackKind.BrowserClosed, "owned browser close cancels after grace");
    browser.Roots = null;
    var timedOut = await login.LoginAsync("A", TimeSpan.FromSeconds(7));
    Assert(!timedOut.Ok && timedOut.Callback == CallbackKind.Timeout, "unreadable ownership waits for timeout");
    browser.Roots = [new(123, 456, "fixture-chrome.exe", "fixture-profile")];
    callbacks.WaitsBeforeResult = 7200; callbacks.Kind = CallbackKind.Code;
    network.Json(200, TokenResponse("id-A"));
    var longWaitStart = clock.UtcNow; var longWaitRequests = network.Requests.Count;
    var lateAuthorization = await login.LoginAsync("A");
    Assert(lateAuthorization.Ok && clock.UtcNow - longWaitStart > TimeSpan.FromHours(2)
        && network.Requests.Count == longWaitRequests + 1, "default login still accepts a valid callback after two hours with one exchange");
    var longWaitAuth = repository.ReadAuth("A").ToJsonString();
    longWaitStart = clock.UtcNow; longWaitRequests = network.Requests.Count;
    callbacks.Kind = CallbackKind.Waiting;
    browser.OnObserve = () => { if (clock.UtcNow - longWaitStart >= TimeSpan.FromHours(2)) browser.Roots = []; };
    var closedAfterLongWait = await login.LoginAsync("A");
    Assert(closedAfterLongWait.Callback == CallbackKind.BrowserClosed && network.Requests.Count == longWaitRequests
        && repository.ReadAuth("A").ToJsonString() == longWaitAuth, "closing the login browser ends unlimited waiting without changing credentials or exchanging a code");
    browser.Roots = [new(123, 456, "fixture-chrome.exe", "fixture-profile")];
    longWaitStart = clock.UtcNow;
    browser.OnObserve = () => { if (clock.UtcNow - longWaitStart >= TimeSpan.FromHours(2)) login.Cancel(); };
    var cancelledAfterLongWait = await login.LoginAsync("A");
    Assert(cancelledAfterLongWait.Callback == CallbackKind.Cancelled && network.Requests.Count == longWaitRequests
        && repository.ReadAuth("A").ToJsonString() == longWaitAuth, "manual cancellation still ends unlimited waiting without saving credentials");
    browser.OnObserve = null; callbacks.WaitsBeforeResult = 0;
    callbacks.Kind = CallbackKind.Code;
    browser.OnOpen = () => Assert(login.Cancel(), "cancel browser-stage request");
    var cancelled = await login.LoginAsync("A");
    Assert(cancelled.Callback == CallbackKind.Cancelled, "cancel completes callback without exchange");
    browser.OnOpen = null;
    pending.IsPending = true;
    var pendingLogin = await login.LoginAsync("A");
    Assert(!pendingLogin.Ok && pendingLogin.Error == AccountError.Busy, "pending launch blocks new login");
    pending.IsPending = false;
    Directory.CreateDirectory(home);
    File.WriteAllText(Path.Combine(home, "auth.json"), repository.ReadAuth("A").ToJsonString());
    pending.IsPending = true;
    var savedGuarded = repository.SaveLogin("A", JwtClaims.ToAuth(TokenResponse("id-A", "new-at"), clock.UtcNow));
    Assert(savedGuarded.Saved && !savedGuarded.ActiveSynced && savedGuarded.Warnings.Count == 1, "pending sync guard preserves active auth");
    pending.IsPending = false;
    var saved = repository.SaveLogin("A", JwtClaims.ToAuth(TokenResponse("id-A", "synced-at"), clock.UtcNow));
    Assert(saved.ActiveSynced && Access(JsonNode.Parse(File.ReadAllText(Path.Combine(home, "auth.json")))!.AsObject()) == "synced-at", "same identity active credentials sync");
    var services = new AccountService(repository, network, clock);
    network.Json(200, new JsonObject { ["access_token"] = "refreshed-at" });
    await services.RefreshAsync("A");
    Assert(AccountJsonString(repository.ReadAuth("A")["tokens"]!.AsObject(), "refresh_token") == "short-rt", "refresh retains old refresh token when absent");
    var refreshRequest = network.Requests[^1];
    Assert(refreshRequest.ContentType == "application/json" && !Encoding.UTF8.GetString(refreshRequest.Body!).Contains("scope", StringComparison.Ordinal), "refresh JSON shape without scope");
    network.Json(400, new JsonObject { ["error"] = "short-rt refreshed-at" });
    try { await services.RefreshAsync("A"); throw new InvalidOperationException("error not thrown"); }
    catch (AccountException error) { Assert(!error.Message.Contains("short-rt", StringComparison.Ordinal) && !error.Message.Contains("refreshed-at", StringComparison.Ordinal), "short secrets redacted"); }
    network.Json(401, new JsonObject { ["error"] = "expired" });
    network.Json(200, new JsonObject { ["access_token"] = "quota-refreshed" });
    network.Json(200, new JsonObject
    {
        ["email"] = "account-cc6d96611c@example.invalid", ["plan_type"] = "plus", ["rate_limit"] = new JsonObject { ["allowed"] = true, ["primary_window"] = new JsonObject { ["used_percent"] = 20.5, ["limit_window_seconds"] = 18000, ["reset_after_seconds"] = 60 } },
        ["rate_limit_reset_credits"] = new JsonObject { ["available_count"] = 2 }
    });
    network.Json(200, new JsonObject { ["availableCount"] = 2, ["credits"] = new JsonArray(new JsonObject { ["expiresAt"] = clock.UtcNow.AddDays(1).ToString("O"), ["resetType"] = "weekly", ["status"] = "available" }), ["totalEarnedCount"] = 4 });
    var quotaStart = network.Requests.Count;
    var quota = await services.FetchQuotaAsync("A");
    Assert(quota.Refreshed && quota.Primary?.RemainingPercent == 79.5 && quota.ResetCredits.Count == 2 && quota.ResetCredits.TotalEarned == 4, "quota windows and camelCase reset details");
    Assert(network.Requests.Count == quotaStart + 4 && network.Requests[^1].Method == HttpMethod.Get && network.Requests[^1].Url == AccountEndpoints.ResetCredits, "read-only detail and single refresh retry");
    Assert(repository.ReadQuotaCache("A")?["ok"]?.GetValue<bool>() == true, "quota cache atomically persisted");
    network.Json(200, new JsonObject { ["rate_limit"] = new JsonObject { ["primary_window"] = new JsonObject { ["reset_at"] = 123 }, ["secondary_window"] = new JsonObject { ["used_percent"] = 0 } } });
    var missingWindow = await services.FetchQuotaAsync("A");
    Assert(missingWindow.Primary is null && missingWindow.Secondary?.RemainingPercent == 100, "unknown window stays unknown and valid zero means full remaining");
    network.Json(401, new()); network.Json(200, new JsonObject { ["access_token"] = "retry-at" }); network.Json(401, new());
    var repeatedStart = network.Requests.Count;
    await Expect(AccountError.Http, async () => await services.FetchQuotaAsync("A"), "second 401 stops");
    Assert(network.Requests.Count == repeatedStart + 3, "no refresh loop");
    // Split-control semantics: these calls remain independent, including frozen accounts.
    var beforeSplitAuth = repository.ReadAuth("A");
    var splitStart = network.Requests.Count;
    repository.Profiles.SetTestsFrozen("A", true);
    network.Json(200, new JsonObject { ["plan_type"] = "plus" });
    var directQuota = await services.FetchQuotaAsync("A");
    Assert(!directQuota.Refreshed && network.Requests.Count == splitStart + 1 && network.Requests[^1].Url == AccountEndpoints.Usage,
        "primary usage with valid access does not refresh or test frozen account");
    splitStart = network.Requests.Count;
    network.Json(200, new JsonObject { ["access_token"] = "split-refreshed" });
    await services.RefreshAsync("A");
    Assert(network.Requests.Count == splitStart + 1 && network.Requests[^1].Method == HttpMethod.Post && network.Requests[^1].Url == AccountEndpoints.Token,
        "secondary refresh sends only token POST while frozen");
    repository.Profiles.SetTestsFrozen("A", false);
    var splitAuth = repository.ReadAuth("A");
    foreach (var missing in new[] { "access_token", "account_id" })
    {
        var brokenAuth = splitAuth.DeepClone().AsObject(); brokenAuth["tokens"]!.AsObject().Remove(missing);
        repository.Profiles.SaveAccount("A", brokenAuth, overwrite: true);
        splitStart = network.Requests.Count;
        await Expect(AccountError.MissingCredentials, async () => await services.FetchQuotaAsync("A"), "usage refuses missing " + missing);
        Assert(network.Requests.Count == splitStart, "missing " + missing + " never starts token or browser fallback");
    }
    var noRefresh = splitAuth.DeepClone().AsObject(); noRefresh["tokens"]!.AsObject().Remove("refresh_token");
    repository.Profiles.SaveAccount("A", noRefresh, overwrite: true);
    splitStart = network.Requests.Count;
    await Expect(AccountError.MissingCredentials, async () => await services.RefreshAsync("A"), "missing refresh token stays explicit");
    Assert(network.Requests.Count == splitStart, "missing refresh token makes no request");
    repository.Profiles.SaveAccount("A", splitAuth, overwrite: true);
    foreach (var failure in new[] { "invalid-grant", "malformed", "missing-access", "identity-mismatch", "network" })
    {
        var preserved = File.ReadAllBytes(Path.Combine(repository.AccountPath("A"), "auth.json"));
        if (failure == "invalid-grant") network.Json(401, new JsonObject { ["error"] = "invalid_grant" });
        else if (failure == "malformed") network.Text(200, "{broken");
        else if (failure == "missing-access") network.Json(200, new());
        else if (failure == "identity-mismatch") network.Json(200, TokenResponse("wrong-identity"));
        else network.Stream(200, () => throw new IOException("fixture network failure"));
        var expected = failure == "invalid-grant" ? AccountError.Http : failure == "identity-mismatch" ? AccountError.IdentityMismatch
            : failure == "network" ? AccountError.Network : AccountError.CorruptResponse;
        await Expect(expected, async () => await services.RefreshAsync("A"), "refresh bounded failure " + failure);
        Assert(File.ReadAllBytes(Path.Combine(repository.AccountPath("A"), "auth.json")).SequenceEqual(preserved), "failed refresh preserves credentials " + failure);
    }
    // Optional detail has the same one-refresh budget as usage, and keeps available count on failure.
    JsonObject QuotaWithCredits() => new() { ["rate_limit_reset_credits"] = new JsonObject { ["available_count"] = 2 } };
    network.Json(200, QuotaWithCredits()); network.Json(401, new());
    network.Json(200, new JsonObject { ["access_token"] = "detail-refreshed" }); network.Json(200, new());
    splitStart = network.Requests.Count;
    var refreshedDetail = await services.FetchQuotaAsync("A");
    Assert(refreshedDetail.Refreshed && network.Requests.Skip(splitStart).Select(r => r.Url).SequenceEqual(new[] {
        AccountEndpoints.Usage, AccountEndpoints.ResetCredits, AccountEndpoints.Token, AccountEndpoints.ResetCredits }), "reset detail 401 uses one refresh without rereading usage");
    network.Json(401, new()); network.Json(200, new JsonObject { ["access_token"] = "rotated-retained" });
    network.Json(200, QuotaWithCredits()); network.Json(401, new());
    splitStart = network.Requests.Count;
    var unavailableDetail = await services.FetchQuotaAsync("A");
    Assert(unavailableDetail.Refreshed && unavailableDetail.ResetCredits.Count == 2 && network.Requests.Count == splitStart + 4,
        "optional detail failure retains count and cannot spend a second refresh");
    network.Json(401, new()); network.Json(200, new JsonObject { ["access_token"] = "rotated-before-failure" }); network.Text(200, "{broken");
    await Expect(AccountError.CorruptResponse, async () => await services.FetchQuotaAsync("A"), "malformed usage after successful refresh fails");
    Assert(Access(repository.ReadAuth("A")) == "rotated-before-failure", "failed usage never rolls back successfully rotated authorization");
    repository.SaveLogin("A", beforeSplitAuth);
    network.Text(200, "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Ciallo～(∠・ω< )⌒☆\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"model\":\"resolved-model\",\"usage\":{\"cached_input_tokens\":4096}}}\n\ndata: [DONE]\n\n");
    var connected = await services.TestAccountAsync("A");
    Assert(connected.Status == "good" && connected.Strict && connected.Usage.CachedInputTokens == 4096 && connected.Model == "resolved-model", "SSE completed response retains factual usage only");
    network.Text(200, "data: {\"type\":\"response.output_text.delta\",\"delta\":\"ciallo\"}\n\ndata: [DONE]\n\n");
    await Expect(AccountError.Network, async () => await services.TestAccountAsync("A"), "SSE without completed fails");
    network.Text(200, "data: {\"type\":\"response.failed\"}\n\n");
    await Expect(AccountError.Network, async () => await services.TestAccountAsync("A"), "SSE failure fails");
    repository.Profiles.SaveProvider("P", "https://fixture.invalid/v1", "k");
    var provider = new ProviderService(repository, network, clock);
    network.Json(200, new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = "model-one" }, new JsonObject { ["id"] = "model-two" }) });
    var checkedProvider = await provider.CheckAsync("P");
    Assert(checkedProvider.ModelCount == 2 && !network.Requests[^1].RequireProxy && network.Requests[^1].Proxy == "", "provider models explicit direct route");
    network.Json(403, new JsonObject { ["error"] = "API Key k rejected" });
    try { await provider.CheckAsync("P"); throw new InvalidOperationException("provider fail accepted"); }
    catch (AccountException error) { Assert(!error.Message.Contains("Key k", StringComparison.Ordinal), "single-character provider key redacted"); }
    var egress = new EgressService(repository, network);
    network.Json(200, new JsonObject { ["success"] = true, ["ip"] = "192.0.2.1", ["country_code"] = "JP", ["city"] = "Fixture", ["timezone"] = new JsonObject { ["id"] = "Asia/Tokyo" } });
    var defaults = await egress.ApplyDefaultsAsync("A");
    Assert(defaults.Changed.Count == 2 && defaults.Environment is { Timezone: "Asia/Tokyo", Locale: "ja-JP", LocaleAuto: true }, "exit defaults fill empty fields");
    repository.Profiles.UpdateEnvironment("A", timezone: "America/Chicago", locale: "en-US");
    var manualStart = network.Requests.Count;
    await egress.ApplyDefaultsAsync("A");
    Assert(network.Requests.Count == manualStart && repository.Profiles.ReadEnvironment("A").Locale == "en-US", "manual defaults never overridden or reprobed");
    repository.Profiles.UpdateEnvironment("A", timezone: "");
    network.Text(200, "ip=192.0.2.1\nloc=US\ncolo=FIX\n");
    var candidates = await egress.SuggestTimezoneAsync("A");
    Assert(candidates.Candidates.Count == 6 && repository.Profiles.ReadEnvironment("A").Timezone == "", "multizone suggestions do not write");
    Assert(CountryEnvironmentRules.Locale("unknown") == "" && CountryEnvironmentRules.Timezones("unknown").Count == 0, "unknown country not guessed");
    Assert(CountryNames.Display("US") == "美国" && CountryNames.Display("jp") == "日本" && CountryNames.Display("SG") == "新加坡"
        && CountryNames.Display("DE") == "德国" && CountryNames.Display("CH") == "瑞士" && CountryNames.Display("ES") == "西班牙", "country display uses Chinese names independent of OS language");
    Assert(CountryNames.Display("?", "Reported country") == "Reported country" && CountryNames.Display(null) == "国家未知", "unknown country remains explicit without guessing or changing locale");
    repository.Profiles.UpdateEnvironment("A", proxy: "");
    var noProxyStart = network.Requests.Count;
    await Expect(AccountError.ExitUnavailable, async () => await services.FetchQuotaAsync("A"), "quota without proxy refused");
    await Expect(AccountError.ExitUnavailable, async () => await egress.ProbeAsync("A"), "egress without proxy refused");
    Assert(network.Requests.Count == noProxyStart, "no-proxy makes zero network calls");
    repository.Profiles.UpdateEnvironment("A", proxy: "socks5://127.0.0.1:7897");
    await Expect(AccountError.ExitUnavailable, async () => await services.RefreshAsync("A"), "socks account HTTP refused");
    repository.Profiles.UpdateEnvironment("A", proxy: "http://127.0.0.1:7897");
    var transportPolicy = false;
    var directPolicy = false;
    var realWithFakeHandler = new HttpAccountTransport(handler =>
    {
        if (handler.UseProxy) transportPolicy = !handler.AllowAutoRedirect && handler.Proxy?.GetProxy(AccountEndpoints.Usage)?.AbsoluteUri == "http://127.0.0.1:7897/" && !handler.Proxy.IsBypassed(new Uri("http://127.0.0.1/"));
        else directPolicy = !handler.AllowAutoRedirect;
        return new FakeHttpHandler();
    });
    await using (var response = await realWithFakeHandler.SendAsync(new(HttpMethod.Get, AccountEndpoints.Usage, "http://127.0.0.1:7897", true, new Dictionary<string, string>()), default)) Assert(response.Status == 302, "redirect exposed without following");
    await using (var response = await realWithFakeHandler.SendAsync(new(HttpMethod.Get, new("https://fixture.invalid/models"), "", false, new Dictionary<string, string>()), default)) Assert(response.Status == 302, "direct provider redirect not followed");
    Assert(transportPolicy && directPolicy, "explicit proxy/direct and no automatic redirect policies");
    var chromeArgs = ChromeArguments.Build(Path.Combine(root, "browser"), new("http://127.0.0.1:7897", "Asia/Tokyo", "ja-JP"), new("https://fixture.invalid/authorize"), true);
    Assert(chromeArgs.Contains("--disable-quic") && chromeArgs.Contains("--webrtc-ip-handling-policy=disable_non_proxied_udp") && chromeArgs.Contains("--lang=ja-JP") && chromeArgs[^1] == "about:blank", "browser proxy locale and deferred launch args");
    Assert(ChromeArguments.Build(Path.Combine(root, "browser"), new("socks5://127.0.0.1:7897"), new("https://fixture.invalid/home"), false).Contains("--proxy-server=socks5://127.0.0.1:7897"), "standalone browser retains SOCKS5 support while account HTTP refuses it");
    Assert(ChromeArguments.RootProfile(chromeArgs) == Path.GetFullPath(Path.Combine(root, "browser")) && ChromeArguments.RootProfile(chromeArgs.Concat(["--type=renderer"]).ToArray()) is null, "browser root profile excludes child launcher identities");
    Assert(ChromeArguments.RootProfile(chromeArgs.Concat(["--user-data-dir=other"]).ToArray()) is null, "ambiguous profile arguments rejected");
    network.Json(200, TokenResponse("id-A"));
    browser.ThrowOnObserve = true;
    Assert((await login.LoginAsync("A")).Ok, "valid code takes priority over unavailable browser observation");
    browser.ThrowOnObserve = false;
    browser.Warnings = ["fixture CDP warning"];
    network.Json(200, TokenResponse("id-A"));
    Assert((await login.LoginAsync("A")).Warnings?.Contains("fixture CDP warning") == true, "browser warnings retained on successful login");
    callbacks.Kind = CallbackKind.StateMismatch;
    Assert((await login.LoginAsync("A")).Warnings?.Contains("fixture CDP warning") == true, "browser warnings retained on failed login");
    callbacks.Kind = CallbackKind.Code; browser.Warnings = [];
    pending.IsPending = true;
    await Expect(AccountError.Busy, () => { provider.Update("P", "https://updated.invalid"); return Task.CompletedTask; }, "provider pending edit guard");
    pending.IsPending = false;
    Assert(!provider.Update("P", "https://updated.invalid", model: "edited").ActiveUpdated, "inactive provider edit uses new edit API");
    var sharedNetwork = new AccountNetwork(network);
    network.Stream(200, () => new SlowStream());
    await Expect(AccountError.Timeout, async () => await sharedNetwork.JsonAsync(new(HttpMethod.Get, AccountEndpoints.Usage, "http://127.0.0.1:7897", true, new Dictionary<string, string>()), TimeSpan.FromMilliseconds(20), default), "slow response body classified as timeout");
    network.Stream(200, () => new SlowStream());
    using (var cancelledToken = new CancellationTokenSource())
    {
        cancelledToken.Cancel();
        try { await sharedNetwork.JsonAsync(new(HttpMethod.Get, AccountEndpoints.Usage, "http://127.0.0.1:7897", true, new Dictionary<string, string>()), TimeSpan.FromSeconds(1), cancelledToken.Token); throw new InvalidOperationException("user cancel ignored"); }
        catch (OperationCanceledException) { count++; }
    }
    network.DropQueued();
    network.Text(200, "data: []\n\n");
    await Expect(AccountError.CorruptResponse, async () => await services.TestAccountAsync("A"), "non-object SSE is typed failure");
    network.Stream(200, () => new DisconnectAfterPayload("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"ciallo\"}]}]}}\n\n"));
    await Expect(AccountError.Network, async () => await services.TestAccountAsync("A"), "disconnect after completed without EOF remains inconclusive");
    network.Stream(200, () => new DisconnectAfterPayload("data: {\"type\":\"response.output_text.delta\",\"delta\":\"ciallo\"}\n\n"));
    await Expect(AccountError.Network, async () => await services.TestAccountAsync("A"), "SSE disconnect before completion fails");
    network.Text(200, "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"incomplete\"}}\n\n");
    await Expect(AccountError.Network, async () => await services.TestAccountAsync("A"), "incomplete completed payload rejected");
    network.Text(200, "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n");
    await Expect(AccountError.CorruptResponse, async () => await services.TestAccountAsync("A"), "empty completed output rejected");
    repository.Profiles.UpdateEnvironment("A", timezone: "Asia/Tokyo", locale: "ja-JP");
    var chrome = new FakeChrome();
    var cdp = new FakeCdpFactory();
    await using (var realBrowserWithFakes = new ChromeLoginBrowser(repository, chrome, cdp, clock, _ => false))
    {
        var opened = await realBrowserWithFakes.OpenAsync("A", new("https://fixture.invalid/authorize"), default);
        Assert(opened.Warnings.Count == 0 && chrome.Starts.Count == 1 && chrome.Starts[0][^1] == "about:blank", "browser CDP production flow starts deferred");
        var commands = cdp.Connection.Calls.Select(call => call.Method).ToArray();
        Assert(Array.IndexOf(commands, "Emulation.setTimezoneOverride") < Array.IndexOf(commands, "Page.navigate"), "timezone set before first real navigation");
        Assert(cdp.Connection.Calls.Any(call => call.Session == "future" && call.Method == "Emulation.setTimezoneOverride"), "future pages get timezone keeper");
    }
    chrome.Starts.Clear(); cdp.FailTimezone = true;
    await using (var failedCdpBrowser = new ChromeLoginBrowser(repository, chrome, cdp, clock, _ => false))
    {
        var opened = await failedCdpBrowser.OpenAsync("A", new("https://fixture.invalid/authorize"), default);
        Assert(opened.Warnings.Count == 1 && chrome.Starts.Count == 2 && chrome.Starts[1][^1] == "https://fixture.invalid/authorize", "failed CDP before page opens falls back once");
    }
    chrome.Starts.Clear(); cdp.FailTimezone = false; cdp.MismatchedTimezone = true;
    await using (var mismatchedCdpBrowser = new ChromeLoginBrowser(repository, chrome, cdp, clock, _ => false))
    {
        var opened = await mismatchedCdpBrowser.OpenAsync("A", new("https://fixture.invalid/authorize"), default);
        Assert(opened.Warnings.Count == 1 && chrome.Starts.Count == 1, "opened page with unconfirmed timezone never opens duplicate");
    }
    chrome.Starts.Clear();
    await using (var alignedBrowser = new ChromeLoginBrowser(repository, chrome, cdp, clock, _ => true))
    {
        await alignedBrowser.OpenAsync("A", new("https://fixture.invalid/authorize"), default);
        Assert(chrome.Starts.Count == 1 && !chrome.Starts[0].Contains("--remote-debugging-port=0"), "already aligned system timezone omits CDP");
    }
    var coordinatedChrome = new FakeChrome { RootsAppearAfterStart = true };
    var coordinated = new FakeCoordinator();
    await using (var coordinatedBrowser = new ChromeLoginBrowser(repository, coordinatedChrome, cdp, clock, _ => true, coordinated))
    {
        await coordinatedBrowser.OpenAsync("A", new("https://fixture.invalid/home"), default);
        Assert(coordinated.Lease.Tracked.Count == 1 && coordinated.Lease.Disposed && coordinated.Lease.TrackedBeforeDispose, "owned launch lease covers root registration before disposal");
        await coordinatedBrowser.OpenAsync("A", new("https://fixture.invalid/home"), default);
        Assert(coordinated.Lease.Tracked.Count == 0, "existing external or previously opened root never newly claimed");
    }
    var externalCoordinator = new FakeCoordinator();
    await using (var reusedExternal = new ChromeLoginBrowser(repository, new FakeChrome(), cdp, clock, _ => true, externalCoordinator))
    {
        var opened = await reusedExternal.OpenAsync("A", new("https://fixture.invalid/home"), default);
        Assert(externalCoordinator.BeginCount == 0 && opened.Warnings.Count > 0 && opened.LaunchLease is null, "external existing root is not claimed and never gets system timezone coordinator");
    }
    var failedTrackCoordinator = new FakeCoordinator { WarnOnTrack = true };
    await using (var trackWarningsBrowser = new ChromeLoginBrowser(repository, new FakeChrome { RootsAppearAfterStart = true }, cdp, clock, _ => true, failedTrackCoordinator))
    {
        var opened = await trackWarningsBrowser.OpenAsync("A", new("https://fixture.invalid/home"), default);
        Assert(opened.Warnings.Contains("fixture ownership unconfirmed"), "ownership warnings appended during TrackRoot reach returned browser result");
    }
    count += await AccountCliChecks.Run(Path.Combine(root, "cli"));
    count += await FingerprintAccountChecks.Run(Path.Combine(root, "fingerprint"));
    count += await NewAccountLoginChecks.Run(Path.Combine(root, "new-login"));
    count += LocalAccountImportChecks.Run(Path.Combine(root, "local-import"));
    count += await AccountPreparationChecks.Run(Path.Combine(root, "preparation"));
    count += await RegionEgressChecks.Run(Path.Combine(root, "region-egress"));
    count += await StrictEgressChecks.Run(Path.Combine(root, "strict-egress"));
    count += await StrictBrowserChecks.Run(Path.Combine(root, "strict-browser"));
    count += await PersistentBrowserChecks.Run(Path.Combine(root, "persistent-browser"));
    count += await TwoStageLoginChecks.Run(Path.Combine(root, "two-stage-login"));
    count += await CialloChecks.Run(Path.Combine(root, "ciallo"));
    count += await AccountEvidenceChecks.Run(Path.Combine(root, "account-evidence"));
    count += EnglishPromptChecks.Run(Path.Combine(root, "english-prompts"));
    count += await SafetyBufferingChecks.Run(Path.Combine(root, "safety-buffering"));
    count += await ResponsesFailureChecks.Run(Path.Combine(root, "responses-failures"));
    count += await BrowserTimezoneTargetChecks.Run();
    count += await VisualResponsesChecks.Run(Path.Combine(root, "visual-responses"));
    count += await TransportFailureChecks.Run(Path.Combine(root, "transport-failures"));
    count += await LoginCompletionChecks.Run(Path.Combine(root, "login-completion"));
    Console.WriteLine($"PASS {count} isolated account checks; fake network only; fixture root: {root}");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine($"FAIL after {count} checks: {error}; fixture root: {root}"); return 1; }

static string AccountJsonString(JsonObject value, string key) => value[key]?.GetValue<string>() ?? "";
static string AccountId(JsonObject auth) => AccountJsonString(auth["tokens"]!.AsObject(), "account_id");
static string Access(JsonObject auth) => AccountJsonString(auth["tokens"]!.AsObject(), "access_token");

sealed class FakeClock : IAccountClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan duration) => UtcNow += duration;
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Advance(duration); return Task.CompletedTask; }
}
sealed class FakePending : IPendingAccountGuard { public bool IsPending { get; set; } }
sealed class FakeTransport : IAccountTransport
{
    private readonly Queue<Func<AccountHttpResponse>> responses = new();
    public List<AccountHttpRequest> Requests { get; } = [];
    public void Json(int status, JsonObject payload) => Text(status, payload.ToJsonString());
    public void Text(int status, string content) => responses.Enqueue(() => new(status, new MemoryStream(Encoding.UTF8.GetBytes(content))));
    public void Stream(int status, Func<Stream> content) => responses.Enqueue(() => new(status, content()));
    public void DropQueued() => responses.Clear();
    public Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); if (responses.Count == 0) throw new InvalidOperationException("Unexpected fake request"); return Task.FromResult(responses.Dequeue()()); }
}
sealed class FakeCallbackFactory(FakeClock clock) : ILoginCallbackFactory
{
    public int Starts { get; private set; }
    public List<string> States { get; } = [];
    public int WaitsBeforeResult { get; set; }
    public CallbackKind Kind { get; set; } = CallbackKind.Code;
    public Task<ILoginCallback> StartAsync(string state, CancellationToken cancellationToken) { Starts++; States.Add(state); return Task.FromResult<ILoginCallback>(new FakeCallback(clock, Kind, WaitsBeforeResult)); }
}
sealed class FakeCallback(FakeClock clock, CallbackKind kind, int waitsBeforeResult) : ILoginCallback
{
    private bool cancelled;
    private int waits;
    public int Port => 1455;
    public bool Cancel() { if (cancelled) return false; cancelled = true; return true; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<LoginCallback> WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); clock.Advance(duration); return Task.FromResult(cancelled ? new(CallbackKind.Cancelled) : waits++ < waitsBeforeResult ? new(CallbackKind.Waiting) : new LoginCallback(kind, kind == CallbackKind.Code ? "one-use-code" : null)); }
}
sealed class FakeBrowser : ILoginBrowser, IResumableLoginBrowser
{
    public int ResumeCount { get; private set; }
    public Task ResumeAsync(BrowserLaunch launch, CancellationToken token)
    { token.ThrowIfCancellationRequested(); ResumeCount++; return Task.CompletedTask; }
    public IReadOnlyList<string> Warnings { get; set; } = [];
    public IReadOnlyList<BrowserIdentity>? Roots { get; set; } = [new(123, 456, "fixture-chrome.exe", "fixture-profile")];
    public Action? OnOpen { get; set; }
    public Action? OnObserve { get; set; }
    public bool ThrowOnObserve { get; set; }
    public Task<BrowserLaunch> OpenAsync(string profileName, Uri authorizeUrl, CancellationToken cancellationToken)
    { OnOpen?.Invoke(); return Task.FromResult(new BrowserLaunch("fixture-chrome.exe", "fixture-profile", Warnings)); }
    public Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(BrowserLaunch launch, CancellationToken cancellationToken)
    { OnObserve?.Invoke(); return ThrowOnObserve ? throw new IOException("fixture ownership unreadable") : Task.FromResult(Roots); }
}
sealed class FakeHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new StringContent("fixture redirect"), Headers = { Location = new("https://never-requested.invalid/") } });
}
sealed class SlowStream : MemoryStream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
}
sealed class DisconnectAfterPayload(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Position == Length ? ValueTask.FromException<int>(new IOException("fixture disconnect")) : base.ReadAsync(buffer, cancellationToken);
}
sealed class FakeChrome : IChromeSystem
{
    private bool started;
    public bool RootsAppearAfterStart { get; init; }
    public List<IReadOnlyList<string>> Starts { get; } = [];
    public string? FindChrome() => "C:\\fixture\\chrome.exe";
    public Task StartAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Starts.Add(arguments);
        started = true;
        var profile = ChromeArguments.RootProfile(arguments)!;
        Directory.CreateDirectory(profile); File.WriteAllText(Path.Combine(profile, "DevToolsActivePort"), "12345\n/devtools/browser/fixture-id\n");
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(string executable, string profileDirectory, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<BrowserIdentity>?>(RootsAppearAfterStart && !started ? [] : [new(123, 456, executable, profileDirectory)]);
}
sealed class FakeCoordinator : IChromeLaunchCoordinator
{
    public bool WarnOnTrack { get; init; }
    public int BeginCount { get; private set; }
    public FakeLease Lease { get; private set; } = new();
    public Task<IChromeLaunchLease> BeginAsync(string profileName, EnvironmentSettings environment, CancellationToken cancellationToken)
    { BeginCount++; Lease = new() { WarnOnTrack = WarnOnTrack }; return Task.FromResult<IChromeLaunchLease>(Lease); }
}
sealed class FakeLease : IChromeLaunchLease
{
    private readonly List<string> warnings = [];
    public bool WarnOnTrack { get; init; }
    public bool Disposed { get; private set; }
    public bool TrackedBeforeDispose { get; private set; }
    public List<BrowserIdentity> Tracked { get; } = [];
    public IReadOnlyList<string> Warnings => warnings;
    public void TrackRoot(BrowserIdentity identity) { Tracked.Add(identity); TrackedBeforeDispose = !Disposed; if (WarnOnTrack) warnings.Add("fixture ownership unconfirmed"); }
    public Task FailureAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
sealed class FakeCdpFactory : ICdpConnectionFactory
{
    public FakeCdp Connection { get; private set; } = new();
    public bool FailTimezone { get; set; }
    public bool MismatchedTimezone { get; set; }
    public Task<ICdpConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    { Connection = new() { FailTimezone = FailTimezone, MismatchedTimezone = MismatchedTimezone }; return Task.FromResult<ICdpConnection>(Connection); }
}
sealed class FakeCdp : ICdpConnection
{
    public bool FailTimezone { get; init; }
    public bool MismatchedTimezone { get; init; }
    public List<(string Method, string? Session)> Calls { get; } = [];
    public Task<JsonObject> CallAsync(string method, JsonObject parameters, string? sessionId, CancellationToken cancellationToken)
    {
        Calls.Add((method, sessionId));
        if (FailTimezone && method == "Emulation.setTimezoneOverride") throw new AccountException(AccountError.Browser, "fixture rejected timezone");
        return Task.FromResult(method switch
        {
            "Target.createTarget" => new JsonObject { ["targetId"] = "target" },
            "Target.attachToTarget" => new JsonObject { ["sessionId"] = "session" },
            "Runtime.evaluate" => new JsonObject { ["result"] = new JsonObject { ["value"] = new JsonObject { ["timezone"] = MismatchedTimezone ? "UTC" : "Asia/Tokyo", ["expectedTimezone"] = "Asia/Tokyo", ["language"] = "en-US" }.ToJsonString() } },
            _ => new JsonObject()
        });
    }
    public async IAsyncEnumerable<JsonObject> Events([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new JsonObject { ["method"] = "Target.attachedToTarget", ["params"] = new JsonObject { ["sessionId"] = "future", ["targetInfo"] = new JsonObject { ["type"] = "page" } } };
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
