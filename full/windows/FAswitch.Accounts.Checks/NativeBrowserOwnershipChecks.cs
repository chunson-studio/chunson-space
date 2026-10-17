using System.Text.Json;
using FAswitch.Accounts;
using FAswitch.Core;
using FAswitch.Windows;

internal static class NativeBrowserOwnershipChecks
{
    public static async Task RunPersistence(string executable, string output)
    {
        executable = Path.GetFullPath(executable); output = Path.GetFullPath(output);
        if (!File.Exists(executable) || !InstalledBrowsers.SupportedExecutable(executable) || Directory.Exists(output)) throw new ArgumentException("Use an installed browser and a new evidence directory.");
        PhysicalFileStorage.RejectLinks(output); Directory.CreateDirectory(output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50)); var token = deadline.Token;
        var repository = new AccountRepository(Path.Combine(output, "isolated")); var system = new NativeRecordingChrome(executable);
        await using var route = new NativeBrowserRouteFixture();
        await using var browser = new ChromeLoginBrowser(repository, system, new CdpConnectionFactory(), installedBrowser: true);
        string profile = ""; bool saved = false, keptOpen = false, reopened = false, retained = false, closed = false;
        IReadOnlyList<BrowserIdentity> roots = [];
        void Navigate(string path) => new PhysicalFileStorage().WriteAtomic(Path.Combine(profile, "preparing.html"), System.Text.Encoding.UTF8.GetBytes(
            "<!doctype html><script>location.replace('http://chunson-route-check.invalid/" + path + "');</script>"));
        try
        {
            await browser.PrepareAsync(route.Proxy, token);
            profile = Directory.EnumerateDirectories(Path.Combine(repository.Root, "browser-envs", AccountBrowserProfiles.PoolName), "auth-*").Single();
            roots = await system.ObserveAsync(executable, profile, token) ?? [];
            if (roots.Count != 1) throw new InvalidOperationException("Expected one fixture root.");
            Navigate("remember"); saved = await route.StoredAsync(token);
            repository.Profiles.CreateBoundAccount("Fixture", new(route.Proxy), false);
            await browser.KeepAccountProfileAsync("Fixture", new(executable, profile, []), token);
            await browser.CleanupAsync(null, token);
            keptOpen = (await system.ObserveAsync(executable, profile, token))?.Count == 1;
            if (!await system.CloseOwnedAsync(roots, token)) throw new InvalidOperationException("Fixture close failed.");
            await browser.PrepareAsync("Fixture", route.Proxy, token);
            reopened = new AccountBrowserProfiles(repository.Root).Read("Fixture") == profile;
            roots = await system.ObserveAsync(executable, profile, token) ?? [];
            Navigate("verify"); retained = await route.RestoredAsync(token);
        }
        finally
        {
            closed = (await browser.CleanupAsync(null, default)).Success;
            if (roots.Count > 0) closed &= await system.CloseOwnedAsync(roots, default);
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = saved && keptOpen && reopened && retained && closed,
                saved, keptOpen, reopened, cookieAndLocalStorageRetained = retained, closed, accountRequests = 0, externalNavigations = 0,
                note = "Real installed browser, local-only proxy, synthetic persistent cookie and localStorage. No real account sign-in tested." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (!(saved && keptOpen && reopened && retained && closed)) throw new InvalidOperationException("Native browser persistence acceptance failed.");
        Console.WriteLine("PASS native cookie and localStorage persist after closing and reopening the same account profile.");
    }
    public static async Task RunLocal(string executable, string output)
    {
        executable = Path.GetFullPath(executable); output = Path.GetFullPath(output);
        if (!File.Exists(executable) || !InstalledBrowsers.SupportedExecutable(executable) || Directory.Exists(output))
            throw new ArgumentException("Provide an installed browser and a new evidence directory.");
        PhysicalFileStorage.RejectLinks(output); Directory.CreateDirectory(output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var repository = new AccountRepository(Path.Combine(output, "isolated"));
        var system = new NativeRecordingChrome(executable);
        await using var route = new NativeBrowserRouteFixture();
        await using var browser = new ChromeLoginBrowser(repository, system, new CdpConnectionFactory(), installedBrowser: true);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        bool visible = false, englishPreferences = false, noDebug = false, cleaned = false, noResolverOverride = false, proxyAndCallback = false;
        long prepareMs = 0, windowMs = 0; string title = "";
        try
        {
            await browser.PrepareAsync(route.Proxy, deadline.Token); prepareMs = timer.ElapsedMilliseconds;
            var profile = Directory.EnumerateDirectories(Path.Combine(repository.Root, "browser-envs", AccountBrowserProfiles.PoolName), "auth-*").Single();
            var preferences = await File.ReadAllTextAsync(Path.Combine(profile, "Default", "Preferences"), deadline.Token);
            englishPreferences = preferences.Contains("en-US");
            var owned = await system.ObserveAsync(executable, profile, deadline.Token);
            if (owned is not { Count: 1 }) throw new InvalidOperationException("Expected one owned root.");
            using var process = System.Diagnostics.Process.GetProcessById(owned[0].Pid);
            while (!visible)
            {
                deadline.Token.ThrowIfCancellationRequested(); process.Refresh(); title = process.MainWindowTitle;
                visible = process.MainWindowHandle != 0 && title.Contains("Preparing sign-in", StringComparison.Ordinal);
                if (!visible) await Task.Delay(100, deadline.Token);
            }
            windowMs = timer.ElapsedMilliseconds;
            noDebug = !File.Exists(Path.Combine(profile, "DevToolsActivePort"));
            noResolverOverride = system.Arguments.All(arg => !arg.StartsWith("--host-resolver-rules")
                && !arg.StartsWith("--test-type") && !arg.StartsWith("--disable-infobars"));
            // Exercise the same local-file handoff with a fixture URL. The proxy
            // never forwards anything and the callback contains only fake values.
            new PhysicalFileStorage().WriteAtomic(Path.Combine(profile, "preparing.html"), System.Text.Encoding.UTF8.GetBytes(
                "<!doctype html><title>Local route check</title><script>location.replace('http://chunson-route-check.invalid/probe');</script>"));
            proxyAndCallback = await route.VerifyAsync(deadline.Token);
        }
        finally
        {
            cleaned = (await browser.CleanupAsync(null, default)).Success;
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new {
                passed = visible && englishPreferences && noDebug && noResolverOverride && proxyAndCallback && cleaned, executable, visible, title, englishPreferences,
                noDebug, noResolverOverride, proxyAndCallback, cleaned, prepareMs, windowMs, accountRequests = 0, externalNavigations = 0,
                note = "Real installed browser, local-only HTTP proxy and synthetic loopback callback. No OAuth, CAPTCHA or account success tested."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (!visible || !englishPreferences || !noDebug || !noResolverOverride || !proxyAndCallback || !cleaned) throw new InvalidOperationException("Native preparation/route acceptance failed.");
        Console.WriteLine($"PASS installed local browser preparation {prepareMs} ms, visible by {windowMs} ms; no account requests.");
    }
    public static async Task RunPreview(string executable, string output)
    {
        executable = Path.GetFullPath(executable); output = Path.GetFullPath(output);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "chrome.exe" || Directory.Exists(output)) throw new ArgumentException("Provide Chrome and a new output directory.");
        Directory.CreateDirectory(output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token; var watch = System.Diagnostics.Stopwatch.StartNew();
        var repository = new AccountRepository(Path.Combine(output, "isolated"));
        await using var browser = new ChromeLoginBrowser(repository, new WindowsChromeSystem(), new CdpConnectionFactory(),
            prepareBrowser: (_, _) => Task.FromResult(executable), previewDuringPreparation: true);
        bool english = false, cleaned = false; long openedMs = 0; string profile = "";
        try
        {
            await browser.PrepareAsync("http://127.0.0.1:1", token); openedMs = watch.ElapsedMilliseconds;
            profile = Directory.EnumerateDirectories(Path.Combine(repository.Root, "browser-envs", "new-account"), "auth-*").Single();
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            while (!File.Exists(portFile)) await Task.Delay(100, token);
            var port = await File.ReadAllLinesAsync(portFile, token);
            await using var connection = await new CdpConnectionFactory().ConnectAsync(new($"ws://127.0.0.1:{port[0]}{port[1]}"), token);
            System.Text.Json.Nodes.JsonObject? target = null;
            for (var i = 0; i < 30 && target is null; i++)
            {
                var targets = await connection.CallAsync("Target.getTargets", new(), null, token);
                target = (targets["targetInfos"] as System.Text.Json.Nodes.JsonArray)?.OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault(t => t["url"]?.GetValue<string>() == new Uri(Path.Combine(profile, "preparing.html")).AbsoluteUri);
                if (target is null) await Task.Delay(100, token);
            }
            if (target is null) throw new InvalidOperationException("Preparation tab not found.");
            var attached = await connection.CallAsync("Target.attachToTarget", new() { ["targetId"] = target["targetId"]!.GetValue<string>(), ["flatten"] = true }, null, token);
            var session = attached["sessionId"]!.GetValue<string>();
            var value = await connection.CallAsync("Runtime.evaluate", new() { ["expression"] = "JSON.stringify({language:navigator.language,languages:navigator.languages,title:document.title})", ["returnByValue"] = true }, session, token);
            var observed = System.Text.Json.Nodes.JsonNode.Parse(value["result"]!["value"]!.GetValue<string>())!;
            english = observed["language"]?.GetValue<string>() == "en-US" && observed["title"]?.GetValue<string>() == "Preparing sign-in";
            var screenshot = await connection.CallAsync("Page.captureScreenshot", new() { ["format"] = "png" }, session, token);
            await File.WriteAllBytesAsync(Path.Combine(output, "preparing.png"), Convert.FromBase64String(screenshot["data"]!.GetValue<string>()), token);
            await File.WriteAllTextAsync(Path.Combine(output, "browser-language.json"), observed.ToJsonString(), token);
        }
        finally
        {
            cleaned = (await browser.CleanupAsync(null, default)).Success;
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = english && cleaned, english, cleaned, openedMs,
                externalNavigations = 0, actualAccountRequests = 0, proxy = "blocked loopback test port", note = "Installed browser preview only; firewall setup and login latency not measured." }));
        }
        if (!english || !cleaned) throw new InvalidOperationException("English preview or cleanup not confirmed.");
        Console.WriteLine($"PASS native English preview and cleanup; process launch {openedMs} ms; no account requests.");
    }
    public static async Task Run(string executable, string output)
    {
        executable = Path.GetFullPath(executable); output = Path.GetFullPath(output);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "chrome.exe" || Directory.Exists(output) || File.Exists(output))
            throw new ArgumentException("Provide an installed Chrome executable and a new evidence directory.");
        PhysicalFileStorage.RejectLinks(output);
        Directory.CreateDirectory(output);
        var profile = Path.Combine(output, "isolated-browser"); Directory.CreateDirectory(profile);
        profile = WindowsDesktopLaunchSystem.ResolveRealPath(profile);
        var system = new WindowsChromeSystem();
        BrowserIdentity? started = null;
        bool matched = false, knownMatched = false, staleRejected = false, closed = false, removed = false;
        try
        {
            var arguments = ChromeArguments.Build(profile, new("http://127.0.0.1:1"), new("about:blank"), false).ToList();
            arguments.InsertRange(0, ["--headless=new", "--disable-background-networking", "--disable-component-update", "--host-resolver-rules=MAP * ~NOTFOUND"]);
            started = await system.StartTrackedAsync(executable, arguments, default);
            if (started is null) throw new InvalidOperationException("No owned browser process was created.");
            IReadOnlyList<BrowserIdentity>? observed = null;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                observed = await system.ObserveAsync(executable, profile, default);
                if (observed is { Count: > 0 }) break;
                await Task.Delay(250);
            }
            matched = observed?.Any(item => item == started) == true
                && new WindowsProcessSystem().Snapshot().Any(item => item.Pid == started.Pid && item.CreationTime == started.CreationTime
                    && item.Executable.Equals(started.Executable, StringComparison.OrdinalIgnoreCase));
            knownMatched = (await system.ObserveKnownAsync(executable, profile, [started], default))?.SingleOrDefault() == started;
            staleRejected = (await system.ObserveKnownAsync(executable, profile, [started with { CreationTime = started.CreationTime + 1 }], default)) is { Count: 0 };
            if (!knownMatched || !staleRejected) throw new InvalidOperationException("Native fast observer must confirm exact identity and reject stale PID identity.");
            if (!matched) throw new InvalidOperationException("CIM profile observation and native ownership must match exactly.");
        }
        finally
        {
            if (started is not null) closed = await system.CloseOwnedAsync([started], default);
            if (closed)
            {
                var parent = WindowsDesktopLaunchSystem.ResolveRealPath(output);
                if (!profile.Equals(Path.Combine(parent, "isolated-browser"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Profile is outside this check's new evidence directory.");
                PhysicalFileStorage.RejectLinks(profile); Directory.Delete(profile, true); removed = true;
            }
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new {
                passed = matched && knownMatched && staleRejected && closed && removed, matched, knownMatched, staleRejected, closed, removed, actualInstalledChrome = true,
                actualAccountRequests = false, requestedExternalNavigations = false, actualProxyChanges = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (!closed || !removed) throw new InvalidOperationException("Owned browser cleanup was not confirmed.");
        Console.WriteLine("PASS installed-browser native identity and exact-session cleanup; no account requests");
    }
}
