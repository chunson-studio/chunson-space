using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ChunsonSpace.Accounts;

/// <summary>Offscreen, isolated local Chromium. No account transport, credentials, or file navigation.</summary>
public sealed class LocalArtworkSession : IAsyncDisposable
{
    private static readonly HashSet<Process> owned = [];
    private static bool shuttingDown;
    private readonly Process process;
    private readonly string home;
    private readonly ICdpConnection connection;
    private readonly string session;
    private readonly CancellationTokenSource stopped = new();
    private readonly Task events;
    private readonly SemaphoreSlim interaction = new(1,1);
    private LocalArtworkSession(Process process, string home, ICdpConnection connection, string session)
    {
        this.process = process; this.home = home; this.connection = connection; this.session = session;
        events = DrainEvents();
    }
    public static Task<LocalArtworkSession> OpenAsync(string executable, string content, CancellationToken token)
        => OpenDocumentAsync(executable, VisualTestArtifact.Wrap(content), token);
    internal static async Task<LocalArtworkSession> OpenDocumentAsync(string executable, string safeDocument, CancellationToken token)
    {
        var home = Path.Combine(Path.GetTempPath(), "ChunsonSpace-artwork-" + Guid.NewGuid().ToString("N"));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--disable-component-update", "--disable-extensions", "--disable-sync", "--disable-features=MediaRouter,OptimizationHints", "--proxy-server=http://127.0.0.1:9", "--proxy-bypass-list=<-loopback>", "--host-resolver-rules=MAP * ~NOTFOUND", "--user-data-dir=" + home, "--window-size=960,600", "--hide-scrollbars", "--remote-debugging-port=0", "about:blank" }) info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info };
        using var cancellation = token.Register(() => Kill(process));
        ICdpConnection? connection = null;
        try
        {
            lock(owned) { token.ThrowIfCancellationRequested(); if(shuttingDown) throw new OperationCanceledException(); process.Start(); owned.Add(process); }
            _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            string[] address = [];
            while (address.Length < 2)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("本地作品引擎已退出。");
                var endpoint = Path.Combine(home, "DevToolsActivePort");
                if (File.Exists(endpoint)) try { address = await File.ReadAllLinesAsync(endpoint, deadline.Token); } catch (IOException) { }
                if (address.Length < 2) await Task.Delay(80, deadline.Token);
            }
            connection = await new CdpConnectionFactory(32 * 1024 * 1024).ConnectAsync(new Uri("ws://127.0.0.1:" + address[0] + address[1]), deadline.Token);
            var target = await connection.CallAsync("Target.createTarget", new() { ["url"] = "about:blank" }, null, deadline.Token);
            var attached = await connection.CallAsync("Target.attachToTarget", new() { ["targetId"] = target["targetId"]!.ToString(), ["flatten"] = true }, null, deadline.Token);
            var session = attached["sessionId"]!.ToString();
            await connection.CallAsync("Browser.setDownloadBehavior", new() { ["behavior"] = "deny" }, null, deadline.Token);
            await connection.CallAsync("Network.enable", new(), session, deadline.Token);
            await connection.CallAsync("Network.setBlockedURLs", new() { ["urls"] = new JsonArray("*") }, session, deadline.Token);
            await connection.CallAsync("Emulation.setDeviceMetricsOverride", new() { ["width"] = 960, ["height"] = 600, ["deviceScaleFactor"] = 1, ["mobile"] = false }, session, deadline.Token);
            var tree = await connection.CallAsync("Page.getFrameTree", new(), session, deadline.Token);
            // Rebuild the sandbox from validated source; never navigate saved HTML or a file URL.
            var document = safeDocument.Replace("<iframe ", "<iframe onload=\"this.dataset.ready='true'\" ", StringComparison.Ordinal);
            await connection.CallAsync("Page.setDocumentContent", new() { ["frameId"] = tree["frameTree"]!["frame"]!["id"]!.ToString(), ["html"] = document }, session, deadline.Token);
            while (true)
            {
                var state = await connection.CallAsync("Runtime.evaluate", new() { ["expression"] = "document.readyState === 'complete' && [...document.querySelectorAll('iframe')].every(f => f.dataset.ready === 'true')", ["returnByValue"] = true }, session, deadline.Token);
                if (state["result"]?["value"]?.GetValue<bool>() == true) break;
                await Task.Delay(50, deadline.Token);
            }
            await connection.CallAsync("Runtime.evaluate", new() { ["expression"] = "new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r(true))))", ["awaitPromise"] = true }, session, deadline.Token);
            return new(process, home, connection, session);
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync();
            try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } } catch (InvalidOperationException) { }
            lock(owned) owned.Remove(process);
            process.Dispose(); LocalVisualRenderer.TryDeletePrivateDirectory(home, Path.GetTempPath(), "ChunsonSpace-artwork-"); throw;
        }
    }
    private async Task DrainEvents()
    {
        try { await foreach (var unused in connection.Events(stopped.Token)) { } }
        catch (OperationCanceledException) { }
    }
    public async Task<byte[]> CaptureAsync(double scale, CancellationToken token)
    {
        await interaction.WaitAsync(token);
        try
        {
            scale=Math.Clamp(scale,1,4);
            // Scale capture resolution, not viewport DPR: OOPIF mouse routing must stay in CSS pixels.
            var picture = await connection.CallAsync("Page.captureScreenshot", new() { ["format"] = "png", ["captureBeyondViewport"] = false, ["clip"]=new JsonObject { ["x"]=0,["y"]=0,["width"]=960,["height"]=600,["scale"]=scale } }, session, token);
            return Convert.FromBase64String(picture["data"]!.ToString());
        }
        finally { interaction.Release(); }
    }
    public async Task ClickAsync(double x, double y, CancellationToken token)
    {
        await interaction.WaitAsync(token);
        try
        {
            await connection.CallAsync("Input.dispatchMouseEvent", new() { ["type"]="mouseMoved", ["x"]=Math.Clamp(x,0,959), ["y"]=Math.Clamp(y,0,599) },session,token);
            foreach (var type in new[] { "mousePressed", "mouseReleased" })
                await connection.CallAsync("Input.dispatchMouseEvent", new() { ["type"] = type, ["x"] = Math.Clamp(x, 0, 959), ["y"] = Math.Clamp(y, 0, 599), ["button"] = "left", ["clickCount"] = 1 }, session, token);
            await connection.CallAsync("Runtime.evaluate",new(){["expression"]="new Promise(r => requestAnimationFrame(() => r(true)))",["awaitPromise"]=true},session,token);
        }
        finally { interaction.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        stopped.Cancel(); await connection.DisposeAsync(); await events; stopped.Dispose();
        try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } } catch (InvalidOperationException) { }
        lock(owned) owned.Remove(process);
        process.Dispose(); LocalVisualRenderer.TryDeletePrivateDirectory(home, Path.GetTempPath(), "ChunsonSpace-artwork-");
    }
    private static void Kill(Process process) { try { if(!process.HasExited) process.Kill(true); } catch(Exception e) when(e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
    public void Stop() => Kill(process);
    public static void StopAll() { lock(owned) { shuttingDown=true; foreach(var process in owned) Kill(process); } }
}
