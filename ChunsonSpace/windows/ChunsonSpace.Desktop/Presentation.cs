using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DelegateCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record IdentityItem(string Name, string Kind, string Subtitle, string Initials, string Color, string Proxy, string Timezone, string Locale, string Model, bool FollowSystemProxy = false, bool TestsFrozen = false, AccountMarks? Marks = null, DateTimeOffset? LoggedInAt = null, string? DisplayName = null) : INotifyPropertyChanged
{
    public string Label => DisplayName ?? Name;
    public event PropertyChangedEventHandler? PropertyChanged;
    private string loginTimeLabel = FormatLoginHours(LoggedInAt, DateTimeOffset.UtcNow);
    public string LoginTimeLabel => loginTimeLabel;
    public string LoginTimeSummary => "已登录：" + LoginTimeLabel;
    internal static string FormatLoginHours(DateTimeOffset? first, DateTimeOffset now) => first is { } time
        ? Math.Max(0, (now - time).TotalHours).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " 小时" : "未知";
    internal void RefreshLoginTime(DateTimeOffset now)
    {
        var next = FormatLoginHours(LoggedInAt, now);
        if (next == loginTimeLabel) return;
        loginTimeLabel = next;
        PropertyChanged?.Invoke(this, new(nameof(LoginTimeLabel)));
        PropertyChanged?.Invoke(this, new(nameof(LoginTimeSummary)));
    }
    public string Key => $"{Kind}:{Name}";
    public IdentityRef Identity => new(Kind == "Account" ? IdentityKind.Account : IdentityKind.Provider, Name);
    public EnvironmentSettings Environment => new(Proxy, Timezone, Locale, FollowSystemProxy: FollowSystemProxy);
    public string ProxyDisplay => FollowSystemProxy ? "跟随 FlClash 当前网络" : Proxy.Length > 0 ? Proxy : "尚未配置代理";
    public string ExitSummary => FollowSystemProxy ? "跟随本机" : Proxy.Length > 0 ? "独立 IP" : "未配置";
    public string PortLabel => FollowSystemProxy ? "系统端口" : Uri.TryCreate(Proxy, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";
    public string SavedStateLabel => Subtitle == "待登录" ? "待登录" : "已保存";
    public string TestFreezeActionLabel => TestsFrozen ? "🧊 已冻结" : "未冻结";
    public string TestFreezeHint => TestsFrozen ? "点击解冻此账号；解冻后可进行模型测试。" : "点击冻结此账号；冻结后保留资料，模型测试会跳过。";
    public AccountMarks AccountMarks => Marks ?? new();
    public string QualityLabel => AccountMarkDisplay.Quality(AccountMarks.Quality);
    public string MarkActionLabel => QualityLabel + " ▾";
    public string MarkDetails => AccountMarkDisplay.Details(AccountMarks);
    public bool HasProblemMark => AccountMarks.Problem;
    public bool HasRecoveredMark => AccountMarks.Recovered;
    public string OperationIssueLabel => AccountMarks.ReadFailed ? "标记读取失败" : AccountMarks.Inconclusive ? "本次没判断成功" : AccountMarkDisplay.Issue(AccountMarks.Issue);
    public bool HasBothIssueAndInconclusive => AccountMarks.Inconclusive && AccountMarks.Issue != AccountIssue.None;
    public string ConfirmedIssueLabel => AccountMarkDisplay.Issue(AccountMarks.Issue);
    public bool HasOperationIssue => OperationIssueLabel.Length > 0;
    public string KindLabel => Kind == "Account" ? "账号档案" : "历史档案";
}


public static class PreviewDataSource
{
    public static IReadOnlyList<IdentityItem> Identities { get; } =
    [
        new("日常工作", "Account", "Plus · 演示账号", "工", WorkspaceTheme.AvatarColor(0), "http://127.0.0.1:18081", "America/Chicago", "en-US", "Codex 默认"),
        new("开发测试", "Account", "Pro · 演示账号", "测", WorkspaceTheme.AvatarColor(1), "http://127.0.0.1:18082", "Asia/Shanghai", "en-US", "Codex 默认"),
        new("个人空间", "Account", "Free · 演示账号", "个", WorkspaceTheme.AvatarColor(2), "http://127.0.0.1:18083", "Asia/Shanghai", "en-US", "Codex 默认"),
        new("备用账号 01", "Account", "已保存登录资料", "备", WorkspaceTheme.AvatarColor(3), "http://127.0.0.1:18084", "Asia/Tokyo", "en-US", "Codex 默认"),
        new("备用账号 02", "Account", "已保存登录资料", "备", WorkspaceTheme.AvatarColor(4), "http://127.0.0.1:18085", "America/New_York", "en-US", "Codex 默认"),
        new("备用账号 03", "Account", "待登录", "备", WorkspaceTheme.AvatarColor(5), "http://127.0.0.1:18086", "Asia/Singapore", "en-US", "Codex 默认"),
        new("备用账号 04", "Account", "已保存登录资料", "备", WorkspaceTheme.AvatarColor(6), "http://127.0.0.1:18087", "Europe/London", "en-US", "Codex 默认"),
        new("备用账号 05", "Account", "已保存登录资料", "备", WorkspaceTheme.AvatarColor(7), "http://127.0.0.1:18088", "Europe/Berlin", "en-US", "Codex 默认"),
        new("备用账号 06", "Account", "已保存登录资料", "备", WorkspaceTheme.AvatarColor(8), "http://127.0.0.1:18089", "Asia/Hong_Kong", "en-US", "Codex 默认"),
        new("备用账号 07", "Account", "已保存登录资料", "备", WorkspaceTheme.AvatarColor(9), "", "", "en-US", "Codex 默认", true),
    ];
}

public sealed partial class ShellViewModel : ObservableObject
{
    internal void RefreshLoginTimes(DateTimeOffset now)
    {
        foreach (var identity in identities) identity.RefreshLoginTime(now);
    }
    private string page = "Dashboard";
    private string search = "";
    private IdentityItem? selected;
    private IdentityItem? active = PreviewDataSource.Identities[0];
    private IReadOnlyList<IdentityItem> identities = PreviewDataSource.Identities;
    private string feedback = "选择档案，查看独立的环境配置。";
    private string feedbackTitle = "准备就绪";
    private string feedbackKind = "Info";
    private bool isolateSessions = true;
    private bool closeToTray = true;

    public ShellViewModel(DesktopWorkspace? workspace = null)
    {
        selected = PreviewDataSource.Identities[1];
        NavigateCommand = new(p => Navigate(p?.ToString() ?? "Dashboard"), _ => !operationBusy);
        SimulateSwitchCommand = new(_ => SimulateSwitch(false), _ => Selected is not null);
        SimulateLaunchCommand = new(_ => SimulateSwitch(true), _ => Selected is not null);
        SimulateFailureCommand = new(_ => ShowError());
        ClearSearchCommand = new(_ => { AccountFilter = "全部账号"; Search = ""; });
        SaveSettingsCommand = new(_ => SaveSettings(), _ => !busy);
        ShowClockCommand = new(_ => ShowClock(), _ => workspace is { IsFixture: false });
        UnavailableCommand = new(_ => { }, _ => false);
        InitializeWorkspace(workspace);
        InitializeUpdateCommands();
        RefreshBeijingTime(DateTimeOffset.UtcNow);
        ResultsGallery.Enter();
    }
    public DelegateCommand NavigateCommand { get; }
    public DelegateCommand SimulateSwitchCommand { get; }
    public DelegateCommand SimulateLaunchCommand { get; }
    public DelegateCommand SimulateFailureCommand { get; }
    public DelegateCommand ClearSearchCommand { get; }
    public DelegateCommand SaveSettingsCommand { get; }
    public DelegateCommand ShowClockCommand { get; }
    public void ShowClock()
    {
        if (workspace is not { IsFixture: false }) return;
        try { Chunson.Widgets.ClockLauncher.Start(); }
        catch (Exception error) when (error is System.IO.IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { ShowFeedback("北京时间组件未启动", error.Message, "Error"); }
    }
    public DelegateCommand UnavailableCommand { get; }
    public string Page => page;
    public bool IsIdentityPage => page == "Accounts";
    public bool IsAccountsPage => page == "Accounts";
    public bool IsSettingsPage => page == "Settings";
    public bool IsProxyPage => false;
    public string PageTitle => page switch { "Settings" => "设置", "Dashboard" => "仪表盘", "Tests" => "测试", "Results" => "结果", _ => "账号" };
    public string PageDescription => page switch { "Settings" => "管理唯一受管分身与桌面行为。", "Proxy" => "配置 FlClash DNS 与订阅 IP。", _ => "授权资料独立保留，切换时载入唯一受管分身。" };
    public string Search { get => search; set { if (Set(ref search, value)) UpdateItems(); } }
    public IEnumerable<IdentityItem> FilteredItems => identities.Where(i => i.Kind == "Account" && (!IsAccountsPage || accountFilter == "全部账号" || accountFilter == "当前使用" && i.Key == active?.Key || accountFilter == "待登录" && i.SavedStateLabel == "待登录") && (string.IsNullOrWhiteSpace(search) || (i.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || i.Label.Contains(search, StringComparison.OrdinalIgnoreCase)) || i.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
    public int ItemCount => FilteredItems.Count();
    public string ItemCountLabel => $"{ItemCount} 个档案";
    public bool IsEmpty => ItemCount == 0;
    public bool HasNoAccounts => IsAccountsPage && !identities.Any(i => i.Kind == "Account");
    public bool HasSearchMiss => IsEmpty && identities.Any(i => i.Kind == "Account");
    public string EmptyTitle => HasNoAccounts ? IsLoggingIn ? "正在添加账号" : "还没有账号" : HasSearchMiss ? "没有匹配的档案" : "还没有账号";
    public string EmptyDescription => HasNoAccounts ? IsAccountPreparation ? "先通过忘记密码重置密码，再登录并退出所有设备；手动关闭第一步浏览器，等待临时资料清理后开始新的授权。" : IsLoggingIn ? "完成浏览器中的 Codex 授权后，账号会自动加入列表。" : "点击上方「添加账号」，可先处理账号，也可直接登录授权。" : HasSearchMiss ? "档案仍保存在本机。请更换搜索内容，或清除筛选查看全部档案。" : "登录账号后即可开始使用。";
    public string EmptyDetailDescription => IsAccountPreparation ? "当前只准备账号，尚未发起 Codex 授权。" : IsLoggingIn ? "完成授权后，可在这里查看账号出口和运行环境。" : HasNoAccounts ? "登录账号后，可在这里查看出口和运行环境。" : "选择一个账号，即可查看出口和运行环境。";
    public bool HasSelection => selected is not null;
    public bool HasNoSelection => selected is null;
    public IdentityItem? Selected { get => selected; set { if (updatingIdentityPage && value is null) return; if (value is not null) EnsureIdentityVisible(value); if (Set(ref selected, value)) { Raise(nameof(HasSelection)); Raise(nameof(HasNoSelection)); Raise(nameof(SelectedIsActive)); Raise(nameof(IdentityState)); NotifyHomeDetails(); SimulateSwitchCommand.Refresh(); SimulateLaunchCommand.Refresh(); RefreshSelectionCommands(); } } }
    public IdentityItem? Active => active;
    public bool SelectedIsActive => selected is not null && selected.Key == active?.Key;
    public string ActiveLabel => active is null ? "尚未载入账号" : $"{active.Label} · {active.KindLabel}";
    public string RunningLabel => workspace is null ? "未运行 · 演示" : launchInProgress ? "正在启动" : lastOutcome == ChunsonSpace.Application.OperationOutcome.PendingLaunch ? "启动待确认"
        : runningIdentity is not null ? $"{runningIdentity.Name} · {(workspace.IsFixture ? "隔离替身 · " : "")}运行中" : runtimeProcessAlive ? "运行中 · 账号待确认" : runtimeExited ? "已退出" : "未运行";
    public IdentityState IdentityState => new(Selected?.Identity, Active?.Identity, runningIdentity);
    public string Feedback => feedback;
    public string FeedbackTitle => feedbackTitle;
    private string feedbackDiagnostics = "";
    public string FeedbackErrorReport => SupportReport.Create(feedbackTitle, feedback + feedbackDiagnostics, privateValues: [Selected?.Name]);
    public bool IsError => feedbackKind == "Error";
    public string FeedbackAccent => IsError ? "#AE493C" : "#246B55";
    public bool IsolateSessions { get => isolateSessions; set { if (Set(ref isolateSessions, value)) Raise(nameof(EnvironmentSummary)); } }
    public bool CloseToTray { get => closeToTray; set => Set(ref closeToTray, value); }
    public void Navigate(string target)
    {
        if (target is not ("Dashboard" or "Accounts" or "Tests" or "Settings" or "Results")) return;
        if (page == target) return;
        CloseArtwork();
        page = target;
        IsDetailOpen = false;
        diagnosticOpen = false; Raise(nameof(IsDiagnosticOpen));
        foreach (var name in new[] { nameof(Page), nameof(IsDashboardPage), nameof(IsTestsPage), nameof(IsIdentityPage), nameof(IsAccountsPage), nameof(IsSettingsPage), nameof(IsProxyPage), nameof(PageTitle), nameof(PageDescription), nameof(Search) }) Raise(name);
        UpdateItems();
        Raise(nameof(IsResultsPage));
        Raise(nameof(IsWorkspacePage));
        if (IsResultsPage || IsDashboardPage)
        {
            ResultsGallery.SetProfileScope(IsSingleAccountMode ? DisplayedAccount?.Name ?? "" : null);
            ResultsGallery.Enter();
        }
        else resultsGallery?.Leave();
        if (IsIdentityPage && Selected is null) Selected = FilteredItems.FirstOrDefault();
    }
    public void ShowError() => ShowFeedback("模拟切换未完成", "启动确认失败。已恢复原演示状态；恢复材料保留，当前活动身份未改变。", "Error");
    private void SimulateSwitch(bool launch)
    {
        if (selected is null) return;
        active = selected;
        Raise(nameof(Active)); Raise(nameof(ActiveLabel)); Raise(nameof(SelectedIsActive)); Raise(nameof(IdentityState));
        NotifyHomeDetails();
        if (accountFilter != "全部账号") UpdateItems();
        ShowFeedback(launch ? "模拟切换并启动完成" : "模拟切换完成", $"演示活动身份已设为“{active.Name}”。没有访问凭据、启动客户端或更改系统环境。", "Success");
    }
    private void ShowFeedback(string title, string message, string kind, string diagnostics = "")
    {
        feedbackTitle = title; feedback = message; feedbackKind = kind;
        feedbackDiagnostics = diagnostics;
        Raise(nameof(FeedbackErrorReport));
        foreach (var name in new[] { nameof(Feedback), nameof(FeedbackTitle), nameof(IsError), nameof(FeedbackAccent), nameof(ShowOperationFeedback), nameof(IsCompactFeedback), nameof(IsExpandedFeedback), nameof(CompactFeedbackText), nameof(FeedbackNextAction), nameof(HasFeedbackNextAction) }) Raise(name);
    }
    private void UpdateItems()
    {
        var items = FilteredItems.ToArray();
        if (selected is not null && !items.Contains(selected)) Selected = null;
        Raise(nameof(FilteredItems)); Raise(nameof(ItemCount)); Raise(nameof(ItemCountLabel)); Raise(nameof(IsEmpty));
        Raise(nameof(HasNoAccounts)); Raise(nameof(HasSearchMiss)); Raise(nameof(EmptyTitle)); Raise(nameof(EmptyDescription)); Raise(nameof(EmptyDetailDescription));
        NotifyHomeDetails();
        ResetIdentityPage();
    }
}
