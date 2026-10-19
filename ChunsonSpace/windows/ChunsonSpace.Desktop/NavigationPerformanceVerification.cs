using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.NodeTesting;

namespace ChunsonSpace.Desktop;

// Only reached by --verify-ui navigation-performance. Real WPF bindings and local
// history readers, synthetic accounts/files, no normal startup or business requests.
internal static class NavigationPerformanceVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, Private)!;
    public class CountingStorage : DispatchProxy
    {
        internal IFileStorage Inner = null!;
        internal int UiThread, UiReads, UiDirectoryScans, WorkerReads;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (Environment.CurrentManagedThreadId == UiThread)
            {
                if (method!.Name == "Read") Interlocked.Increment(ref UiReads);
                if (method.Name == "Directories") Interlocked.Increment(ref UiDirectoryScans);
            }
            else if (method!.Name == "Read") Interlocked.Increment(ref WorkerReads);
            try { return method!.Invoke(Inner, args); }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
    }
    private sealed class CountingClock(int uiThread) : IAccountClock
    {
        public int UiReads;
        public int WorkerReads;
        public DateTimeOffset UtcNow
        {
            get
            {
                if (Environment.CurrentManagedThreadId == uiThread) Interlocked.Increment(ref UiReads);
                else Interlocked.Increment(ref WorkerReads);
                return DateTimeOffset.UtcNow;
            }
        }
        public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
    }
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var fixture = Path.Combine(directory, "synthetic");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var workspace = DesktopWorkspace.CreateManagedFixture(fixture);
        for (int a = 0; a < 8; a++)
        {
            var name = "Synthetic-" + a;
            workspace.Profiles.CreateBoundAccount(name, new("", "Asia/Shanghai", "en-US", FollowSystemProxy: true));
            var folder = Path.Combine(workspace.Accounts!.Repository.AccountPath(name), "ciallo-tests");
            Directory.CreateDirectory(folder);
            for (int h = 0; h < 20; h++)
                File.WriteAllText(Path.Combine(folder, h + ".json"), JsonSerializer.Serialize(
                    new CialloHistoryEntry(DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddMinutes(a * 20 + h),
                        new([new("gpt-6.1-sol", "saved-model", "high", "completed", "Synthetic reply " + h, TimeSpan.FromSeconds(1)),
                             new("gpt-6-astra", "saved-model", "high", "completed", "Synthetic reply " + h, TimeSpan.FromSeconds(1))], false), false)));
        }
        var clock = new CountingClock(Environment.CurrentManagedThreadId);
        var scores = new IpScoreCache(Path.Combine(fixture, "fixture-scores.json"), clock);
        var score = new IpPureScore(null, "synthetic") { Provider = "IPPure", DirectQuality = 90 };
        var report = new IpQualityReport(true, "192.0.2.24", "", "", "", "", "",
            null, null, null, null, null, DateTimeOffset.UtcNow.AddMinutes(-1), "synthetic", "IPPure") { Score = score };
        scores.Put(report.Ip, score, report); scores.Save();
        Field(workspace.ProxyServices.NodeTesting!, "scores").SetValue(workspace.ProxyServices.NodeTesting, scores);
        var countedStorage = DispatchProxy.Create<IFileStorage, CountingStorage>();
        var storageCounts = (CountingStorage)countedStorage;
        storageCounts.Inner = (IFileStorage)Field(workspace.Profiles, "files").GetValue(workspace.Profiles)!;
        storageCounts.UiThread = Environment.CurrentManagedThreadId;
        Field(workspace.Profiles, "files").SetValue(workspace.Profiles, countedStorage);
        var model = new ShellViewModel(workspace);
        Field(model, "lastVerifiedLocalEnvironment").SetValue(model, new LocalEnvironmentReport(true, true, "", report.Ip, "", "", "", "synthetic"));
        await model.ResultsGallery.CurrentLoad;
        var gallery = model.ResultsGallery;
        var originalRead = (Func<SavedResultsSnapshot>)Field(gallery, "read").GetValue(gallery)!;
        int historyLoads = 0, uiHistoryLoads = 0, replacements = 0, notifications = 0, scoreNotifications = 0;
        var historyDurations = new List<double>();
        Field(gallery, "read").SetValue(gallery, (Func<SavedResultsSnapshot>)(() =>
        {
            Interlocked.Increment(ref historyLoads);
            if (System.Windows.Application.Current.Dispatcher.CheckAccess()) Interlocked.Increment(ref uiHistoryLoads);
            var watch = Stopwatch.StartNew(); var snapshot = originalRead();
            lock (historyDurations) historyDurations.Add(watch.Elapsed.TotalMilliseconds);
            return snapshot;
        }));
        ((INotifyCollectionChanged)gallery.Items).CollectionChanged += (_, _) => replacements++;
        model.PropertyChanged += (_, e) => { notifications++; if (e.PropertyName == nameof(model.DashboardScore)) scoreNotifications++; };
        var listener = new UiVerification.BindingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var window = UiVerification.CreateWindow(model);
        var samples = new List<object>(); var checks = new List<object>();
        void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) throw new InvalidOperationException(name); }
        async Task Settle()
        {
            for (int i = 0; i < 12; i++)
            {
                var pending = gallery.CurrentLoad; await pending;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (pending == gallery.CurrentLoad && !gallery.IsLoading) return;
            }
            throw new InvalidOperationException("Gallery did not settle");
        }
        async Task Measure(string phase, string target, Action action)
        {
            var n = notifications; var s = scoreNotifications; var l = historyLoads; var r = replacements;
            var u = clock.UiReads; var b = clock.WorkerReads;
            var reads = storageCounts.UiReads; var scans = storageCounts.UiDirectoryScans;
            var watch = Stopwatch.StartNew(); double inputMs = 0;
            var ping = window.Dispatcher.InvokeAsync(() => inputMs = watch.Elapsed.TotalMilliseconds, DispatcherPriority.Input);
            action(); var commandMs = watch.Elapsed.TotalMilliseconds;
            await ping;
            var renderWatch = Stopwatch.StartNew();
            UiVerification.ArrangeForCapture(window.RenderRoot, 1040, 670);
            var bitmap = new RenderTargetBitmap(1040, 670, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window.RenderRoot);
            var renderMs = renderWatch.Elapsed.TotalMilliseconds;
            var firstRenderMs = watch.Elapsed.TotalMilliseconds;
            await Settle();
            samples.Add(new { phase, target, commandMs, dispatcherInputMs = inputMs, layoutAndBitmapRenderMs = renderMs, firstRenderMs,
                settledMs = watch.Elapsed.TotalMilliseconds, propertyNotifications = notifications - n,
                scoreNotifications = scoreNotifications - s, scoreLookupsOnUi = clock.UiReads - u, scoreLookupsOnWorker = clock.WorkerReads - b,
                profileReadsOnUi = storageCounts.UiReads - reads, profileDirectoryScansOnUi = storageCounts.UiDirectoryScans - scans,
                historyLoads = historyLoads - l, collectionChanges = replacements - r, page = model.Page });
        }
        try
        {
            window.Show(); UiVerification.ArrangeForCapture(window.RenderRoot, 1040, 670); await Settle();
            UiVerification.AssertWindowsOutsideDesktop();
            foreach (var phase in new[] { "first-route", "warm-1", "warm-2", "warm-3" })
                foreach (var route in new[] { "Accounts", "Tests", "Results", "Settings", "Dashboard" })
                    await Measure(phase, route, () => model.NavigateCommand.Execute(route));
            foreach (var dark in new[] { false, true })
            {
                model.DarkTheme = dark;
                foreach (var mode in new[] { "单账号", "多账号" })
                {
                    await Measure(dark ? "night-mode" : "paper-mode", mode, () => model.SetInterfaceModeCommand.Execute(mode));
                    var dash = (Dashboard)window.FindName("DashboardPage");
                    Check((dark ? "night " : "paper ") + mode + " navigation/mode visible",
                        UiVerification.FullyVisible((FrameworkElement)window.FindName("NavDashboard"), window.RenderRoot)
                        && UiVerification.FullyVisible((FrameworkElement)dash.FindName("InterfaceModePicker"), window.RenderRoot)
                        && model.InterfaceMode == mode);
                }
            }
            await Measure("warm-burst", "Results/Settings/Dashboard x8", () =>
            {
                for (int i = 0; i < 8; i++) { model.Navigate("Results"); model.Navigate("Settings"); model.Navigate("Dashboard"); }
            });
            Check("all 320 synthetic history rows remain", gallery.Items.Cast<SavedResultRow>().Count() == 320);
            Check("history reader stays off Dispatcher", uiHistoryLoads == 0);
            Check("no account/network requests", workspace.Accounts!.OfflineTransport!.RequestCount == 0);
            Check("no clone launch/stop", workspace.OfflineRuntime!.LastLaunchRequest is null && workspace.OfflineRuntime.StopCalls == 0);
            Check("no new binding errors", listener.Errors.Count == 0);
        }
        finally
        {
            window.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            File.WriteAllText(Path.Combine(directory, "performance.json"), JsonSerializer.Serialize(new
            {
                scope = "synthetic WPF command, Dispatcher Input queue, layout and offscreen RenderTargetBitmap; not real user operations or screen-present latency",
                coldDefinition = "first visit of each route in a new process; OS disk cache was not flushed; startup preload excluded",
                syntheticAccounts = 8, historyFiles = 160, resultRows = 320, injectedLatencyMs = 0,
                samples, checks, historyLoads, uiHistoryLoads, historyDurations, bindingErrors = listener.Errors,
                actualAccountRequests = 0, actualSystemWrites = 0, actualUserProgramOperations = 0
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

