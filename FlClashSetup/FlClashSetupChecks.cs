using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
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
        var payload = new byte[] { 1, 3, 5, 7, 9 };
        var manifest = new FlClashInstaller("https://example.invalid/fixture.exe", Convert.ToHexString(SHA256.HashData(payload)), payload.Length);
        HttpMessageHandler handler = new FixtureDownload(payload);
        var service = new FlClashSetupService(_ =>
        {
            routeCalls++;
            return routeError is null ? Task.CompletedTask : Task.FromException(routeError);
        }, () => found, path => launches.Add(path), () => new HttpClient(handler, false), manifest, Path.Combine(root, "downloads"));
        var missing = await service.InspectAsync(default);
        check(!missing.Ready && routeCalls == 0, "setup missing FlClash cannot pass or query a route");
        found = new("fixture/FlClash.exe", false);
        check(!(await service.InspectAsync(default)).Ready && routeCalls == 0, "setup installed but stopped cannot pass");
        found = found with { Running = true };
        routeError = new IOException("主端口不可用");
        check(!(await service.InspectAsync(default)).Ready, "setup running FlClash without its main listener cannot pass");
        routeError = null;
        check((await service.InspectAsync(default)).Ready && routeCalls == 2, "setup only ready after local route check");
        var beforeCancel = routeCalls;
        try { await service.InspectAsync(new CancellationToken(true)); check(false, "cancelled inspection must stop"); }
        catch (OperationCanceledException) { check(routeCalls == beforeCancel, "setup cancelled inspection makes no route call"); }
        try { await service.InstallAsync(null, default); check(false, "existing install must not be replaced"); }
        catch (InvalidOperationException) { check(launches.Count == 0, "setup never overwrites existing FlClash"); }
        found = new(null, false);
        await service.InstallAsync(null, default);
        check(launches.Count == 1 && File.ReadAllBytes(launches[0]).SequenceEqual(payload), "setup verified download alone reaches installer launcher");
        check(!(await service.InspectAsync(default)).Ready, "setup launching installer does not claim installation succeeded");
        launches.Clear();
        var badCases = new[]
        {
            new FixtureDownload(new byte[] { 9, 7, 5, 3, 1 }),
            new FixtureDownload(payload[..3], declaredLength: payload.Length),
            new FixtureDownload([1, 2, 3, 4, 5, 6], declaredLength: payload.Length),
            new FixtureDownload(payload, code: HttpStatusCode.NotFound)
        };
        foreach (var bad in badCases)
        {
            handler = bad;
            var count = Directory.EnumerateFiles(Path.Combine(root, "downloads"), "*.exe", SearchOption.AllDirectories).Count();
            try { await service.InstallAsync(null, default); check(false, "invalid download must fail"); }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                check(launches.Count == 0, "setup invalid hash, length or HTTP never executes");
                check(Directory.EnumerateFiles(Path.Combine(root, "downloads"), "*.exe", SearchOption.AllDirectories).Count() == count,
                    "setup invalid download removes its partial file");
            }
        }
        handler = new FixtureDownload(payload);
        try { await service.InstallAsync(null, new CancellationToken(true)); check(false, "cancelled download must fail"); }
        catch (OperationCanceledException) { check(launches.Count == 0, "setup cancelled download never executes"); }
        var fakeExecutable = Path.Combine(root, "FlClash.exe"); File.WriteAllText(fakeExecutable, "fixture");
        check(!FlClashSetupService.IsFlClash(fakeExecutable), "setup rejects arbitrary executable renamed FlClash");
        try { service.OpenInstalled(fakeExecutable); check(false, "invalid installed path must fail"); }
        catch (InvalidOperationException) { check(launches.Count == 0, "setup invalid portable selection never executes"); }

        var window = new FlClashSetupWindow(service, missing)
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, Width = 580, Height = 510
        };
        window.Show();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        check(!window.IsActive && window.Left + window.ActualWidth < SystemParameters.VirtualScreenLeft, "setup fixture remains offscreen and inactive");
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(root, "setup-missing.png"))) encoder.Save(file);
        var scroll = (ScrollViewer)window.Content;
        check(scroll.ScrollableHeight < 1, "setup instructions and actions fit compact window");
        window.Close();

        // Exercise the same modal accept/close boundary used by both startup paths.
        found = new("fixture/FlClash.exe", true);
        var ready = new FlClashSetupWindow(service, missing) { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        ready.Loaded += (_, _) => ready.Dispatcher.BeginInvoke(() =>
        {
            var panel = (StackPanel)((ScrollViewer)ready.Content).Content;
            var button = ((WrapPanel)panel.Children[4]).Children.OfType<Button>().Single(item => (string)item.Content == "检查并继续");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        check(ready.ShowDialog() == true, "setup modal continue accepts only freshly verified readiness");
        var cancelled = new FlClashSetupWindow(service, missing) { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        cancelled.Loaded += (_, _) => cancelled.Dispatcher.BeginInvoke(cancelled.Close);
        check(cancelled.ShowDialog() != true, "setup window close cancels entry into the tool");
        check(launches.Count == 0, "setup UI verification launches no external process");
    }

    private sealed class FixtureDownload(byte[] payload, long? declaredLength = null, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(code) { Content = new ByteArrayContent(payload) };
            response.Content.Headers.ContentLength = declaredLength ?? payload.Length;
            return Task.FromResult(response);
        }
    }
}
