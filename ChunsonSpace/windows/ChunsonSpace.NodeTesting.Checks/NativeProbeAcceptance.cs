using System.Diagnostics;
using System.Text.Json;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.NodeTesting;
using ChunsonSpace.Proxy;

static class NativeProbeAcceptance
{
    public static async Task<int> RunAsync(string sourceRoot, string subscriptionId, string executable, string output, int maximum)
    {
        if (maximum is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maximum));
        output = Path.GetFullPath(output); sourceRoot = Path.GetFullPath(sourceRoot);
        if (Directory.Exists(output)) throw new IOException("Use a new evidence directory.");
        string HashShared(string path)
        {
            PhysicalFileStorage.RejectLinks(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
        }
        var originals = new[] { "config.yaml", "database.sqlite", "shared_preferences.json" }.Select(name => Path.Combine(sourceRoot, name)).Where(File.Exists).ToDictionary(path => path, HashShared);
        var subscription = SubscriptionCatalogReader.Read(ProxyClientKind.FlClash, sourceRoot).Subscriptions.Single(s => s.Id == subscriptionId);
        var nodes = new NodeCatalogImporter().Read(subscription.File, sourceRoot).Nodes.Where(n => n.Supported).Take(maximum).ToArray();
        Directory.CreateDirectory(output);
        var transport = new HttpAccountTransport();
        var quality = new NetCoffeeService(transport);
        var runtime = new WindowsMihomoProbeRuntime(executable, Path.Combine(output, "jobs"));
        var batch = new BatchNodeProbeService(runtime, quality, new ProxyLatencyService(transport), new IpScoreCache(Path.Combine(output, "ip-scores.json")));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var watch = Stopwatch.StartNew();
        var report = await batch.ProbeAsync(nodes, token: cancel.Token);
        watch.Stop();
        var unchanged = originals.All(pair => HashShared(pair.Key) == pair.Value);
        var clean = !Directory.EnumerateDirectories(Path.Combine(output, "jobs"), "job-*").Any();
        var passed = report.Results.Count == nodes.Length && !report.Cancelled && !report.StopUnconfirmed && unchanged && clean;
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed, elapsedSeconds = watch.Elapsed.TotalSeconds, testedNodes = nodes.Length,
            runtime.PeakConcurrentSessions, runtime.BinaryVerificationCount, unchangedClientFiles = unchanged, allTemporaryJobsRemoved = clean, accountRequests = 0, proxySelectionChanged = false, report }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { passed, seconds = watch.Elapsed.TotalSeconds, nodes = nodes.Length, report.UniqueIps, report.ScoreRequests, report.ReusedScores, successfulReports = report.Results.Count(r => r.Success), timeouts = report.Results.Count(r => r.Latency?.State == ProxyLatencyState.Timeout), unchanged, clean }));
        return passed ? 0 : 1;
    }
}
