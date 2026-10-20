using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

internal static class OnlineUpdateVerification
{
    // Invoked only by the parameterized installer fixture, before normal application startup.
    internal static async Task PrepareInUseAsync(string directory)
    {
        using var input = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "input.json")));
        var root = input.RootElement.GetProperty("installRoot").GetString()!;
        var stage = input.RootElement.GetProperty("stage").GetString()!;
        var contents = new Dictionary<string, byte[]>
        {
            ["ChunsonSwitch.exe"] = File.ReadAllBytes(Path.Combine(root, "ChunsonSwitch.exe")),
            ["ChunsonSwitch.dll"] = "new app"u8.ToArray(),
            ["Dependency.dll"] = "new dependency"u8.ToArray(),
            ["Widgets/BeijingClock.exe"] = File.ReadAllBytes(input.RootElement.GetProperty("clockNew").GetString()!)
        };
        var handler = new RangeHandler(contents: contents);
        using var service = new GitHubUpdateService(handler);
        var release = await service.CheckAsync(CancellationToken.None);
        var manifest = UpdateRules.ParseManifest(handler.ManifestBytes(), release);
        var clock = manifest.Files.Single(f => f.Path == "Widgets/BeijingClock.exe");
        var original = await GitHubUpdateService.HashAsync(Path.Combine(root, clock.Path), CancellationToken.None);
        if (original == clock.Sha256) throw new InvalidOperationException("Clock fixture must change bytes.");
        var prepared = await service.PrepareAsync(release, root, stage, null, CancellationToken.None);
        var clockRanges = handler.Ranges.Count(r => r.Item1 < clock.Offset + clock.CompressedSize && r.Item2 >= clock.Offset);
        if (clockRanges != 0 || prepared.Files.Any(f => UpdateRules.IndependentComponent(f.Path)) ||
            File.Exists(Path.Combine(stage, clock.Path)) || prepared.Files.Length != 2 ||
            handler.Transferred != prepared.Files.Sum(f => f.CompressedSize)) throw new InvalidOperationException("Independent component crossed manager update boundary.");
        await File.WriteAllTextAsync(Path.Combine(directory, "prepared.json"), JsonSerializer.Serialize(prepared, UpdateRules.Json));
        await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), handler.ManifestBytes());
        using var helper = typeof(AppUpdateInstaller).Assembly.GetManifestResourceStream("ChunsonSpace.Install-AppUpdate.ps1")!;
        using var bytes = new MemoryStream(); await helper.CopyToAsync(bytes);
        await File.WriteAllBytesAsync(Path.Combine(directory, "Install-AppUpdate.ps1"), bytes.ToArray());
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            passed = true, originalClockSha256 = original, manifestClockSha256 = clock.Sha256,
            clockRangeRequests = clockRanges, clockBytesDownloaded = 0, clockStaged = false,
            changedPaths = prepared.Files.Select(f => f.Path), handler.Transferred,
            ranges = handler.Ranges.Select(r => new { from = r.Item1, to = r.Item2 }),
            embeddedHelperSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())),
            assemblySha256 = await GitHubUpdateService.HashAsync(typeof(AppUpdateInstaller).Assembly.Location, CancellationToken.None),
            realNetworkRequests = 0
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Narrow release-number regression: no normal workspace, HTTP transport or installer.
    internal static async Task RunVersionOrderAsync(string directory)
    {
        if (Directory.Exists(directory)) throw new IOException("Use new evidence directory.");
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        void Check(string name, bool ok) { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); }
        void Reject(string name, Action action) { try { action(); } catch (InvalidDataException) { Check(name, true); return; } Check(name, false); }
        var current = UpdateRules.ParseVersion("v1.7");
        var legacy = UpdateRules.ParseVersion("v1.61");
        Check("assembly and display are exactly 1.7", UpdateRules.CurrentVersion == current && UpdateRules.DisplayVersion(current) == "1.7");
        Check("two, three and four components normalize identically", current == UpdateRules.ParseVersion("1.7.0") && current == UpdateRules.ParseVersion("1.7.0.0"));
        Check("legacy identity is unchanged", legacy == new Version(1, 61, 0, 0) && legacy > current);
        var ordered = new[] { "1.5.0", "1.6.0", "1.6.99", "1.61", "1.7", "1.7.0.1", "1.7.1", "1.8", "1.9.9.9", "1.10", "1.61.0.1", "2.0" };
        for (var i = 0; i < ordered.Length; i++)
        {
            var left = UpdateRules.ParseVersion(ordered[i]);
            Check("same version " + ordered[i], UpdateRules.CompareVersions(left, left) == 0);
            for (var j = i + 1; j < ordered.Length; j++)
            {
                var right = UpdateRules.ParseVersion(ordered[j]);
                Check(ordered[i] + " precedes " + ordered[j], UpdateRules.CompareVersions(left, right) < 0 && UpdateRules.CompareVersions(right, left) > 0);
            }
        }
        foreach (var invalid in new[] { "1", "1.7-beta", "v1.7+build", "65536.0", "1.7.0.0.1", "https://example.invalid/1.7" })
            Reject("reject invalid version " + invalid, () => UpdateRules.ParseVersion(invalid));
        var handler = new RangeHandler();
        byte[] Metadata(string tag)
        {
            var node = JsonNode.Parse(handler.Metadata())!;
            node["tag_name"] = tag; node["html_url"] = UpdateRules.Repository + "/releases/tag/" + tag;
            foreach (var asset in node["assets"]!.AsArray())
                asset!["browser_download_url"] = UpdateRules.Repository + "/releases/download/" + tag + "/" + asset["name"]!.GetValue<string>();
            return Encoding.UTF8.GetBytes(node.ToJsonString());
        }
        var release = UpdateRules.ParseRelease(Metadata("v1.7"));
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(handler.ManifestBytes(), UpdateRules.Json)! with { Version = "1.7.0.0" };
        Check("tag v1.7 accepts exact normalized manifest", UpdateRules.ParseManifest(JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateRules.Json), release).Version == "1.7.0.0");
        Reject("legacy manifest never aliases 1.7", () => UpdateRules.ParseManifest(JsonSerializer.SerializeToUtf8Bytes(manifest with { Version = "1.61.0.0" }, UpdateRules.Json), release));
        Reject("future manifest never aliases 1.7", () => UpdateRules.ParseManifest(JsonSerializer.SerializeToUtf8Bytes(manifest with { Version = "1.7.0.1" }, UpdateRules.Json), release));
        var foreign = JsonNode.Parse(Metadata("v1.7"))!; foreign["assets"]![0]!["browser_download_url"] = UpdateRules.Repository + "/releases/download/v1.61/" + UpdateRules.ArchiveName;
        Reject("asset URL must match immutable tag", () => UpdateRules.ParseRelease(Encoding.UTF8.GetBytes(foreign.ToJsonString())));
        Check("independent clock exclusion preserved", UpdateRules.IndependentComponent("Widgets/BeijingClock.exe"));
        var fixtureRoot = Path.Combine(directory, "fixture");
        Directory.CreateDirectory(fixtureRoot);
        File.WriteAllText(Path.Combine(fixtureRoot, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var workspace = new DesktopWorkspace(fixtureRoot, Path.Combine(fixtureRoot, "active"), Path.Combine(fixtureRoot, "desktop"), fixture: true);
        var model = new ShellViewModel(workspace);
        var metadataHandler = new VersionMetadataHandler();
        model.UseUpdateService(new(metadataHandler));
        try
        {
            foreach (var tag in new[] { "v1.61", "v1.7", "v1.7.0.1", "v1.8" })
            {
                metadataHandler.Body = Metadata(tag);
                await model.CheckForUpdatesAsync(true);
                var expected = tag is "v1.7.0.1" or "v1.8";
                Check("actual update presentation " + tag, model.HasUpdate == expected && (expected || model.UpdateStatus.Contains("最新")));
            }
        }
        finally { model.DisposeUpdates(); await workspace.Accounts!.DisposeAsync(); }
        File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new
        {
            passed = true, checks, fakeMetadataRequests = metadataHandler.Calls, realRequests = 0,
            actualAccountOperations = false, actualProxyChanges = false, actualSystemTimezoneChanges = false,
            normalAppStartsOrStops = 0, screenshotCount = 0,
            scope = "1.7 version ordering, tag/manifest exact identity and actual update presentation only"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class VersionMetadataHandler : HttpMessageHandler
    {
        internal byte[] Body = []; internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.AbsoluteUri != UpdateRules.Latest) throw new InvalidOperationException("Only fake release metadata is allowed.");
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) });
        }
    }

    internal static async Task RunAsync(string directory)
    {
        if (Directory.Exists(directory)) throw new IOException("Use new evidence directory.");
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        void Check(string name, bool ok) { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); }
        void Reject(string name, Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or JsonException) { Check(name, true); return; } Check(name, false); }
        Check("numeric normalized version comparison", UpdateRules.ParseVersion("v1.5.0") == UpdateRules.ParseVersion("1.5.0.0") && UpdateRules.ParseVersion("1.10.0") > UpdateRules.ParseVersion("1.9.9.9"));
        Check("four-part assembly versions stay visible", UpdateRules.DisplayVersion(new Version(1, 5, 0, 1)) == "1.5.0.1");
        foreach (var invalid in new[] { "1.5", "1.5.0-beta", "v1.5.0+build", "65536.0.0", "https://evil/1.0.0" }) Reject("version rejects " + invalid, () => UpdateRules.ParseVersion(invalid));
        var handler = new RangeHandler();
        var release = UpdateRules.ParseRelease(handler.Metadata());
        foreach (var field in new[] { "draft", "prerelease" })
        { var node = JsonNode.Parse(handler.Metadata())!; node[field] = true; Reject(field + " rejected", () => UpdateRules.ParseRelease(Encoding.UTF8.GetBytes(node.ToJsonString()))); }
        var foreign = JsonNode.Parse(handler.Metadata())!; foreign["html_url"] = "https://github.com/other/repo/releases/tag/v99.0.0";
        Reject("foreign page rejected", () => UpdateRules.ParseRelease(Encoding.UTF8.GetBytes(foreign.ToJsonString())));
        foreach (var path in new[] { "../auth.json", "D:/auth.json", "/auth.json", "ChunsonSwitch.dll:evil", "accounts/auth.json", "projects/a.dll", "ThirdParty/../auth.json", "ThirdParty/NUL.txt", "ThirdParty/a.", "ThirdParty/a\\b" }) Reject("path rejects " + path, () => UpdateRules.RelativePath(path));
        var parsed = UpdateRules.ParseManifest(handler.ManifestBytes(), release);
        Reject("overlap rejected", () => UpdateRules.ParseManifest(JsonSerializer.SerializeToUtf8Bytes(parsed with { Files = [parsed.Files[0], parsed.Files[1] with { Offset = parsed.Files[0].Offset }] }, UpdateRules.Json), release));
        Reject("range beyond ZIP rejected", () => UpdateRules.ParseManifest(JsonSerializer.SerializeToUtf8Bytes(parsed with { Files = [parsed.Files[0], parsed.Files[1] with { Offset = parsed.AssetSize }] }, UpdateRules.Json), release));
        Reject("duplicate casing rejected", () => UpdateRules.ParseManifest(JsonSerializer.SerializeToUtf8Bytes(parsed with { Files = [parsed.Files[0], parsed.Files[1] with { Path = "CHUNSONSWITCH.EXE" }] }, UpdateRules.Json), release));
        var install = Path.Combine(directory, "app"); Directory.CreateDirectory(install);
        foreach (var pair in handler.Contents) File.WriteAllBytes(Path.Combine(install, pair.Key), pair.Key == "ChunsonSwitch.dll" ? "old bytes"u8.ToArray() : pair.Value);
        using (var service = new GitHubUpdateService(handler))
        {
            var prepared = await Task.Run(() => service.PrepareAsync(release, install, Path.Combine(directory, "stage-ok"), null, default));
            Check("only changed member transferred, unchanged runtime and exe transfer zero", prepared.Files.Length == 1 && prepared.Files[0].Path == "ChunsonSwitch.dll" && handler.Ranges.Count == 1 && handler.Transferred == parsed.Files[1].CompressedSize);
            Check("decompressed file has expected SHA and current installation unchanged", File.ReadAllBytes(Path.Combine(prepared.Directory, "ChunsonSwitch.dll")).SequenceEqual(handler.Contents["ChunsonSwitch.dll"]) && File.ReadAllText(Path.Combine(install, "ChunsonSwitch.dll")) == "old bytes");
        }
        foreach (var mode in new[] { "200", "wrong-range", "short", "corrupt", "over-inflate", "cancel" })
        {
            var fake = new RangeHandler { Mode = mode };
            using var service = new GitHubUpdateService(fake); using var cancel = new CancellationTokenSource();
            if (mode == "cancel") cancel.Cancel();
            bool rejected = false;
            try { await Task.Run(() => service.PrepareAsync(UpdateRules.ParseRelease(fake.Metadata()), install, Path.Combine(directory, "stage-" + mode), null, cancel.Token)); }
            catch (Exception e) when (e is InvalidDataException or HttpRequestException or OperationCanceledException) { rejected = true; }
            Check(mode + " rejected before installation", rejected && File.ReadAllText(Path.Combine(install, "ChunsonSwitch.dll")) == "old bytes");
            if (mode == "200") Check("Range 200 body never read, no full download fallback", fake.BodyReads == 0 && fake.Ranges.Count == 1);
        }
        var stored = new RangeHandler(stored: true);
        using (var service = new GitHubUpdateService(stored))
        {
            var prepared = await Task.Run(() => service.PrepareAsync(UpdateRules.ParseRelease(stored.Metadata()), install, Path.Combine(directory, "stage-stored"), null, default));
            Check("stored ZIP member also verifies exact bytes", prepared.Files.Single().Method == 0 && File.ReadAllBytes(Path.Combine(prepared.Directory, "ChunsonSwitch.dll")).SequenceEqual(stored.Contents["ChunsonSwitch.dll"]));
        }
        var listener = new UiVerification.BindingListener(); PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        var model = new ShellViewModel(); var uiHandler = new RangeHandler(); model.UseUpdateService(new(uiHandler));
        var window = UiVerification.CreateWindow(model);
        try
        {
            uiHandler.Mode = "failure";
            await model.CheckForUpdatesAsync(false); await model.CheckForUpdatesAsync(false);
            Check("automatic error silent and once per launch", model.UpdateStatus == "" && !model.HasUpdate && uiHandler.MetadataCalls == 1);
            model.CheckUpdateCommand.Execute(null); await model.UpdateOperation;
            Check("manual failure inline and command recovers", model.UpdateStatus.Contains("失败") && model.CheckUpdateCommand.CanExecute(null));
            uiHandler.Mode = "latest"; model.CheckUpdateCommand.Execute(null); await model.UpdateOperation;
            Check("manual current version inline", model.UpdateStatus.Contains("最新") && !model.HasUpdate);
            uiHandler.Mode = "legacy"; await model.CheckForUpdatesAsync(true);
            Check("legacy release view only", model.HasUpdate && !model.CanDownloadUpdate && !model.OnlineUpdateCommand.CanExecute(null));
            uiHandler.Mode = ""; await model.CheckForUpdatesAsync(true); model.ExitManagerForUpdate = _ => Task.FromResult(false);
            typeof(ShellViewModel).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [true]);
            Check("shared busy boundary disables online update", !model.OnlineUpdateCommand.CanExecute(null));
            typeof(ShellViewModel).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [false]);
            Check("busy restored enables update", model.OnlineUpdateCommand.CanExecute(null));
            uiHandler.Mode = "failure-prepare"; model.OnlineUpdateCommand.Execute(null); await model.UpdateOperation;
            Check("actual online command failure restores busy and cancellation state", !model.IsBusy && !model.IsPreparingUpdate && model.OnlineUpdateCommand.CanExecute(null) && model.UpdateStatus.Contains("未完成"));
            uiHandler.Mode = ""; await model.CheckForUpdatesAsync(true);
            foreach (var dark in new[] { false, true })
            {
                model.DarkTheme = dark; model.Navigate("Settings"); UiVerification.ArrangeForCapture(window.RenderRoot, 1040, 670);
                var checkButton = (Button)window.FindName("CheckUpdateButton"); var online = (Button)window.FindName("NoticeOnlineUpdateButton"); var view = (Button)window.FindName("NoticeViewUpdateButton");
                Check(dark + " real UI bindings", checkButton.Command == model.CheckUpdateCommand && online.Command == model.OnlineUpdateCommand && view.Command == model.ViewUpdateCommand && ((Button)window.FindName("ExitManagerButton")).Command == model.ExitManagerCommand && ((TextBlock)window.FindName("UpdateStatusText")).Text == model.UpdateStatus);
                Check(dark + " update notice and settings feedback visible", UiVerification.FullyVisible(checkButton, window.RenderRoot) && UiVerification.FullyVisible(online, window.RenderRoot) && UiVerification.FullyVisible((TextBlock)window.FindName("UpdateStatusText"), window.RenderRoot));
                var bitmap = new RenderTargetBitmap(1040, 670, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window.RenderRoot);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(directory, dark ? "update-dark.png" : "update-light.png")); encoder.Save(output);
            }
            Check("update binding errors absent", listener.Errors.Count == 0);
        }
        finally { window.Close(); model.DisposeUpdates(); PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
        // Source-confirmed close behavior and ownership cleanup failure, all fake callbacks.
        var exitModel = new ShellViewModel(); var exitWindow = UiVerification.CreateWindow(exitModel); int closed = 0, detached = 0, shutdown = 0;
        using (var life = new DesktopLifetime(exitWindow, exitModel, new FakeTray(), new FakeLease(), () => Task.FromResult(false), () => { closed++; throw new IOException("fixture ownership unavailable"); }, () => true, () => shutdown++, () => { detached++; return Task.CompletedTask; }))
        {
            Check("close defaults to exit instead of unconditional hiding", !life.TryHideOnClose());
            exitModel.CloseToTray = true; Check("explicit tray preference respected", life.TryHideOnClose()); exitModel.CloseToTray = false;
            Check("owned-close failure preserves safeguards and recovers busy", !await life.RequestExitAsync() && closed == 1 && !exitModel.IsBusy && exitModel.ExitCommand.CanExecute(null));
            Check("manager-only exit avoids ownership failure and preserves clients", await life.RequestManagerExitAsync() && closed == 1 && detached == 1 && shutdown == 1);
        }
        var fixtureRoot = Path.Combine(directory, "switch-fixture"); Directory.CreateDirectory(fixtureRoot); File.WriteAllText(Path.Combine(fixtureRoot, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var fixture = new DesktopWorkspace(fixtureRoot, Path.Combine(fixtureRoot, "active"), Path.Combine(fixtureRoot, "desktop"), fixture: true);
        fixture.Profiles.CreateBoundAccount("fixture", new("http://127.0.0.1:1234", "Asia/Shanghai", "zh-CN"));
        fixture.Profiles.SaveAccount("fixture", new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "fixture", ["access_token"] = "synthetic" } }, overwrite: true);
        fixture.OfflineRuntime!.StopSucceeds = false;
        var switchModel = new ShellViewModel(fixture);
        switchModel.LaunchCommand.Execute(null); await switchModel.CurrentOperation;
        Check("real switch-and-launch failure restores busy and exit command", !switchModel.IsBusy && switchModel.ExitManagerCommand.CanExecute(null) && fixture.OfflineRuntime.StopCalls > 0 && fixture.OfflineRuntime.LastLaunchRequest is null);
        switchModel.CloseToTray = true; switchModel.SaveSettingsCommand.Execute(null);
        Check("tray preference persists through existing settings", fixture.Profiles.ReadSettings().CloseToTray);
        switchModel.DisposeUpdates(); await fixture.Accounts!.DisposeAsync();
        File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new { passed = true, checks, realRequests = 0, realAccountOperations = 0, actualAccountOperations = false, actualProxyChanges = false, actualFingerprintRequests = false, processStops = 0, screenshots = 2, scope = "online-update and manager exit only" }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class FakeTray : ITrayHost
    {
        public event EventHandler? RestoreRequested { add { } remove { } }
        public event EventHandler? ExitRequested { add { } remove { } }
        public void Show(string tooltip) { } public void Hide() { } public void Dispose() { }
    }
    private sealed class FakeLease : ISingleInstanceLease
    {
        public bool IsPrimary => true; public bool RequestActivation() => true;
        public Task ListenForActivationAsync(Action activate, CancellationToken token) => Task.Delay(Timeout.Infinite, token);
        public void Dispose() { }
    }
    private sealed class RangeHandler : HttpMessageHandler
    {
        public string Mode = ""; public int MetadataCalls, BodyReads; public long Transferred;
        public List<(long, long)> Ranges = [];
        public Dictionary<string, byte[]> Contents = new() { ["ChunsonSwitch.exe"] = "unchanged host"u8.ToArray(), ["ChunsonSwitch.dll"] = Encoding.UTF8.GetBytes(new string('x', 10000) + "changed"), ["System.Runtime.dll"] = "unchanged runtime"u8.ToArray() };
        private readonly byte[] zip;
        private readonly UpdateManifest manifest;
        public RangeHandler(bool stored = false, Dictionary<string, byte[]>? contents = null)
        {
            if (contents is not null) Contents = contents;
            using var data = new MemoryStream(); var files = new List<UpdateFile>();
            foreach (var pair in Contents)
            {
                using var compressed = new MemoryStream(); using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true)) deflate.Write(pair.Value);
                var blob = stored ? pair.Value : compressed.ToArray(); files.Add(new(pair.Key, stored ? 0 : 8, data.Length, blob.Length, pair.Value.Length, Convert.ToHexStringLower(SHA256.HashData(pair.Value)))); data.Write(blob);
            }
            zip = data.ToArray(); manifest = new(1, "99.0.0", UpdateRules.ArchiveName, zip.Length, Convert.ToHexStringLower(SHA256.HashData(zip)), files.ToArray());
        }
        public byte[] ManifestBytes() => JsonSerializer.SerializeToUtf8Bytes(Mode == "over-inflate" ? manifest with { Files = manifest.Files.Select(f => f.Path == "ChunsonSwitch.dll" ? f with { Size = 1 } : f).ToArray() } : manifest, UpdateRules.Json);
        public byte[] Metadata()
        {
            var tag = Mode == "latest" ? "v1.5.0" : "v99.0.0";
            var assets = new List<object> { new { id = 1, name = UpdateRules.ArchiveName, size = zip.Length, browser_download_url = UpdateRules.Repository + "/releases/download/" + tag + "/" + UpdateRules.ArchiveName } };
            if (Mode != "legacy") assets.Add(new { id = 2, name = UpdateRules.ManifestName, size = ManifestBytes().Length, browser_download_url = UpdateRules.Repository + "/releases/download/" + tag + "/" + UpdateRules.ManifestName });
            return JsonSerializer.SerializeToUtf8Bytes(new { tag_name = tag, draft = false, prerelease = false, html_url = UpdateRules.Repository + "/releases/tag/" + tag, assets });
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsoluteUri == UpdateRules.Latest)
            { MetadataCalls++; if (Mode == "failure") throw new HttpRequestException("synthetic"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Metadata()) }); }
            if (request.RequestUri.AbsolutePath.EndsWith(UpdateRules.ManifestName)) { if (Mode == "failure-prepare") throw new HttpRequestException("synthetic prepare failure"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ManifestBytes()) }); }
            var range = request.Headers.Range!.Ranges.Single(); long from = range.From!.Value, to = range.To!.Value; Ranges.Add((from, to));
            if (Mode == "200") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ReadObservedStream(zip, () => BodyReads++)) });
            var body = zip[(int)from..((int)to + 1)]; if (Mode == "corrupt") body[body.Length / 2] ^= 0xff;
            if (Mode == "short") body = body[..^1];
            Transferred += body.Length;
            var content = new ByteArrayContent(body); content.Headers.ContentRange = new ContentRangeHeaderValue(Mode == "wrong-range" ? from + 1 : from, to, zip.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
    }
    private sealed class ReadObservedStream(byte[] data, Action read) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) { read(); return base.Read(buffer, offset, count); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { read(); return base.ReadAsync(buffer, token); }
    }
}
