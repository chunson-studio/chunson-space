using System.Text;
using System.Text.Json.Nodes;
using FAswitch.Accounts;
using FAswitch.Core;

internal static class PersistentBrowserChecks
{
    public static async Task<int> Run(string root)
    {
        int count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS " + label); }
        const string proxy = "http://127.0.0.1:7890";
        var repository = new AccountRepository(root);
        var chrome = new ProfileChrome();
        var clock = new FakeClock(); var transport = new FakeTransport(); var callbacks = new FakeCallbackFactory(clock);
        var id = "fixture." + OAuthPkce.Base64Url(Encoding.UTF8.GetBytes(new JsonObject { ["email"] = "account-ef610da68c@example.invalid",
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = "persistent-fixture" } }.ToJsonString())) + ".unsigned";
        JsonObject Tokens() => new() { ["id_token"] = id, ["access_token"] = "fixture-at", ["refresh_token"] = "fixture-rt" };
        transport.Json(200, Tokens());
        var onboarding = new OnboardingProxyBrowser(root, proxy, chrome, installedBrowser: true);
        var login = new LoginService(repository, transport, callbacks, onboarding, clock,
            verifiedAdmission: (_, _) => Task.FromResult(new EgressResult(true, "203.0.113.9")));
        var result = await login.LoginNewAsync(proxy, onboarding);
        var profiles = new AccountBrowserProfiles(root);
        var profile = profiles.Read(result.Profile)!;
        Check(result.Ok && repository.ReadAuth(result.Profile)["tokens"] is not null && Directory.Exists(profile), "successful first OAuth binds the actual browser without moving its live directory");
        Check(!File.ReadAllText(Path.Combine(profile, "preparing.html")).Contains("location.replace"), "successful authorization retires its old URL without clearing browser cookies");
        var marker = Path.Combine(profile, "Default", "fixture-web-state"); File.WriteAllText(marker, "fixture-signed-in");
        await onboarding.CleanupAsync(null, default); await onboarding.DisposeAsync();
        Check(chrome.Roots.Count == 1 && chrome.Closes == 0 && File.Exists(marker), "authorization release and disposal preserve the signed-in window and profile");
        await using var browser = new ChromeLoginBrowser(repository, chrome, new CdpConnectionFactory(), installedBrowser: true);
        var opened = await browser.OpenVerifiedAsync(result.Profile, new("https://chatgpt.com/"), proxy, new(true, "203.0.113.9"), default);
        Check(opened.ProfileDirectory == profile && chrome.Roots.Count == 1 && chrome.StartedRoots == 1 && File.ReadAllText(marker) == "fixture-signed-in", "open browser reuses the same live profile and root without copying cookies");
        callbacks.Kind = CallbackKind.Cancelled;
        var reauth = new LoginService(repository, transport, callbacks, browser, clock, verifiedAdmission: (_, _) => Task.FromResult(new EgressResult(true, "203.0.113.9")));
        var cancelled = await reauth.LoginAsync(result.Profile);
        Check(cancelled.Callback == CallbackKind.Cancelled && chrome.Roots.Count == 1 && chrome.Closes == 0 && File.Exists(marker), "cancel reauthorization keeps a preexisting account browser and web data");
        var refused = false;
        try { await browser.OpenVerifiedAsync(result.Profile, new("https://chatgpt.com/"), "http://127.0.0.1:7891", new(true, "203.0.113.10"), default); }
        catch (AccountException) { refused = true; }
        Check(refused && chrome.StartedRoots == 1, "running profile cannot silently ignore a changed proxy port");
        chrome.Roots.Clear(); // Simulated user closes this account's browser.
        var preferences = Path.Combine(profile, "Default", "Preferences");
        var data = JsonNode.Parse(File.ReadAllText(preferences))!.AsObject(); data["fixture_existing_preference"] = "keep"; File.WriteAllText(preferences, data.ToJsonString());
        opened = await browser.OpenVerifiedAsync(result.Profile, new("https://chatgpt.com/"), "http://127.0.0.1:7891", new(true, "203.0.113.10"), default);
        Check(opened.ProfileDirectory == profile && chrome.StartedRoots == 2 && File.Exists(marker)
            && JsonNode.Parse(File.ReadAllText(preferences))?["fixture_existing_preference"]?.GetValue<string>() == "keep", "closed profile reopens on the chosen port without resetting cookies or unrelated preferences");
        chrome.Roots.Clear();
        cancelled = await reauth.LoginAsync(result.Profile);
        Check(cancelled.Callback == CallbackKind.Cancelled && chrome.Roots.Count == 0 && Directory.Exists(profile) && File.Exists(marker), "cancel a newly opened reauthorization closes only its new window and keeps account data");
        transport.Json(200, Tokens()); callbacks.Kind = CallbackKind.Code;
        var success = await reauth.LoginAsync(result.Profile);
        Check(success.Ok && profiles.Read(result.Profile) == profile && chrome.Roots.Count == 1 && File.Exists(marker), "successful reauthorization retains the same profile and window");
        repository.Profiles.CreateBoundAccount("Other", new(proxy), false);
        var other = await browser.OpenVerifiedAsync("Other", new("https://chatgpt.com/"), proxy, new(true, "203.0.113.9"), default);
        Check(other.ProfileDirectory != profile && !File.Exists(Path.Combine(other.ProfileDirectory, "Default", "fixture-web-state")), "different accounts never share a browser profile");
        refused = false; try { profiles.Bind("Other", profile); } catch (IOException) { refused = true; }
        Check(refused && profiles.Read("Other") == other.ProfileDirectory, "binding cannot replace or borrow another account profile");
        chrome.Roots.Clear();
        repository.Profiles.Rename(new(IdentityKind.Account, result.Profile), "Renamed");
        Check(profiles.Read("Renamed") == profile && File.Exists(marker) && profiles.Read(result.Profile) is null, "renaming the account preserves the same browser directory and updates ownership");
        var deleted = new ProfileMaintenance(new(root, Path.Combine(root, "active-home"))).DeleteAccount("Renamed");
        Check(deleted.Success && !Directory.Exists(profile) && Directory.Exists(other.ProfileDirectory)
            && deleted.Archives.Any(path => File.Exists(Path.Combine(path, "Default", "fixture-web-state"))), "account deletion archives its originally provisional browser and preserves other accounts");
        var malformed = Path.Combine(root, "browser-envs", "Other", "browser-profile.json");
        File.WriteAllText(malformed, "{\"directory\":\"../../outside/auth-00000000000000000000000000000000\"}");
        refused = false; try { _ = profiles.Read("Other"); } catch (Exception error) when (error is IOException or StorageException) { refused = true; }
        Check(refused, "persistent browser references cannot escape the workspace browser root");
        var cancelledRoot = Path.Combine(root, "cancelled-authorizations");
        var cancelledRepository = new AccountRepository(cancelledRoot);
        var cancelChrome = new ProfileChrome();
        var cancelClock = new FakeClock();
        var cancelCallbacks = new FakeCallbackFactory(cancelClock) { Kind = CallbackKind.Cancelled };
        var cancelTransport = new FakeTransport();
        await using var cancelBrowser = new OnboardingProxyBrowser(cancelledRoot, proxy, cancelChrome, installedBrowser: true);
        var cancelLogin = new LoginService(cancelledRepository, cancelTransport, cancelCallbacks, cancelBrowser, cancelClock,
            verifiedAdmission: (_, _) => Task.FromResult(new EgressResult(true, "203.0.113.9")));
        var cancelledFresh = await cancelLogin.LoginNewAsync(proxy, cancelBrowser);
        Check(cancelledFresh.Callback == CallbackKind.Cancelled && cancelChrome.Roots.Count == 0
            && cancelledRepository.Profiles.ListAccounts().Count == 0 && !Directory.EnumerateDirectories(Path.Combine(cancelledRoot, "browser-envs", AccountBrowserProfiles.PoolName)).Any(),
            "cancel fresh installed authorization releases callback browser and provisional profile without an account");
        var closesBeforeManualExit = cancelChrome.Closes;
        cancelChrome.CloseAfterObservations = 2; cancelChrome.Observations = 0; cancelChrome.FailClose = true;
        cancelCallbacks.Kind = CallbackKind.Code; cancelCallbacks.WaitsBeforeResult = 100;
        await using var manualBrowser = new OnboardingProxyBrowser(cancelledRoot, proxy, cancelChrome, installedBrowser: true);
        var manualClosed = await cancelLogin.LoginNewAsync(proxy, manualBrowser);
        Check(manualClosed.Callback == CallbackKind.BrowserClosed && cancelChrome.Roots.Count == 0 && cancelChrome.Closes == closesBeforeManualExit
            && cancelTransport.Requests.Count == 0 && manualClosed.Warnings?.Any(warning => warning.Contains("清理未确认")) != true,
            "manually closed installed authorization releases without trying to stop an obsolete process identity");
        cancelChrome.CloseAfterObservations = null; cancelChrome.FailClose = false; cancelCallbacks.Kind = CallbackKind.Cancelled; cancelCallbacks.WaitsBeforeResult = 0;
        FileStream? lockedCache = null; string? lockedProfile = null;
        cancelChrome.OnStarted = path => { lockedProfile = path; lockedCache = new FileStream(Path.Combine(path, "locked-cache"), FileMode.Create, FileAccess.ReadWrite, FileShare.None); };
        await using var lockedBrowser = new OnboardingProxyBrowser(cancelledRoot, proxy, cancelChrome, installedBrowser: true);
        var cachePending = await cancelLogin.LoginNewAsync(proxy, lockedBrowser);
        Check(cachePending.Callback == CallbackKind.Cancelled && cachePending.Warnings?.Any(warning => warning.Contains("不会阻止下次登录")) == true
            && cancelChrome.Roots.Count == 0 && Directory.Exists(lockedProfile) && cancelledRepository.Profiles.ListAccounts().Count == 0,
            "a locked cache warns separately after confirmed browser exit and never retains the authorization session");
        lockedCache!.Dispose(); cancelChrome.OnStarted = null;
        await using var nextBrowser = new OnboardingProxyBrowser(cancelledRoot, proxy, cancelChrome, installedBrowser: true);
        var nextCancelled = await cancelLogin.LoginNewAsync(proxy, nextBrowser);
        Check(nextCancelled.Callback == CallbackKind.Cancelled && nextCancelled.Error != AccountError.Busy && cancelCallbacks.Starts == 4
            && cancelTransport.Requests.Count == 0 && Directory.Exists(lockedProfile),
            "next authorization is admitted while earlier released cache remains without token requests or foreign deletion");
        var rawRoot = Path.Combine(root, "virtual-local"); var physicalRoot = Path.Combine(root, "physical-cache");
        var rawProfile = Path.Combine(rawRoot, "browser-envs", ".web-sessions", "auth-" + new string('a', 32));
        string Resolve(string path) => path.Replace(rawRoot, physicalRoot, StringComparison.Ordinal);
        Check(ChromeLoginBrowser.ResolveCleanupProfileDirectory(rawRoot, rawProfile, Resolve) == Resolve(rawProfile),
            "cleanup canonicalizes both root and profile through the same redirected filesystem mapping");
        refused = false;
        try { _ = ChromeLoginBrowser.ResolveCleanupProfileDirectory(rawRoot, rawProfile, path => path == rawProfile ? Path.Combine(root, "outside", Path.GetFileName(rawProfile)) : Resolve(path)); }
        catch (IOException) { refused = true; }
        Check(refused, "canonical cleanup rejects an escaped physical profile rather than deleting it");
        var alignedRoot = Path.Combine(root, "aligned-browser-profiles");
        var alignedRepository = new AccountRepository(alignedRoot);
        alignedRepository.Profiles.CreateBoundAccount("East", new(proxy, "Asia/Tokyo"), false);
        alignedRepository.Profiles.CreateBoundAccount("West", new("http://127.0.0.1:7900", "Asia/Tokyo"), false);
        var alignedChrome = new ProfileChrome(); var timezoneController = new FixtureTimezoneController();
        await using var aligned = new ChromeLoginBrowser(alignedRepository, alignedChrome, new FakeCdpFactory(), installedBrowser: true, installedTimezone: timezoneController, systemTimezoneMatches: _ => true);
        var east = await aligned.OpenVerifiedAsync("East", new("https://chatgpt.com/"), proxy, new(true, "203.0.113.10", Timezone: "America/New_York"), default);
        var west = await aligned.OpenVerifiedAsync("West", new("https://chatgpt.com/"), "http://127.0.0.1:7900", new(true, "203.0.113.11", Timezone: "Europe/London"), default);
        Check(east.ProfileDirectory != west.ProfileDirectory && timezoneController.Timezones[east.ProfileDirectory] == "America/New_York"
            && timezoneController.Timezones[west.ProfileDirectory] == "Europe/London", "native account browsers receive independent exact-exit timezones");
        Check(alignedRepository.Profiles.ReadEnvironment("East").Timezone == "America/New_York"
            && alignedRepository.Profiles.ReadEnvironment("West").Timezone == "Europe/London", "saved browser environment uses confirmed exit timezone rather than old profile location");
        Check(east.Warnings.Any(warning => warning.Contains("时区已读回确认")), "browser reports timezone confirmation only after controller succeeds");
        refused = false;
        try { await aligned.OpenVerifiedAsync("East", new("https://chatgpt.com/"), proxy, new(true, "203.0.113.10", Timezone: "America/Phoenix"), default); }
        catch (AccountException) { refused = true; }
        Check(refused && alignedChrome.Roots.Count == 2 && timezoneController.Timezones[east.ProfileDirectory] == "America/New_York", "changed exit timezone refuses to repurpose an open browser and leaves the other account unchanged");
        var retainedWeb = Path.Combine(east.ProfileDirectory, "Default", "fixture-web-state");
        File.WriteAllText(retainedWeb, "fixture-signed-in");
        Check(await aligned.CloseForNetworkChangeAsync("East", default) && !alignedChrome.Roots.ContainsKey(east.ProfileDirectory)
            && alignedChrome.Roots.ContainsKey(west.ProfileDirectory) && File.ReadAllText(retainedWeb) == "fixture-signed-in", "network change closes only exact account browser and preserves signed-in files and other browser");
        timezoneController.Timezones.Remove(east.ProfileDirectory);
        alignedRepository.Profiles.SaveEnvironment("East", new("", "America/Phoenix", "en-US", FollowSystemProxy: true));
        await aligned.OpenVerifiedAsync("East", new("https://chatgpt.com/"), "http://127.0.0.1:7897", new(true, "203.0.113.12", Timezone: "America/Phoenix"), default);
        Check(!timezoneController.Timezones.ContainsKey(east.ProfileDirectory) && File.Exists(retainedWeb), "stored follow mode overrides prior dedicated browser route policy without clearing web login");
        await aligned.CloseForNetworkChangeAsync("East", default);
        alignedRepository.Profiles.SaveEnvironment("East", new(proxy, "America/New_York", "en-US", FollowSystemProxy: false));
        await aligned.OpenVerifiedAsync("East", new("https://chatgpt.com/"), proxy, new(true, "203.0.113.10", Timezone: "America/New_York"), default);
        Check(timezoneController.Timezones[east.ProfileDirectory] == "America/New_York" && timezoneController.Timezones[west.ProfileDirectory] == "Europe/London", "switching back restores independent timezone without affecting another account");
        var knownRoot = alignedChrome.Roots[east.ProfileDirectory];
        alignedChrome.Roots[east.ProfileDirectory] = new(9999, 9999, knownRoot.Executable, east.ProfileDirectory);
        refused = false; var closesBeforeUnknown = alignedChrome.Closes;
        try { await aligned.CloseForNetworkChangeAsync("East", default); } catch (AccountException) { refused = true; }
        Check(refused && alignedChrome.Closes == closesBeforeUnknown && File.Exists(retainedWeb), "network switch refuses an unknown browser identity before closing or deleting anything");
        alignedChrome.Roots[east.ProfileDirectory] = knownRoot;
        await aligned.CleanupAsync(null, default);
        var currentChecks = 0;
        alignedRepository.Profiles.CreateBoundAccount("Current", new(proxy), false);
        await using var current = new ChromeLoginBrowser(alignedRepository, alignedChrome, new FakeCdpFactory(), installedBrowser: true,
            installedTimezone: timezoneController, alignInstalledTimezone: false, systemTimezoneMatches: _ => { currentChecks++; return true; });
        var currentPage = await current.OpenVerifiedAsync("Current", new("https://chatgpt.com/"), proxy, new(true, "203.0.113.12", Timezone: "Asia/Tokyo"), default);
        Check(currentChecks == 1 && !timezoneController.Timezones.ContainsKey(currentPage.ProfileDirectory), "current-network browser validates system timezone without setting an override");
        alignedRepository.Profiles.CreateBoundAccount("Mismatch", new(proxy), false);
        await using var mismatch = new ChromeLoginBrowser(alignedRepository, alignedChrome, new FakeCdpFactory(), installedBrowser: true,
            installedTimezone: timezoneController, alignInstalledTimezone: false, systemTimezoneMatches: _ => false);
        refused = false;
        try { await mismatch.OpenVerifiedAsync("Mismatch", new("https://chatgpt.com/"), proxy, new(true, "203.0.113.12", Timezone: "Asia/Tokyo"), default); }
        catch (AccountException) { refused = true; }
        Check(refused, "current-network timezone mismatch prevents remote page navigation without system writes");
        foreach (var independent in new[] { false, true })
        {
            var modeRoot = Path.Combine(root, "onboarding-mode-" + independent);
            var modeRepository = new AccountRepository(modeRoot);
            var modeClock = new FakeClock(); var modeTransport = new FakeTransport(); modeTransport.Json(200, Tokens());
            var modeChrome = new ProfileChrome();
            var localZone = TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var mapped) ? mapped : TimeZoneInfo.Local.Id;
            var expectedZone = independent ? "America/New_York" : localZone;
            await using var modeBrowser = new OnboardingProxyBrowser(modeRoot, proxy, modeChrome, installedBrowser: true,
                installedTimezone: new FixtureTimezoneController(), alignInstalledTimezone: independent);
            var modeLogin = new LoginService(modeRepository, modeTransport, new FakeCallbackFactory(modeClock), modeBrowser, modeClock,
                verifiedAdmission: (_, _) => Task.FromResult(new EgressResult(true, "203.0.113.19", Timezone: expectedZone)));
            var modeResult = await modeLogin.LoginNewAsync(proxy, modeBrowser);
            var modeSettings = modeRepository.Profiles.ReadEnvironment(modeResult.Profile);
            Check(modeResult.Ok && modeSettings.FollowSystemProxy == !independent && modeSettings.Proxy == (independent ? proxy : "")
                && modeSettings.Timezone == expectedZone, "new authorization persists linked network/timezone mode " + independent);
            modeChrome.Roots.Clear();
        }
        return count;
    }
    private sealed class FixtureTimezoneController : IBrowserTimezoneController
    {
        public Dictionary<string, string> Timezones { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task EnsureAsync(string root, BrowserIdentity browser, string timezone, CancellationToken token)
        { Timezones[browser.ProfileDirectory] = timezone; return Task.CompletedTask; }
    }
    private sealed class ProfileChrome : IChromeSystem
    {
        public Dictionary<string, BrowserIdentity> Roots { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int StartedRoots { get; private set; }
        public int Closes { get; private set; }
        public bool FailClose { get; set; }
        public int? CloseAfterObservations { get; set; }
        public int Observations { get; set; }
        public Action<string>? OnStarted { get; set; }
        public string? FindChrome() => @"C:\fixture\chrome.exe";
        public Task StartAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            var profile = ChromeArguments.RootProfile(args)!;
            if (!Roots.ContainsKey(profile)) { StartedRoots++; Roots[profile] = new(StartedRoots, StartedRoots, executable, profile); OnStarted?.Invoke(profile); }
            return Task.CompletedTask;
        }
        public async Task<BrowserIdentity?> StartTrackedAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
        { await StartAsync(exe, args, token); return Roots[ChromeArguments.RootProfile(args)!]; }
        public Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(string exe, string profile, CancellationToken token)
        {
            if (++Observations >= CloseAfterObservations) Roots.Clear();
            return Task.FromResult<IReadOnlyList<BrowserIdentity>?>(Roots.TryGetValue(profile, out var value) ? [value] : []);
        }
        public Task<bool> CloseOwnedAsync(IReadOnlyList<BrowserIdentity> roots, CancellationToken token)
        { Closes++; if (FailClose) return Task.FromResult(false); foreach (var root in roots) if (Roots.GetValueOrDefault(root.ProfileDirectory) == root) Roots.Remove(root.ProfileDirectory); return Task.FromResult(true); }
    }
}
