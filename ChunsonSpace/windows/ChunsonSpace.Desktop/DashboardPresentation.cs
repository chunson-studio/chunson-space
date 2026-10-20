using ChunsonSpace.Application;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

public sealed partial class ShellViewModel
{
    private string interfaceMode = "auto";
    public bool IsSingleAccountMode => interfaceMode == "single" || interfaceMode == "auto" && AccountTotal <= 1;
    public bool IsMultiAccountMode => !IsSingleAccountMode;
    public bool CanChangeInterfaceMode => !globalBusy;
    public IReadOnlyList<string> InterfaceModes { get; } = ["单账号", "多账号"];
    public DelegateCommand SetInterfaceModeCommand => new(value => InterfaceMode = value?.ToString() ?? "", _ => CanChangeInterfaceMode);
    public string InterfaceMode
    {
        get => IsSingleAccountMode ? "单账号" : "多账号";
        set
        {
            if (value is not ("单账号" or "多账号") || globalBusy) return;
            var next = value == "单账号" ? "single" : "multi";
            if (interfaceMode == next) return;
            interfaceMode = next;
            if (IsSingleAccountMode) { Selected = DisplayedAccount; Navigate("Dashboard"); }
            ResultsGallery.SetProfileScope(IsSingleAccountMode ? DisplayedAccount?.Name ?? "" : null);
            RefreshInterfaceMode(); SaveSettings();
        }
    }
    public IdentityItem? DisplayedAccount => active ?? Selected ?? identities.FirstOrDefault();
    public string DashboardTitle => IsSingleAccountMode ? "首页" : "仪表盘";
    public string HealthActionLabel => IsSingleAccountMode ? "检测当前账号" : "一键账号检测";
    private void RefreshInterfaceMode()
    {
        if (IsSingleAccountMode) resultsGallery?.SetProfileScope(DisplayedAccount?.Name ?? "");
        foreach (var name in new[] { nameof(IsSingleAccountMode), nameof(IsMultiAccountMode), nameof(InterfaceMode), nameof(DisplayedAccount), nameof(DashboardTitle), nameof(HealthActionLabel), nameof(ModelTestProfiles) }) Raise(name);
        TestAllAccountsCommand?.Refresh(); OpenVisualTestsCommand?.Refresh();
    }
    public bool IsDashboardPage => page == "Dashboard";
    public bool IsTestsPage => page == "Tests";
    public string BeijingTime { get; private set; } = "";
    public string BeijingDate { get; private set; } = "";
    internal void RefreshBeijingTime(DateTimeOffset utc)
    {
        var beijing = utc.ToOffset(TimeSpan.FromHours(8));
        BeijingTime = beijing.ToString("HH:mm:ss"); BeijingDate = beijing.ToString("yyyy年MM月dd日");
        Raise(nameof(BeijingTime)); Raise(nameof(BeijingDate));
    }
    public int AccountTotal => identities.Count;
    public int NormalAccounts => identities.Count(i => i.Marks?.Quality == AccountQuality.Normal);
    public int AbnormalAccounts => identities.Count(i => i.Marks?.Quality == AccountQuality.SuspectedDegraded);
    public int UnknownAccounts => AccountTotal - NormalAccounts - AbnormalAccounts;
    public string DashboardSummary => $"共 {AccountTotal} 个账号 · {UnknownAccounts} 个未判断";
    public string CurrentAccountDisplay => active?.Label ?? "尚未载入账号";
    public string DashboardIpv4 => currentLocalEnvironment?.Addresses.Ipv4 is { Available: true } v4 ? v4.Ip : "未知";
    public string DashboardIpv6 => currentLocalEnvironment?.Addresses.Ipv6 is { Available: true } v6 ? "IPv6  " + v6.Ip : "IPv6  未知";
    public string DashboardTimezone => currentLocalEnvironment is { Verified: true } report ? report.ExitTimezone : "时区尚未确认";
    public string DashboardRegion => currentLocalEnvironment is { Verified: true } report ? report.CountryCode : "地区未知";
    public string DashboardEnvironmentTime => environmentSyncState
        + (currentLocalEnvironment is { } report ? $" · 最近检测 {report.CheckedAt.ToLocalTime():MM-dd HH:mm}" : "")
        + "\n" + LastEnvironmentSyncLabel;
    private string dashboardScore = "尚无评分摘要";
    private string? loadingScoreIp;
    private Task dashboardScoreLoad = Task.CompletedTask;
    private string? DashboardScoreIp => lastVerifiedLocalEnvironment?.Ip ?? workspace?.Switching?.LastEgressEvidence?.Ip;
    public string DashboardScore => dashboardScore;
    private void RefreshDashboardScore()
    {
        if (!IsDashboardPage) return;
        var ip = DashboardScoreIp;
        if (!dashboardScoreLoad.IsCompleted && loadingScoreIp == ip) return;
        loadingScoreIp = ip;
        dashboardScoreLoad = ReadDashboardScoreAsync(ip);
    }
    private async Task ReadDashboardScoreAsync(string? ip)
    {
        var label = "尚无评分摘要";
        try
        {
            if (!string.IsNullOrEmpty(ip) && workspace?.ProxyServices.NodeTesting is { } nodes
                && await Task.Run(() => nodes.ReadCachedExit(ip)) is { Score.Quality: { } score } cached)
                label = $"最近评分 {score}/100 · {cached.FetchedAt.ToLocalTime():MM-dd HH:mm}";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or StorageException or FormatException) { }
        if (ip != DashboardScoreIp) return;
        Set(ref dashboardScore, label, nameof(DashboardScore));
    }
    public DelegateCommand LaunchDisplayedCommand => new(_ => { Selected = DisplayedAccount; HomeLaunchCommand.Execute(null); }, _ => !globalBusy && DisplayedAccount is not null);
    public DelegateCommand ManageDisplayedCommand => new(_ => { Selected = DisplayedAccount; OpenDetailsCommand.Execute(null); }, _ => !globalBusy && DisplayedAccount is not null);
    public IEnumerable<SavedResultRow> SingleRecentResults => ResultsGallery.Items.Cast<SavedResultRow>().Where(r => r.Profile == DisplayedAccount?.Name).Take(4);
    public IEnumerable<SavedResultRow> RecentResults => ResultsGallery.Items.Cast<SavedResultRow>().Take(4);
    private void RefreshDashboard()
    {
        RefreshDashboardScore();
        RefreshInterfaceMode();
        foreach (var property in new[] { nameof(AccountTotal), nameof(NormalAccounts), nameof(AbnormalAccounts), nameof(UnknownAccounts), nameof(DashboardSummary), nameof(CurrentAccountDisplay), nameof(DashboardIpv4), nameof(DashboardIpv6), nameof(DashboardTimezone), nameof(DashboardRegion), nameof(DashboardEnvironmentTime), nameof(DashboardScore), nameof(RecentResults), nameof(SingleRecentResults) }) Raise(property);
    }
}
