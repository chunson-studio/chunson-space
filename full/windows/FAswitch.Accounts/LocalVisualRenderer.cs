using System.Diagnostics;
using System.Text;

namespace FAswitch.Accounts;

public sealed class LocalVisualRenderer(Func<string> findBrowser)
{
    public async Task RenderAsync(string page, string screenshot, CancellationToken token)
    {
        // Chromium creates many nested profile files; account result paths can already be near MAX_PATH.
        var home = Path.Combine(Path.GetTempPath(), "faswitch-render-" + Guid.NewGuid().ToString("N"));
        var info = NewProcess(findBrowser(), Path.GetDirectoryName(page)!);
        foreach (var arg in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--disable-component-update", "--disable-extensions", "--proxy-server=http://127.0.0.1:9", "--host-resolver-rules=MAP * ~NOTFOUND", "--user-data-dir=" + home, "--window-size=960,600", "--hide-scrollbars", "--remote-debugging-port=0", "about:blank" }) info.ArgumentList.Add(arg);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
            var endpoint = Path.Combine(home, "DevToolsActivePort");
            string[] address = [];
            while (address.Length < 2)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("本机预览浏览器已退出。");
                if (File.Exists(endpoint)) try { address = await File.ReadAllLinesAsync(endpoint, deadline.Token); } catch (IOException) { }
                if (address.Length < 2) await Task.Delay(100, deadline.Token);
            }
            await using var connection = await new CdpConnectionFactory().ConnectAsync(new Uri("ws://127.0.0.1:" + address[0] + address[1]), deadline.Token);
            var target = await connection.CallAsync("Target.createTarget", new() { ["url"] = "about:blank" }, null, deadline.Token);
            var attached = await connection.CallAsync("Target.attachToTarget", new() { ["targetId"] = target["targetId"]!.ToString(), ["flatten"] = true }, null, deadline.Token);
            var session = attached["sessionId"]!.ToString();
            await connection.CallAsync("Emulation.setDeviceMetricsOverride", new() { ["width"] = 960, ["height"] = 600, ["deviceScaleFactor"] = 1, ["mobile"] = false }, session, deadline.Token);
            await connection.CallAsync("Page.navigate", new() { ["url"] = new Uri(page).AbsoluteUri }, session, deadline.Token);
            while (true)
            {
                var state = await connection.CallAsync("Runtime.evaluate", new() { ["expression"] = "document.readyState", ["returnByValue"] = true }, session, deadline.Token);
                if (state["result"]?["value"]?.ToString() == "complete") break;
                await Task.Delay(100, deadline.Token);
            }
            // Advance timers in sandboxed frames after the document has loaded.
            await Task.Delay(2000, deadline.Token);
            var picture = await connection.CallAsync("Page.captureScreenshot", new() { ["format"] = "png", ["captureBeyondViewport"] = false }, session, deadline.Token);
            await File.WriteAllBytesAsync(screenshot, Convert.FromBase64String(picture["data"]!.ToString()), deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("本地作品渲染未完成，未发送检查请求。模型请求本身不设总超时。"); }
        finally
        {
            try { if (process.Id > 0 && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } } catch (InvalidOperationException) { }
            TryDeletePrivateDirectory(home, Path.GetTempPath(), "faswitch-render-");
        }
    }
    internal static bool TryDeletePrivateDirectory(string path, string parent, string prefix)
    {
        try
        {
            var resolved = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith(prefix, StringComparison.Ordinal)
                || !Directory.Exists(resolved) || Directory.EnumerateFileSystemEntries(resolved, "*", SearchOption.AllDirectories).Append(resolved).Any(item => (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)) return false;
            Directory.Delete(resolved, true); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    private static ProcessStartInfo NewProcess(string executable, string home) => new(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = home, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
}
