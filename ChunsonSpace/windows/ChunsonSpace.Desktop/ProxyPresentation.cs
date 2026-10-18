using System.IO;
using System.Net;
using ChunsonSpace.Accounts;
using ChunsonSpace.Proxy;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

public sealed class ProxyPanelViewModel : ObservableObject
{
    public const string FlClashReleases = "https://github.com/chen08209/FlClash/releases";
    public const string ClashVergeReleases = "https://github.com/clash-verge-rev/clash-verge-rev/releases";
    private readonly DesktopProxyServices? services;
    private readonly Func<CancellationToken, Task<LoginProxySelection>>? resolveProtectionProxy;
    private bool protectionReading;
    private bool dnsReading;
    private readonly Action<bool> setShellBusy;
    private readonly IExternalUrlOpener urls;
    private ProxyClientKind client;
    private string dataRoot;
    private string appPath = "";
    private string group = "";
    private string backupPath = "";
    private string exportPath = "";
    private bool enableTun = true;
    private bool busy;
    private bool hostBusy;
    private bool previewReady;
    private bool error;
    private bool entered;
    private bool explicitSource;
    private bool loadRequested;
    private bool reading;
    private bool configurationRead;
    private FlClashApplicationDiscoveryStatus applicationDiscovery;
    private string readStatus = "尚未读取";
    private string selectionNote = "";
    private string status = "进入此页后会自动读取本地配置，不修改设置。";
    private string dnsStage = "DNS 配置：尚未读取本地设置";
    private string protectionStage = "尚未读取系统防护状态；不会自动开启。";
    private string dnsSummary = "未检查";
    private string protectionSummary = "未检查";
    private IReadOnlyList<ProxyPortCandidate> ports = [];
    private IReadOnlyList<ProxyGroup> groups = [];
    private IReadOnlyList<DnsChange> changes = [];
    private IReadOnlyList<string> warnings = [];
    private IReadOnlyList<string> apps = [];
    private ClashVergeRepairPlan? plan;
    public ProxyPanelViewModel(DesktopProxyServices? services, Action<bool>? setShellBusy = null, Func<CancellationToken, Task<LoginProxySelection>>? resolveProtectionProxy = null)
    {
        this.services = services; this.resolveProtectionProxy = resolveProtectionProxy; this.setShellBusy = setShellBusy ?? (_ => { });
        urls = services?.Urls ?? new OfflineUrlOpener();
        dataRoot = services?.FlClashRoot ?? "";
        NodeTesting = new(services?.NodeTesting, () => (client, dataRoot));
        InspectCommand = new(_ => CurrentOperation = InspectAsync(false), _ => services is not null && CanEdit);
        PreviewCommand = new(_ => CurrentOperation = InspectAsync(true), _ => services is not null && CanEdit && (IsFlClash || groups.Any(candidate => candidate.Usable) && group.Length > 0));
        ApplyCommand = new(_ => CurrentOperation = ApplyAsync(), _ => services is not null && CanEdit && previewReady && (!IsFlClash || appPath.Length > 0 && applicationDiscovery == FlClashApplicationDiscoveryStatus.Found));
        EnableProtectionCommand = new(_ => CurrentOperation = EnableProtectionAsync(), _ => services is not null && resolveProtectionProxy is not null && CanEdit && !protectionReading);
        InspectProtectionCommand = new(_ => CurrentOperation = RefreshProtectionAsync(), _ => services is not null && CanEdit && !protectionReading);
        ConfigureNetworkCommand = new(_ => CurrentOperation = ConfigureNetworkAsync(), _ => services is not null && CanEdit && IsFlClash);
        RestoreProtectionCommand = new(_ => CurrentOperation = RestoreProtectionAsync(), _ => services is not null && CanEdit && !protectionReading);
        RestoreCommand = new(_ => CurrentOperation = RestoreAsync(), _ => services is not null && CanEdit && !string.IsNullOrWhiteSpace(backupPath));
        ExportReportCommand = new(_ => CurrentOperation = ExportReportAsync(), _ => CanEdit && !IsFlClash && plan is not null && previewReady && !string.IsNullOrWhiteSpace(exportPath));
        DownloadFlClashCommand = new(_ => OpenRelease(FlClashReleases), _ => CanEdit);
        DownloadClashVergeCommand = new(_ => OpenRelease(ClashVergeReleases), _ => CanEdit);
    }
    public IReadOnlyList<ProxyClientKind> Clients => services?.IsFixture == true ? [ProxyClientKind.FlClash, ProxyClientKind.ClashVerge] : [ProxyClientKind.FlClash];
    public ProxyClientKind Client { get => client; set { if (Clients.Contains(value) && CanEdit && Set(ref client, value)) { explicitSource = true; ResetClientSource(); BeginRead(); } } }
    public bool IsFlClash => client == ProxyClientKind.FlClash;
    public bool IsClashVerge => !IsFlClash;
    public bool CanEdit => !busy && !hostBusy;
    public string DataRoot { get => dataRoot; set { if (CanEdit && Set(ref dataRoot, value)) { explicitSource = true; group = ""; Invalidate(true); ResetConfigurationStatus(); SetReadStatus("路径已更改，请重新读取"); Report("数据目录已更改。点击重新读取后选择配置中的分组。", false); } } }
    public string ApplicationPath { get => appPath; set { if (CanEdit && Set(ref appPath, value)) { Invalidate(); Raise(nameof(ApplicationDiscoveryText)); } } }
    public string ProxyGroup { get => group; set { if (CanEdit && Set(ref group, value)) { Invalidate(); Raise(nameof(GroupHint)); } } }
    public bool EnableTun { get => enableTun; set { if (CanEdit && Set(ref enableTun, value)) Invalidate(); } }
    public string BackupPath { get => backupPath; set { if (CanEdit && Set(ref backupPath, value)) RefreshCommands(); } }
    public string ExportPath { get => exportPath; set { if (CanEdit && Set(ref exportPath, value)) RefreshCommands(); } }
    public bool IsBusy => busy;
    public bool IsError => error;
    public bool PreviewReady => previewReady;
    public string Status => status;
    public string DnsStage => dnsStage;
    public string ProtectionStage => protectionStage;
    public string DnsSummary => dnsSummary;
    public string ProtectionSummary => protectionSummary;
    public string BrowserStage => "授权浏览器：登录时自动使用本机 Chrome、Edge 或 Brave。";
    public NodeTestingViewModel NodeTesting { get; }
    public string ReadStatus => readStatus;
    public bool HasGroups => groups.Count > 0;
    public string GroupHint => reading ? "正在读取配置中的代理分组…" : selectionNote.Length > 0 ? selectionNote : groups.Count == 0
        ? "没有可选分组。请查看读取状态；客户端需先导入并选择一个配置。"
        : !groups.Any(candidate => candidate.Usable) ? "配置已读取，但这些分组无法确认指向代理。请在客户端检查直连、循环或未解析的提供器。"
        : group.Length > 0 ? "当前代理分组已确认。可以一键设置，或先查看变更。"
        : $"识别到 {groups.Count} 个分组。无法确定唯一代理组，请从列表选择；用于加密 DNS 与国外服务分流。";
    public string ApplicationDiscoveryText => !IsFlClash ? "Clash Verge 使用持久配置；应用前退出客户端。"
        : appPath.Length > 0 ? applicationDiscovery == FlClashApplicationDiscoveryStatus.InvalidPath ? "所选 FlClash 应用位置无效，请在高级设置修正。" : "FlClash 应用位置：" + Path.GetFileName(appPath)
        : applicationDiscovery == FlClashApplicationDiscoveryStatus.NotFound ? "未识别到 FlClash 应用。可以读取配置；应用修复前请在高级设置选择程序。"
        : apps.Count > 1 ? $"找到 {apps.Count} 个应用位置，请在高级设置选择。" : "尚未识别应用位置；读取配置后会显示结果。";
    public string ModeDescription => services is null ? "演示界面；配置操作未连接工作区。" : services.IsFixture ? "隔离工作区：Clash Verge 使用真实文件服务，FlClash 使用明确的离线协议替身。" : "配置检查只读；应用与恢复由你点击执行。";
    public string ClientInstructions => IsFlClash ? "应用及恢复会请求系统授权并重启 FlClash，启用 TUN 与 DNS 分流；使用标准数据目录，可指定应用路径。" : "应用及恢复前请完全退出 Clash Verge；保存后重新打开客户端。运行或状态未知时拒绝修改。";
    public IReadOnlyList<ProxyPortCandidate> Ports => ports;
    public IReadOnlyList<ProxyGroup> Groups => groups;
    public IReadOnlyList<string> ApplicationCandidates => apps;
    public IReadOnlyList<DnsChange> Changes => changes;
    public IReadOnlyList<string> Warnings => warnings;
    public Task CurrentOperation { get; private set; } = Task.CompletedTask;
    public DelegateCommand InspectCommand { get; }
    public DelegateCommand PreviewCommand { get; }
    public DelegateCommand ApplyCommand { get; }
    public DelegateCommand EnableProtectionCommand { get; }
    public DelegateCommand InspectProtectionCommand { get; }
    public DelegateCommand ConfigureNetworkCommand { get; }
    public DelegateCommand RestoreProtectionCommand { get; }
    public DelegateCommand RestoreCommand { get; }
    public DelegateCommand ExportReportCommand { get; }
    public DelegateCommand DownloadFlClashCommand { get; }
    public DelegateCommand DownloadClashVergeCommand { get; }
    public void EnterPage()
    {
        if (services is null) { SetReadStatus("演示界面未连接配置"); return; }
        if (services.IsFixture && !entered && !explicitSource && !services.HasConfiguration(ProxyClientKind.FlClash) && services.HasConfiguration(ProxyClientKind.ClashVerge))
        { client = ProxyClientKind.ClashVerge; Raise(nameof(Client)); ResetClientSource(); }
        entered = true; BeginRead();
    }
    private void ResetClientSource()
    {
        Set(ref dataRoot, IsFlClash ? services?.FlClashRoot ?? "" : services?.ClashVergeRoot ?? "", nameof(DataRoot));
        group = ""; appPath = ""; backupPath = ""; exportPath = "";
        applicationDiscovery = FlClashApplicationDiscoveryStatus.Unknown;
        Invalidate(true);
        ResetConfigurationStatus();
        foreach (var name in new[] { nameof(IsFlClash), nameof(IsClashVerge), nameof(ClientInstructions), nameof(ApplicationPath), nameof(BackupPath), nameof(ExportPath), nameof(ApplicationDiscoveryText) }) Raise(name);
    }
    private void BeginRead()
    {
        if (services is null) return;
        if (!CanEdit) { loadRequested = true; SetReadStatus("当前操作结束后自动读取"); return; }
        loadRequested = false; CurrentOperation = InspectAsync(false);
    }
    private FlClashDnsRequest Request() => new(dataRoot, Empty(appPath), Empty(group), Empty(backupPath), services!.IsFixture);
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private Task InspectAsync(bool preview) => RunAsync(() => ReadConfigurationAsync(preview));
    private async Task ReadConfigurationAsync(bool preview)
    {
        reading = true; configurationRead = false; SetReadStatus("正在读取本地配置…"); Raise(nameof(GroupHint));
        SetDnsStage("DNS 配置：正在只读核对设置…", "检查中");
        Report("正在读取 " + (IsFlClash ? "FlClash" : "Clash Verge") + " 配置；不会修改、重启或发送网络请求。", false);
            Invalidate(true);
            if (IsFlClash)
            {
                var result = await services!.FlClash.PreviewAsync(Request() with { AllSubscriptions = true });
                ApplyFlResult(result);
                configurationRead = result.ConfigurationStatus == FlClashConfigurationStatus.Ready || result.ConfigurationStatus == FlClashConfigurationStatus.Unknown && groups.Count > 0;
                previewReady = preview && result.Status == FlClashDnsStatus.PreviewReady;
                SetReadStatus(result.ConfigurationStatus == FlClashConfigurationStatus.Missing ? "未找到 FlClash 配置。请先导入配置，或展开高级设置检查目录。"
                    : configurationRead ? result.Status == FlClashDnsStatus.Failed ? "配置已读取；DNS 覆写需要处理：" + result.Message
                        : result.Scope == "all-subscriptions" ? $"已读取 {result.SubscriptionCount} 个订阅 · 统一 DNS" : $"已读取 {groups.Count} 个代理分组"
                    : "读取失败：" + result.Message);
                if (!preview && result.Status is FlClashDnsStatus.PreviewReady or FlClashDnsStatus.SelectionRequired)
                    Report("已自动读取本地配置；尚未修改设置。\n" + result.Message, false);
            }
            else
            {
                if (!File.Exists(Path.Combine(dataRoot, "profiles.yaml"))) throw new InvalidOperationException("未找到 Clash Verge 配置。请先启动客户端并导入配置，或展开高级设置选择已有数据目录。");
                var inspection = await Task.Run(() => services!.ClashVerge.Inspect(dataRoot));
                ApplyInspection(inspection);
                configurationRead = true;
                SetReadStatus($"已读取 {groups.Count} 个代理分组");
                if (preview)
                {
                    plan = await Task.Run(() => services!.ClashVerge.Preview(new(dataRoot, Empty(group), enableTun)));
                    changes = plan.Changes; warnings = plan.Warnings; group = plan.ProxyGroup;
                    previewReady = true; Report("预览已生成；尚未修改配置。", false);
                }
                else Report("端口与代理分组已读取；尚未验证监听状态或网络。", false);
                SetDnsStage("DNS 配置：配置已读取；尚未确认统一设置");
            }
            RaiseResults();
    }
    private Task ApplyAsync() => RunAsync(async () => { await ApplyChangesAsync(); });
    private Task ConfigureNetworkAsync() => RunAsync(async () =>
    {
        var request = Request() with { ProxyGroup = null, AllSubscriptions = true };
        Report("正在读取全部订阅的 DNS 配置…", false);
        var preview = await services!.FlClash.PreviewAsync(request);
        ApplyFlResult(preview);
        if (preview.Status != FlClashDnsStatus.PreviewReady) return;
        request = request with { ApplicationPath = preview.ApplicationPath ?? request.ApplicationPath };
        await ApplyFlClashChangesAsync(request);
    });
    public async Task RefreshProtectionAsync()
    {
        if (services is null || protectionReading || !CanEdit) return;
        protectionReading = true;
        SetProtectionStage("正在只读检查系统防火墙规则…", "检查中"); RefreshCommands();
        try { ShowProtection(await services.InspectProtectionAsync()); }
        catch (Exception) { SetProtectionStage("无法读取系统规则，请点击刷新重试；未修改任何配置。", "未确认"); }
        finally { protectionReading = false; RefreshCommands(); }
    }
    public async Task RefreshDnsStatusAsync()
    {
        if (services is null || dnsReading || busy || !IsFlClash) return;
        dnsReading = true;
        var source = dataRoot;
        SetDnsStage("DNS 配置：正在只读核对全部订阅…", "检查中");
        try
        {
            var result = await services.FlClash.PreviewAsync(new(source, null, null, null, services.IsFixture) { AllSubscriptions = true });
            if (source == dataRoot && IsFlClash && !busy) ReadDnsStatus(result);
        }
        catch (Exception exception) when (exception is ProxyException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (source == dataRoot && IsFlClash && !busy) SetDnsStage("DNS 配置：只读核对失败，请在网络与 IP 中重新读取。", "未确认");
        }
        finally { dnsReading = false; }
    }
    private void ShowProtection(DirectConnectionProtectionResult result)
    {
        var summary = result.Status switch
        {
            DirectConnectionProtectionStatus.Disabled or DirectConnectionProtectionStatus.Restored => "已关闭",
            DirectConnectionProtectionStatus.Configured when result.RulesVerified => "已开启",
            DirectConnectionProtectionStatus.ProtectionStale => "需修复",
            _ => "未确认"
        };
        SetProtectionStage(result.Message, summary);
    }
    private Task EnableProtectionAsync() => RunAsync(async () =>
    {
        await Task.CompletedTask;
        Report("系统防护功能已移除，不再创建禁止直连规则。", true);
    });
    private async Task<bool> ApplyChangesAsync()
    {
            if (IsFlClash) return await ApplyFlClashChangesAsync(Request() with { ProxyGroup = null, AllSubscriptions = true });
            reading = false;
            SetDnsStage("DNS 配置：正在备份并应用…", "配置中");
            Report("正在备份并设置 DNS 与规则…", false);
            bool saved;
            {
                var result = await Task.Run(() => services!.ClashVerge.Apply(plan!, services.BackupRoot));
                Set(ref backupPath, result.BackupPath, nameof(BackupPath)); warnings = result.Warnings;
                Report(result.SettingsSaved ? "设置已保存。请重新打开 Clash Verge；尚未验证运行配置、DNS 或网络。" : "设置未保存。", !result.SettingsSaved);
                saved = result.SettingsSaved;
            }
            SetDnsStage(saved ? "DNS 配置：持久设置已保存；运行效果尚未验证" : "DNS 配置：未完成", saved ? "设置已保存" : "未确认");
            previewReady = false; plan = null; RaiseResults();
            return saved;
    }
    private async Task<bool> ApplyFlClashChangesAsync(FlClashDnsRequest request)
    {
        reading = false;
        SetDnsStage("DNS 配置：正在备份并配置全部订阅…", "配置中");
        var result = await services!.FlClash.ApplyAsync(request with { ProxyGroup = null, AllSubscriptions = true });
        ApplyFlResult(result);
        SetDnsStage(result.SettingsSaved ? "DNS 配置：全部订阅持久设置已保存；" +
            (result.GeneratedConfigVerified ? "当前生成配置已核对；实际网络效果尚未验证" : "重载待确认") : "DNS 配置：未完成", result.SettingsSaved ? "设置已保存" : "未确认");
        previewReady = false; plan = null; RaiseResults();
        return result.SettingsSaved;
    }
    private Task RestoreProtectionAsync() => RunAsync(async () =>
    {
        var result = await services!.Protection.RestoreAsync(new([], ""));
        ShowProtection(result);
        Report(protectionStage + "\nDNS 配置保持当前状态。", !result.Success);
    });
    private async Task RestoreAsync()
    {
        await RunAsync(async () =>
        {
            if (IsFlClash) ApplyFlResult(await services!.FlClash.RestoreAsync(Request()));
            else
            {
                var result = await Task.Run(() => services!.ClashVerge.Restore(backupPath));
                warnings = result.Warnings;
                Report("持久配置已恢复。请重新打开 Clash Verge；尚未验证网络。", false);
            }
            previewReady = false; plan = null; RaiseResults();
            if (!error) SetDnsStage("DNS 配置：持久设置已恢复；运行效果尚未验证", "已恢复");
        });
    }
    private async Task ExportReportAsync()
    {
        await RunAsync(async () =>
        {
            var script = ClashVergeScript.Generate(plan!.ProxyGroup, plan.Request.EnableTun);
            var full = Path.GetFullPath(exportPath);
            if (File.Exists(full)) throw new InvalidOperationException("导出目标已存在。请选择新文件，避免覆盖已有配置。");
            await File.WriteAllTextAsync(full, script);
            Report("全局脚本已导出；尚未接入客户端。不包含订阅或节点凭据。", false);
        });
    }
    private void ApplyInspection(ProxyInspection inspection)
    {
        ports = inspection.Ports; groups = inspection.Groups; warnings = inspection.Warnings;
        if (!groups.Any(candidate => candidate.Name == group && candidate.Usable)) group = "";
        if (group.Length == 0 && inspection.SuggestedGroup is not null) group = inspection.SuggestedGroup;
    }
    private void ReadDnsStatus(FlClashDnsResult result)
    {
        if (result.Status == FlClashDnsStatus.PreviewReady && result.Scope == "all-subscriptions" && result.SubscriptionCount > 0)
        {
            var matches = result.ChangedFields.Count == 0;
            SetDnsStage(matches ? $"DNS 配置：{result.SubscriptionCount} 个订阅持久设置已保存（本次只读核对）；运行效果尚未验证"
                : $"DNS 配置：{result.SubscriptionCount} 个订阅的统一设置待补齐；尚未应用", matches ? "已配置" : "待补齐");
        }
        else SetDnsStage("DNS 配置：本次读取尚未确认统一设置");
    }
    private void ApplyFlResult(FlClashDnsResult result)
    {
        if (result.Status == FlClashDnsStatus.PreviewReady || reading) ReadDnsStatus(result);
        groups = result.Groups.Select(name => new ProxyGroup(name, result.NodeTargets?.Contains(name) == true ? "固定节点" : "配置分组", true, "由 FlClash 检查报告提供；尚未验证网络。")).ToArray();
        selectionNote = result.SelectionNote ?? "";
        apps = result.ApplicationCandidates;
        applicationDiscovery = result.ApplicationDiscoveryStatus;
        if (result.ApplicationPath is { Length: > 0 } discovered) Set(ref appPath, discovered, nameof(ApplicationPath));
        else if (appPath.Length == 0 && apps.Count == 1) Set(ref appPath, apps[0], nameof(ApplicationPath));
        ports = (result.ProxyPorts ?? []).Select(candidate =>
        {
            var binding = candidate.BindAddress == "*" ? "127.0.0.1" : candidate.BindAddress;
            var local = IPAddress.TryParse(binding, out var address) &&
                (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));
            var host = local ? address!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[::1]"
                : address.Equals(IPAddress.Any) ? "127.0.0.1" : address.ToString() : binding;
            var authentication = candidate.RequiresAuthentication is null ? "认证未知" : candidate.RequiresAuthentication.Value ? "需要认证" : "无认证";
            return new ProxyPortCandidate("FlClash " + candidate.Kind + " · " + authentication,
                host is null ? $"绑定未确认 · 端口 {candidate.Port}" : $"http://{host}:{candidate.Port}", !local || candidate.RequiresAuthentication != false);
        }).ToArray();
        changes = result.ChangedFields.Select(field => new DnsChange(field, "由 FlClash 配置报告确认的变更项。")).ToArray();
        if (!groups.Any(candidate => candidate.Name == group)) group = "";
        if (group.Length == 0 && result.SelectedGroup is not null && groups.Any(candidate => candidate.Name == result.SelectedGroup && candidate.Usable)) group = result.SelectedGroup;
        if (!string.IsNullOrWhiteSpace(result.BackupDirectory)) Set(ref backupPath, result.BackupDirectory, nameof(BackupPath));
        warnings = ["配置阶段与网络验证分别报告；没有执行 DNS 或出口请求。"];
        var stage = result.Status switch
        {
            FlClashDnsStatus.GeneratedConfigVerified => "设置已保存，生成配置已核对；尚未验证 DNS 或网络。",
            FlClashDnsStatus.ReloadUnconfirmed or FlClashDnsStatus.SettingsSaved => "设置已保存，重载尚未确认；请检查 FlClash。",
            FlClashDnsStatus.Restored => "持久配置已恢复；尚未验证网络。",
            FlClashDnsStatus.PreviewReady => "预览已生成；尚未修改配置。",
            FlClashDnsStatus.SelectionRequired => "请选择代理分组或应用路径后重新预览。",
            _ => "操作结果未完成或待确认。"
        };
        Report(stage + "\n" + result.Message, result.Status is FlClashDnsStatus.Failed or FlClashDnsStatus.Uncertain or FlClashDnsStatus.SelectionRequired);
    }
    private async Task RunAsync(Func<Task> operation)
    {
        busy = true; Raise(nameof(IsBusy)); Raise(nameof(CanEdit)); setShellBusy(true); RefreshCommands();
        try { await operation(); }
        catch (ProxyException exception) { previewReady = false; plan = null; if (dnsStage.Contains("正在")) SetDnsStage("DNS 配置：未完成 · " + exception.Message); if (reading) SetReadStatus((configurationRead ? exception.Code == ProxyError.Conflict ? "配置已读取；修复预览存在冲突：" : "配置已读取；修复条件未满足：" : "配置读取失败：") + exception.Message); Report(exception.Message + (exception.RecoveryPath is { Length: > 0 } recovery ? "\n恢复材料：" + recovery : ""), true); }
        catch (Exception exception) when (exception is AccountException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { previewReady = false; plan = null; if (reading) SetReadStatus(exception.Message); Report(exception.Message, true); }
        finally
        {
            if (dnsSummary is "检查中" or "配置中") SetDnsStage("DNS 配置：本次操作未完成，需重新检查");
            if (protectionSummary is "配置中" or "检查中") SetProtectionStage("禁止直连：本次操作未完成，需重新检查");
            reading = false; RaiseResults(); busy = false; Raise(nameof(IsBusy)); Raise(nameof(CanEdit)); setShellBusy(false); RefreshCommands();
        }
    }
    private void OpenRelease(string url)
    {
        try { urls.Open(new(url)); Report(services?.IsFixture == false ? "已打开官方发布页面。" : "已记录官方发布页面目标；演示或隔离模式未打开浏览器。", false); }
        catch (Exception) { Report("无法打开发布页面，请检查默认浏览器。", true); }
    }
    private void Invalidate(bool clearSource = false) { previewReady = false; plan = null; changes = []; if (clearSource) { ports = []; groups = []; apps = []; warnings = []; selectionNote = ""; RaiseResults(); } Raise(nameof(PreviewReady)); Raise(nameof(Changes)); RefreshCommands(); }
    private void RaiseResults() { foreach (var name in new[] { nameof(Ports), nameof(Groups), nameof(HasGroups), nameof(GroupHint), nameof(ApplicationDiscoveryText), nameof(ApplicationCandidates), nameof(Changes), nameof(Warnings), nameof(ProxyGroup), nameof(PreviewReady) }) Raise(name); RefreshCommands(); }
    private void SetReadStatus(string message) { readStatus = message; Raise(nameof(ReadStatus)); }
    private void ResetConfigurationStatus() { SetDnsStage("DNS 配置：数据来源已更改，需重新检查"); }
    private void SetDnsStage(string message, string summary = "未确认") { dnsStage = message; dnsSummary = summary; Raise(nameof(DnsStage)); Raise(nameof(DnsSummary)); }
    private void SetProtectionStage(string message, string summary = "未确认") { protectionStage = message; protectionSummary = summary; Raise(nameof(ProtectionStage)); Raise(nameof(ProtectionSummary)); }
    private void Report(string message, bool failed) { status = message; error = failed; Raise(nameof(Status)); Raise(nameof(IsError)); }
    public void SetHostBusy(bool value) { hostBusy = value; Raise(nameof(CanEdit)); RefreshCommands(); if (!value && loadRequested && !busy) BeginRead(); }
    public void RefreshCommands() { foreach (var command in new[] { InspectCommand, PreviewCommand, ApplyCommand, EnableProtectionCommand, InspectProtectionCommand, ConfigureNetworkCommand, RestoreProtectionCommand, RestoreCommand, ExportReportCommand, DownloadFlClashCommand, DownloadClashVergeCommand }) command.Refresh(); }
}

public sealed partial class ShellViewModel
{
    public ProxyPanelViewModel ProxyPanel { get; private set; } = null!;
    public DelegateCommand UseProxyCandidateCommand { get; private set; } = null!;
    private void InitializeProxyCommands()
    {
        ProxyPanel = new(workspace?.ProxyServices, SetBusy, workspace?.Accounts?.ResolveLoginProxyAsync);
        UseProxyCandidateCommand = new(p =>
        {
            if (p is ProxyPortCandidate candidate && Editor is { IsEnvironmentEditor: true } && !candidate.RequiresAuthentication)
                { Editor.FollowSystemProxy = false; Editor.Proxy = candidate.Address; }
        }, p => !busy && Editor is { IsEnvironmentEditor: true } && p is ProxyPortCandidate { RequiresAuthentication: false });
    }
}
