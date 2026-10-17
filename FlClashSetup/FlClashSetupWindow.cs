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
    private readonly Button install = new() { Content = "下载并安装 FlClash" };
    private readonly Button open = new() { Content = "打开 FlClash" };
    private readonly Button choose = new() { Content = "选择已有的 FlClash…" };
    private readonly Button check = new() { Content = "检查并继续" };
    private FlClashReadiness state;

    internal FlClashSetupWindow(FlClashSetupService service, FlClashReadiness state)
    {
        this.service = service; this.state = state;
        Title = "准备 FlClash · Chunson";
        Width = 660; Height = 510; MinWidth = 580; MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 240));
        Foreground = new SolidColorBrush(Color.FromRgb(36, 55, 42));
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 15;
        var stack = new StackPanel { Margin = new Thickness(28) };
        stack.Children.Add(new TextBlock { Text = "先准备好 FlClash", FontSize = 27, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = "本工具目前只适配 FlClash。", Margin = new Thickness(0, 12, 0, 8) });
        stack.Children.Add(new TextBlock { Text = "1. 安装并打开 FlClash\n2. 在 FlClash 中添加自己的订阅，选择节点\n3. 保留主 HTTP / mixed 端口，然后检查并继续\n\n支持只开 TUN，但仍需要主端口。安装程序不包含订阅。", TextWrapping = TextWrapping.Wrap, LineHeight = 25 });
        stack.Children.Add(status);
        var actions = new WrapPanel();
        foreach (var button in new[] { install, open, choose, check })
        {
            // Keep setup controls independent of the main application's templates.
            button.Style = new Style(typeof(Button));
            button.Padding = new Thickness(13, 9, 13, 9); button.Margin = new Thickness(0, 0, 8, 9);
            actions.Children.Add(button);
        }
        stack.Children.Add(actions);
        stack.Children.Add(new TextBlock { Text = "安装来源：github.com/chen08209/FlClash · v0.9.0\n下载后核对 SHA-256，再打开官方安装向导。完成后请返回检查。", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        install.Click += async (_, _) => await ActAsync(async () =>
        {
            await service.InstallAsync(new Progress<int>(value => status.Text = $"正在下载官方安装包… {value}%"), stopped.Token);
            status.Text = "已打开官方安装向导。请完成安装、启动 FlClash 并添加订阅，再点击“检查并继续”。";
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
        status.Text = state.Message; EnableActions(true);
    }

    private async Task ActAsync(Func<Task> action)
    {
        EnableActions(false);
        try { await action(); }
        catch (OperationCanceledException) { if (!stopped.IsCancellationRequested) status.Text = "下载等待超时，请重试，或自行安装官方 FlClash 后选择已有版本。"; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = ex.Message; }
        finally { if (!stopped.IsCancellationRequested) EnableActions(true); }
    }
    private void EnableActions(bool enabled)
    {
        install.Visibility = state.Installation.Executable is null ? Visibility.Visible : Visibility.Collapsed;
        open.Visibility = state.Installation.Executable is null ? Visibility.Collapsed : Visibility.Visible;
        install.IsEnabled = open.IsEnabled = choose.IsEnabled = check.IsEnabled = enabled;
    }

    public static async Task<bool> EnsureAsync(Func<CancellationToken, Task> checkRoute, CancellationToken token = default)
    {
        var service = new FlClashSetupService(checkRoute);
        var state = await service.InspectAsync(token);
        if (state.Ready) return true;
        return new FlClashSetupWindow(service, state).ShowDialog() == true;
    }
}
