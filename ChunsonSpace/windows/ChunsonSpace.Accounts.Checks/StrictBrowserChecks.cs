using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using System.Threading.Channels;

internal static class StrictBrowserChecks
{
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); count++; Console.WriteLine("PASS " + name); }
        var repository = new AccountRepository(root);
        repository.Profiles.CreateBoundAccount("A", new("http://127.0.0.1:7890", "Asia/Tokyo", "ja-JP"), false);
        const string proxy = "http://127.0.0.1:7890";
        const string ip = "203.0.113.7";
        var nativeChrome = new StrictChrome(); var unusedCdp = new StrictCdpFactory();
        await using (var native = new ChromeLoginBrowser(repository, nativeChrome, unusedCdp, installedBrowser: true))
        {
            await native.PrepareAsync(proxy, default);
            var nativeArgs = nativeChrome.Starts.Single();
            var nativeProfile = ChromeArguments.RootProfile(nativeArgs)!;
            var localPage = Path.Combine(nativeProfile, "preparing.html");
            Check(nativeArgs.Last().StartsWith("file:") && !File.ReadAllText(localPage).Contains("auth.openai.com")
                && nativeArgs.Contains("--lang=en-US") && nativeArgs.Contains("--proxy-server=" + proxy), "installed browser opens English local page before admission with exact proxy");
            Check(nativeArgs.All(arg => !arg.Contains("remote-debugging") && !arg.Contains("user-agent")) && unusedCdp.Latest is null,
                "installed browser never enables CDP or user-agent override");
            Check(nativeArgs.All(arg => !arg.StartsWith("--host-resolver-rules") && !arg.StartsWith("--test-type") && !arg.StartsWith("--disable-infobars")),
                "native login removes the warning-producing resolver override without suppressing security warnings");
            Check(nativeArgs.Contains("--disable-quic") && nativeArgs.Contains("--webrtc-ip-handling-policy=disable_non_proxied_udp")
                && !nativeArgs.Any(arg => arg.Contains("direct://") || arg.StartsWith("--proxy-bypass-list")),
                "native login retains proxy restrictions without a direct fallback or custom callback bypass");
            Check(JsonNode.Parse(File.ReadAllText(Path.Combine(nativeProfile, "Default", "Preferences")))?["net"]?["network_prediction_options"]?.GetValue<int>() == 2,
                "isolated native profile disables speculative page preloading");
            var pendingUrl = OAuthPkce.Authorize(1455, "fixture-challenge", "fixture-state");
            var ready = await native.OpenVerifiedAsync(LoginService.NewAccountRequestProfile, pendingUrl, proxy, new(true, ip, Timezone: "Asia/Tokyo"), default);
            Check(nativeChrome.Starts.Count == 2 && ready.ProfileDirectory == nativeProfile && File.ReadAllText(localPage).Contains("location.replace")
                && File.ReadAllText(localPage).Contains(System.Text.Json.JsonSerializer.Serialize(pendingUrl.AbsoluteUri))
                && unusedCdp.Latest is null, "prepared tab retains exact OAuth session after admission");
            var webLogin = nativeChrome.Starts.Last();
            Check(webLogin.Last() == "https://chatgpt.com/" && ChromeArguments.RootProfile(webLogin) == nativeProfile
                && webLogin.Contains("--proxy-server=" + proxy) && webLogin.Contains("--lang=en-US"),
                "ChatGPT login opens a separate tab in the same authorized profile proxy and language");
            await native.ResumeAsync(ready, default);
            var continued = nativeChrome.Starts.Last();
            Check(nativeChrome.Starts.Count == 3 && continued.Last() == pendingUrl.AbsoluteUri
                && ChromeArguments.RootProfile(continued) == nativeProfile && continued.Contains("--proxy-server=" + proxy)
                && continued.Contains("--lang=en-US") && unusedCdp.Latest is null,
                "native resume keeps exact PKCE/state URL, profile, language and route without CDP");
            nativeChrome.ExternalObservation = true;
            var refusedResume = false;
            try { await native.ResumeAsync(ready, default); } catch (AccountException) { refusedResume = true; }
            Check(refusedResume && nativeChrome.Starts.Count == 3, "resume rejects changed browser process identity");
            nativeChrome.ExternalObservation = false;
            Check((await native.CleanupAsync(ready, default)).Success && !Directory.Exists(nativeProfile), "native login cleanup preserves account library and only removes owned temporary profile");
            refusedResume = false;
            try { await native.ResumeAsync(ready, default); } catch (AccountException) { refusedResume = true; }
            Check(refusedResume && nativeChrome.Starts.Count == 3, "ended native session cannot open its authorization again");
        }
        var invalidNativeChrome = new StrictChrome();
        await using (var invalidNative = new ChromeLoginBrowser(repository, invalidNativeChrome, new StrictCdpFactory(), installedBrowser: true))
        {
            var refused = false;
            try { await invalidNative.PrepareAsync("http://proxy.example:7890", default); } catch (AccountException) { refused = true; }
            Check(refused && invalidNativeChrome.Starts.Count == 0, "native login still refuses a non-loopback proxy before launching");
        }
        var chrome = new StrictChrome();
        var factory = new StrictCdpFactory();
        var probes = 0;
        var protection = 0;
        await using var browser = new ChromeLoginBrowser(repository, chrome, factory, new StrictClock(),
            egressProbe: (route, _) => { Check(route == proxy, "strict browser probes the fixed token proxy snapshot"); probes++; return Task.FromResult(new EgressResult(true, ip, Timezone: "Asia/Tokyo")); },
            protectionVerifier: (exe, route, _) => { protection++; return Task.FromResult(exe == chrome.FindChrome() && route == proxy); });
        var launch = await browser.OpenWithProxyAsync("A", new("https://fixture.invalid/authorize"), proxy, default);
        Check(probes == 1 && protection == 1, "strict browser confirms program protection and egress before launch");
        Check(chrome.Starts.Single().Last() == "about:blank", "strict browser starts blank before authorizing navigation");
        Check(chrome.Starts.Single().Contains("--lang=en-US") && chrome.Starts.Single().Contains("--accept-lang=en-US"), "strict browser forces English despite saved locale");
        Check(chrome.Starts.Single().Contains("--disable-quic") && chrome.Starts.Single().Contains("--webrtc-ip-handling-policy=disable_non_proxied_udp") && chrome.Starts.Single().Any(arg => arg.StartsWith("--host-resolver-rules=")), "strict browser applies DNS QUIC and WebRTC bypass restrictions");
        Check(chrome.Starts.Single().Contains("--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1"), "strict DNS block leaves the actual numeric loopback proxy reachable");
        Check(ChromeArguments.RestrictedDns("http://127.0.0.2:7890").EndsWith("EXCLUDE 127.0.0.2")
            && ChromeArguments.RestrictedDns("http://[::1]:7890").EndsWith("EXCLUDE [::1]"), "DNS exception follows the validated loopback host rather than a fixed proxy address");
        var unsafeDnsRejected = false;
        try { _ = ChromeArguments.RestrictedDns("http://*.example:7890"); } catch (AccountException) { unsafeDnsRejected = true; }
        Check(unsafeDnsRejected, "proxy hostname cannot inject wildcard DNS exclusions");
        var calls = factory.Latest!.Calls;
        Check(calls.All(call => call.Method is not "Emulation.setUserAgentOverride" and not "Network.setExtraHTTPHeaders"), "English preferences preserve native User-Agent and Client Hints");
        foreach (var kind in new[] { "iframe", "worker", "service_worker", "shared_worker" })
        {
            await factory.Latest.EmitTargetAsync(kind);
            Check(factory.Latest.ResumedTargets.Contains(kind), "auto-attached " + kind + " does not remain paused");
        }
        var navigation = calls.FindIndex(call => call.Method == "Page.navigate");
        Check(navigation > calls.FindIndex(call => call.Method == "Runtime.evaluate" && call.Expression.Contains("fetch(")), "browser actual IP comparison precedes OAuth navigation");
        Check(calls.FindIndex(call => call.Method == "Emulation.setTimezoneOverride") < navigation && calls.FindIndex(call => call.Method == "Emulation.setLocaleOverride") < navigation, "timezone and English overrides precede OAuth navigation");
        Check(launch.Warnings.Any(message => message.Contains(ip) && message.Contains("Asia/Tokyo") && message.Contains("en-US")), "confirmed browser evidence includes actual IP timezone and English");
        var previousProfile = launch.ProfileDirectory;
        launch = await browser.OpenWithProxyAsync("A", new("https://fixture.invalid/authorize"), proxy, default);
        Check(previousProfile != launch.ProfileDirectory && Path.GetFileName(launch.ProfileDirectory).StartsWith("auth-"), "every authorization uses a new independent browser profile");
        chrome.FailClose = true;
        var cleanup = await browser.CleanupAsync(launch, default);
        Check(!cleanup.Success && Directory.Exists(launch.ProfileDirectory), "unconfirmed browser exit preserves authorization profile data");
        var disposeRejected = false;
        try { await browser.DisposeAsync(); } catch (AccountException) { disposeRejected = true; }
        Check(disposeRejected && Directory.Exists(launch.ProfileDirectory), "failed strict cleanup keeps browser undisposed and available for a confirmed retry");
        chrome.FailClose = false;
        cleanup = await browser.CleanupAsync(launch, default);
        Check(cleanup.Success && !Directory.Exists(launch.ProfileDirectory) && Directory.Exists(previousProfile) && repository.Exists("A"), "confirmed cleanup removes only this session and preserves other session and account library");

        var preparedChrome = new StrictChrome(); var preparedFactory = new StrictCdpFactory(); var preparations = 0;
        await using (var prepared = new ChromeLoginBrowser(repository, preparedChrome, preparedFactory, new StrictClock(),
            egressProbe: (_, _) => throw new InvalidOperationException("duplicate egress probe"),
            protectionVerifier: (_, _, _) => throw new InvalidOperationException("duplicate protection check"),
            prepareBrowser: (_, token) => { token.ThrowIfCancellationRequested(); preparations++; return Task.FromResult(preparedChrome.FindChrome()!); }))
        {
            await prepared.PrepareAsync(proxy, default);
            var evidenceOnce = new EgressResult(true, ip, Timezone: "Asia/Tokyo");
            var ready = await prepared.OpenVerifiedAsync("A", new("https://fixture.invalid/authorize"), proxy, evidenceOnce, default);
            Check(preparations == 1 && preparedFactory.Latest!.Calls.Any(call => call.Method == "Runtime.evaluate" && call.Expression.Contains("fetch(")), "prepared browser reuses admission and protection but retains actual browser IP check");
            await prepared.CleanupAsync(ready, default);
            preparedFactory.WrongIp = true; var rejectedPrepared = false;
            try { await prepared.OpenVerifiedAsync("A", new("https://fixture.invalid/authorize"), proxy, evidenceOnce, default); }
            catch (AccountException) { rejectedPrepared = true; }
            Check(preparations == 2 && rejectedPrepared && preparedFactory.Latest!.Calls.All(call => call.Method != "Page.navigate"), "consumed preparation is not reused and mismatched actual browser IP remains rejected");
        }

        var previewChrome = new StrictChrome(); var previewFactory = new StrictCdpFactory();
        await using (var preview = new ChromeLoginBrowser(repository, previewChrome, previewFactory, new StrictClock(),
            egressProbe: (_, _) => throw new InvalidOperationException("unexpected network request"),
            prepareBrowser: (_, _) => Task.FromResult(previewChrome.FindChrome()!), previewDuringPreparation: true))
        {
            await preview.PrepareAsync(proxy, default);
            var args = previewChrome.Starts.Single(); var dir = ChromeArguments.RootProfile(args)!;
            previewFactory.PreviewUrl = args.Last();
            Check(args.Last().StartsWith("file:") && args.Contains("--lang=en-US") && previewFactory.Latest is null,
                "protected English preparation page appears before admission without external navigation");
            Check(JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "Local State")))?["intl"]?["app_locale"]?.GetValue<string>() == "en-US"
                && JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "Default", "Preferences")))?["intl"]?["accept_languages"]?.GetValue<string>() == "en-US,en", "new isolated profile has explicit English UI and language preferences");
            var ready = await preview.OpenVerifiedAsync("A", new("https://fixture.invalid/authorize"), proxy, new(true, ip, Timezone: "Asia/Tokyo"), default);
            Check(previewChrome.Starts.Count == 1 && ready.ProfileDirectory == dir && previewFactory.Latest!.Calls.All(call => call.Method != "Target.createTarget")
                && previewFactory.Latest.Calls.Any(call => call.Method == "Page.navigate"), "verified login reuses preparation process and tab with actual browser checks");
            Check((await preview.CleanupAsync(ready, default)).Success && !Directory.Exists(dir), "prepared and authorized session retains exact cleanup ownership");
        }
        async Task Reject(bool blockProtection, bool missingEgress, bool wrongTimezone, bool wrongLanguage, bool wrongIp, string name)
        {
            var fakeChrome = new StrictChrome();
            var fakeCdp = new StrictCdpFactory { WrongTimezone = wrongTimezone, WrongLanguage = wrongLanguage, WrongIp = wrongIp };
            await using var rejectedBrowser = new ChromeLoginBrowser(repository, fakeChrome, fakeCdp, new StrictClock(),
                egressProbe: (_, _) => Task.FromResult(new EgressResult(!missingEgress, ip, Timezone: "Asia/Tokyo")),
                protectionVerifier: (_, _, _) => Task.FromResult(!blockProtection));
            var rejected = false;
            try { await rejectedBrowser.OpenWithProxyAsync("A", new("https://fixture.invalid/authorize"), proxy, default); }
            catch (AccountException) { rejected = true; }
            Check(rejected && (fakeCdp.Latest is null || fakeCdp.Latest.Calls.All(call => call.Method != "Page.navigate")) && fakeChrome.Starts.All(args => args.Last() == "about:blank"), name);
        }
        await Reject(true, false, false, false, false, "unconfirmed browser firewall prevents opening any authorization page");
        await Reject(false, true, false, false, false, "unknown egress prevents opening any authorization page");
        await Reject(false, false, true, false, false, "timezone mismatch refuses navigation without fallback");
        await Reject(false, false, false, true, false, "language mismatch refuses navigation without fallback");
        await Reject(false, false, false, false, true, "browser IP mismatch refuses navigation without fallback");
        foreach (var wrongIp in new[] { false, true })
        {
            var documentFactory = new StrictCdpFactory { FetchUnavailable = true, WrongIp = wrongIp, PauseProbeUntilAttached = true };
            await using var documentBrowser = new ChromeLoginBrowser(repository, new StrictChrome(), documentFactory, new StrictClock(),
                egressProbe: (_, _) => Task.FromResult(new EgressResult(true, ip, Timezone: "Asia/Tokyo")), protectionVerifier: (_, _, _) => Task.FromResult(true));
            var rejected = false;
            try { await documentBrowser.OpenWithProxyAsync("A", new("https://fixture.invalid/authorize"), proxy, default); }
            catch (AccountException) { rejected = true; }
            var documentCalls = documentFactory.Latest!.Calls;
            Check(documentCalls.Any(call => call.Method == "Page.navigate" && call.Expression == AccountEndpoints.Egress.AbsoluteUri)
                && rejected == wrongIp && documentCalls.Any(call => call.Method == "Page.navigate" && call.Expression == "https://fixture.invalid/authorize") == !wrongIp,
                wrongIp ? "top-level public probe still refuses a mismatched IP before OAuth" : "failed blank-page fetch uses verified public JSON navigation before OAuth");
        }
        foreach (var mode in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var backupFactory = new StrictCdpFactory { EgressBackupMode = mode };
            var backupChrome = new StrictChrome();
            await using var backupBrowser = new ChromeLoginBrowser(repository, backupChrome, backupFactory, new StrictClock(),
                egressProbe: (_, _) => Task.FromResult(new EgressResult(true, ip, CountryCode: "JP", Timezone: "Asia/Tokyo")), protectionVerifier: (_, _, _) => Task.FromResult(true));
            var rejected = false;
            try { await backupBrowser.OpenWithProxyAsync("A", new("https://fixture.invalid/authorize"), proxy, default); }
            catch (AccountException) { rejected = true; }
            var backupCalls = backupFactory.Latest!.Calls;
            var shouldReject = mode is 3 or 5 or 6;
            Check(rejected == shouldReject && backupCalls.Any(call => call.Method == "Page.navigate" && call.Expression == "https://fixture.invalid/authorize") == !shouldReject,
                "browser backup mode " + mode + " uses a complete matching provider or refuses before OAuth");
            Check(backupCalls.Any(call => call.Method == "Runtime.evaluate" && call.Expression.Contains(IpQualityService.Endpoint.Host))
                && backupFactory.Latest.ProbeSessions.SetEquals(["fixture-session"])
                && backupChrome.Starts.All(args => args.Contains("--proxy-server=" + proxy)), "browser fallback remains inside the same bound renderer and proxy");
            Check(mode != 3 || !backupCalls.Any(call => call.Expression.Contains(NetCoffeeService.TraceEndpoint.Host)), "browser measured disagreement never tries a third source to find a passing answer");
            if (mode is 2 or 4 or 6)
                Check(backupCalls.Any(call => call.Expression.Contains(NetCoffeeService.LookupEndpoint.AbsoluteUri + ip)), "browser NetCoffee lookup is bound to its actual trace IP");
        }
        var aliasChrome = new StrictChrome();
        var aliasFactory = new StrictCdpFactory { Timezone = "Asia/Kolkata" };
        await using (var aliasBrowser = new ChromeLoginBrowser(repository, aliasChrome, aliasFactory, new StrictClock(),
            egressProbe: (_, _) => Task.FromResult(new EgressResult(true, ip, Timezone: "Asia/Kolkata")), protectionVerifier: (_, _, _) => Task.FromResult(true)))
        {
            var aliasLaunch = await aliasBrowser.OpenWithProxyAsync("A", new("https://fixture.invalid/authorize"), proxy, default);
            Check(aliasFactory.Latest!.Calls.Any(call => call.Method == "Page.navigate") && aliasLaunch.Warnings.Any(message => message.Contains("Asia/Kolkata")), "strict login accepts canonical Intl name for an equivalent IANA alias");
            Check(aliasFactory.Latest.Calls.Where(call => call.Expression.Contains("JSON.stringify({timezone")).All(call => call.Expression.Contains("expectedTimezone:new Intl.DateTimeFormat") && call.Expression.Contains("Asia/Kolkata")), "every strict login environment read canonicalizes target in the same Intl context");
        }
        var wrongAliasFactory = new StrictCdpFactory { Timezone = "Asia/Kolkata", ActualTimezone = "Asia/Colombo" };
        await using (var differentBrowser = new ChromeLoginBrowser(repository, new StrictChrome(), wrongAliasFactory, new StrictClock(),
            egressProbe: (_, _) => Task.FromResult(new EgressResult(true, ip, Timezone: "Asia/Kolkata")), protectionVerifier: (_, _, _) => Task.FromResult(true)))
        {
            var rejected = false;
            try { await differentBrowser.OpenWithProxyAsync("A", new("https://fixture.invalid/authorize"), proxy, default); } catch (AccountException) { rejected = true; }
            Check(rejected && wrongAliasFactory.Latest!.Calls.All(call => call.Method != "Page.navigate"), "strict login rejects a different IANA zone even when current offset is equal");
        }
        var retryChrome = new StrictChrome { FailStart = true };
        await using (var retryBrowser = new ChromeLoginBrowser(repository, retryChrome, new StrictCdpFactory(), new StrictClock(),
            egressProbe: (_, _) => Task.FromResult(new EgressResult(true, ip, Timezone: "Asia/Tokyo")), protectionVerifier: (_, _, _) => Task.FromResult(true)))
        {
            var callbackClock = new FakeClock(); var callbacks = new FakeCallbackFactory(callbackClock) { Kind = CallbackKind.Cancelled }; var transport = new FakeTransport();
            var login = new LoginService(repository, transport, callbacks, retryBrowser, callbackClock);
            var first = await login.LoginAsync("A");
            var failedProfile = ChromeArguments.RootProfile(retryChrome.Starts.Single())!;
            Check(first.Error == AccountError.Browser && !Directory.Exists(failedProfile) && retryChrome.CloseCalls == 0, "known zero processes after rejected Chrome creation clean failed profile without stopping any process");
            retryChrome.FailStart = false;
            var second = await login.LoginAsync("A");
            Check(second.Callback == CallbackKind.Cancelled && second.Error != AccountError.Busy && callbacks.Starts == 2 && retryChrome.Starts.Count == 2 && transport.Requests.Count == 0, "confirmed empty failed launch permits next login without pending cleanup lockout");
        }
        foreach (var unknown in new[] { true, false })
        {
            var failedChrome = new StrictChrome { FailStart = true, UnknownAfterFailedStart = unknown, ExternalAfterFailedStart = !unknown };
            var failedBrowser = new ChromeLoginBrowser(repository, failedChrome, new StrictCdpFactory(), new StrictClock(),
                egressProbe: (_, _) => Task.FromResult(new EgressResult(true, ip, Timezone: "Asia/Tokyo")), protectionVerifier: (_, _, _) => Task.FromResult(true));
            var callbackClock = new FakeClock(); var callbacks = new FakeCallbackFactory(callbackClock) { Kind = CallbackKind.Cancelled };
            var login = new LoginService(repository, new FakeTransport(), callbacks, failedBrowser, callbackClock);
            _ = await login.LoginAsync("A");
            var failedProfile = ChromeArguments.RootProfile(failedChrome.Starts.Single())!;
            var next = await login.LoginAsync("A");
            Check(Directory.Exists(failedProfile) && next.Error == AccountError.Busy && callbacks.Starts == 1 && failedChrome.CloseCalls == 0, unknown ? "unknown process observation preserves failed profile and blocks new authorization" : "unowned browser observation preserves failed profile without closing external process");
            failedChrome.UnknownObservation = false; failedChrome.ExternalObservation = false;
            Check((await failedBrowser.CleanupAsync(null, default)).Success && !Directory.Exists(failedProfile), "failed launch cleanup recovers only after empty process observations are confirmed");
            await failedBrowser.DisposeAsync();
        }
        var newChrome = new StrictChrome();
        await using var onboarding = new OnboardingProxyBrowser(root, proxy, newChrome,
            egressProbe: (_, _) => Task.FromResult(new EgressResult(true, ip, Timezone: "Asia/Tokyo")), protectionVerifier: (_, _, _) => Task.FromResult(true), cdp: new StrictCdpFactory());
        var newLaunch = await onboarding.OpenAsync(LoginService.NewAccountRequestProfile, OAuthPkce.Authorize(1455, "fixture", "fixture"), default);
        Check(newLaunch.ProfileDirectory.Contains("auth-") && !repository.Exists(LoginService.NewAccountRequestProfile), "strict onboarding verifies browser without creating an empty account profile");
        Check(new WindowsChromeSystem(() => null).FindChrome() is null, "missing prepared browser never falls back to regular Chrome");
        return count;
    }
    private sealed class StrictClock : IAccountClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;
        public Task DelayAsync(TimeSpan duration, CancellationToken token) { token.ThrowIfCancellationRequested(); UtcNow += duration; return Task.CompletedTask; }
    }
    private sealed class StrictChrome : IChromeSystem
    {
        public List<IReadOnlyList<string>> Starts { get; } = [];
        private readonly Dictionary<string, BrowserIdentity> roots = new(StringComparer.OrdinalIgnoreCase);
        public bool FailClose { get; set; }
        public bool FailStart { get; set; }
        public bool UnknownAfterFailedStart { get; set; }
        public bool ExternalAfterFailedStart { get; set; }
        public bool UnknownObservation { get; set; }
        public bool ExternalObservation { get; set; }
        public int CloseCalls { get; private set; }
        public string? FindChrome() => @"C:\fixture-managed-browser\chrome.exe";
        public async Task<BrowserIdentity?> StartTrackedAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        { await StartAsync(executable, args, token); return roots[ChromeArguments.RootProfile(args)!]; }
        public Task StartAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            Starts.Add(args); var profile = ChromeArguments.RootProfile(args)!; Directory.CreateDirectory(profile);
            if (FailStart) { UnknownObservation = UnknownAfterFailedStart; ExternalObservation = ExternalAfterFailedStart; throw new AccountException(AccountError.Browser, "fake Chrome creation rejected before starting a process"); }
            roots.TryAdd(profile, new(1000 + Starts.Count, 1000 + Starts.Count, FindChrome()!, profile));
            File.WriteAllText(Path.Combine(profile, "DevToolsActivePort"), "12345\n/devtools/browser/fixture\n"); return Task.CompletedTask;
        }
        public Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(string exe, string profile, CancellationToken token) => Task.FromResult<IReadOnlyList<BrowserIdentity>?>(UnknownObservation ? null : ExternalObservation ? [new(9900, 9900, FindChrome()!, profile)] : roots.TryGetValue(profile, out var identity) ? [identity] : []);
        public Task<bool> CloseOwnedAsync(IReadOnlyList<BrowserIdentity> identities, CancellationToken token)
        { CloseCalls++; if (FailClose) return Task.FromResult(false); foreach (var identity in identities) if (roots.TryGetValue(identity.ProfileDirectory, out var actual) && actual == identity) roots.Remove(identity.ProfileDirectory); return Task.FromResult(true); }
    }
    private sealed class StrictCdpFactory : ICdpConnectionFactory
    {
        public bool WrongTimezone { get; init; }
        public bool WrongLanguage { get; init; }
        public bool WrongIp { get; set; }
        public bool FetchUnavailable { get; init; }
        public bool PauseProbeUntilAttached { get; init; }
        public string Timezone { get; init; } = "Asia/Tokyo";
        public string? ActualTimezone { get; init; }
        public string PreviewUrl { get; set; } = "";
        public int EgressBackupMode { get; init; }
        public StrictCdp? Latest { get; private set; }
        public Task<ICdpConnection> ConnectAsync(Uri uri, CancellationToken token)
        { Latest = new(WrongTimezone, WrongLanguage, WrongIp, Timezone, ActualTimezone, FetchUnavailable, PauseProbeUntilAttached, PreviewUrl, EgressBackupMode); return Task.FromResult<ICdpConnection>(Latest); }
    }
    private sealed class StrictCdp(bool wrongTimezone, bool wrongLanguage, bool wrongIp, string timezone, string? actualTimezone, bool fetchUnavailable, bool pauseProbeUntilAttached, string previewUrl, int backupMode) : ICdpConnection
    {
        private readonly Channel<JsonObject> events = Channel.CreateUnbounded<JsonObject>();
        private readonly TaskCompletionSource resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Method, string Expression)> Calls { get; } = [];
        public HashSet<string> ResumedTargets { get; } = [];
        public HashSet<string> ProbeSessions { get; } = [];
        private TaskCompletionSource? targetResumed;
        public async Task EmitTargetAsync(string kind)
        {
            targetResumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await events.Writer.WriteAsync(new JsonObject { ["method"] = "Target.attachedToTarget", ["params"] = new JsonObject
            { ["sessionId"] = kind, ["targetInfo"] = new JsonObject { ["type"] = kind, ["targetId"] = "fixture-" + kind }, ["waitingForDebugger"] = true } });
            await targetResumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        public async Task<JsonObject> CallAsync(string method, JsonObject parameters, string? sessionId, CancellationToken token)
        {
            var expression = parameters["expression"]?.GetValue<string>() ?? parameters["url"]?.GetValue<string>() ?? ""; Calls.Add((method, expression));
            if (method == "Runtime.runIfWaitingForDebugger" && sessionId is "iframe" or "worker" or "service_worker" or "shared_worker")
            { ResumedTargets.Add(sessionId); targetResumed?.TrySetResult(); }
            if (method == "Runtime.runIfWaitingForDebugger" && sessionId == "auto-primary") resumed.TrySetResult();
            if (pauseProbeUntilAttached && method == "Runtime.evaluate" && expression.Contains("fetch("))
            {
                await events.Writer.WriteAsync(new JsonObject { ["method"] = "Target.attachedToTarget", ["params"] = new JsonObject
                { ["sessionId"] = "auto-primary", ["targetInfo"] = new JsonObject { ["type"] = "page", ["targetId"] = "fixture" } } }, token);
                await resumed.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
            }
            JsonObject Value(string value) => new() { ["result"] = new JsonObject { ["value"] = value } };
            if (method == "Runtime.evaluate" && (expression.Contains("fetch(") || expression.Contains("document.body.innerText")))
            {
                ProbeSessions.Add(sessionId ?? "");
                if (backupMode > 0)
                {
                    if (expression.Contains(AccountEndpoints.Egress.Host)) return Value("{\"success\":false}");
                    if (expression.Contains(IpQualityService.Endpoint.Host))
                        return Value(backupMode is 2 or 5 or 6 ? "{\"error_code\":\"unavailable\"}" : new JsonObject { ["ip"] = backupMode == 3 ? "203.0.113.99" : "203.0.113.7", ["country_code"] = "JP", ["timezone"] = backupMode == 4 ? "" : timezone }.ToJsonString());
                    if (expression.Contains(NetCoffeeService.TraceEndpoint.AbsoluteUri)) return Value(backupMode == 5 ? "" : "ip=203.0.113.7\nloc=JP\n");
                    if (expression.Contains(NetCoffeeService.LookupEndpoint.AbsoluteUri)) return Value(new JsonObject { ["ip"] = backupMode == 6 ? "203.0.113.99" : "203.0.113.7", ["countryCode"] = "JP", ["timezone"] = timezone }.ToJsonString());
                }
            }
            var result = method switch
            {
                "Browser.getVersion" => new JsonObject { ["userAgent"] = "fixture-chrome" },
                "Target.createTarget" => new JsonObject { ["targetId"] = "fixture" },
                "Target.getTargets" => new JsonObject { ["targetInfos"] = new JsonArray(new JsonObject { ["targetId"] = "fixture", ["type"] = "page", ["url"] = previewUrl }) },
                "Target.attachToTarget" => new JsonObject { ["sessionId"] = "fixture-session" },
                "Page.navigate" => new JsonObject { ["frameId"] = "fixture-frame" },
                "Runtime.evaluate" when expression.Contains("fetch(") && fetchUnavailable => new JsonObject { ["exceptionDetails"] = new JsonObject { ["text"] = "fixture fetch rejected" } },
                "Runtime.evaluate" when expression.Contains("fetch(") || expression.Contains("document.body.innerText") => Value(new JsonObject { ["success"] = true, ["ip"] = wrongIp ? "203.0.113.8" : "203.0.113.7", ["timezone"] = new JsonObject { ["id"] = timezone } }.ToJsonString()),
                "Runtime.evaluate" when expression.Contains("JSON.stringify({timezone") => Value(new JsonObject { ["timezone"] = wrongTimezone ? "UTC" : actualTimezone ?? (timezone == "Asia/Kolkata" ? "Asia/Calcutta" : timezone), ["expectedTimezone"] = timezone == "Asia/Kolkata" ? "Asia/Calcutta" : timezone, ["language"] = wrongLanguage ? "ja-JP" : "en-US" }.ToJsonString()),
                _ => new JsonObject()
            };
            return result;
        }
        public IAsyncEnumerable<JsonObject> Events(CancellationToken token) => events.Reader.ReadAllAsync(token);
        public ValueTask DisposeAsync() { events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
