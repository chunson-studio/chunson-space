using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ChunsonLogin;

public sealed class LoginViewModel : INotifyPropertyChanged
{
    private readonly EnvironmentCoordinator? coordinator;
    private readonly DedicatedBrowser? browser;
    private CancellationTokenSource? operation;
    private EnvironmentSnapshot? snapshot;
    private string statusTitle = "先检测当前网络", statusMessage = "确认出口国家/地区和时区，再由你点击同步到 Windows。";
    private bool busy;
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<BrowserInstallation> Browsers { get; }
    public BrowserInstallation? SelectedBrowser { get; set; }
    public bool IsPreview => coordinator is null;
    public bool IsBusy { get => busy; private set { busy = value; Notify(); Notify(nameof(CanAct)); Notify(nameof(CanOpen)); } }
    public bool CanAct => !IsBusy && !IsPreview;
    public bool CanOpen => CanAct && SelectedBrowser is not null;
    public string StatusTitle { get => statusTitle; private set { statusTitle = value; Notify(); } }
    public string StatusMessage { get => statusMessage; private set { statusMessage = value; Notify(); } }
    public string Ip => snapshot?.Egress.Ip ?? "尚未检测";
    public string Place => snapshot is null ? "跟随 FlClash 当前网络" : $"{snapshot.Egress.Country} · {snapshot.Egress.City}";
    public string TargetTimezone => snapshot?.Egress.IanaTimezone ?? "等待出口检测";
    public string CurrentTimezone => snapshot?.SystemTimezone ?? TimeZoneInfo.Local.Id;
    public string TargetCountry => SystemRegion.Label(snapshot?.Egress.CountryCode ?? "");
    public string CurrentCountry => SystemRegion.Label(snapshot?.SystemCountry ?? "");
    public string NetworkSummary => snapshot?.Route.Proxy ?? "支持只开 TUN；自动识别主入口";
    public string ResultBadge => snapshot is null ? "待检测" : snapshot.Matches ? "地区与时区一致" : "需要同步";
    public string CheckedAt => snapshot is null ? "" : $"检测于 {snapshot.Egress.CheckedAt.ToLocalTime():HH:mm:ss} · "
        + (snapshot.Egress.QueryNote.Length > 0 ? snapshot.Egress.QueryNote + " · " : "") + "切换节点后请重新检测";
    public string BrowserNote => Browsers.Count == 0 ? "未找到 Chrome 或 Edge，请先安装其中一个浏览器。" : "独立保存登录状态 · 英文界面与语言偏好 · 不影响日常浏览器";

    public LoginViewModel(EnvironmentCoordinator? coordinator, DedicatedBrowser? browser, IReadOnlyList<BrowserInstallation> browsers)
    { this.coordinator = coordinator; this.browser = browser; Browsers = browsers; SelectedBrowser = browsers.FirstOrDefault(); }

    public Task DetectAsync() => ExecuteAsync("正在检测出口", async token =>
    {
        SetSnapshot(await coordinator!.DetectAsync(token));
        StatusTitle = snapshot!.Matches ? "出口与系统地区、时区一致" : "检测完成，需要同步地区与时区";
        StatusMessage = snapshot.Matches ? "可以打开英文专用浏览器。切换 FlClash 节点后，重新检测即可。" : "点击“同步地区与时区”；同步前会重新核对网络，保留系统语言和输入法。";
    });

    public Task SyncAsync() => ExecuteAsync("正在同步地区与时区", async token =>
    {
        SetSnapshot(await coordinator!.SyncAsync(token));
        StatusTitle = "地区与时区已同步";
        StatusMessage = "已分别回读确认 Windows 国家/地区与时区。系统语言、输入法保持不变；退出工具不还原。";
    });

    public Task OpenAsync() => ExecuteAsync("正在检查浏览器环境", async token =>
    {
        if (SelectedBrowser is null) throw new InvalidOperationException("请先安装 Chrome 或 Edge。");
        SetSnapshot(await coordinator!.DetectAsync(token));
        if (!snapshot!.Matches)
        {
            StatusTitle = "请先同步地区与时区";
            StatusMessage = "Windows 国家/地区或时区与出口不一致。点击“同步地区与时区”，完成后再打开浏览器。";
            return;
        }
        var selected = SelectedBrowser;
        var proxy = snapshot.Route.Proxy;
        await Task.Run(() => browser!.Open(selected, proxy), token);
        StatusTitle = "已请求打开专用浏览器";
        StatusMessage = "ChatGPT 和 Claude 环境检测页会分别打开。关闭本工具会保留浏览器与登录资料。";
    });

    public void ShowClock()
    {
        if (IsPreview) return;
        try { Chunson.Widgets.ClockLauncher.Start(); }
        catch (Exception error) when (error is System.IO.IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { StatusTitle = "北京时间组件未启动"; StatusMessage = error.Message; }
    }

    private async Task ExecuteAsync(string title, Func<CancellationToken, Task> action)
    {
        if (!CanAct) return;
        using var stopped = new CancellationTokenSource(); operation = stopped; IsBusy = true;
        StatusTitle = title; StatusMessage = "可以最小化窗口，检测会继续。";
        try { await action(stopped.Token); }
        catch (OperationCanceledException) { StatusTitle = "操作已取消"; StatusMessage = "不会恢复或清理任何时区、浏览器登录资料。"; }
        catch (Exception ex)
        {
            // Retain old measurement only as history, never as a fresh successful result.
            StatusTitle = "本次操作未完成";
            StatusMessage = ex.Message + (snapshot is null ? "" : " 上方仍是上次检测结果。");
        }
        finally { operation = null; IsBusy = false; }
    }
    public void Cancel() => operation?.Cancel();
    private void SetSnapshot(EnvironmentSnapshot value)
    {
        snapshot = value;
        foreach (var name in new[] { nameof(Ip), nameof(Place), nameof(TargetTimezone), nameof(CurrentTimezone), nameof(TargetCountry), nameof(CurrentCountry), nameof(NetworkSummary), nameof(ResultBadge), nameof(CheckedAt) }) Notify(name);
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public static LoginViewModel Preview(bool matches = false, bool edgeOnly = false)
    {
        var browserName = edgeOnly ? "Microsoft Edge" : "Google Chrome";
        var model = new LoginViewModel(null, null, [new(browserName, "preview.exe", edgeOnly ? "Edge" : "Chrome")]);
        model.SetSnapshot(new(new("http://127.0.0.1:7890", "FlClash"), new("203.0.113.24", "United States", "New York", "America/New_York", "Eastern Standard Time", DateTimeOffset.Now, CountryCode: "US"), matches ? "Eastern Standard Time" : "Pacific Standard Time", matches ? "US" : "JP"));
        model.StatusTitle = "界面预览 · 示例数据";
        model.StatusMessage = "此窗口只展示布局，不检测网络、不修改地区或时区、不打开浏览器。";
        return model;
    }
}
