using System.Text.Json;
using FAswitch.Accounts;
using FAswitch.NodeTesting;
using FAswitch.Proxy;

static class NativeManagedRouteAcceptance
{
    public static async Task<int> RunAsync(string sourceRoot, string subscriptionId, string nodeId, string expectedIp, string executable, string output)
    {
        output = Path.GetFullPath(output); sourceRoot = Path.GetFullPath(sourceRoot);
        if (Directory.Exists(output)) throw new IOException("Use a new evidence directory.");
        string Hash(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        var files = new[] { "config.yaml", "database.sqlite", "shared_preferences.json" }.Select(name => Path.Combine(sourceRoot, name)).Where(File.Exists).ToDictionary(path => path, Hash);
        NodeCandidate Resolve()
        {
            var subscription = SubscriptionCatalogReader.Read(ProxyClientKind.FlClash, sourceRoot).Subscriptions.Single(item => item.Id == subscriptionId);
            return new NodeCatalogImporter().Read(subscription.File, sourceRoot).Nodes.First(item => item.Id == nodeId && item.Supported);
        }
        var node = Resolve(); Directory.CreateDirectory(output);
        var raw = new HttpAccountTransport(); var quality = new NetCoffeeService(raw);
        var cache = new IpScoreCache(Path.Combine(output, "netcoffee-scores.json"));
        ManagedNodeRoutes Create() => new(Path.Combine(output, "bindings.json"), _ => Resolve(), binding =>
            new WindowsMihomoProbeRuntime(executable, Path.Combine(output, "jobs", binding.Id), preferredPort: binding.Port),
            async (proxy, token) => {
                var measured = await quality.ProbeScreeningExitAsync(proxy, token);
                if (!measured.HasVerifiedExit) return measured;
                if (cache.Find(measured.Ip) is { } cached) return cached.Apply(measured);
                var report = await quality.AddScoreAsync(measured, proxy, token);
                if (report.Score is { Quality: >= 0 and <= 100 } score) { cache.Put(measured.Ip, score, report); cache.Save(); }
                return report;
            });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        // This supplied expected IP is only a selection precondition. Both native sessions must independently verify it and score >=85.
        var selection = new IpQualityReport(true, expectedIp, "", "", "", "", "", null, null, null, null, null,
            DateTimeOffset.UtcNow, "Selection from earlier measured result; runtime verification required", "selection", new(15, "minimum selection threshold"));
        string proxy = ""; IpQualityReport? first = null, resumed = null; string error = ""; bool cleaned = false;
        try
        {
            await using (var firstOwner = Create())
            {
                proxy = await firstOwner.BindAsync(subscriptionId, node, selection, deadline.Token);
                first = await firstOwner.ProbeAdmissionAsync(proxy, deadline.Token);
            }
            await using (var nextOwner = Create()) resumed = await nextOwner.ProbeAdmissionAsync(proxy, deadline.Token);
            cleaned = !Directory.EnumerateDirectories(Path.Combine(output, "jobs"), "job-*", SearchOption.AllDirectories).Any();
        }
        catch (Exception failure) { error = ErrorRedaction.Scrub(failure.Message); }
        var unchanged = files.All(file => Hash(file.Key) == file.Value);
        var passed = first?.Admission.Allowed == true && resumed?.Admission.Allowed == true && first.Ip == expectedIp && resumed.Ip == expectedIp && cleaned && unchanged;
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed, proxy, first, resumed, cleaned, unchanged, error, accountRequests = 0, mainRouteChanged = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { passed, proxy, ip = resumed?.Ip, quality = resumed?.Admission.QualityScore, cleaned, unchanged, error }));
        return passed ? 0 : 1;
    }
}
