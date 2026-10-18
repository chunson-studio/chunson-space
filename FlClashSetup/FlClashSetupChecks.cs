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
        var inspections = 0;
        var notifications = 0;
        var launches = new List<string>();
        Exception? launchError = null;
        var service = new FlClashSetupService(() => { inspections++; return found; },
            path => { if (launchError is not null) throw launchError; launches.Add(path); });
        Task<bool> Ensure(CancellationToken token = default) => FlClashSetupWindow.EnsureAsync(token, service, _ => notifications++);

        check(!await Ensure() && notifications == 1 && launches.Count == 0,
            "setup missing FlClash only notifies without network or browser requests");
        found = new("fixture/FlClash.exe", false);
        check(await Ensure() && notifications == 1 && launches.Count == 0,
            "installed but stopped FlClash enters workspace without starting FlClash or checking a route");
        check(await Ensure() && notifications == 1,
            "installed FlClash with unreadable process module cannot be blocked by Running false");
        found = found with { Running = true };
        check(await Ensure() && notifications == 1 && launches.Count == 0,
            "running installation enters workspace without subscription or listener readiness");

        var beforeCancel = inspections;
        try { await Ensure(new CancellationToken(true)); check(false, "cancelled startup must stop"); }
        catch (OperationCanceledException)
        { check(inspections == beforeCancel && notifications == 1 && launches.Count == 0, "cancelled startup performs no discovery notification or launch"); }
        using (var cancelledDuringDiscovery = new CancellationTokenSource())
        {
            var cancelling = new FlClashSetupService(() => { cancelledDuringDiscovery.Cancel(); return found; });
            try
            {
                await FlClashSetupWindow.EnsureAsync(cancelledDuringDiscovery.Token, cancelling, _ => notifications++);
                check(false, "cancellation during installation discovery must stop");
            }
            catch (OperationCanceledException)
            { check(notifications == 1, "cancellation during discovery prevents startup and notification"); }
        }
        var fakeExecutable = Path.Combine(root, "FlClash.exe"); File.WriteAllText(fakeExecutable, "fixture");
        check(!FlClashSetupService.IsFlClash(fakeExecutable), "setup rejects arbitrary executable renamed FlClash");

        FlClashSetupWindow Window() => new(service)
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        static Button DownloadButton(FlClashSetupWindow window) => ((WrapPanel)((StackPanel)((ScrollViewer)window.Content).Content).Children[4])
            .Children.OfType<Button>().Single();
        void Capture(FlClashSetupWindow window)
        {
            window.UpdateLayout();
            check(!window.IsActive && window.Left + window.ActualWidth < SystemParameters.VirtualScreenLeft, "setup fixture remains offscreen and inactive");
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(root, "setup-missing.png"))) encoder.Save(file);
            check(((ScrollViewer)window.Content).ScrollableHeight < 1, "setup instructions and download action fit compact window");
        }
        var notification = Window(); notification.Show();
        await notification.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(notification);
        check(DownloadButton(notification).IsVisible && Equals(DownloadButton(notification).Content, "前往 FlClash 官方下载页"),
            "missing notification exposes only the official download action without readiness controls");
        notification.Close();

        var download = Window();
        download.Loaded += (_, _) => download.Dispatcher.BeginInvoke(() => DownloadButton(download).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        check(download.ShowDialog() != true && !download.IsVisible, "download click closes notification and declines startup into workspace");
        check(launches.SequenceEqual(new[] { "https://github.com/chen08209/FlClash/releases/latest" }),
            "notification opens only the official latest release page without installing or starting FlClash");
        launches.Clear();

        launchError = new IOException("fixture browser unavailable");
        var failed = Window();
        failed.Loaded += (_, _) => failed.Dispatcher.BeginInvoke(() =>
        {
            DownloadButton(failed).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var status = (TextBlock)((StackPanel)((ScrollViewer)failed.Content).Content).Children[3];
            check(failed.IsVisible && status.Text.Contains("fixture browser unavailable") && DownloadButton(failed).IsEnabled,
                "browser launch failure keeps notification visible with an error and retry action");
            failed.Close();
        });
        check(failed.ShowDialog() != true && launches.Count == 0, "browser launch failure cannot enter workspace");
        launchError = null;

        var cancelled = Window();
        cancelled.Loaded += (_, _) => cancelled.Dispatcher.BeginInvoke(cancelled.Close);
        check(cancelled.ShowDialog() != true, "closing missing notification exits startup without opening browser");
        check(launches.Count == 0 && !Directory.Exists(Path.Combine(root, "downloads")), "setup never downloads or runs an installer");
    }
}
