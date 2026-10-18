using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Chunson.Setup;

internal sealed class FlClashSetupWindow : Window
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 18), Visibility = Visibility.Collapsed };
    private readonly Button download = new() { Content = "前往 FlClash 官方下载页" };

    internal FlClashSetupWindow(FlClashSetupService service)
    {
        Title = "需要安装 FlClash";
        Width = MinWidth = 560; Height = MinHeight = 290;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = TryFindResource("CanvasBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(248, 244, 237));
        Foreground = TryFindResource("InkBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(52, 60, 67));
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 15;
        var stack = new StackPanel { Margin = new Thickness(28) };
        stack.Children.Add(new TextBlock { Text = "未检测到 FlClash", FontSize = 27, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = "本工具目前只适配 FlClash。", Margin = new Thickness(0, 12, 0, 8) });
        stack.Children.Add(new TextBlock
        {
            Text = "点击下方链接，在浏览器中自行下载最新版本。\n安装并配置自己的订阅后，重新打开本工具。",
            TextWrapping = TextWrapping.Wrap, LineHeight = 25
        });
        stack.Children.Add(status);
        var actions = new WrapPanel();
        // Keep the notification independent of the main application's button templates.
        download.Style = new Style(typeof(Button));
        download.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
        download.SetResourceReference(Control.ForegroundProperty, "InkBrush");
        download.SetResourceReference(Control.BorderBrushProperty, "OutlineBrush");
        download.Padding = new Thickness(13, 9, 13, 9); download.Margin = new Thickness(0, 0, 8, 9);
        actions.Children.Add(download); stack.Children.Add(actions);
        stack.Children.Add(new TextBlock
        {
            Text = "点击后本工具退出。已有便携版？先打开 FlClash，再启动本工具。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
        });
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        download.Click += (_, _) =>
        {
            download.IsEnabled = false;
            try { service.OpenDownloadPage(); DialogResult = false; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { status.Visibility = Visibility.Visible; status.Text = ex.Message; download.IsEnabled = true; }
        };
    }

    public static async Task<bool> EnsureAsync(CancellationToken token = default,
        FlClashSetupService? service = null, Action<FlClashSetupService>? notify = null)
    {
        service ??= new FlClashSetupService();
        var installation = await service.InspectAsync(token);
        token.ThrowIfCancellationRequested();
        // Startup checks installation only. Network readiness belongs to the operation that needs it.
        if (installation.Executable is not null) return true;
        if (notify is not null) notify(service);
        else
        {
            var window = new FlClashSetupWindow(service);
            using var cancellation = token.Register(() => window.Dispatcher.BeginInvoke(window.Close));
            window.ShowDialog();
        }
        token.ThrowIfCancellationRequested();
        return false;
    }
}
