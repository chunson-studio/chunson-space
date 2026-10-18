using System.Text.Json;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;

internal static class BrowserSessionInspection
{
    public static async Task Run(string executable, string accountRoot, string profile, string output)
    {
        executable = Path.GetFullPath(executable); accountRoot = Path.GetFullPath(accountRoot);
        profile = new AccountBrowserProfiles(accountRoot).Validate(profile); output = Path.GetFullPath(output);
        if (!File.Exists(executable) || !InstalledBrowsers.SupportedExecutable(executable) || File.Exists(output) || Directory.Exists(output))
            throw new ArgumentException("Use an installed browser, a valid project profile, and a new report directory.");
        PhysicalFileStorage.RejectLinks(output);
        var browserRoot = Path.Combine(accountRoot, "browser-envs");
        var physicalRoot = WindowsDesktopLaunchSystem.ResolveRealPath(browserRoot);
        var physicalProfile = WindowsDesktopLaunchSystem.ResolveRealPath(profile);
        var roots = await new WindowsChromeSystem().ObserveAsync(executable, profile, default);
        var routePath = Path.Combine(profile, "chunson-browser-route.json");
        var recorded = File.Exists(routePath) ? JsonNode.Parse(await File.ReadAllTextAsync(routePath))?["Roots"] as JsonArray : null;
        var snapshot = new WindowsProcessSystem().Snapshot();
        var identities = recorded?.OfType<JsonObject>().Select(root => new {
            pid = root["Pid"]?.GetValue<int>() ?? 0,
            exactProcessAlive = snapshot.Any(process => process.Pid == root["Pid"]?.GetValue<int>()
                && process.CreationTime == root["CreationTime"]?.GetValue<long>()
                && process.Executable.Equals(root["Executable"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
        }).ToArray();
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "inspection.json"), JsonSerializer.Serialize(new {
            readOnly = true, actualAccountRequests = false, browserLaunches = 0, stoppedProcesses = 0, profileDeletions = 0,
            accountRoot, profile, physicalRoot, physicalProfile,
            oldScopeMatches = profile.StartsWith(physicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            canonicalScopeMatches = physicalProfile.StartsWith(physicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            cleanupResolvedProfile = ChromeLoginBrowser.ResolveCleanupProfileDirectory(accountRoot, profile),
            observationKnown = roots is not null, observedRootCount = roots?.Count, recordedRoots = identities
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Read-only browser session inspection saved; no browser or account action performed.");
    }
}
