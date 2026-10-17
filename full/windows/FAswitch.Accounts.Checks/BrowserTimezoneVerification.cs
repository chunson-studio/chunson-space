using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FAswitch.Accounts;
using FAswitch.Core;

internal static class BrowserTimezoneVerification
{
    public static async Task RunAsync(string executable, string manager, string directory)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory)) throw new IOException("Use a new evidence directory.");
        Directory.CreateDirectory(directory);
        var systemZone = TimeZoneInfo.Local.Id;
        var roots = new List<BrowserIdentity>();
        var chrome = new WindowsChromeSystem(() => executable);
        var checks = new List<object>();
        void Check(bool passed, string name) { checks.Add(new { name, passed }); if (!passed) throw new IOException(name); }
        await using var east = new LocalProxy();
        await using var west = new LocalProxy();
        var samples = new List<object>();
        var closed = false;
        try
        {
            var accountRoot = Path.Combine(directory, "accounts");
            var profiles = new AccountBrowserProfiles(accountRoot);
            var controller = new BrowserTimezoneController(manager);
            foreach (var (proxy, timezone) in new[] { (east, "America/New_York"), (west, "Europe/London") })
            {
                var profile = profiles.Create();
                var args = ChromeArguments.Build(profile, new(proxy.Address, Locale: "en-US"), new("about:blank"), false).ToList();
                args.InsertRange(0, ["--remote-debugging-port=0", "--headless=new", "--disable-background-networking", "--disable-popup-blocking"]);
                var root = await chrome.StartTrackedAsync(executable, args, default) ?? throw new IOException("No test browser identity.");
                roots.Add(root);
                await Task.Delay(200);
                await controller.EnsureAsync(accountRoot, root, timezone, default);
                var endpoint = File.ReadAllLines(Path.Combine(profile, "DevToolsActivePort"));
                await using var navigation = await new CdpConnectionFactory().ConnectAsync(new($"ws://127.0.0.1:{endpoint[0]}{endpoint[1]}"), default);
                await navigation.CallAsync("Target.createTarget", new() { ["url"] = "http://timezone-fixture.invalid/main" }, null, default);
                await proxy.WaitAsync(4);
                foreach (var kind in new[] { "main", "frame", "worker", "popup" })
                {
                    var reading = proxy.Samples.SingleOrDefault(sample => sample.Kind == kind);
                    Check(reading?.Timezone == timezone, timezone + " " + kind + " timezone");
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
                    Check(reading!.January == -(int)zone.GetUtcOffset(new DateTime(2026, 1, 1)).TotalMinutes
                        && reading.July == -(int)zone.GetUtcOffset(new DateTime(2026, 7, 1)).TotalMinutes, timezone + " " + kind + " seasonal offset");
                }
                samples.Add(new { timezone, proxy = proxy.Address, readings = proxy.Samples.ToArray() });
            }
            Check(roots.Count == 2 && roots[0].Pid != roots[1].Pid && roots[0].ProfileDirectory != roots[1].ProfileDirectory, "two distinct browser processes and profiles");
            Check(await chrome.CloseOwnedAsync([roots[0]], default), "closing first browser is exact and confirmed");
            await Task.Delay(900);
            Check(BrowserTimezoneController.IsAlive(roots[1].Pid, roots[1].CreationTime, roots[1].Executable), "second browser remains alive");
            var remainingEndpoint = File.ReadAllLines(Path.Combine(roots[1].ProfileDirectory, "DevToolsActivePort"));
            await using (var navigation = await new CdpConnectionFactory().ConnectAsync(new($"ws://127.0.0.1:{remainingEndpoint[0]}{remainingEndpoint[1]}"), default))
                await navigation.CallAsync("Target.createTarget", new() { ["url"] = "http://timezone-fixture.invalid/after" }, null, default);
            await west.WaitAsync(5);
            Check(west.Samples.Single(sample => sample.Kind == "after").Timezone == "Europe/London", "remaining browser new tab retains its timezone after first browser closes");
            var firstState = JsonSerializer.Deserialize<BrowserTimezoneState>(File.ReadAllText(Path.Combine(roots[0].ProfileDirectory, BrowserTimezoneController.StateFile)))!;
            Check(!BrowserTimezoneController.IsAlive(firstState.HelperPid, firstState.HelperCreated), "first controller exits with its browser");
            TimeZoneInfo.ClearCachedData();
            Check(TimeZoneInfo.Local.Id == systemZone, "Windows timezone unchanged");
            closed = await chrome.CloseOwnedAsync([roots[1]], default);
            Check(closed, "second browser closes independently");
            await Task.Delay(900);
            var secondState = JsonSerializer.Deserialize<BrowserTimezoneState>(File.ReadAllText(Path.Combine(roots[1].ProfileDirectory, BrowserTimezoneController.StateFile)))!;
            Check(!BrowserTimezoneController.IsAlive(secondState.HelperPid, secondState.HelperCreated), "second controller exits with its browser");
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { passed = true, actualAccountRequests = false, externalNetwork = false, headless = true, systemZone, checks, samples, closed }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "failure.json"), JsonSerializer.Serialize(new { error = error.Message, checks, east = east.Samples.ToArray(), west = west.Samples.ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally
        {
            if (!closed && roots.Count > 0) _ = await chrome.CloseOwnedAsync(roots, default);
        }
    }
    private sealed record Sample(string Kind, string Timezone, int January, int July);
    private sealed class LocalProxy : IAsyncDisposable
    {
        private readonly TcpListener server = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopped = new();
        private readonly Task worker;
        private readonly ConcurrentBag<Task> connections = [];
        public ConcurrentBag<Sample> Samples { get; } = [];
        public string Address { get; }
        public LocalProxy()
        { server.Start(); Address = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port; worker = AcceptAsync(); }
        private async Task AcceptAsync()
        {
            try { while (!stopped.IsCancellationRequested) { var client = await server.AcceptTcpClientAsync(stopped.Token); connections.Add(RespondAsync(client)); } }
            catch (OperationCanceledException) { }
        }
        private async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token); timeout.CancelAfter(10000);
                    var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
                    var first = await reader.ReadLineAsync(timeout.Token) ?? "";
                    string? line; do { line = await reader.ReadLineAsync(timeout.Token); } while (!string.IsNullOrEmpty(line));
                    var parts = first.Split(' ');
                    if (parts.Length < 2 || parts[0] != "GET" || !Uri.TryCreate(parts[1], UriKind.Absolute, out var uri)
                        || !uri.Host.EndsWith("timezone-fixture.invalid", StringComparison.Ordinal)) return;
                    var path = uri.AbsolutePath;
                    var kind = path == "/main" ? "main" : path == "/frame" ? "frame" : path == "/popup" ? "popup" : path == "/after" ? "after" : "worker";
                    var script = "const data={Kind:" + JsonSerializer.Serialize(kind) + ",Timezone:Intl.DateTimeFormat().resolvedOptions().timeZone,January:new Date(2026,0,1).getTimezoneOffset(),July:new Date(2026,6,1).getTimezoneOffset()};fetch('/report?'+encodeURIComponent(JSON.stringify(data)));";
                    string body;
                    if (path == "/report") { Samples.Add(JsonSerializer.Deserialize<Sample>(Uri.UnescapeDataString(uri.Query[1..]))!); body = "ok"; }
                    else if (path == "/worker.js") body = script;
                    else body = "<!doctype html><meta charset=utf-8><title>Local timezone verification</title><script>" + script
                        + (path == "/main" ? "new Worker('/worker.js');window.open('http://popup.timezone-fixture.invalid/popup');" : "") + "</script>"
                        + (path == "/main" ? "<iframe src='http://child.timezone-fixture.invalid/frame'></iframe>" : "");
                    var response = "HTTP/1.1 200 OK\r\nContent-Type: " + (path == "/worker.js" ? "application/javascript" : "text/html") + "; charset=utf-8\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(response), timeout.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
            }
        }
        public async Task WaitAsync(int count)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(18);
            while (Samples.Select(sample => sample.Kind).Distinct().Count() < count && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
            if (Samples.Select(sample => sample.Kind).Distinct().Count() < count) throw new IOException("Local timezone pages did not complete: " + string.Join(',', Samples.Select(sample => sample.Kind)));
        }
        public async ValueTask DisposeAsync() { stopped.Cancel(); server.Stop(); await worker; await Task.WhenAll(connections); stopped.Dispose(); }
    }
}
