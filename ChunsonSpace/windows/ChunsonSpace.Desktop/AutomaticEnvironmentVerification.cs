using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

internal static partial class LocalEnvironmentVerification
{
    internal static async Task RunAutomaticAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>(); var screenshots = new List<string>(); bool passed = false;
        var workspaces = new List<DesktopWorkspace>(); var models = new List<ShellViewModel>();
        var listener = new UiVerification.BindingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        void Check(string name, bool ok) { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); }
        async Task Idle() => await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        (DesktopWorkspace Workspace, ShellViewModel Model) Fixture(string name, bool automatic = false)
        {
            var root = Path.Combine(directory, name); Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "ChunsonSpace-fixture.json"), "{}");
            var original = Path.Combine(root, "original"); Directory.CreateDirectory(original);
            File.WriteAllText(Path.Combine(original, "auth.json"), new JsonObject { ["tokens"] = new JsonObject
                { ["account_id"] = "synthetic-environment", ["access_token"] = "synthetic-only" } }.ToJsonString());
            var w = DesktopWorkspace.CreateManagedFixture(root, new(root, original, null, null), automatic);
            w.AutomaticEnvironmentOnStartup = automatic;
            var m = new ShellViewModel(w); workspaces.Add(w); models.Add(m);
            return (w, m);
        }
        MainWindow? window = null;
        try
        {
            var preview = new ShellViewModel(); models.Add(preview);
            await preview.StartLocalEnvironmentAsync();
            var isolated = Fixture("default-offline");
            await isolated.Model.StartLocalEnvironmentAsync();
            Check("preview and default offline startup have no import, network or host monitor", preview.EnvironmentMonitor is null
                && isolated.Model.EnvironmentMonitor is null && isolated.Model.AccountTotal == 0
                && isolated.Workspace.Accounts!.OfflineTransport!.RequestCount == 0);

            var (workspace, model) = Fixture("startup", automatic: true);
            var clock = new EnvironmentClock();
            model.EnvironmentNow = clock.GetUtcNow;
            model.StartEnvironmentMonitor(clock);
            var region = workspace.Accounts!.OfflineRegion!;
            var transport = new IpSeparationVerification.FakeTransport();
            var ip = "8.8.8.8"; var zone = "America/New_York"; var country = "US"; bool apiFailure = false, partial = false, ipv4Failure = false, ipv6Failure = false;
            Func<AccountHttpRequest, CancellationToken, Task<AccountHttpResponse>> response = (r, _) =>
            {
                if (r.Url == IpInfoService.Ipv4Endpoint)
                    return Task.FromResult(IpSeparationVerification.FakeTransport.Text(ipv4Failure ? "unavailable" : ip, ipv4Failure ? 503 : 200));
                if (r.Url == IpInfoService.Ipv6Endpoint)
                    return Task.FromResult(IpSeparationVerification.FakeTransport.Text(ipv6Failure ? "unavailable" : "2606:4700:4700::1111", ipv6Failure ? 503 : 200));
                if (r.Url.Host != "ipinfo.io") throw new InvalidOperationException("Unexpected provider");
                var queriedIp = Uri.UnescapeDataString(r.Url.AbsolutePath.Split('/')[1]);
                return Task.FromResult(IpSeparationVerification.FakeTransport.Text(apiFailure ? "{}" :
                    JsonSerializer.Serialize(new { ip = queriedIp, country, timezone = partial ? "" : zone }), apiFailure ? 429 : 200));
            };
            transport.Handle = response;
            var egress = new EgressService(workspace.Accounts.Repository, transport, clock);
            var route = "http://127.0.0.1:7890"; string? readbackOverride = null;
            workspace.LocalEnvironment = new(_ => Task.FromResult(new LoginProxySelection(route, "synthetic")),
                egress.ProbeRegionProxyAsync, new FixtureAdmission(),
                () => TimeZoneInfo.FindSystemTimeZoneById(readbackOverride ?? region.State.WindowsTimezone),
                () => region.State.GeoId switch { 244 => "US", 122 => "JP", 45 => "CN", _ => "" },
                egress.WatchRegionProxyAsync, egress.IsCurrentPrimaryAsync, clock.GetUtcNow);

            // Reuse the original import interception to prove the startup sequence really awaits the import.
            var storage = DispatchProxy.Create<IFileStorage, LocalAccountImportVerification.ImportStorage>();
            var import = (LocalAccountImportVerification.ImportStorage)storage;
            import.Source = Path.Combine(workspace.OriginalHome!, "auth.json");
            using var release = new ManualResetEventSlim();
            import.Release = release;
            typeof(AccountRepository).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace.Accounts.Repository, storage);
            var authBefore = File.ReadAllBytes(import.Source);
            model.Navigate("Settings"); var mode = model.InterfaceMode;
            var startup = model.StartLocalEnvironmentAsync();
            await import.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check("startup awaits busy import before environment without blocking navigation", model.IsBusy && transport.Requests.IsEmpty
                && model.NavigateCommand.CanExecute("Dashboard") && !startup.IsCompleted);
            release.Set(); await startup;
            Check("startup imports then synchronizes and preserves page, mode and original auth", model.AccountTotal == 1
                && model.IsSettingsPage && model.InterfaceMode == mode && authBefore.SequenceEqual(File.ReadAllBytes(import.Source))
                && region.State == new RegionState(true, 244, "Eastern Standard Time"));
            Check("successful fresh readback immediately notifies card and records timestamp", model.DashboardEnvironmentTime.Contains("已同步")
                && model.RegionStatus.Contains("已同步") && workspace.Profiles.ReadLastEnvironmentSync() == clock.GetUtcNow()
                && region.TimezoneWrites == 1 && region.RegionWrites == 1);
            Check("automatic startup uses non-elevating writer path", region.AutomaticTimezoneWrites == 1);
            await model.StartLocalEnvironmentAsync();
            Check("repeated startup is one-shot", import.Reads == 1 && region.TimezoneWrites == 1);

            var saved = workspace.Profiles.ReadLastEnvironmentSync();
            var settingsBytes = File.ReadAllBytes(Path.Combine(workspace.AccountRoot, "settings.json"));
            var count = transport.Requests.Count;
            clock.Advance(TimeSpan.FromMinutes(5)); await Idle(); await model.LocalEnvironmentOperation;
            Check("watchdog same IP uses one address request and no OS/settings/timestamp rewrite", transport.Requests.Count == count + 1
                && region.TimezoneWrites == 1 && region.RegionWrites == 1 && workspace.Profiles.ReadLastEnvironmentSync() == saved
                && settingsBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(workspace.AccountRoot, "settings.json"))));
            count = transport.Requests.Count;
            clock.Advance(TimeSpan.FromMinutes(30)); await Idle(); await model.LocalEnvironmentOperation;
            Check("same-IP regional evidence expires on fake time without resetting successful sync", transport.Requests.Count == count + 2
                && workspace.Profiles.ReadLastEnvironmentSync() == saved && region.TimezoneWrites == 1);

            model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
            Check("manual sync remains available and matching retry does not rewrite timestamp", !model.IsError && region.TimezoneWrites == 1
                && workspace.Profiles.ReadLastEnvironmentSync() == saved && model.FeedbackTitle == "地区与时区同步完成");
            region.State = region.State with { WindowsTimezone = "Tokyo Standard Time" };
            clock.Advance(TimeSpan.FromMinutes(5)); await Idle(); await model.LocalEnvironmentOperation;
            Check("same IP watchdog repairs externally changed system timezone", region.TimezoneWrites == 2
                && region.State.WindowsTimezone == "Eastern Standard Time" && workspace.Profiles.ReadLastEnvironmentSync() > saved);

            count = transport.Requests.Count; ip = "1.1.1.1"; zone = "Asia/Tokyo"; country = "JP";
            model.EnvironmentMonitor!.SignalChange(); model.EnvironmentMonitor.SignalChange(); model.EnvironmentMonitor.SignalChange();
            clock.Advance(TimeSpan.FromSeconds(1)); await Idle();
            Check("local changes debounce before probing", transport.Requests.Count == count);
            clock.Advance(TimeSpan.FromSeconds(14)); await Idle(); await model.LocalEnvironmentOperation;
            Check("manual node-change event syncs new primary and matching region without NIC change", region.State == new RegionState(true, 122, "Tokyo Standard Time")
                && model.DashboardTimezone == "Asia/Tokyo" && transport.Requests.Skip(count).Count(r => r.Url.Host == "ipinfo.io") == 1);
            Check("monitor watches known selection/config metadata and rejects unrelated secret file names",
                LocalEnvironmentMonitor.Relevant("shared_preferences.json") && LocalEnvironmentMonitor.Relevant("cache.db")
                && LocalEnvironmentMonitor.Relevant("database.sqlite-wal") && !LocalEnvironmentMonitor.Relevant("subscription.yaml"));

            var writes = region.TimezoneWrites; saved = workspace.Profiles.ReadLastEnvironmentSync();
            partial = true; await model.RefreshLocalEnvironmentAsync();
            Check("partial location never writes or stamps success and old success is explicitly historical", region.TimezoneWrites == writes
                && workspace.Profiles.ReadLastEnvironmentSync() == saved && model.DashboardEnvironmentTime.StartsWith("同步未完成")
                && !model.LocalEnvironmentStatus.Contains("已核对") && model.LastEnvironmentSyncLabel.StartsWith("上次成功"));
            partial = false; apiFailure = true; await model.RefreshLocalEnvironmentAsync();
            Check("API quota failure stays truthful and preserves original HTTP failure detail", region.TimezoneWrites == writes
                && model.LocalEnvironmentDetail.Contains("429") && model.DashboardTimezone == "时区尚未确认");
            apiFailure = false; country = ""; await model.RefreshLocalEnvironmentAsync();
            Check("missing country blocks writing without timezone fallback", region.TimezoneWrites == writes
                && workspace.Profiles.ReadLastEnvironmentSync() == saved);
            country = "US"; zone = "America/Chicago"; region.AllowTimezone = false;
            var automaticWrites = region.AutomaticTimezoneWrites;
            await model.RefreshLocalEnvironmentAsync();
            Check("automatic permission failure uses silent path and leaves manual retry usable", region.AutomaticTimezoneWrites == automaticWrites + 1
                && model.SyncLocalTimezoneCommand.CanExecute(null) && workspace.Profiles.ReadLastEnvironmentSync() == saved);
            model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
            Check("denied writer leaves pending state and no success timestamp", model.IsError && model.RegionStatus.Contains("未完成")
                && region.State.WindowsTimezone == "Tokyo Standard Time" && workspace.Profiles.ReadLastEnvironmentSync() == saved);
            region.AllowTimezone = true; region.AllowRegion = false;
            model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
            Check("partial OS write is preserved without rollback or false success", model.IsError && region.State.WindowsTimezone == "Central Standard Time"
                && region.State.GeoId == 122 && workspace.Profiles.ReadLastEnvironmentSync() == saved);
            region.AllowRegion = true; model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
            Check("manual retry repairs partial result and immediately updates common success", !model.IsError && region.State.GeoId == 244
                && model.DashboardEnvironmentTime.StartsWith("已同步") && model.LocalEnvironmentStatus.Contains("已核对"));
            saved = workspace.Profiles.ReadLastEnvironmentSync(); readbackOverride = "Tokyo Standard Time";
            model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
            Check("writer success alone cannot override mismatching fresh system readback", model.IsError
                && model.DashboardEnvironmentTime.StartsWith("同步未完成") && workspace.Profiles.ReadLastEnvironmentSync() == saved);
            readbackOverride = null; await model.RefreshLocalEnvironmentAsync();

            var restored = new ShellViewModel(workspace); models.Add(restored);
            Check("restart restores last success in existing settings but never claims current match before checking",
                restored.LastEnvironmentSyncLabel == model.LastEnvironmentSyncLabel && restored.DashboardEnvironmentTime.StartsWith("待检测"));

            // Hold old A lookup; cancellation is intentionally ignored by the synthetic responder.
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delayed = new TaskCompletionSource<AccountHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            ip = "8.8.4.4"; zone = "America/New_York"; writes = region.TimezoneWrites;
            transport.Handle = (r, t) =>
            {
                if (r.Url.Host == "ipinfo.io") { entered.TrySetResult(); return delayed.Task; }
                return response(r, t);
            };
            var old = model.RefreshLocalEnvironmentAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var same = model.RefreshLocalEnvironmentAsync();
            Check("simultaneous refreshes share one in-flight task", ReferenceEquals(old, same));
            ip = "9.9.9.9"; zone = "Asia/Tokyo"; country = "JP"; transport.Handle = response;
            model.EnvironmentMonitor.SignalChange();
            delayed.SetResult(IpSeparationVerification.FakeTransport.Text("{\"ip\":\"8.8.4.4\",\"country\":\"US\",\"timezone\":\"America/New_York\"}"));
            await old;
            Check("A-to-B change rejects late A evidence before any write", region.TimezoneWrites == writes && !model.DashboardEnvironmentTime.StartsWith("已同步"));
            clock.Advance(TimeSpan.FromSeconds(15)); await Idle(); await model.LocalEnvironmentOperation;
            Check("coalesced replacement applies only B", model.DashboardTimezone == "Asia/Tokyo"
                && region.State.WindowsTimezone == "Tokyo Standard Time" && region.TimezoneWrites == writes + 1);

            // Physical NIC and proxy URL are unchanged; pre-write address validation must still reject A.
            ip = "8.8.8.8"; country = "US"; zone = "America/New_York"; writes = region.TimezoneWrites;
            transport.Handle = async (r, t) =>
            {
                var result = await response(r, t);
                if (r.Url.Host == "ipinfo.io") ip = "1.0.0.1";
                return result;
            };
            await model.RefreshLocalEnvironmentAsync();
            Check("fresh primary validation rejects silent node change before OS write", region.TimezoneWrites == writes
                && model.RegionStatus.Contains("出口已变化"));
            transport.Handle = response; await model.RefreshLocalEnvironmentAsync();

            ipv6Failure = true;
            var dual = await egress.ProbeRegionProxyAsync(route);
            Check("IPv6 failure leaves IPv4 primary region usable", dual.Ok && dual.Addresses.Ipv4.Available && !dual.Addresses.Ipv6.Available);
            ipv6Failure = false; ipv4Failure = true;
            var v6 = await egress.WatchRegionProxyAsync(route);
            Check("lightweight monitor falls back to IPv6 independently", v6.Ok && !v6.Addresses.Ipv4.Available
                && v6.Ip == "2606:4700:4700::1111");
            ipv4Failure = false;
            saved = workspace.Profiles.ReadLastEnvironmentSync();
            var regionWrites = region.RegionWrites;
            ip = "9.9.9.9"; zone = "Asia/Tokyo"; country = "JP";
            region.TimezoneWritten = () => { model.InvalidateEnvironment(); ip = "1.1.1.1"; zone = "America/New_York"; country = "US"; };
            await model.RefreshLocalEnvironmentAsync(); region.TimezoneWritten = null;
            Check("change during write stops subsequent region commit and retains actual timezone without rollback",
                region.State.WindowsTimezone == "Tokyo Standard Time" && region.RegionWrites == regionWrites
                && workspace.Profiles.ReadLastEnvironmentSync() == saved && !model.DashboardEnvironmentTime.StartsWith("已同步"));
            await model.RefreshLocalEnvironmentAsync();
            Check("only IPinfo transport requests and no account, score, model, node-control calls", transport.Requests.All(r =>
                r.Url.Host is "v4.api.ipinfo.io" or "v6.api.ipinfo.io" or "ipinfo.io") && workspace.Accounts.OfflineTransport!.RequestCount == 0
                && workspace.OfflineRuntime!.StopCalls == 0 && workspace.ProxyServices.FixtureFlClashRunner!.Invocations.Count == 0);

            await model.RefreshLocalEnvironmentAsync();
            window = UiVerification.CreateWindow(model); window.Show(); await Idle();
            foreach (var dark in new[] { false, true })
            {
                model.DarkTheme = dark;
                foreach (var single in new[] { true, false })
                {
                    model.InterfaceMode = single ? "单账号" : "多账号"; model.Navigate("Dashboard");
                    UiVerification.ArrangeForCapture(window.RenderRoot, 1280, 850); window.UpdateLayout(); await Idle();
                    var dashboard = (Dashboard)window.FindName("DashboardPage");
                    var texts = Descendants<TextBlock>(dashboard).Where(t => t.IsVisible && t.Text == model.DashboardEnvironmentTime).ToArray();
                    var sync = Descendants<Button>(dashboard).Where(b => b.IsVisible && b.Command == model.SyncLocalTimezoneCommand).ToArray();
                    Check($"{dark}/{single}: actual region label and manual sync binding visible", texts.Length == 1 && sync.Length == 1
                        && UiVerification.FullyVisible(texts[0], window.RenderRoot) && sync[0].IsEnabled);
                    if (single) Check($"{dark}: populated single-home import remains enabled",
                        Descendants<Button>(dashboard).Any(b => b.IsVisible && b.IsEnabled && b.Command == model.ImportLocalAccountCommand));
                    if (!single)
                    {
                        var bitmap = new RenderTargetBitmap(1280, 850, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window.RenderRoot);
                        var path = Path.Combine(directory, dark ? "region-night.png" : "region-paper.png");
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(path); encoder.Save(output); screenshots.Add(path);
                    }
                }
            }
            model.Navigate("Settings"); UiVerification.ArrangeForCapture(window.RenderRoot, 1280, 850);
            Check("settings uses same sync state and success history", ((TextBlock)window.FindName("EnvironmentRegionStatusText")).Text == model.RegionStatus
                && ((Button)window.FindName("OneClickEnvironmentRegionButton")).Command == model.ConfigureRegionCommand);
            model.Navigate("Dashboard"); window.Hide();
            ip = "9.9.9.9"; zone = "Asia/Tokyo"; country = "JP";
            clock.Advance(TimeSpan.FromMinutes(5)); await Idle(); await model.LocalEnvironmentOperation;
            Check("hidden/tray window keeps process-owned watchdog active", model.EnvironmentMonitor is not null && region.State.WindowsTimezone == "Tokyo Standard Time");
            count = transport.Requests.Count;
            window.Close(); window = null;
            clock.Advance(TimeSpan.FromMinutes(10)); await Idle();
            Check("actual window close disposes monitor and all future timer probes", model.EnvironmentMonitor is null && transport.Requests.Count == count);

            var exiting = Fixture("exit", automatic: true); var exitClock = new EnvironmentClock();
            exiting.Model.StartEnvironmentMonitor(exitClock);
            Check("update/exit drain uses same monitor shutdown", await exiting.Model.DrainTasksForExitAsync(explicitExit: true)
                && exiting.Model.EnvironmentMonitor is null);
            Check("no WPF binding errors", listener.Errors.Count == 0);
            passed = true;
        }
        finally
        {
            window?.Close();
            foreach (var model in models) { model.StopEnvironmentMonitor(); model.DisposeUpdates(); }
            foreach (var workspace in workspaces) await workspace.Accounts!.DisposeAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new
            { passed, scope = "automatic-environment", checks, screenshots, bindingErrors = listener.Errors,
                realRequests = 0, actualAccountOperations = false, actualSystemTimezoneChanges = false, actualProxyChanges = false,
                monitor = new { debounceSeconds = 2, minimumEventProbeSeconds = 15, fallbackMinutes = 5, fakeTime = true } },
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    // Only time delivery is fake; callbacks execute the production monitor and view model.
    private sealed class EnvironmentClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        private readonly List<EnvironmentTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new EnvironmentTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        internal void Advance(TimeSpan interval)
        {
            now += interval;
            foreach (var timer in timers.ToArray())
                if (!timer.Disposed && timer.Due <= now)
                { timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + timer.Period; timer.Callback(timer.State); }
        }
        private sealed class EnvironmentTimer(EnvironmentClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal TimerCallback Callback = callback; internal object? State = state; internal bool Disposed;
            internal DateTimeOffset Due; internal TimeSpan Period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { Period = period; Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.now + dueTime; return !Disposed; }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
