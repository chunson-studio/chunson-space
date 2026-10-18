using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;

internal static class RendererEgressProbe
{
    public static async Task RunAsync(string browser, string proxy, string endpoint, string output)
    {
        browser = Path.GetFullPath(browser); output = Path.GetFullPath(output);
        proxy = ChromeArguments.RequireLoopbackProxy(HttpAccountTransport.RequireHttpProxy(proxy));
        if (!File.Exists(browser) || !InstalledBrowsers.SupportedExecutable(browser) || Directory.Exists(output)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var destination) || destination.Scheme != "https" || destination.UserInfo.Length > 0)
            throw new ArgumentException("Use an installed browser, explicit loopback HTTP proxy, public HTTPS endpoint and new output directory.");
        PhysicalFileStorage.RejectLinks(output); Directory.CreateDirectory(output);
        var profile = Path.Combine(output, "isolated-browser"); Directory.CreateDirectory(profile);
        var system = new NativeRecordingChrome(browser);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var token = deadline.Token;
        var root = await system.StartTrackedAsync(browser,
            ["--headless=new", "--disable-gpu", "--disable-background-networking", "--no-first-run", "--no-default-browser-check",
                "--user-data-dir=" + profile, "--proxy-server=" + proxy, "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1", "about:blank"], token)
            ?? throw new IOException("Probe browser was not created.");
        var failures = new ConcurrentQueue<JsonObject>();
        JsonObject? blank = null; JsonObject? navigated = null; bool closed = false;
        try
        {
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            while (!File.Exists(portFile)) await Task.Delay(100, token);
            var lines = await File.ReadAllLinesAsync(portFile, token);
            await using var rpc = await new ManagedRuntimeCdpFactory().ConnectAsync(new($"ws://127.0.0.1:{int.Parse(lines[0])}{lines[1]}"), token);
            using var eventsStopped = new CancellationTokenSource();
            var events = Task.Run(async () =>
            {
                try
                {
                    await foreach (var packet in rpc.Events(eventsStopped.Token))
                        if (packet["method"]?.GetValue<string>() == "Network.loadingFailed" && packet["params"] is JsonObject error)
                            failures.Enqueue(new() { ["errorText"] = error["errorText"]?.DeepClone(), ["blockedReason"] = error["blockedReason"]?.DeepClone(), ["corsErrorStatus"] = error["corsErrorStatus"]?.DeepClone() });
                }
                catch (OperationCanceledException) when (eventsStopped.IsCancellationRequested) { }
            });
            try
            {
                var targets = await rpc.CallAsync("Target.getTargets", new(), null, token);
                var page = (targets["targetInfos"] as JsonArray)!.OfType<JsonObject>().First(target => target["type"]?.GetValue<string>() == "page");
                var session = (await rpc.CallAsync("Target.attachToTarget", new() { ["targetId"] = page["targetId"]!.DeepClone(), ["flatten"] = true }, null, token))["sessionId"]!.GetValue<string>();
                await rpc.CallAsync("Network.enable", new(), session, token);
                var expression = "(async()=>{try{const r=await fetch(" + JsonSerializer.Serialize(endpoint) + ",{cache:'no-store',credentials:'omit'});return JSON.stringify({ok:r.ok,status:r.status,body:await r.text()});}catch(e){return JSON.stringify({error:String(e)});}})()";
                var evaluated = await rpc.CallAsync("Runtime.evaluate", new() { ["expression"] = expression, ["awaitPromise"] = true, ["returnByValue"] = true }, session, token);
                blank = JsonNode.Parse(evaluated["result"]!["value"]!.GetValue<string>()) as JsonObject;
                var navigation = await rpc.CallAsync("Page.navigate", new() { ["url"] = endpoint }, session, token);
                if (navigation["errorText"] is { } navigationError) navigated = new() { ["error"] = navigationError.DeepClone() };
                else for (var attempt = 0; attempt < 100 && navigated is null; attempt++)
                {
                    var body = await rpc.CallAsync("Runtime.evaluate", new() { ["expression"] = "document.querySelector('pre')?.textContent || document.body?.innerText || ''", ["returnByValue"] = true }, session, token);
                    try { navigated = JsonNode.Parse(body["result"]?["value"]?.GetValue<string>() ?? "") as JsonObject; } catch (JsonException) { }
                    if (navigated is null) await Task.Delay(100, token);
                }
            }
            finally { eventsStopped.Cancel(); await events; }
        }
        finally
        {
            closed = await system.CloseOwnedAsync([root], CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                blankFetch = blank, topLevelNavigation = navigated, networkFailures = failures.ToArray(), closed,
                accountRequests = 0, scoreRequests = 0, codexLaunches = 0,
                note = "Isolated installed browser; public endpoint only; explicit existing proxy; no account profile, OAuth or Codex switch."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (!closed) throw new IOException("Probe browser cleanup was not confirmed.");
        Console.WriteLine("Renderer egress comparison saved; exact probe browser closed.");
    }
}
