using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChunsonLogin;

public static class BrowserChecks
{
    public static void Run(string fixtureRoot, Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetFullPath(fixtureRoot), "browser-checks-" + Guid.NewGuid().ToString("N"));
        var installationRoot = Path.Combine(root, "fake installations");
        check(BrowserLocator.Find([installationRoot]).Count == 0, "no installed browser produces an empty choice list");
        var installedEdgePath = Path.Combine(installationRoot, "Microsoft", "Edge", "Application", "msedge.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(installedEdgePath)!);
        File.WriteAllText(installedEdgePath, "fixture-only-not-executable");
        var edgeOnly = BrowserLocator.Find([installationRoot]);
        check(edgeOnly is [{ Name: "Edge", ProfileKey: "edge" }] && edgeOnly[0].Executable == installedEdgePath,
            "an Edge-only installation is discovered without requiring Chrome");
        check(new LoginViewModel(null, null, edgeOnly).SelectedBrowser == edgeOnly[0], "Edge is automatically selected when it is the only installed browser");
        var installedChromePath = Path.Combine(installationRoot, "Google", "Chrome", "Application", "chrome.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(installedChromePath)!);
        File.WriteAllText(installedChromePath, "fixture-only-not-executable");
        var both = BrowserLocator.Find([installationRoot, installationRoot]);
        check(both.Select(item => item.ProfileKey).SequenceEqual(new[] { "chrome", "edge" }), "both browsers are offered once in stable selection order");
        check(new LoginViewModel(null, null, BrowserLocator.Find([])).SelectedBrowser is null, "missing browser installation leaves no fabricated default selection");
        var chrome = new BrowserInstallation("Chrome", Path.Combine(root, "fake browser", "chrome.exe"), "chrome");
        var edge = new BrowserInstallation("Edge", Path.Combine(root, "fake browser", "msedge.exe"), "edge");
        bool? observedRunning = false;
        var observations = 0;
        var service = new DedicatedBrowser(Path.Combine(root, "英文 data"), (_, _) => { observations++; return observedRunning; });
        const string proxy = "http://127.0.0.1:7890";
        var start = service.Prepare(chrome, proxy);
        var arguments = start.ArgumentList.ToArray();
        var profile = arguments.Single(value => value.StartsWith("--user-data-dir=", StringComparison.Ordinal))[16..];
        check(start.FileName == chrome.Executable && !start.UseShellExecute, "browser uses a structured executable and arguments without a shell");
        check(profile == Path.Combine(root, "英文 data", "browser-profiles", "chrome"), "browser profile has a fixed independent absolute path, including spaces and Unicode");
        check(arguments.Contains("--lang=en-US") && arguments.Contains("--accept-lang=en-US,en"), "browser receives English UI and web language preferences");
        check(arguments.Contains("--proxy-server=" + proxy) && arguments.Count(value => value.StartsWith("--proxy-server=", StringComparison.Ordinal)) == 1,
            "browser uses exactly the supplied local FlClash HTTP listener");
        check(arguments.Where(value => value.StartsWith("https://", StringComparison.Ordinal)).SequenceEqual(new[] { "https://chatgpt.com/", "https://ippure.com/claude" }),
            "only ChatGPT and the requested IPPure Claude page are opened");
        check(arguments.All(value => !value.StartsWith("--remote-debugging", StringComparison.Ordinal)
            && !value.StartsWith("--user-agent", StringComparison.Ordinal) && value != "--no-sandbox" && value != "--no-proxy-server"),
            "browser does not enable debugging, fingerprint overrides, sandbox bypass, or direct-route override");

        var localState = Path.Combine(profile, "Local State");
        var preferences = Path.Combine(profile, "Default", "Preferences");
        check(JsonNode.Parse(File.ReadAllText(localState))?["intl"]?["app_locale"]?.GetValue<string>() == "en-US",
            "new browser Local State selects English");
        check(JsonNode.Parse(File.ReadAllText(preferences))?["intl"]?["accept_languages"]?.GetValue<string>() == "en-US,en"
            && JsonNode.Parse(File.ReadAllText(preferences))?["intl"]?["selected_languages"]?.GetValue<string>() == "en-US,en",
            "new profile records English language preferences");
        var savedPreferences = JsonNode.Parse(File.ReadAllText(preferences))!.AsObject();
        savedPreferences["fixture_preserve"] = "signed-in-data-is-not-copied-or-deleted";
        var exactPreferences = savedPreferences.ToJsonString();
        File.WriteAllText(preferences, exactPreferences);
        var cookieMarker = Path.Combine(profile, "Default", "fixture-web-state");
        File.WriteAllText(cookieMarker, "fixture-only");
        var reopened = service.Prepare(chrome, proxy);
        check(reopened.ArgumentList.SequenceEqual(start.ArgumentList) && File.ReadAllText(preferences) == exactPreferences
            && File.ReadAllText(cookieMarker) == "fixture-only", "reopening keeps the same profile and leaves existing preferences and web data untouched");
        check(observations == 0, "same-route URL handoff does not require process inspection or waiting for the short launcher process");
        var profilePattern = DedicatedBrowser.ProfileCommandPattern(profile);
        check(Regex.IsMatch("chrome.exe \"--user-data-dir=" + profile + "\" https://chatgpt.com/", profilePattern)
            && Regex.IsMatch("chrome.exe --user-data-dir=\"" + profile + "\"", profilePattern)
            && Regex.IsMatch("chrome.exe --user-data-dir \"" + profile + "\"", profilePattern),
            "process ownership pattern recognizes Windows quoted user-data-dir forms");
        check(!Regex.IsMatch("chrome.exe \"--user-data-dir=" + profile + "-other\"", profilePattern)
            && !Regex.IsMatch("chrome.exe --user-data-dir=C:\\other", profilePattern),
            "process ownership pattern does not confuse neighbouring or unrelated profiles");
        var edgeStart = service.Prepare(edge, proxy);
        var edgeProfile = edgeStart.ArgumentList.Single(value => value.StartsWith("--user-data-dir=", StringComparison.Ordinal))[16..];
        check(edgeProfile != profile && edgeProfile.EndsWith(Path.DirectorySeparatorChar + "edge", StringComparison.Ordinal)
            && !File.Exists(Path.Combine(edgeProfile, "Default", "fixture-web-state")), "Chrome and Edge never share browser data");

        File.WriteAllText(preferences, "{broken-json");
        Throws(() => service.Prepare(chrome, proxy), "corrupt preferences refuse launch preparation without resetting the file");
        check(File.ReadAllText(preferences) == "{broken-json" && File.ReadAllText(cookieMarker) == "fixture-only", "corrupt preferences and existing web data remain recoverable");
        File.WriteAllText(preferences, exactPreferences);
        var goodLocalState = File.ReadAllText(localState);
        File.WriteAllText(localState, "[]");
        Throws(() => service.Prepare(chrome, proxy), "non-object Local State is refused rather than overwritten");
        check(File.ReadAllText(localState) == "[]", "invalid Local State is preserved exactly");
        File.WriteAllText(localState, goodLocalState);

        foreach (var invalid in new[] { "", "https://127.0.0.1:7890", "http://user:account-ec22fda0d8@example.invalid:7890", "http://203.0.113.8:7890", "http://127.0.0.1:7890/path", "http://127.0.0.1:7890/?x=1", "http://127.0.0.1:7890/#fragment" })
            Throws(() => service.Prepare(chrome, invalid), "invalid or credential-bearing proxy is rejected");
        var routePath = Path.Combine(profile, "chunson-login-route.json");
        var originalRoute = File.ReadAllText(routePath);
        File.Delete(routePath);
        Throws(() => service.Prepare(chrome, proxy), "an interrupted first-time route write cannot reset an existing profile");
        check(!File.Exists(routePath) && File.ReadAllText(preferences) == exactPreferences && File.ReadAllText(cookieMarker) == "fixture-only",
            "missing route metadata preserves every existing browser data file");
        File.WriteAllText(routePath, originalRoute);
        var partialRoot = Path.Combine(root, "partial-first-start");
        var partialProfile = Path.Combine(partialRoot, "browser-profiles", "chrome");
        Directory.CreateDirectory(partialProfile);
        var partialLocalState = Path.Combine(partialProfile, "Local State");
        File.WriteAllText(partialLocalState, goodLocalState);
        Throws(() => new DedicatedBrowser(partialRoot).Prepare(chrome, proxy), "interrupted preference initialization is reported without recreating an existing profile");
        check(File.ReadAllText(partialLocalState) == goodLocalState && !Directory.Exists(Path.Combine(partialProfile, "Default")),
            "partial initialization leaves the incomplete directory unchanged for recovery");
        observedRunning = true;
        Throws(() => service.Prepare(chrome, "http://127.0.0.1:7891"), "a live browser cannot silently ignore a changed proxy through URL handoff");
        check(File.ReadAllText(routePath) == originalRoute, "a refused proxy change preserves the original route record");
        observedRunning = null;
        Throws(() => service.Prepare(chrome, "http://127.0.0.1:7891"), "unknown process state cannot admit a changed proxy");
        check(File.ReadAllText(routePath) == originalRoute, "unknown process state preserves the original route record");
        observedRunning = false;
        check(service.Prepare(chrome, "http://[::1]:7890").ArgumentList.Contains("--proxy-server=http://[::1]:7890"), "a confirmed stopped profile accepts an IPv6 loopback proxy");
        check(JsonNode.Parse(File.ReadAllText(routePath))?["proxy"]?.GetValue<string>() == "http://[::1]:7890"
            && File.ReadAllText(preferences) == exactPreferences && File.ReadAllText(cookieMarker) == "fixture-only", "network changes preserve existing preferences and web login data");
        _ = service.Prepare(chrome, proxy);
        Throws(() => service.Prepare(chrome with { ProfileKey = "../outside" }, proxy), "browser profile keys cannot escape the tool directory");
        Throws(() => service.Prepare(chrome with { Executable = Path.Combine(root, "wrong.exe") }, proxy), "browser executable must match its profile type");
        Throws(() => new DedicatedBrowser("relative-path").Prepare(chrome, proxy), "relative profile roots are rejected");
        Throws(() => service.Open(chrome, proxy), "opening a missing browser fails before any process is started");

        void Throws(Action action, string label)
        {
            var refused = false;
            try { action(); }
            catch (Exception error) when (error is ArgumentException or IOException) { refused = true; }
            check(refused, label);
        }
    }
}
