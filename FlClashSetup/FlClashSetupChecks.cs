using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Chunson.Setup;

internal static class FlClashSetupChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var found = new FlClashInstallation(null, false);
        var routeCalls = 0;
        var launches = new List<string>();
        Exception? routeError = null;
        Exception? launchError = null;
        var service = new FlClashSetupService(_ =>
        {
            routeCalls++;
            return routeError is null ? Task.CompletedTask : Task.FromException(routeError);
        }, () => found, path => { if (launchError is not null) throw launchError; launches.Add(path); });
        var missing = await service.InspectAsync(default);
        check(!missing.Ready && routeCalls == 0 && launches.Count == 0, "setup missing FlClash only notifies without route or browser requests");
        found = new("fixture/FlClash.exe", false);
        var installed = await service.InspectAsync(default);
        check(!installed.Ready && routeCalls == 0, "setup installed but stopped cannot pass");
        found = found with { Running = true };
        routeError = new IOException("主端口不可用");
        check(!(await service.InspectAsync(default)).Ready, "setup running FlClash without its main listener cannot pass");
        routeError = null;
        check((await service.InspectAsync(default)).Ready && routeCalls == 2, "setup only ready after local route check");
        var beforeCancel = routeCalls;
        try { await service.InspectAsync(new CancellationToken(true)); check(false, "cancelled inspection must stop"); }
        catch (OperationCanceledException) { check(routeCalls == beforeCancel, "setup cancelled inspection makes no route call"); }
        var fakeExecutable = Path.Combine(root, "FlClash.exe"); File.WriteAllText(fakeExecutable, "fixture");
        check(!FlClashSetupService.IsFlClash(fakeExecutable), "setup rejects arbitrary executable renamed FlClash");
        try { service.OpenInstalled(fakeExecutable); check(false, "invalid installed path must fail"); }
        catch (InvalidOperationException) { check(launches.Count == 0, "setup invalid portable selection never executes"); }

        FlClashSetupWindow Window(FlClashReadiness state) => new(service, state)
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        static Button FindButton(FlClashSetupWindow window, string content) => ((WrapPanel)((StackPanel)((ScrollViewer)window.Content).Content).Children[4])
            .Children.OfType<Button>().Single(item => Equals(item.Content, content));
        void Capture(FlClashSetupWindow window, string name)
        {
            window.UpdateLayout();
            check(!window.IsActive && window.Left + window.ActualWidth < SystemParameters.VirtualScreenLeft, "setup fixture remains offscreen and inactive");
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(root, name + ".png"))) encoder.Save(file);
            check(((ScrollViewer)window.Content).ScrollableHeight < 1, "setup instructions and actions fit compact window");
        }
        foreach (var (state, name) in new[] { (missing, "setup-missing"), (installed, "setup-installed") })
        {
            var window = Window(state); window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(window, name);
            check(FindButton(window, "前往 FlClash 官方下载页").IsVisible == (state == missing)
                && FindButton(window, "检查并继续").IsVisible == (state != missing), "missing setup exposes download notification while installed setup exposes readiness checks");
            window.Close();
        }
        var beforeDownload = routeCalls;
        var download = Window(missing);
        download.Loaded += (_, _) => download.Dispatcher.BeginInvoke(() => FindButton(download, "前往 FlClash 官方下载页").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        check(download.ShowDialog() != true && !download.IsVisible, "download click closes notification and declines startup into workspace");
        check(launches.SequenceEqual(new[] { "https://github.com/chen08209/FlClash/releases/latest" }) && routeCalls == beforeDownload,
            "notification opens only official release page with no route probe or installer execution");
        launches.Clear();

        launchError = new IOException("fixture browser unavailable");
        var failed = Window(missing);
        failed.Loaded += (_, _) => failed.Dispatcher.BeginInvoke(() =>
        {
            FindButton(failed, "前往 FlClash 官方下载页").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var status = (TextBlock)((StackPanel)((ScrollViewer)failed.Content).Content).Children[3];
            check(failed.IsVisible && status.Text.Contains("fixture browser unavailable"), "browser launch failure stays visible with actionable error");
            failed.Close();
        });
        check(failed.ShowDialog() != true && launches.Count == 0, "browser launch failure cannot enter workspace");
        launchError = null;

        var ready = Window(installed);
        ready.Loaded += (_, _) => ready.Dispatcher.BeginInvoke(() => FindButton(ready, "检查并继续").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        check(ready.ShowDialog() == true, "setup modal continue accepts only freshly verified readiness");
        var cancelled = Window(missing);
        cancelled.Loaded += (_, _) => cancelled.Dispatcher.BeginInvoke(cancelled.Close);
        check(cancelled.ShowDialog() != true, "closing missing notification exits startup without opening browser");
        check(launches.Count == 0 && !Directory.Exists(Path.Combine(root, "downloads")), "setup never downloads or runs an installer");
    }
}
