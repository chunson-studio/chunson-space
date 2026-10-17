using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using FAswitch.Accounts;
using FAswitch.Core;
using FAswitch.Windows;

namespace FAswitch.Desktop;

internal static class VisualRuntimeVerification
{
    public static async Task RunAsync(string directory, string? savedSvg = null)
    {
        if (Directory.Exists(directory)) throw new IOException("Use a new verification directory.");
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(string name, bool condition) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        var repository = new AccountRepository(Path.Combine(directory, "fixture"));
        repository.Profiles.CreateBoundAccount("fixture", new("http://127.0.0.1:7901"));
        repository.Profiles.SaveAccount("fixture", new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "fixture-id", ["access_token"] = "fixture-secret" } }, overwrite: true);
        var original = File.ReadAllBytes(Path.Combine(repository.AccountPath("fixture"), "auth.json"));
        string? usedHome = null; bool cancel = false; bool rotate = false; bool tool = false; string? failure = null;
        var runtime = new CodexVisualTestRuntime(repository, () => "fixture-cli.exe", () => throw new InvalidOperationException("Not a render test"), execute: (info, prompt, token) =>
        {
            if (info.ArgumentList.Contains("--version")) return Task.FromResult((0, "codex-cli 0.163.0-fixture", ""));
            usedHome = info.Environment["CODEX_HOME"];
            Check("native request reads only a private copied auth", usedHome is not null && File.ReadAllBytes(Path.Combine(usedHome, "auth.json")).SequenceEqual(original) && info.WorkingDirectory == usedHome);
            if (cancel) throw new OperationCanceledException(token);
            if (rotate) File.WriteAllText(Path.Combine(usedHome!, "auth.json"), "{\"tokens\":{\"access_token\":\"rotated-secret\"}}");
            if (failure is not null) return Task.FromResult((1, JsonSerializer.Serialize(new { type = "turn.failed", error = new { message = failure } }), "PATH helper warning"));
            var events = JsonSerializer.Serialize(new { type = "item.completed", item = new { type = tool ? "command_execution" : "agent_message", text = "fixture-secret state=running " + OfflineVisualTestRuntime.Svg } }) + "\n" + JsonSerializer.Serialize(new { type = "turn.completed", usage = new { input_tokens = 5000, cached_input_tokens = 4096, output_tokens = 200 } });
            return Task.FromResult((0, events, "Bearer fixture-secret"));
        });
        var success = Path.Combine(directory, "cli-success");
        var reply = await runtime.ReplyAsync("fixture", "gpt-6-astra", "fixture prompt", [], success, default);
        Check("native event parser captures reply/cache and redacts credentials", reply.CachedTokens == 4096 && reply.Text.Contains("<已隐去>") && !File.ReadAllText(Path.Combine(success, "events.jsonl")).Contains("fixture-secret"));
        Check("native completion removes private auth and leaves original unchanged", !Directory.Exists(usedHome) && File.ReadAllBytes(Path.Combine(repository.AccountPath("fixture"), "auth.json")).SequenceEqual(original));
        Check("redaction preserves generated animation state assignments", reply.Text.Contains("state=running"));
        Check("installed codex-cli version prefix is accepted", reply.CliVersion == "codex-cli 0.163.0-fixture");
        foreach (var (message, expected) in new[] {
            ("Selected model is at capacity. Please try a different model.", "模型当前容量不足"),
            ("Connection failed: error sending request", "请求连接失败"),
            ("Usage limit reached", "用量或请求限制"),
            ("Unexpected fixture failure", "Unexpected fixture failure") })
        {
            failure = message;
            try { await runtime.ReplyAsync("fixture", "gpt-6-astra", "fixture failure", [], Path.Combine(directory, "error-" + checks.Count), default); throw new InvalidOperationException("failure accepted"); }
            catch (IOException e) { Check("failure cause preserved: " + expected, e.Message.Contains(expected) && e.Message.Contains(message) && !Directory.Exists(usedHome)); }
        }
        failure = null;
        cancel = true;
        try { await runtime.ReplyAsync("fixture", "gpt-6-astra", "fixture cancel", [], Path.Combine(directory, "cli-cancel"), default); throw new InvalidOperationException("cancel not propagated"); }
        catch (OperationCanceledException) { Check("native cancellation cleans copied authorization", !Directory.Exists(usedHome)); }
        cancel = false; tool = true;
        try { await runtime.ReplyAsync("fixture", "gpt-6-astra", "fixture tool", [], Path.Combine(directory, "cli-tool"), default); throw new InvalidOperationException("tool accepted"); }
        catch (IOException) { Check("unexpected tool calls reject the run", !Directory.Exists(usedHome)); }
        tool = false; rotate = true;
        try { await runtime.ReplyAsync("fixture", "gpt-6-astra", "fixture rotate", [], Path.Combine(directory, "cli-rotate"), default); throw new InvalidOperationException("rotated auth accepted"); }
        catch (IOException) { Check("rotated copy retained and source preserved", Directory.Exists(usedHome) && File.ReadAllBytes(Path.Combine(repository.AccountPath("fixture"), "auth.json")).SequenceEqual(original)); }
        // Only fake credentials live here; leave the deliberately rotated fixture for inspection.
        var failedPreview = new OfflineVisualTestRuntime { FailRender = true };
        var previewResult = await new VisualTestService(repository, failedPreview).RunAsync("fixture", "gpt-6-astra", VisualTestKind.Static, "");
        Check("preview failure retains completed reply and names local stage without retry", !previewResult.Completed && previewResult.Steps.Count == 1
            && previewResult.Message.Contains("模型回复和作品已保存，本地预览未完成") && failedPreview.Requests.Count == 1
            && File.Exists(Path.Combine(previewResult.Directory, previewResult.Steps[0].Artifact!)));
        var browser = InstalledBrowsers.Find().FirstOrDefault() ?? throw new IOException("Install a Chromium browser for offline rendering verification.");
        var renderer = new CodexVisualTestRuntime(repository, () => throw new InvalidOperationException("Model requests forbidden"), () => browser.Executable);
        var viewerDirectory = Path.Combine(directory, "本地 作品 #100% @fixture"); Directory.CreateDirectory(viewerDirectory);
        var viewerResult = previewResult with { Directory = viewerDirectory };
        var viewerRecord = Path.Combine(viewerDirectory, "result.json");
        File.WriteAllText(viewerRecord, JsonSerializer.Serialize(viewerResult));
        var recordBytes = File.ReadAllBytes(viewerRecord);
        var repairedGallery = VisualTestViewer.PreparePage(viewerResult);
        var repairedPreview = VisualTestViewer.PreparePage(viewerResult, 0);
        Check("missing gallery and step preview rebuild from saved replies without another model request", File.Exists(repairedGallery)
            && File.ReadAllText(repairedGallery).Contains("srcdoc") && File.ReadAllText(repairedPreview).Contains("sandbox='allow-scripts'") && failedPreview.Requests.Count == 1);
        var launch = VisualTestViewer.StartInfo(browser.Executable, repairedGallery);
        Check("viewer opens one escaped local file URL with installed browser rather than HTML association", !launch.UseShellExecute
            && launch.ArgumentList.Count == 1 && new Uri(launch.ArgumentList[0]).IsFile
            && new Uri(launch.ArgumentList[0]).LocalPath == repairedGallery && launch.ArgumentList[0].Contains("%23") && launch.ArgumentList[0].Contains("%25"));
        var launchDom = await ReadViewerLaunchAsync(launch);
        Check("installed browser loads the actual viewer launch argument with Unicode spaces and URL delimiters", launchDom.Contains("<title>作品对比</title>")
            && launchDom.Contains("srcdoc") && !launchDom.Contains("ERR_FILE_NOT_FOUND") && !launchDom.Contains("ERR_NAME_NOT_RESOLVED"));
        // Fixtures on D: do not exercise MSIX LocalAppData redirection. The visible
        // browser may already run outside the package, unlike a new headless child.
        var localFixtureRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "faswitch-viewer-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localFixtureRoot);
        try
        {
            var localPage = Path.Combine(localFixtureRoot, "作品 #100%.html");
            File.WriteAllText(localPage, "<!doctype html><meta charset='utf-8'><title>LOCAL PATH FIXTURE</title><p>fixture-only</p>");
            var physicalPage = WindowsDesktopLaunchSystem.ResolveRealPath(localPage);
            var localLaunch = VisualTestViewer.StartInfo(browser.Executable, localPage);
            Check("viewer resolves LocalAppData to the physical path before browser handoff", new Uri(localLaunch.ArgumentList.Single()).LocalPath == physicalPage
                && localLaunch.WorkingDirectory == Path.GetDirectoryName(Path.GetFullPath(browser.Executable))
                && File.ReadAllBytes(localPage).SequenceEqual(File.ReadAllBytes(physicalPage)));
            var localDom = await ReadViewerLaunchAsync(localLaunch);
            Check("installed browser reads the physical LocalAppData fixture", localDom.Contains("LOCAL PATH FIXTURE") && localDom.Contains("fixture-only")
                && !localDom.Contains("ERR_FILE_NOT_FOUND"));
            var deepLocal = Path.Combine(localFixtureRoot, new string('x', 180)); Directory.CreateDirectory(deepLocal);
            var deepLocalPage = Path.Combine(deepLocal, "preview.html"); File.Copy(localPage, deepLocalPage);
            var deepLocalPhysical = WindowsDesktopLaunchSystem.ResolveRealPath(deepLocalPage);
            var deepLocalLaunch = VisualTestViewer.StartInfo(browser.Executable, deepLocalPage);
            var shortPage = new Uri(deepLocalLaunch.ArgumentList.Single()).LocalPath;
            Check("long physical LocalAppData page becomes a byte-identical short preview", deepLocalPhysical.Length > 260
                && shortPage.Length < 260 && shortPage != deepLocalPhysical && File.ReadAllBytes(shortPage).SequenceEqual(File.ReadAllBytes(deepLocalPage)));
            var deepLocalDom = await ReadViewerLaunchAsync(deepLocalLaunch);
            Check("installed browser reads a short preview for a source longer than 260 characters", deepLocalDom.Contains("LOCAL PATH FIXTURE")
                && !deepLocalDom.Contains("ERR_FILE_NOT_FOUND"));
            File.WriteAllText(deepLocalPage, "<!doctype html><title>UPDATED FIXTURE</title>");
            var updatedLaunch = VisualTestViewer.StartInfo(browser.Executable, deepLocalPage);
            Check("reopening refreshes the same short preview from the saved source", new Uri(updatedLaunch.ArgumentList.Single()).LocalPath == shortPage
                && File.ReadAllText(shortPage).Contains("UPDATED FIXTURE"));
            var secondPage = Path.Combine(deepLocal, "gallery.html"); File.Copy(localPage, secondPage);
            var secondLaunch = VisualTestViewer.StartInfo(browser.Executable, secondPage);
            var secondShort = new Uri(secondLaunch.ArgumentList.Single()).LocalPath;
            Check("gallery and individual artwork keep separate short preview identities", secondShort != shortPage
                && File.ReadAllBytes(secondShort).SequenceEqual(File.ReadAllBytes(secondPage)));
            // Delete only the two cache files just generated from this unique fixture.
            foreach (var fixtureCache in new[] { shortPage, secondShort })
            {
                var cacheRoot = WindowsDesktopLaunchSystem.ResolveRealPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChunsonSwitch", "visual-previews"));
                if (Path.GetDirectoryName(fixtureCache) == cacheRoot)
                { PhysicalFileStorage.RejectLinks(fixtureCache); File.Delete(fixtureCache); }
            }
            File.WriteAllText(Path.Combine(directory, "local-path-check.json"), JsonSerializer.Serialize(new {
                redirected = !StringComparer.OrdinalIgnoreCase.Equals(localPage, physicalPage), physicalLength = deepLocalPhysical.Length,
                actualUserResultRead = false, actualUserBrowserInspected = false }));
        }
        finally
        {
            // Remove only this newly-created fixture, never an account or user browser directory.
            var physicalRoot = WindowsDesktopLaunchSystem.ResolveRealPath(localFixtureRoot);
            var physicalParent = WindowsDesktopLaunchSystem.ResolveRealPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (Path.GetDirectoryName(physicalRoot) == physicalParent && Path.GetFileName(physicalRoot).StartsWith("faswitch-viewer-check-", StringComparison.Ordinal))
            { PhysicalFileStorage.RejectLinks(physicalRoot); Directory.Delete(physicalRoot, recursive: true); }
        }
        Check("viewer preparation preserves original result and source authorization", recordBytes.SequenceEqual(File.ReadAllBytes(viewerRecord))
            && original.SequenceEqual(File.ReadAllBytes(Path.Combine(repository.AccountPath("fixture"), "auth.json"))));
        try { VisualTestViewer.PreparePage(viewerResult with { Directory = Path.Combine(viewerDirectory, "missing") }); throw new InvalidOperationException("missing record accepted"); }
        catch (IOException) { Check("missing source record reports local error without opening browser", true); }
        await renderer.RenderAsync(repairedGallery, Path.Combine(directory, "repaired-gallery.png"), default);
        var svgPage = Path.Combine(directory, "svg-preview.html"); var svgShot = Path.Combine(directory, "svg.png");
        File.WriteAllText(svgPage, VisualTestArtifact.Wrap(OfflineVisualTestRuntime.Svg));
        await renderer.RenderAsync(svgPage, svgShot, default);
        var animatedPage = Path.Combine(directory, "animation-preview.html"); var animatedShot = Path.Combine(directory, "animation.png");
        File.WriteAllText(animatedPage, VisualTestArtifact.Wrap("<html><body style='background:#d9f2ff'><div id='status' style='font-size:42px'>WAIT</div><script>setTimeout(()=>{document.getElementById('status').textContent='ANIMATION READY';document.body.style.background='#b4d88a'},500)</script><button onclick=\"this.textContent='Paused'\">Pause</button></body></html>"));
        await renderer.RenderAsync(animatedPage, animatedShot, default);
        foreach (var shot in new[] { svgShot, animatedShot })
        {
            using var stream = File.OpenRead(shot); var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Check("real browser produced 960x600 artifact " + Path.GetFileName(shot), frame.PixelWidth == 960 && frame.PixelHeight == 600 && stream.Length > 1000);
        }
        Check("browser rendered different SVG and HTML documents", !File.ReadAllBytes(svgShot).SequenceEqual(File.ReadAllBytes(animatedShot)));
        var deepDirectory = Path.Combine(directory, "saved-results", new string('x', Math.Clamp(242 - directory.Length - 15, 1, 200)));
        Directory.CreateDirectory(deepDirectory);
        var deepPage = Path.Combine(deepDirectory, "preview.html"); var deepShot = Path.Combine(deepDirectory, "preview.png");
        File.Copy(svgPage, deepPage);
        await renderer.RenderAsync(deepPage, deepShot, default);
        Check("long saved-result paths render the same artwork with a short private browser profile", deepDirectory.Length >= 240
            && File.ReadAllBytes(svgShot).SequenceEqual(File.ReadAllBytes(deepShot)));
        using (var stream = File.OpenRead(animatedShot))
        {
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var rgba = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4]; rgba.CopyPixels(new System.Windows.Int32Rect(900, 550, 1, 1), pixel, 4, 0);
            Check("sandboxed animation JavaScript advances beyond initial frame", pixel[0] == 0x8a && pixel[1] == 0xd8 && pixel[2] == 0xb4);
        }
        if (savedSvg is not null && Path.GetExtension(savedSvg).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            var savedBytes = File.ReadAllBytes(savedSvg);
            var savedResult = JsonSerializer.Deserialize<VisualTestResult>(savedBytes) ?? throw new IOException("Saved result unreadable.");
            var replayDirectory = Path.Combine(directory, "saved-gallery-replay"); Directory.CreateDirectory(replayDirectory);
            var replay = savedResult with { Directory = replayDirectory };
            File.WriteAllText(Path.Combine(replayDirectory, "result.json"), JsonSerializer.Serialize(replay));
            var savedPage = VisualTestViewer.PreparePage(replay);
            await renderer.RenderAsync(savedPage, Path.Combine(directory, "saved-gallery.png"), default);
            Check("saved gallery rebuilt and rendered locally without source mutation or model requests", savedBytes.SequenceEqual(File.ReadAllBytes(savedSvg)));
        }
        else if (savedSvg is not null)
        {
            var originalSvg = File.ReadAllBytes(savedSvg);
            var svg = VisualTestArtifact.Extract(System.Text.Encoding.UTF8.GetString(originalSvg), false) ?? throw new IOException("The saved input must be a valid static SVG.");
            var savedPage = Path.Combine(directory, "saved-artwork-preview.html");
            File.WriteAllText(savedPage, VisualTestArtifact.Wrap(svg));
            await renderer.RenderAsync(savedPage, Path.Combine(directory, "saved-artwork.png"), default);
            Check("saved SVG rendered locally without modifying its source or issuing a model request", originalSvg.SequenceEqual(File.ReadAllBytes(savedSvg)));
        }
        File.WriteAllText(Path.Combine(directory, "verification.json"), JsonSerializer.Serialize(new { passed = true, actualAccountRequests = 0, nativeCli = "injected process fixture", actualBrowser = browser.Name, originalAuthPreserved = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<string> ReadViewerLaunchAsync(System.Diagnostics.ProcessStartInfo launch)
    {
        var privateProfile = Path.Combine(Path.GetTempPath(), "faswitch-viewer-check-" + Guid.NewGuid().ToString("N"));
        var arguments = new[] { "--headless=new", "--disable-gpu", "--dump-dom", "--no-first-run", "--no-default-browser-check",
            "--disable-background-networking", "--disable-component-update", "--disable-extensions", "--proxy-server=http://127.0.0.1:9",
            "--host-resolver-rules=MAP * ~NOTFOUND", "--user-data-dir=" + privateProfile };
        for (var i = 0; i < arguments.Length; i++) launch.ArgumentList.Insert(i, arguments[i]);
        launch.CreateNoWindow = true; launch.RedirectStandardOutput = true; launch.RedirectStandardError = true;
        launch.StandardOutputEncoding = System.Text.Encoding.UTF8; launch.StandardErrorEncoding = System.Text.Encoding.UTF8;
        using var process = new System.Diagnostics.Process { StartInfo = launch };
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // Local browser check only, never a model deadline.
        var started = false;
        try
        {
            process.Start(); started = true;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(guard.Token); await error;
            return await output;
        }
        finally
        {
            if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            // Only the newly-created short fixture profile may be removed; never a user's browser profile.
            var resolved = Path.GetFullPath(privateProfile);
            if (Path.GetDirectoryName(resolved) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                && Path.GetFileName(resolved).StartsWith("faswitch-viewer-check-", StringComparison.Ordinal) && Directory.Exists(resolved)
                && !Directory.EnumerateFileSystemEntries(resolved, "*", SearchOption.AllDirectories).Append(resolved).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
                try { Directory.Delete(resolved, recursive: true); } catch (IOException) { }
        }
    }
}
