using System.IO;
using FAswitch.Accounts;

namespace FAswitch.Desktop;

public sealed class CialloRow(string model) : ObservableObject
{
    public string Model { get; } = model;
    private string state = "待测试", text = "", detail = "思考程度：high";
    public string State => state;
    public string Text => text;
    public string Detail => detail;
    public bool Finished { get; private set; }
    public bool Failed { get; private set; }
    public void Update(CialloReply reply)
    {
        Finished = reply.State != "running";
        Failed = reply.State is not ("running" or "completed" or "cancelled");
        state = reply.State switch { "running" => "正在回复…", "completed" => "已完成", "cancelled" => "已取消", _ => "未完成" };
        text = reply.Text; detail = $"high · {reply.Elapsed.TotalSeconds:N1} 秒 · 缓存 token：{reply.Usage?.CachedInputTokens?.ToString("N0") ?? "未返回"}"
            + (reply.ResponseModel.Length > 0 ? " · 响应声明：" + reply.ResponseModel : "");
        Raise(nameof(State)); Raise(nameof(Text)); Raise(nameof(Detail));
    }
}
public sealed class CialloPanelViewModel : ObservableObject
{
    private readonly DesktopAccountServices? services;
    private readonly Action<bool> shellBusy;
    private readonly AccountTestQueue queue;
    private readonly TestElapsedClock elapsed;
    private string? profile; private bool busy; private string status = "给每个模型发送同一句 ciallo；原始回复逐条显示。会使用所选账号额度。";
    private CancellationTokenSource? cancellation;
    private IReadOnlyList<CialloRow> rows = CialloTest.Models.Select(model => new CialloRow(model)).ToArray();
    private IReadOnlyList<CialloHistoryEntry> history = [];
    private CialloHistoryEntry? selectedHistory;
    private DateTimeOffset? reportStarted;
    public CialloPanelViewModel(DesktopAccountServices? services, IReadOnlyList<string> profiles, string? selected, Action<bool> shellBusy, Action close, AccountTestQueue? queue = null)
    {
        this.services = services; this.shellBusy = shellBusy; this.queue = queue ?? new(); Profiles = profiles; profile = profiles.Contains(selected) ? selected : profiles.FirstOrDefault();
        elapsed = new(() => Raise(nameof(ProgressSummary)));
        StartCommand = new(_ => CurrentOperation = RunAsync(), _ => CanStart);
        CancelCommand = new(_ => cancellation?.Cancel(), _ => busy);
        CloseCommand = new(_ => close());
        this.queue.Changed += Refresh;
        LoadHistory(true);
    }
    public IReadOnlyList<string> Profiles { get; }
    public string? SelectedProfile { get => profile; set { if (!busy && value is not null && Profiles.Contains(value) && Set(ref profile, value)) { LoadHistory(true); StartCommand.Refresh(); } } }
    public IReadOnlyList<CialloHistoryEntry> History => history;
    public string HistoryHint => history.Count == 0 ? "暂无历史记录；新测试将自动保存" : $"{history.Count} 条记录 · 选择日期查看，不会重新测试";
    public bool CanSelectHistory => !busy && history.Count > 0;
    public CialloHistoryEntry? SelectedHistory
    {
        get => selectedHistory;
        set { if (!busy && value is not null && history.Contains(value) && Set(ref selectedHistory, value)) ShowHistory(value); }
    }
    public IReadOnlyList<CialloRow> Rows => rows;
    public string Status => status;
    public string ErrorReport => SupportReport.Create("Ciallo 测试", "调用方式：通用请求 / high\n状态：" + status + "\n" + ProgressSummary + "\n" + string.Join("\n", rows.Where(r => r.Failed).Select(r => $"模型：{r.Model}\n{r.Detail}\n错误：{r.Text}")), reportStarted, profile);
    private bool Frozen => profile is not null && services?.Repository.Profiles.AreTestsFrozen(profile) == true;
    private bool CanStart => !busy && queue.Accepting && services is not null && profile is not null && !Frozen;
    public string ProgressSummary => Frozen ? "账号已冻结静置，不能发起测试；可查看历史结果。" : elapsed.Started ? $"已结束 {rows.Count(r => r.Finished)}/{CialloTest.Models.Count} 个模型 · {elapsed.Text}" : "按模型逐项测试；开始后显示实时用时";
    public bool CanEdit => !busy;
    public bool IsBusy => busy;
    public DelegateCommand StartCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DelegateCommand CloseCommand { get; }
    public Task CurrentOperation { get; private set; } = Task.CompletedTask;
    private void LoadHistory(bool showLatest)
    {
        if (showLatest) { elapsed.Reset(); reportStarted = null; }
        history = profile is null || services is null ? [] : services.Accounts.CialloHistory(profile);
        selectedHistory = history.FirstOrDefault();
        if (showLatest)
        {
            if (selectedHistory is not null) ShowHistory(selectedHistory);
            else { rows = CialloTest.Models.Select(model => new CialloRow(model)).ToArray(); status = "暂无保存的 Ciallo 测试；开始后会使用账号额度。"; Raise(nameof(Rows)); Raise(nameof(Status)); }
        }
        foreach (var name in new[] { nameof(History), nameof(SelectedHistory), nameof(HistoryHint), nameof(CanSelectHistory) }) Raise(name);
        Raise(nameof(ErrorReport));
    }
    private void ShowHistory(CialloHistoryEntry entry)
    {
        elapsed.Reset();
        reportStarted = entry.Started;
        rows = entry.Result.Replies.Select(reply => { var row = new CialloRow(reply.RequestedModel); row.Update(reply); return row; }).ToArray();
        status = $"历史记录 · {entry.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {entry.Result.Completed}/{entry.Result.Replies.Count} 个模型完整回复；未发送请求。";
        Raise(nameof(Rows)); Raise(nameof(Status));
        Raise(nameof(ErrorReport));
    }
    private async Task RunAsync()
    {
        if (!CanStart) return;
        reportStarted = DateTimeOffset.Now;
        busy = true; shellBusy(true); Raise(nameof(CanEdit)); Refresh();
        using var cancel = new CancellationTokenSource(); cancellation = cancel;
        rows = CialloTest.Models.Select(model => new CialloRow(model)).ToArray(); Raise(nameof(Rows));
        elapsed.Start();
        try
        {
            var progress = new Progress<CialloReply>(reply => { if (cancellation == cancel) { Rows.Single(row => row.Model == reply.RequestedModel).Update(reply); status = reply.RequestedModel + (reply.State == "running" ? " 正在回复…" : " 已返回结果"); Raise(nameof(Status)); Raise(nameof(ProgressSummary)); } });
            status = queue.IsBusy(profile) ? "等待此账号的上一项测试结束…" : "测试进行中…"; Raise(nameof(Status));
            var result = await queue.RunAsync(profile!, () => services!.Accounts.TestCialloAsync(profile!, progress, cancel.Token), cancel.Token);
            foreach (var reply in result.Replies) Rows.Single(row => row.Model == reply.RequestedModel).Update(reply);
            status = (result.Cancelled ? "已取消" : "测试结束") + $" · {result.Completed}/{CialloTest.Models.Count} 个模型完整回复。未完成的模型不会改用其他模型代答。";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { status = "已取消，未开始的测试不会发送请求。"; }
        catch (Exception error) when (error is AccountException or IOException or FAswitch.Core.StorageException)
        { status = ErrorRedaction.Scrub(error.Message); }
        finally { cancellation = null; busy = false; elapsed.Stop(); shellBusy(false); LoadHistory(false); Raise(nameof(CanEdit)); Raise(nameof(Status)); Refresh(); }
    }
    private void Refresh() { Raise(nameof(ErrorReport)); Raise(nameof(IsBusy)); Raise(nameof(CanSelectHistory)); StartCommand.Refresh(); CancelCommand.Refresh(); CloseCommand.Refresh(); }
    public void RefreshTestEligibility() { Raise(nameof(ProgressSummary)); Refresh(); }
}
public sealed partial class ShellViewModel
{
    private CialloPanelViewModel? ciallo;
    private CialloPanelViewModel? retainedCiallo;
    public CialloPanelViewModel? Ciallo => ciallo;
    public bool IsCialloOpen => ciallo is not null;
    public DelegateCommand OpenCialloCommand { get; private set; } = null!;
    private void InitializeCialloCommands()
    {
        OpenCialloCommand = new(_ => OpenCiallo(VisualTests?.SelectedProfile ?? (Selected?.Kind == "Account" ? Selected.Name : null)), _ => !operationBusy && !exitDraining);
    }
    private void OpenCiallo(string? profile)
    {
        if (operationBusy || exitDraining) return;
        retainedCiallo = cialloPanels.LastOrDefault(panel => panel.SelectedProfile == profile && (panel.IsBusy || panel.Profiles.SequenceEqual(ModelTestProfiles)));
        if (retainedCiallo is null || !retainedCiallo.Profiles.SequenceEqual(ModelTestProfiles) || retainedCiallo.SelectedProfile != profile)
        {
            retainedCiallo = new(workspace?.Accounts, ModelTestProfiles, profile, _ => RefreshBusyState(),
            () => { ciallo = null; Raise(nameof(Ciallo)); Raise(nameof(IsCialloOpen)); }, accountTests);
            cialloPanels.Add(retainedCiallo);
            retainedCiallo.PropertyChanged += BackgroundModelChanged;
        }
        lastBackgroundPanel = retainedCiallo;
        visualTests = null; Raise(nameof(VisualTests)); Raise(nameof(IsVisualTestsOpen));
        ciallo = retainedCiallo; RefreshBackgroundModel();
        Raise(nameof(Ciallo)); Raise(nameof(IsCialloOpen));
    }
}
