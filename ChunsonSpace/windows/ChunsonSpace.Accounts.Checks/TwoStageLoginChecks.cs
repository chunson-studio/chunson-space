using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

internal static class TwoStageLoginChecks
{
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); count++; Console.WriteLine("PASS " + name); }
        const string proxy = "http://127.0.0.1:7890";
        var repository = new AccountRepository(root);
        var chrome = new PersistentBrowserChecks.ProfileChrome();
        var transport = new FakeTransport(); var clock = new FakeClock(); var callbacks = new FakeCallbackFactory(clock);
        var authToken = "fixture." + OAuthPkce.Base64Url(Encoding.UTF8.GetBytes(new JsonObject { ["email"] = "account-35d1faaa62@example.invalid",
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = "two-step-fixture" } }.ToJsonString())) + ".unsigned";
        void QueueToken() => transport.Json(200, new JsonObject { ["id_token"] = authToken, ["access_token"] = "fixture-at", ["refresh_token"] = "fixture-rt" });
        LoginService Service(ILoginBrowser browser) => new(repository, transport, callbacks, browser, clock,
            verifiedAdmission: (_, _) => Task.FromResult(new EgressResult(true, "203.0.113.9")));
        async Task Until(Func<bool> condition, Task<LoginResult> operation)
        {
            for (var attempt = 0; attempt < 400 && !condition() && !operation.IsCompleted; attempt++) await Task.Delay(10);
            if (!condition()) throw new InvalidOperationException("two-stage wait failed: " + (operation.IsCompleted ? (await operation).Message : "pending"));
        }
        await using var onboarding = new OnboardingProxyBrowser(root, proxy, chrome, installedBrowser: true);
        var login = Service(onboarding);
        var pending = login.LoginNewAsync(proxy, onboarding, prepareAccount: true);
        await Until(() => login.Status.Stage == LoginStage.AccountPreparation, pending);
        var preparationProfile = chrome.Roots.Keys.Single();
        File.WriteAllText(Path.Combine(preparationProfile, "fixture-private-cache"), "fixture only");
        var preparation = File.ReadAllText(Path.Combine(preparationProfile, "preparing.html"));
        Check(preparation.Contains("https://chatgpt.com/auth/login") && !preparation.Contains("oauth/authorize"), "first step opens only normal sign-in entry for Forgot password");
        Check(callbacks.Starts == 0 && transport.Requests.Count == 0 && repository.Profiles.ListAccounts().Count == 0,
            "password reset stage creates no callback, token request or saved account");
        Check(!login.CanBeginAuthorization && !login.BeginAuthorization() && !login.CanResumeBrowser && !await login.ResumeBrowserAsync(), "open preparation browser cannot begin or resume OAuth");
        chrome.UnknownWindows = true;
        await Task.Delay(1100);
        Check(!pending.IsCompleted && !login.CanBeginAuthorization && Directory.Exists(preparationProfile), "unknown window state never permits deletion or authorization");
        chrome.UnknownWindows = false;
        // The window was observed before manual close. Its background root remains alive.
        chrome.HiddenProfiles.Add(preparationProfile);
        await Until(() => login.CanBeginAuthorization, pending);
        Check(!Directory.Exists(preparationProfile) && !chrome.Roots.ContainsKey(preparationProfile) && chrome.Closes > 0,
            "manual window close stops owned background process and destroys all first-stage data");
        Check(callbacks.Starts == 0 && transport.Requests.Count == 0 && !pending.IsCompleted, "cleaned preparation still waits for explicit second-step click");
        QueueToken();
        Check(login.BeginAuthorization() && !login.BeginAuthorization(), "explicit second step is consumed exactly once");
        var first = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        var profile = new AccountBrowserProfiles(root).Read(first.Profile)!;
        var firstState = callbacks.States.Single();
        Check(repository.Profiles.ReadAccountMarks(first.Profile) is { Recovered: false, RecoverySource: "", Quality: AccountQuality.Unmarked }, "ordinary prepared authorization does not infer classification recovery");
        repository.Profiles.SetAccountMarks(first.Profile, AccountQuality.SuspectedDegraded, true, false);
        Check(first.Ok && callbacks.Starts == 1 && transport.Requests.Count == 1, "second step creates one fresh callback and one token exchange");
        Check(profile != preparationProfile && chrome.StartedRoots == 2 && Directory.Exists(profile) && chrome.Roots.Count == 1,
            "successful authorization retains a separate fresh profile without first-stage cache");
        Check(transport.Requests[0].Proxy == proxy && chrome.LaunchArguments.Last().Contains("--proxy-server=" + proxy), "authorization and token exchange preserve selected account route");
        await using var browser = new ChromeLoginBrowser(repository, chrome, new CdpConnectionFactory(), installedBrowser: true);
        var reauth = Service(browser);
        var second = reauth.LoginAsync(first.Profile, prepareAccount: true);
        await Until(() => reauth.Status.Stage == LoginStage.AccountPreparation, second);
        var temporary = chrome.Roots.Keys.Single(path => path != profile);
        Check(new AccountBrowserProfiles(root).Read(first.Profile) == profile && callbacks.Starts == 1, "existing account preparation is unbound and never changes its saved browser");
        chrome.Roots.Remove(temporary);
        await Until(() => reauth.CanBeginAuthorization, second);
        Check(!Directory.Exists(temporary) && Directory.Exists(profile) && chrome.Roots.ContainsKey(profile), "closed temporary preparation preserves other account browser and credentials");
        QueueToken(); reauth.BeginAuthorization();
        Check((await second.WaitAsync(TimeSpan.FromSeconds(5))).Ok && callbacks.States.Count == 2 && callbacks.States[1] != firstState, "reauthorization generates a new OAuth state after cleanup");
        Check(repository.Profiles.ReadAccountMarks(first.Profile) is { Recovered: false, Quality: AccountQuality.SuspectedDegraded, Problem: true }, "successful authorization preserves manual quality and problem reminder");
        repository.Profiles.SetAccountMarks(first.Profile, AccountQuality.SuspectedDegraded, true, false);
        var third = reauth.LoginAsync(first.Profile, prepareAccount: true);
        await Until(() => reauth.Status.Stage == LoginStage.AccountPreparation, third);
        temporary = chrome.Roots.Keys.Single(path => path != profile);
        Check(reauth.Cancel(), "cancel works in password preparation stage");
        var cancelled = await third.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!cancelled.Ok && cancelled.Callback == CallbackKind.Cancelled && callbacks.Starts == 2 && transport.Requests.Count == 2,
            "preparation cancellation creates no authorization or token request");
        Check(!Directory.Exists(temporary) && Directory.Exists(profile) && chrome.Roots.Count == 1 && repository.Profiles.ListAccounts().Single().HasCredentials,
            "cancel destroys only the disposable profile and retains saved account");

        Check(repository.Profiles.ReadAccountMarks(first.Profile) is { Recovered: false, Issue: AccountIssue.None }, "cancelled preparation neither adds recovery nor records a fault");
        await using var fresh = new OnboardingProxyBrowser(root, proxy, chrome, installedBrowser: true);
        var blocked = Service(fresh);
        var blockedOperation = blocked.LoginNewAsync(proxy, fresh, prepareAccount: true);
        await Until(() => blocked.Status.Stage == LoginStage.AccountPreparation, blockedOperation);
        temporary = chrome.Roots.Keys.Single(path => path != profile);
        var lockedPath = Path.Combine(temporary, "locked-cache-fixture");
        using (var held = new FileStream(lockedPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            chrome.Roots.Remove(temporary);
            var failed = await blockedOperation.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!failed.Ok && !blocked.CanBeginAuthorization && !blocked.BeginAuthorization() && File.Exists(lockedPath) && callbacks.Starts == 2,
                "locked temporary data blocks second step even after process exit");
            var startsBefore = chrome.StartedRoots;
            var refused = await blocked.LoginNewAsync(proxy, fresh, prepareAccount: true);
            Check(!refused.Ok && chrome.StartedRoots == startsBefore, "unresolved disposable cleanup blocks a new browser attempt");
        }
        var retry = blocked.LoginNewAsync(proxy, fresh, prepareAccount: true);
        await Until(() => blocked.Status.Stage == LoginStage.AccountPreparation, retry);
        Check(!Directory.Exists(temporary), "retry after releasing cache lock finishes prior cleanup");
        temporary = chrome.Roots.Keys.Single(path => path != profile);
        chrome.Roots.Remove(temporary);
        await Until(() => blocked.CanBeginAuthorization, retry);
        Check(blocked.Cancel(), "cancel is available after cleanup while waiting for second step");
        Check(!(await retry.WaitAsync(TimeSpan.FromSeconds(5))).Ok && callbacks.Starts == 2 && !Directory.Exists(temporary), "cancel at ready stage makes no OAuth request or leftover temporary data");
        Check(chrome.Roots.Count == 1 && chrome.Roots.ContainsKey(profile), "all disposable cleanup leaves unrelated saved browser intact");
        var authBefore = File.ReadAllBytes(Path.Combine(repository.AccountPath(first.Profile), "auth.json"));
        var failedRecovery = reauth.LoginAsync(first.Profile, prepareAccount: true);
        await Until(() => reauth.Status.Stage == LoginStage.AccountPreparation, failedRecovery);
        temporary = chrome.Roots.Keys.Single(path => path != profile); chrome.Roots.Remove(temporary);
        await Until(() => reauth.CanBeginAuthorization, failedRecovery);
        transport.Json(500, new JsonObject { ["error"] = "SYNTHETIC restore failed" }); reauth.BeginAuthorization();
        Check(!(await failedRecovery.WaitAsync(TimeSpan.FromSeconds(5))).Ok && repository.Profiles.ReadAccountMarks(first.Profile) is { Recovered: false, Issue: AccountIssue.RestoreFailed, Quality: AccountQuality.SuspectedDegraded, Problem: true }, "failed recovery never receives recovered badge or changes quality");
        Check(authBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(repository.AccountPath(first.Profile), "auth.json"))), "failed recovery retains existing credentials");
        return count;
    }
}
