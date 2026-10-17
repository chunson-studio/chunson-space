using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ChunsonLogin;

internal static class VerificationSuite
{
    public static async Task RunAsync(string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Verification failed: " + name);
            checks.Add(name);
        }
        BrowserChecks.Run(Path.Combine(output, "browser-fixtures"), Check);
        await NetworkChecks.RunAsync(Path.Combine(output, "network-fixtures"), Check);
        RegionChecks.Run(Check);
        Chunson.Widgets.ClockLauncherChecks.Run(Check);
        await CheckCoordinatorAsync(Check);

        var trace = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        try
        {
            foreach (var item in new[] { ("default", 840.0, 790.0, false), ("compact-edge", 620.0, 600.0, true) })
            {
                var model = LoginViewModel.Preview(matches: item.Item4, edgeOnly: item.Item4);
                var window = new MainWindow(model) { Width = item.Item2, Height = item.Item3, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var root = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(output, item.Item1 + ".png"))) encoder.Save(stream);
                Check(root.ActualWidth > 500 && root.ActualHeight > 450, item.Item1 + " window rendered");
                Check(!window.DetectButton.IsEnabled && !window.SyncButton.IsEnabled && !window.OpenButton.IsEnabled, item.Item1 + " preview cannot perform live operations");
                var texts = Descendants<TextBlock>(root).Where(t => t.Text == model.StatusMessage).ToArray();
                Check(texts.Length == 1 && texts[0].ActualHeight >= 14, item.Item1 + " result status visible");
                var pos = texts[0].TranslatePoint(new Point(), root);
                Check(pos.Y >= 0 && pos.Y + texts[0].ActualHeight <= root.ActualHeight + 1, item.Item1 + " status stays above bottom edge");
                Check(window.ResizeMode == ResizeMode.CanResize && window.Icon is not null, item.Item1 + " normal minimize/resize and application icon");
                if (item.Item4) Check(model.SelectedBrowser?.Name == "Microsoft Edge", "Edge-only preview selects Edge");
                window.Close();
            }
            Check(trace.Messages.Count == 0, "WPF bindings resolve");
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(trace); }
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            passed = true, count = checks.Count, checks,
            actualNetworkRequests = 0, actualTimezoneWrites = 0, actualRegionWrites = 0, actualBrowserLaunches = 0,
            screenshots = new[] { "default.png", "compact-edge.png" }, verifiedAt = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static async Task CheckCoordinatorAsync(Action<bool, string> check)
    {
        var route = new FakeRoute(); var probe = new FakeProbe(); var zone = new FakeTimezone(); var country = new FakeRegion();
        var coordinator = new EnvironmentCoordinator(route, probe, zone, country);
        var detected = await coordinator.DetectAsync(default);
        check(!detected.Matches && zone.Writes.Count == 0 && country.Writes.Count == 0, "detection never writes timezone or country");
        var synced = await coordinator.SyncAsync(default);
        check(synced.Matches && zone.Writes.SequenceEqual(["Eastern Standard Time"]), "explicit sync writes detected Windows zone and reads it back");
        check(synced.SystemCountry == "US" && country.Writes.SequenceEqual(["US"]), "explicit sync also writes and confirms exit country");
        check(probe.Calls == 2, "sync performs new lookup rather than using prior screen result");
        await coordinator.SyncAsync(default);
        check(zone.Writes.Count == 1 && country.Writes.Count == 1, "matching timezone and country are not rewritten");
        check(zone.Writes.All(z => z != "China Standard Time"), "no previous/Beijing restore write");

        foreach (var changeAt in new[] { 2, 3 })
        {
            var changing = new FakeRoute { ChangeAt = changeAt }; var unchanged = new FakeTimezone(); var unchangedRegion = new FakeRegion();
            try { await new EnvironmentCoordinator(changing, new FakeProbe(), unchanged, unchangedRegion).SyncAsync(default); throw new Exception("expected route change"); }
            catch (InvalidOperationException ex) { check(ex.Message.Contains("变化"), $"route change at read {changeAt} reported"); }
            check(unchanged.Writes.Count == 0 && unchangedRegion.Writes.Count == 0, $"route change at read {changeAt} blocks both writes");
        }
        var broken = new FakeProbe { Fail = true }; var safeZone = new FakeTimezone(); var safeCountry = new FakeRegion();
        try { await new EnvironmentCoordinator(new FakeRoute(), broken, safeZone, safeCountry).SyncAsync(default); throw new Exception("expected failed probe"); }
        catch (InvalidOperationException) { check(safeZone.Writes.Count == 0 && safeCountry.Writes.Count == 0, "failed lookup never changes system"); }
        var unconfirmed = new FakeTimezone { IgnoreWrite = true }; var unwrittenRegion = new FakeRegion();
        try { await new EnvironmentCoordinator(new FakeRoute(), new FakeProbe(), unconfirmed, unwrittenRegion).SyncAsync(default); throw new Exception("expected readback mismatch"); }
        catch (InvalidOperationException ex) { check(ex.Message.Contains("确认"), "readback mismatch does not report success"); }
        check(unconfirmed.Writes.Count == 1 && unwrittenRegion.Writes.Count == 0, "failed zone readback does not restore older timezone or write country");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancelZone = new FakeTimezone();
        try { await new EnvironmentCoordinator(new FakeRoute(), new FakeProbe(), cancelZone, safeCountry).SyncAsync(cancelled.Token); throw new Exception("expected cancellation"); }
        catch (OperationCanceledException) { check(cancelZone.Writes.Count == 0, "cancel before dispatch never changes timezone"); }

        var deferred = new TaskCompletionSource<EgressInfo>();
        var slowProbe = new FakeProbe { Pending = deferred.Task };
        var vm = new LoginViewModel(new EnvironmentCoordinator(new FakeRoute(), slowProbe, new FakeTimezone(), new FakeRegion()), null, []);
        var pending = vm.DetectAsync();
        check(vm.IsBusy && !vm.CanAct, "active lookup blocks duplicate action only");
        await vm.SyncAsync();
        check(slowProbe.Calls == 1, "duplicate action cannot start a second lookup");
        deferred.SetResult(FakeProbe.Result with { QueryNote = "已使用备用定位服务", Sources = "ipwho.is + ipapi.co" });
        await pending;
        check(!vm.IsBusy && vm.CanAct && !vm.CanOpen, "completed lookup restores controls and missing browser stays unavailable");
        check(vm.StatusTitle.Contains("需要同步"), "timezone mismatch communicated without system write");
        check(vm.CheckedAt.Contains("已使用备用定位服务"), "alternate provider use remains visible in completed result");
        var failingVm = new LoginViewModel(new EnvironmentCoordinator(new FakeRoute(), broken, new FakeTimezone(), new FakeRegion()), null, []);
        await failingVm.DetectAsync();
        check(!failingVm.IsBusy && failingVm.StatusTitle.Contains("未完成"), "failed lookup is readable and retry enabled");

        var matchingZone = new FakeTimezone { Zone = "Eastern Standard Time" }; var wrongCountry = new FakeRegion();
        var regionOnly = new EnvironmentCoordinator(new FakeRoute(), new FakeProbe(), matchingZone, wrongCountry);
        var regionMismatch = await regionOnly.DetectAsync(default);
        check(regionMismatch.TimezoneMatches && !regionMismatch.RegionMatches && !regionMismatch.Matches, "equal timezone cannot hide country mismatch");
        check((await regionOnly.SyncAsync(default)).Matches && matchingZone.Writes.Count == 0 && wrongCountry.Writes.SequenceEqual(["US"]), "country-only mismatch writes only country");
        var unknown = new FakeProbe { Pending = Task.FromResult(FakeProbe.Result with { CountryCode = "" }) };
        var noWriteZone = new FakeTimezone(); var noWriteCountry = new FakeRegion();
        try { await new EnvironmentCoordinator(new FakeRoute(), unknown, noWriteZone, noWriteCountry).SyncAsync(default); throw new Exception("expected unknown country"); }
        catch (InvalidOperationException) { check(noWriteZone.Writes.Count == 0 && noWriteCountry.Writes.Count == 0, "unknown exit country blocks both writes before dispatch"); }
        var retainedZone = new FakeTimezone(); var failedCountry = new FakeRegion { IgnoreWrite = true };
        try { await new EnvironmentCoordinator(new FakeRoute(), new FakeProbe(), retainedZone, failedCountry).SyncAsync(default); throw new Exception("expected country readback mismatch"); }
        catch (InvalidOperationException ex) { check(ex.Message.Contains("没有全部"), "partial country failure cannot claim combined success"); }
        check(retainedZone.Zone == "Eastern Standard Time" && retainedZone.Writes.Count == 1 && failedCountry.Country == "JP", "partial sync retains applied timezone without rollback");
    }
    private sealed class FakeRoute : INetworkRouteResolver
    {
        public int Reads; public int ChangeAt = int.MaxValue;
        public Task<NetworkRoute> ResolveAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new NetworkRoute(++Reads >= ChangeAt ? "http://127.0.0.1:7891" : "http://127.0.0.1:7890", "fixture")); }
    }
    private sealed class FakeProbe : IEgressProbe
    {
        public static EgressInfo Result => new("203.0.113.24", "United States", "New York", "America/New_York", "Eastern Standard Time", DateTimeOffset.UtcNow, CountryCode: "US");
        public int Calls; public bool Fail; public Task<EgressInfo>? Pending;
        public Task<EgressInfo> ProbeAsync(NetworkRoute route, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Fail ? Task.FromException<EgressInfo>(new InvalidOperationException("fixture unavailable")) : Pending ?? Task.FromResult(Result); }
    }
    private sealed class FakeTimezone : ISystemTimezone
    {
        public string Zone = "Pacific Standard Time"; public bool IgnoreWrite; public List<string> Writes { get; } = [];
        public Task<string> ReadAsync(CancellationToken token) => Task.FromResult(Zone);
        public Task SetAsync(string windowsId, CancellationToken token) { Writes.Add(windowsId); if (!IgnoreWrite) Zone = windowsId; return Task.CompletedTask; }
    }
    private sealed class FakeRegion : ISystemRegion
    {
        public string Country = "JP"; public bool IgnoreWrite; public List<string> Writes { get; } = [];
        public string Read() => Country;
        public void Set(string code) { Writes.Add(code); if (!IgnoreWrite) Country = code; }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
