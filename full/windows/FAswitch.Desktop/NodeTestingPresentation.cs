using System.Collections.ObjectModel;
using FAswitch.Accounts;
using FAswitch.Core;
using FAswitch.NodeTesting;
using FAswitch.Proxy;

namespace FAswitch.Desktop;

public sealed class NodeSelectionItem(NodeCandidate node, Action changed, string subscriptionId = "", string subscriptionName = "") : ObservableObject
{
    private string result = "尚未测试";
    public NodeCandidate Node { get; } = node;
    public string Name => Node.Name;
    public string Type => Node.Type;
    public string Source => subscriptionName.Length > 0 ? subscriptionName : Node.Source;
    public string SubscriptionId => subscriptionId;
    public bool Supported => Node.Supported;
    public string Compatibility => Node.Supported ? "可测试" : Node.Reason;
    public string Result => result;
    public string State { get; private set; } = "未检测";
    public long? DelayMs { get; private set; }
    public int? RiskScore { get; private set; }
    public int? QualityScore { get; private set; }
    public bool Eligible { get; private set; }
    public string NetworkGroup { get; private set; } = "待分类";
    public string NetworkTag => NetworkGroup switch { "家庭宽带组" => "家宽", "机房组" => "机房", _ => "待分类" };
    public string ExitIp { get; private set; } = "";
    public IpQualityReport? Quality { get; private set; }
    public string Country => CountryNames.Display(Quality?.CountryCode, Quality?.Country);
    public bool IsEntertainment => NodeScreeningRules.IsEntertainment(Name);
    public bool IsSubscriptionNotice => NodeScreeningRules.IsNotice(Name);
    public bool IsAccountCandidate => NodeScreeningRules.IsAccountCandidate(Node);
    public bool IsPreferredSubscription => IsAccountCandidate && NodeScreeningRules.IsPreferredSubscription(Source);
    public bool ScoreExempt => NodeScreeningRules.IsScoreExempt(Node, subscriptionName);
    public bool CanPrepareLogin => IsAccountCandidate && Eligible;
    public string ScoreStatus => QualityScore is { } score ? $"{score} 分" : ExitIp.Length > 0 ? "评分暂不可用" : State switch { "未检测" => "待检测", "超时" => "连接超时", "已取消" => "检测已取消", _ => "连接未通过" };
    public string ChoiceLabel => QualityScore is { } score ? $"{Country} · {score} 分 · {ExitIp}" + (ScoreExempt ? " · 住宅免检" : score < 85 ? " · 未达85分" : "")
        : $"{Country} · {ScoreStatus} · {(ExitIp.Length > 0 ? ExitIp : Name)}";
    public string ChoiceExplanation => ScoreExempt ? "IPEqual 住宅免评分门槛，分数仅供参考；连接与实际出口仍需确认。" : QualityScore is { } score ? score >= IpAdmissionPolicy.MinimumScore ? "评分已达标；登录时确认实际出口。" : "评分低于 85 分，请选择其他线路。"
        : ExitIp.Length > 0 ? (Quality?.Score?.Message ?? "已取得出口 IP，但评分服务暂未返回有效分数。") + " 请等待后台检测或使用一键检测；达到 85 分后才可登录。"
        : State == "未检测" ? "后台正在检测；取得 85 分以上的结果后可登录。"
        : "本次连接或出口检测未通过，所以还没有分数。可在节点页面一键重试。";
    public string Summary => State + (DelayMs is { } ms ? $" · {ms} ms" : "") + (QualityScore is { } score ? $" · 质量 {score} · {NetworkTag} · " + (Eligible ? "达标" : "不可用") : " · 质量未知");
    public bool ResidentialName => NodeScreeningRules.IsResidentialName(Name);
    public void Apply(NodeProbeResult value)
    {
        var quality = value.Quality is { } report ? NodeScreeningRules.ApplyAdmission(report, Node, subscriptionName) : null;
        Quality = quality;
        ExitIp = quality?.Ip ?? "";
        DelayMs = value.Latency?.Milliseconds; RiskScore = quality?.Score?.Value;
        QualityScore = quality?.Admission.QualityScore; Eligible = value.Success && !value.DelayOnly
            && value.Latency?.State is not (ProxyLatencyState.Timeout or ProxyLatencyState.Failed or ProxyLatencyState.Cancelled)
            && quality?.Admission.Allowed == true; NetworkGroup = quality?.NetworkGroup ?? "待分类";
        State = value.Latency?.State switch { ProxyLatencyState.Success => "可连通", ProxyLatencyState.Timeout => "超时", ProxyLatencyState.Cancelled => "已取消", ProxyLatencyState.Failed => "失败", _ => value.Success ? "可连通" : "失败" };
        var delay = DelayMs is { } ms ? $"HTTPS 延迟：{ms} ms" : "HTTPS 延迟：" + (value.Latency is null ? "未知" : State);
        var decision = quality?.Admission.Message ?? "IP 质量分未知，尚未取得有效评分。";
        
        result = value.DelayOnly ? delay + "\n" + value.Message + "\n\n使用一键检测查询出口和质量分。"
            : $"{decision}\n出口 IP：{Text(quality?.Ip)}\n订阅：{Source}\n节点：{Name}\n类型：{NetworkTag}\n国家：{Country}\n城市：{Text(quality?.City)}\n评分来源：{quality?.Score?.Provider ?? "Net.Coffee"}\n{delay}\n"
                + (quality is { HasVerifiedExit: true } ? $"运营商：{Text(quality.Company)}\nASN：{Text(quality.Asn)}\n时区：{Text(quality.Timezone)}\n数据中心：{Flag(quality.IsDatacenter)} · 代理：{Flag(quality.IsProxy)}\nVPN：{Flag(quality.IsVpn)} · Tor：{Flag(quality.IsTor)} · 滥用：{Flag(quality.IsAbuser)}\n来源：{quality.Source} · {quality.FetchedAt.ToLocalTime():HH:mm:ss}\n{quality.Message}" : "IP 体检未完成：" + value.Message)
                + "\n\n" + (quality?.Score?.Message ?? "未完成风险评分。") + "\nHTTPS 延迟含首次连接与 TLS，不是 ICMP ping。";
        foreach (var name in new[] { nameof(Result), nameof(Quality), nameof(State), nameof(DelayMs), nameof(RiskScore), nameof(QualityScore), nameof(Eligible), nameof(CanPrepareLogin), nameof(NetworkGroup), nameof(NetworkTag), nameof(Summary), nameof(ExitIp), nameof(Country), nameof(ChoiceLabel), nameof(ScoreStatus), nameof(ChoiceExplanation) }) Raise(name);
        changed();
    }
    private static string Flag(bool? value) => value is null ? "未知" : value.Value ? "是" : "否";
    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "未知" : value;
}

public sealed class NodeTestingViewModel : ObservableObject
{
    private readonly DesktopNodeServices? services;
    private readonly Func<(ProxyClientKind Client, string Root)> source;
    private CancellationTokenSource? cancellation;
    private bool open, busy, loadingItems, initialized, automatic, stopping, cleanupUnconfirmed;
    private string status = "后台会自动读取全部订阅并检测。";
    private string sourceLabel = "尚未读取", issues = "";
    private int subscriptionCount;
    private NodeSelectionItem? selectedNode;
    public NodeTestingViewModel(DesktopNodeServices? services, Func<(ProxyClientKind Client, string Root)> source)
    {
        this.services = services; this.source = source;
        OpenCommand = new(_ => { open = true; Raise(nameof(IsOpen)); if (!initialized && !busy && !stopping) CurrentOperation = ReloadAsync(); }, _ => services is not null);
        CloseCommand = new(_ => { open = false; Raise(nameof(IsOpen)); });
        LoadCommand = new(_ => CurrentOperation = ReloadAsync(), _ => !busy && !stopping && services is not null);
        SupplementCommand = new(_ => CurrentOperation = ReloadAsync(supplement: true), _ => !busy && !stopping && services is not null);
        ScreenCommand = new(_ => CurrentOperation = ScreenAsync(), _ => !busy && !stopping && services is not null && Items.Any(item => item.Supported));
        CancelCommand = new(_ => { cancellation?.Cancel(); status = "正在取消，已完成的结果会保留…"; Raise(nameof(Status)); }, _ => busy && cancellation is not null);
        Items.CollectionChanged += (_, _) => { if (!loadingItems) RefreshResults(); };
    }
    public ObservableCollection<NodeSelectionItem> Items { get; } = [];
    public ObservableCollection<NodeSelectionItem> Results { get; } = [];
    public ObservableCollection<NodeSelectionItem> LoginChoices { get; } = [];
    public bool IsLoginChoicesEmpty => LoginChoices.Count == 0;
    public int SubscriptionCount => subscriptionCount;
    public NodeSelectionItem? SelectedNode { get => selectedNode; set { if (value is not null || selectedNode is null) Set(ref selectedNode, value); } }
    public bool IsOpen => open;
    public bool IsBusy => busy;
    public bool HasBackgroundTask => initialized || busy;
    public string BackgroundTitle => "IP 筛选 · " + (busy ? "进行中" : "查看结果");
    public bool IsScreening => cancellation is not null;
    public bool CanEdit => !busy && !stopping;
    public bool IsFilteredEmpty => Results.Count == 0;
    public string EmptyMessage => IsScreening ? "正在检测，达标 IP 会自动出现在这里。" : busy ? "正在读取全部订阅…" : Items.Any(item => item.State != "未检测")
        ? "暂时没有符合准入规则的可用 IP。更新订阅后可点「补测新增与失败」；需要复查全部线路时点「重新检测全部」。" : "正在准备后台检测，达标 IP 会自动去重并显示类型标签。";
    public string Status => status;
    public string SourceLabel => sourceLabel;
    public string Issues => issues;
    public bool HasIssues => issues.Length > 0;
    public string CountLabel => $"{SubscriptionCount} 个订阅 · 全部 {Items.Count} 个节点 · 保留 {Results.Count} 个 IP";
    public string ResultSummary
    {
        get
        {
            var nodes = Items.ToArray();
            var duplicates = nodes.Count(item => item.Eligible) - Results.Count;
            var excluded = nodes.Count(item => item.State != "未检测" && !item.Eligible);
            return $"≥85 或 IPEqual 住宅 · 相同出口已合并 {Math.Max(0, duplicates)} 条 · 排除不通或未达标 {excluded} 条";
        }
    }
    public string ModeDescription => "补测新增与失败：读取最新的本地订阅，跳过已完成评分的节点（包括低分），重试新增、变更、超时和未取得分数的节点。先测 Clash 连通性，不通就跳过 IP 与评分查询。重新检测全部：重新确认全部候选线路的连通性与出口。同一 IP 的成功评分长期复用；家宽/机房按 Net.Coffee 属性显示标签。IPEqual 订阅名称下带“住宅”的节点免评分门槛，分数照常显示。历史结果不代表持续在线，登录时仍核对实际出口。";
    public Task CurrentOperation { get; private set; } = Task.CompletedTask;
    public async Task<NodeSelectionItem> ChooseIndependentAsync(string excludeIp = "", bool random = false)
    {
        if (!initialized && !busy) await LoadAsync(); // Read cached results only; never start a rescan here.
        var choices = LoginChoices.Where(item => item.CanPrepareLogin && ProfileRules.ValidTimezone(item.Quality?.Timezone ?? "") && item.ExitIp != excludeIp)
            .OrderByDescending(item => item.QualityScore).ThenBy(item => item.DelayMs ?? long.MaxValue).ToArray();
        if (choices.Length == 0) throw new InvalidOperationException(excludeIp.Length > 0
            ? "暂时没有其他已达标的 IP，请先在网络与 IP 中筛选；当前 IP 保留。"
            : "暂时没有已检测达标的独立 IP，请先在网络与 IP 中筛选；当前模式保留。");
        return choices[random ? Random.Shared.Next(choices.Length) : 0];
    }
    public Task StartBackgroundAsync()
    {
        if (automatic || services is null || stopping) return CurrentOperation;
        automatic = true;
        return CurrentOperation = StartAfterCurrentAsync(CurrentOperation);
    }
    private async Task StartAfterCurrentAsync(Task current)
    {
        await current;
        if (!stopping) await ReloadAsync(supplement: true);
    }
    private async Task ReloadAsync(bool supplement = false)
    {
        await LoadAsync();
        if ((automatic || supplement) && !stopping) await ScreenAsync(supplement);
    }
    public async Task StopAsync()
    {
        stopping = true; cancellation?.Cancel();
        Raise(nameof(CanEdit)); RefreshCommands();
        await CurrentOperation;
        if (cleanupUnconfirmed) throw new System.IO.IOException("IP 筛选核心退出或清理尚未确认，不能确认安全退出。请保留本窗口与相关运行资料。");
    }
    internal void ResumeAfterStop()
    {
        stopping = false; Raise(nameof(CanEdit)); RefreshCommands();
    }
    public DelegateCommand OpenCommand { get; }
    public DelegateCommand CloseCommand { get; }
    public DelegateCommand LoadCommand { get; }
    public DelegateCommand SupplementCommand { get; }
    public DelegateCommand ScreenCommand { get; }
    public DelegateCommand CancelCommand { get; }
    internal void RefreshResults()
    {
        if (loadingItems) return;
        var eligible = Items.Where(item => item.CanPrepareLogin && item.ExitIp.Length > 0).ToArray();
        var desired = eligible
            .GroupBy(item => System.Net.IPAddress.TryParse(item.ExitIp, out var ip) ? ip.ToString() : item.ExitIp)
            .Select(group => group.OrderBy(item => item.DelayMs ?? long.MaxValue).ThenBy(item => item.Name, StringComparer.Ordinal).First())
            .OrderByDescending(item => item.QualityScore).ThenBy(item => item.DelayMs ?? long.MaxValue).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
        {
            for (var i = Results.Count - 1; i >= 0; i--) if (!desired.Contains(Results[i])) Results.RemoveAt(i);
            for (var i = 0; i < desired.Length; i++)
            {
                var index = Results.IndexOf(desired[i]);
                if (index < 0) Results.Insert(i, desired[i]); else if (index != i) Results.Move(index, i);
            }
        }
        if (selectedNode is null || !Results.Contains(selectedNode))
        { selectedNode = Results.FirstOrDefault(); Raise(nameof(SelectedNode)); }
        var preferred = eligible.Where(item => item.IsPreferredSubscription)
            .GroupBy(item => item.SubscriptionId.Length > 0 ? item.SubscriptionId : item.Source)
            .Select(group => group.OrderByDescending(item => item.Eligible)
                .ThenByDescending(item => item.QualityScore ?? -1)
                .ThenByDescending(item => item.ExitIp.Length > 0)
                .ThenBy(item => item.DelayMs ?? long.MaxValue).First());
        var choices = preferred.Concat(Results.Where(item => !item.IsPreferredSubscription)).Distinct().ToArray();
        for (var i = LoginChoices.Count - 1; i >= 0; i--) if (!choices.Contains(LoginChoices[i])) LoginChoices.RemoveAt(i);
        for (var i = 0; i < choices.Length; i++)
        {
            var index = LoginChoices.IndexOf(choices[i]);
            if (index < 0) LoginChoices.Insert(i, choices[i]); else if (index != i) LoginChoices.Move(index, i);
        }
        Raise(nameof(IsLoginChoicesEmpty));
        foreach (var name in new[] { nameof(CountLabel), nameof(ResultSummary), nameof(IsFilteredEmpty), nameof(EmptyMessage) }) Raise(name);
        RefreshCommands();
    }
    private async Task LoadAsync()
    {
        SetBusy(true);
        status = "正在读取全部订阅…"; Raise(nameof(Status));
        try
        {
            var current = source();
            var catalog = await Task.Run(() =>
            {
                var sources = services!.Subscriptions(current.Client, current.Root);
                return (Sources: sources, Nodes: sources.Subscriptions.Select(subscription => (Subscription: subscription, Catalog: services.LoadSubscription(subscription, current.Root))).ToArray());
            });
            loadingItems = true;
            try
            {
                Items.Clear(); subscriptionCount = catalog.Sources.Subscriptions.Count; selectedNode = null;
                foreach (var entry in catalog.Nodes)
                {
                    foreach (var node in entry.Catalog.Nodes)
                    {
                        var item = new NodeSelectionItem(node, RefreshResults, entry.Subscription.Id, entry.Subscription.Name);
                        if (services!.History.Find(node.Id) is { } previous) item.Apply(previous);
                        Items.Add(item);
                    }
                }
            }
            finally { loadingItems = false; }
            sourceLabel = current.Client + " · 所有已保存订阅";
            issues = string.Join("\n", catalog.Sources.Issues.Concat(catalog.Nodes.SelectMany(entry => entry.Catalog.Issues.Select(issue => new NodeImportIssue(entry.Subscription.Name, issue.Reason)))).Select(issue => issue.Source + "：" + issue.Reason));
            status = Items.Count > 0 ? "已读取全部订阅，准备检测。" : "没有可读取节点；请在 FlClash 更新订阅后重新检测。";
            foreach (var name in new[] { nameof(SubscriptionCount), nameof(SelectedNode), nameof(SourceLabel), nameof(Issues), nameof(HasIssues), nameof(Status) }) Raise(name);
            RefreshResults();
            initialized = true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or FAswitch.Core.StorageException or ProxyException)
        { Items.Clear(); subscriptionCount = 0; selectedNode = null; Raise(nameof(SelectedNode)); Raise(nameof(SubscriptionCount)); status = ErrorRedaction.Scrub(error.Message); Raise(nameof(Status)); RefreshResults(); }
        finally { SetBusy(false); }
    }
    private async Task ScreenAsync(bool supplement = false)
    {
        var candidates = Items.Where(item => item.IsAccountCandidate).DistinctBy(item => item.Node.Id).ToArray();
        var selected = candidates.Where(item => !supplement || !NodeProbeHistory.IsComplete(services!.History.Find(item.Node.Id))).OrderByDescending(item => item.ResidentialName).ToArray();
        var skipped = candidates.Length - selected.Length;
        var label = supplement ? "补测" : "筛选";
        if (selected.Length == 0) { status = $"无需补测 · 已跳过 {skipped} 个已完成节点。"; Raise(nameof(Status)); return; }
        // Previous completed results remain readable while this explicitly requested rescan runs.
        var completed = false; var done = 0;
        var cancellationSource = new CancellationTokenSource(); cancellation = cancellationSource; SetBusy(true);
        status = $"正在{label} 0/{selected.Length} · 跳过已完成 {skipped} 个"; Raise(nameof(Status));
        try
        {
            var progress = new Progress<NodeProbeProgress>(entry =>
            {
                if (completed) return;
                done = Math.Max(done, entry.Completed);
                status = $"正在{label} {done}/{entry.Total} · 跳过 {skipped} 个 · 已保留 {Results.Count} 个 IP"; Raise(nameof(Status));
                if (entry.Result is { } result) foreach (var item in Items.Where(item => item.Node.Id == result.Id)) item.Apply(result);
            });
            var report = await Task.Run(() => services!.CreateBatch().ProbeAsync(selected.Select(item => item.Node).ToArray(), progress, cancellationSource.Token));
            completed = true;
            cleanupUnconfirmed |= report.StopUnconfirmed;
            foreach (var result in report.Results) foreach (var item in Items.Where(item => item.Node.Id == result.Id)) item.Apply(result);
            status = (report.StopUnconfirmed ? "检测中断" : report.Cancelled ? "已取消" : label + "完成") + $" · 本次完成 {report.Results.Count}/{selected.Length} · 跳过 {skipped} 个 · 保留 {Results.Count} 个不同 IP";
            if (report.StopUnconfirmed) status += "\n" + report.Message;
            Raise(nameof(Status));
        }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or OperationCanceledException or MihomoProbeRuntimeException)
        { cleanupUnconfirmed |= error is MihomoProbeRuntimeException { StopUnconfirmed: true }; status = error is OperationCanceledException ? "已取消，已完成的结果保留。" : ErrorRedaction.Scrub(error.Message); Raise(nameof(Status)); }
        finally { completed = true; cancellationSource.Dispose(); cancellation = null; SetBusy(false); }
    }
    private void SetBusy(bool value) { busy = value; Raise(nameof(IsBusy)); Raise(nameof(HasBackgroundTask)); Raise(nameof(BackgroundTitle)); Raise(nameof(IsScreening)); Raise(nameof(CanEdit)); Raise(nameof(EmptyMessage)); RefreshCommands(); }
    private void RefreshCommands() { foreach (var command in new[] { OpenCommand, CloseCommand, LoadCommand, SupplementCommand, ScreenCommand, CancelCommand }) command.Refresh(); }
}
