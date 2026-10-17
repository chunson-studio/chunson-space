using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Chunson.Setup;

internal sealed class FlClashSetupWindow : Window
{
    private readonly FlClashSetupService service;
    private readonly CancellationTokenSource stopped = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 18) };
    private readonly Button download = new() { Content = "前往 FlClash 官方下载页" };
    private readonly Button open = new() { Content = "打开 FlClash" };
    private readonly Button choose = new() { Content = "选择已有的 FlClash…" };
    private readonly Button check = new() { Content = "检查并继续" };
    private FlClashReadiness state;

    internal FlClashSetupWindow(FlClashSetupService service, FlClashReadiness state)
    {
        this.service = service; this.state = state;
        var missing = state.Installation.Executable is null;
        Title = missing ? "需要安装 FlClash" : "准备 FlClash · Chunson";
        Width = missing ? 560 : 660; Height = missing ? 290 : 510;
        MinWidth = missing ? 560 : 580; MinHeight = missing ? 290 : 470;
        ResizeMode = missing ? ResizeMode.NoResize : ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = TryFindResource("CanvasBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(248, 244, 237));
        Foreground = TryFindResource("InkBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(52, 60, 67));
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 15;
        var stack = new StackPanel { Margin = new Thickness(28) };
        stack.Children.Add(new TextBlock { Text = missing ? "未检测到 FlClash" : "先准备好 FlClash", FontSize = 27, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = "本工具目前只适配 FlClash。", Margin = new Thickness(0, 12, 0, 8) });
        stack.Children.Add(new TextBlock { Text = missing
            ? "点击下方链接，在浏览器中自行下载安装。\n安装并配置自己的订阅后，重新打开本工具。"
            : "1. 打开 FlClash，载入自己的订阅并选择节点\n2. 保留主 HTTP / mixed 端口\n3. 点击“检查并继续”\n\n支持只开 TUN，但仍需要主端口。", TextWrapping = TextWrapping.Wrap, LineHeight = 25 });
        stack.Children.Add(status);
        var actions = new WrapPanel();
        foreach (var button in new[] { download, open, choose, check })
        {
            // Keep setup controls independent of the main application's templates.
            button.Style = new Style(typeof(Button));
            button.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
            button.SetResourceReference(Control.ForegroundProperty, "InkBrush");
            button.SetResourceReference(Control.BorderBrushProperty, "OutlineBrush");
            button.Padding = new Thickness(13, 9, 13, 9); button.Margin = new Thickness(0, 0, 8, 9);
            actions.Children.Add(button);
        }
        stack.Children.Add(actions);
        stack.Children.Add(new TextBlock { Text = missing ? "点击后本工具退出。已有便携版？先打开 FlClash，再启动本工具。" : "安装来源：github.com/chen08209/FlClash", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        download.Click += async (_, _) => await ActAsync(() =>
        {
            service.OpenDownloadPage();
            DialogResult = false;
            return Task.CompletedTask;
        });
        open.Click += async (_, _) => await ActAsync(() =>
        {
            service.OpenInstalled(state.Installation.Executable!);
            status.Text = "已请求打开 FlClash。准备好订阅与主端口后，请点击“检查并继续”。";
            return Task.CompletedTask;
        });
        choose.Click += async (_, _) => await ActAsync(() =>
        {
            var picker = new OpenFileDialog { Filter = "FlClash|FlClash.exe", Title = "选择已有的 FlClash.exe" };
            if (picker.ShowDialog(this) == true) service.OpenInstalled(picker.FileName);
            return Task.CompletedTask;
        });
        check.Click += async (_, _) => await ActAsync(async () =>
        {
            state = await service.InspectAsync(stopped.Token);
            status.Text = state.Message;
            if (state.Ready) DialogResult = true;
        });
        Closed += (_, _) => stopped.Cancel();
        status.Text = missing ? "" : state.Message;
        status.Visibility = missing ? Visibility.Collapsed : Visibility.Visible;
        EnableActions(true);
    }

    private async Task ActAsync(Func<Task> action)
    {
        EnableActions(false);
        try { await action(); }
        catch (OperationCanceledException) { if (!stopped.IsCancellationRequested) { status.Visibility = Visibility.Visible; status.Text = "检查已取消，请重试。"; } }
        catch (Exception ex) when (ex is not OutOfMemoryException) { status.Visibility = Visibility.Visible; status.Text = ex.Message; }
        finally { if (!stopped.IsCancellationRequested) EnableActions(true); }
    }
    private void EnableActions(bool enabled)
    {
        var missing = state.Installation.Executable is null;
        download.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        open.Visibility = choose.Visibility = check.Visibility = missing ? Visibility.Collapsed : Visibility.Visible;
        download.IsEnabled = open.IsEnabled = choose.IsEnabled = check.IsEnabled = enabled;
    }

    public static async Task<bool> EnsureAsync(Func<CancellationToken, Task> checkRoute, CancellationToken token = default)
    {
        var service = new FlClashSetupService(checkRoute);
        var state = await service.InspectAsync(token);
        if (state.Ready) return true;
        return new FlClashSetupWindow(service, state).ShowDialog() == true;
    }
}
