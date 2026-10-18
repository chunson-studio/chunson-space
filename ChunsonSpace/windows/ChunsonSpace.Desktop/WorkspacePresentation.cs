using System.Globalization;
using System.Windows.Data;
using System.IO;

namespace ChunsonSpace.Desktop;

public sealed class IdentityStatusConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && values[0] is string key && values[1] is string active && key == active ? "当前使用" : values.Length > 2 && values[2] is string subtitle && subtitle == "待登录" ? "待登录" : "已保存";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed partial class ShellViewModel
{
    private bool detailOpen;
    private int cardColumns = 1;
    private int identityPageSize = 10;
    private double homeDetailWidth = 260;
    private string accountFilter = "全部账号";
    private readonly Dictionary<ChunsonSpace.Core.IdentityRef, ChunsonSpace.Core.ExitObservation> recentExits = [];
    private bool exitSaveFailed;
    private int identityPage;
    private int backupPage;
    private bool updatingIdentityPage;
    public int CardColumns => cardColumns;
    public int IdentityPageSize => identityPageSize;
    public double HomeDetailWidth => homeDetailWidth;
    public IReadOnlyList<string> AccountFilterChoices { get; } = ["全部账号", "当前使用", "待登录"];
    public string AccountFilter { get => accountFilter; set { if (AccountFilterChoices.Contains(value) && Set(ref accountFilter, value)) UpdateItems(); } }
    public string HomeSummary => $"{identities.Count(i => i.Kind == "Account")} 个账号 · 1 个运行环境";
    public string SelectedRoleLabel => SelectedIsActive ? "当前账号" : "已选账号";
    private ChunsonSpace.Core.ExitObservation? SelectedExit => Selected is { } item && recentExits.TryGetValue(item.Identity, out var exit) && exit.Matches(item.Environment) ? exit : null;
    public string SelectedExitSummary => SelectedExit is { Location.Length: > 0 } exit ? exit.Location : Selected?.ExitSummary ?? "请选择账号";
    public string SelectedExitIp => SelectedExit?.Ip ?? "未验证";
    public string SelectedExitPort => Selected?.PortLabel ?? "—";
    public string SelectedExitTimezone => SelectedExit?.Timezone ?? (string.IsNullOrWhiteSpace(Selected?.Timezone) ? "未配置" : Selected.Timezone);
    public string SelectedExitTimezoneLabel => SelectedExit is not null ? "检测时区" : "配置时区";
    public string SelectedExitDetails => SelectedExit is { } exit ? $"最近检测：{exit.CheckedAt.ToLocalTime():MM-dd HH:mm}；不代表当前连接持续在线。" + (exitSaveFailed ? "结果暂未保存到磁盘。" : "") : "尚无此账号当前代理的出口检测结果。配置时区不代表已确认的 IP 所在地区。";
    public string SelectedNetworkProtection => ProxyPanel.ProtectionSummary;
    public string SelectedDnsProtection => ProxyPanel.DnsSummary;
    public string SelectedRuntimeState => launchInProgress ? "正在启动" : lastOutcome == ChunsonSpace.Application.OperationOutcome.PendingLaunch ? "启动待确认"
        : runningIdentity is null ? runtimeProcessAlive ? "运行中 · 账号待确认" : runtimeExited ? "已退出" : "未运行" : runningIdentity == Selected?.Identity ? "运行中" : "其他账号运行中";
    public string SelectedNetworkProtectionDetails => ProxyPanel.ProtectionStage;
    public string SelectedDnsProtectionDetails => ProxyPanel.DnsStage;
    private int IdentityPageCount => Math.Max(1, (ItemCount + IdentityPageSize - 1) / IdentityPageSize);
    public IReadOnlyList<IdentityItem> IdentityPageItems => FilteredItems.Skip(identityPage * IdentityPageSize).Take(IdentityPageSize).ToArray();
    public string IdentityPageLabel => $"{identityPage + 1} / {IdentityPageCount} · 共 {ItemCount} 个";
    public bool HasIdentityPages => IdentityPageCount > 1;
    private int BackupPageCount => Math.Max(1, (Backups.Count + 2) / 3);
    public IReadOnlyList<BackupItem> BackupPageItems => Backups.Skip(backupPage * 3).Take(3).ToArray();
    public string BackupPageLabel => $"{backupPage + 1} / {BackupPageCount} · 共 {Backups.Count} 个";
    public bool IsCompactFeedback => feedbackTitle == "列表已刷新";
    public bool IsExpandedFeedback => !IsCompactFeedback;
    public string CompactFeedbackText => FeedbackTitle + " · " + Feedback;
    public void UpdateViewport(double width, double height)
    {
        if (width <= 0) return;
        Set(ref homeDetailWidth, width < 960 ? 240 : 260, nameof(HomeDetailWidth));
    }
    public void UpdateIdentityViewport(double availableHeight)
    {
        if (availableHeight <= 0 || !IsIdentityPage) return;
        var capacity = Math.Clamp((int)Math.Floor((availableHeight + 0.1) / 44), 1, 10);
        if (Set(ref identityPageSize, capacity, nameof(IdentityPageSize)))
        {
            var index = selected is null ? -1 : Array.IndexOf(FilteredItems.ToArray(), selected);
            identityPage = index >= 0 ? index / IdentityPageSize : Math.Min(identityPage, IdentityPageCount - 1);
            NotifyIdentityPage();
        }
    }
    private void NotifyHomeDetails()
    {
        foreach (var name in new[] { nameof(HomeSummary), nameof(SelectedRoleLabel), nameof(SelectedExitSummary), nameof(SelectedExitIp), nameof(SelectedExitPort), nameof(SelectedExitTimezone), nameof(SelectedExitTimezoneLabel), nameof(SelectedExitDetails), nameof(SelectedNetworkProtection), nameof(SelectedDnsProtection), nameof(SelectedRuntimeState), nameof(SelectedNetworkProtectionDetails), nameof(SelectedDnsProtectionDetails), nameof(HomeLaunchLabel), nameof(HomeLaunchTarget), nameof(FeedbackNextAction), nameof(HasFeedbackNextAction) }) Raise(name);
    }
    private void RememberExit(IdentityItem item, string ip, string timezone, string location = "")
    {
        var exit = new ChunsonSpace.Core.ExitObservation(item.Environment.Proxy, item.Environment.FollowSystemProxy, ip, timezone, location, DateTimeOffset.UtcNow);
        recentExits[item.Identity] = exit;
        try { workspace?.Profiles.SaveExitObservation(item.Identity, exit); exitSaveFailed = false; }
        catch (Exception error) when (error is ChunsonSpace.Core.StorageException or IOException or UnauthorizedAccessException) { exitSaveFailed = true; }
        NotifyHomeDetails();
    }
    private void LoadExitObservations()
    {
        if (workspace is null) return;
        recentExits.Clear();
        foreach (var item in identities)
        {
            try { if (workspace.Profiles.ReadExitObservation(item.Identity) is { } exit) recentExits[item.Identity] = exit; }
            catch (Exception error) when (error is ChunsonSpace.Core.StorageException or IOException or UnauthorizedAccessException) { /* Optional history cannot block account access. */ }
        }
    }
    private void ClearExitObservations()
    {
        foreach (var item in identities) workspace?.Profiles.ClearExitObservation(item.Identity);
        recentExits.Clear(); exitSaveFailed = false;
    }
    public bool IsDetailOpen { get => detailOpen; private set => Set(ref detailOpen, value); }
    public bool ShowOperationFeedback => feedbackTitle is not ("准备就绪" or "工作区已连接");
    public DelegateCommand OpenDetailsCommand { get; private set; } = null!;
    public DelegateCommand CloseDetailsCommand { get; private set; } = null!;
    public DelegateCommand SwitchCardCommand { get; private set; } = null!;
    public DelegateCommand PreviousIdentityPageCommand { get; private set; } = null!;
    public DelegateCommand NextIdentityPageCommand { get; private set; } = null!;
    public DelegateCommand PreviousBackupPageCommand { get; private set; } = null!;
    public DelegateCommand NextBackupPageCommand { get; private set; } = null!;
    private void InitializeWorkspaceCards()
    {
        ProxyPanel.PropertyChanged += (_, update) => { if (update.PropertyName is nameof(ProxyPanelViewModel.DnsStage) or nameof(ProxyPanelViewModel.ProtectionStage) or nameof(ProxyPanelViewModel.DnsSummary) or nameof(ProxyPanelViewModel.ProtectionSummary)) NotifyHomeDetails(); };
        OpenDetailsCommand = new(p => { if (p is IdentityItem item) Selected = item; if (Selected is not null) IsDetailOpen = true; });
        CloseDetailsCommand = new(_ => IsDetailOpen = false, _ => !operationBusy || IsLoggingIn);
        SwitchCardCommand = new(p => { if (p is IdentityItem item) { Selected = item; HomeLaunchCommand.Execute(null); } }, p => !busy && p is IdentityItem);
        PreviousIdentityPageCommand = new(_ => MoveIdentityPage(-1), _ => !operationBusy && !exitDraining && identityPage > 0);
        NextIdentityPageCommand = new(_ => MoveIdentityPage(1), _ => !operationBusy && !exitDraining && identityPage + 1 < IdentityPageCount);
        PreviousBackupPageCommand = new(_ => MoveBackupPage(-1), _ => !busy && backupPage > 0);
        NextBackupPageCommand = new(_ => MoveBackupPage(1), _ => !busy && backupPage + 1 < BackupPageCount);
    }
    private void MoveIdentityPage(int offset)
    {
        identityPage = Math.Clamp(identityPage + offset, 0, IdentityPageCount - 1);
        Selected = IdentityPageItems.FirstOrDefault();
        NotifyIdentityPage();
    }
    private void EnsureIdentityVisible(IdentityItem item)
    {
        if (updatingIdentityPage) return;
        var index = Array.IndexOf(FilteredItems.ToArray(), item);
        if (index < 0 || index / IdentityPageSize == identityPage) return;
        identityPage = index / IdentityPageSize;
        NotifyIdentityPage();
    }
    private void ResetIdentityPage()
    {
        identityPage = 0;
        if (selected is not null && !IdentityPageItems.Contains(selected)) Selected = null;
        NotifyIdentityPage();
    }
    private void NotifyIdentityPage()
    {
        updatingIdentityPage = true;
        try { Raise(nameof(IdentityPageItems)); Raise(nameof(IdentityPageLabel)); Raise(nameof(HasIdentityPages)); Raise(nameof(Selected)); }
        finally { updatingIdentityPage = false; }
        RefreshPagingCommands();
    }
    private void MoveBackupPage(int offset)
    {
        backupPage = Math.Clamp(backupPage + offset, 0, BackupPageCount - 1);
        Raise(nameof(BackupPageItems)); Raise(nameof(BackupPageLabel)); RefreshPagingCommands();
    }
    private void ResetBackupPage() { backupPage = 0; MoveBackupPage(0); }
    private void RefreshPagingCommands()
    {
        foreach (var command in new[] { PreviousIdentityPageCommand, NextIdentityPageCommand, PreviousBackupPageCommand, NextBackupPageCommand }) command?.Refresh();
    }
}

public sealed class EffortLabelConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => ChunsonSpace.Accounts.ModelTestParameters.EffortLabel(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
