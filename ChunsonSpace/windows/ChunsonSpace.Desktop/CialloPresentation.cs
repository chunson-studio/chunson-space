using System.IO;
using ChunsonSpace.Accounts;

namespace ChunsonSpace.Desktop;

public sealed class CialloRow(string model, string effort = ModelTestParameters.DefaultEffort) : ObservableObject
{
    public string Model { get; } = model;
    private string state = "待测试", text = "", detail = "思考程度：" + ModelTestParameters.EffortLabel(effort);
    public string State => state;
    public string DisplayState => Finished && safetySummary.Length > 0 ? "" : state;
    public string Text => text;
    public string Detail => detail;
    private SafetyBufferingObservation? safetyBuffering;
    private string safetySummary = "";
    public string SafetyBufferingSummary => safetySummary;
    public string SafetyBufferingDetails => SafetyBufferingDisplay.Details(safetyBuffering);
    public bool Finished { get; private set; }
    public bool Failed { get; private set; }
    public void Update(CialloReply reply)
    {
        Finished = reply.State != "running";
        safetyBuffering = reply.SafetyBuffering;
        safetySummary = reply.State == "running" ? "" : AccountEvidence.Summary(new(reply.RequestedModel, reply.ResponseModel, reply.Effort, reply.State == "completed", safetyBuffering));
        Raise(nameof(SafetyBufferingSummary)); Raise(nameof(SafetyBufferingDetails));
        Failed = reply.State is not ("running" or "completed" or "cancelled");
        state = reply.State switch { "running" => "正在回复…", "completed" => "已完成", "cancelled" => "已取消", _ => "未完成" };
        text = reply.Text; detail = $"思考：{ModelTestParameters.EffortLabel(reply.Effort)} · {reply.Elapsed.TotalSeconds:N1} 秒 · 缓存用量：{reply.Usage?.CachedInputTokens?.ToString("N0") ?? "未返回"}"
            + (reply.ResponseModel.Length > 0 ? " · 响应声明：" + reply.ResponseModel : "");
        Raise(nameof(State)); Raise(nameof(DisplayState)); Raise(nameof(Text)); Raise(nameof(Detail));
    }
    internal void CancelBeforeRequest()
        => SkipBeforeRequest("本轮已取消，此模型未发送请求。");
    internal void SkipBeforeRequest(string reason)
    {
        if (Finished) return;
        Finished = true;
        state = "未请求";
        safetySummary = "";
        Raise(nameof(SafetyBufferingSummary));
        text = reason;
        Raise(nameof(State)); Raise(nameof(DisplayState)); Raise(nameof(Text)); Raise(nameof(Finished));
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
    private string model = "全部模型", effort = ModelTestParameters.DefaultEffort;
    private string? diagnosticDirectory;
    public IReadOnlyList<string> Models => new[] { "全部模型" }.Concat(CialloTest.Models).ToArray();
    public IReadOnlyList<string> Efforts => ModelTestParameters.Efforts;
    public string Model { get => model; set { if (!busy && Set(ref model, (value == "全部模型" || ModelTestParameters.AllowedModel(value)) ? value : "全部模型")) Refresh(); } }
    public string Effort { get => effort; set { if (!busy && value == ModelTestParameters.DefaultEffort && Set(ref effort, value)) Refresh(); } }
    private IReadOnlyList<string> RequestedModels => model == "全部模型" ? CialloTest.Models : new[] { model };
    public string? DiagnosticDirectory => diagnosticDirectory;
    public bool CanExportDiagnostics => !busy && TestDiagnosticsExport.HasData(diagnosticDirectory);
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
    public string Status => status.Any(c => c is >= '一' and <= '鿿') ? status : "本次没判断成功";
    public string ErrorReport => SupportReport.Create("Ciallo 测试", $"调用方式：通用请求 / {effort}\n状态：" + status + "\n" + ProgressSummary + "\n" + string.Join("\n", rows.Where(r => r.Failed).Select(r => $"模型：{r.Model}\n{r.Detail}\n错误：{r.Text}")), reportStarted, profile);
    private bool Frozen => profile is not null && services?.Repository.Profiles.AreTestsFrozen(profile) == true;
    private bool CanStart => !busy && queue.Accepting && services is not null && profile is not null && !Frozen && (model == "全部模型" || ModelTestParameters.AllowedModel(model)) && effort == ModelTestParameters.DefaultEffort;
    public string ProgressSummary => Frozen ? "账号已冻结静置，不能发起测试；可查看历史结果。" : elapsed.Started ? $"已结束 {rows.Count(r => r.Finished)}/{rows.Count} 个模型 · {elapsed.Text}" : "按模型逐项测试；开始后显示实时用时";
    public bool CanEdit => !busy;
    public bool IsBusy => busy;
    public DelegateCommand StartCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DelegateCommand CloseCommand { get; }
    public Task CurrentOperation { get; private set; } = Task.CompletedTask;
    internal void ReloadHistoryAfterClear()
    {
        if (busy) return;
        history = []; selectedHistory = null; diagnosticDirectory = null;
        rows = RequestedModels.Select(model => new CialloRow(model, effort)).ToArray();
        try { LoadHistory(true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ChunsonSpace.Core.StorageException)
        { status = "历史记录暂时无法读取。"; }
        foreach (var name in new[] { nameof(History), nameof(SelectedHistory), nameof(HistoryHint), nameof(CanSelectHistory), nameof(Rows), nameof(Status) }) Raise(name);
        Refresh();
    }
    private void LoadHistory(bool showLatest)
    {
        if (showLatest) { elapsed.Reset(); reportStarted = null; }
        history = profile is null || services is null ? [] : services.Accounts.CialloHistory(profile);
        // An attempt cancelled while queued has no saved result. Do not select an
        // unrelated previous entry, which would make choosing it again a no-op.
        selectedHistory = showLatest ? history.FirstOrDefault()
            : history.FirstOrDefault(entry => diagnosticDirectory is not null && entry.Result.DiagnosticDirectory == diagnosticDirectory);
        if (showLatest)
        {
            if (selectedHistory is not null) ShowHistory(selectedHistory);
            else { diagnosticDirectory = null; rows = RequestedModels.Select(model => new CialloRow(model, effort)).ToArray(); status = "暂无保存的 Ciallo 测试；开始后会使用账号额度。"; Raise(nameof(Rows)); Raise(nameof(Status)); Refresh(); }
        }
        foreach (var name in new[] { nameof(History), nameof(SelectedHistory), nameof(HistoryHint), nameof(CanSelectHistory) }) Raise(name);
        Raise(nameof(ErrorReport));
    }
    private void ShowHistory(CialloHistoryEntry entry)
    {
        elapsed.Reset();
        reportStarted = entry.Started; diagnosticDirectory = entry.Result.DiagnosticDirectory;
        effort = ModelTestParameters.DefaultEffort;
        model = entry.Result.Replies.Count == 1 && ModelTestParameters.AllowedModel(entry.Result.Replies[0].RequestedModel) ? entry.Result.Replies[0].RequestedModel : "全部模型";
        rows = entry.Result.Replies.Select(reply => { var row = new CialloRow(reply.RequestedModel); row.Update(reply); return row; }).ToArray();
        status = $"历史记录 · {entry.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {entry.Result.Completed}/{entry.Result.Replies.Count} 个模型完整回复；未发送请求。";
        Raise(nameof(Rows)); Raise(nameof(Status));
        Refresh();
    }
    private async Task RunAsync()
    {
        if (!CanStart) return;
        reportStarted = DateTimeOffset.Now; diagnosticDirectory = null;
        selectedHistory = null; Raise(nameof(SelectedHistory));
        var requestedModel = model == "全部模型" ? null : model; var requestedEffort = effort;
        busy = true; shellBusy(true); Raise(nameof(CanEdit)); Refresh();
        using var cancel = new CancellationTokenSource(); cancellation = cancel;
        rows = RequestedModels.Select(model => new CialloRow(model, effort)).ToArray(); Raise(nameof(Rows));
        elapsed.Start();
        try
        {
            var progress = new Progress<CialloReply>(reply => { if (cancellation == cancel) { Rows.Single(row => row.Model == reply.RequestedModel).Update(reply); status = reply.RequestedModel + (reply.State == "running" ? " 正在回复…" : " 已返回结果"); Raise(nameof(Status)); Raise(nameof(ProgressSummary)); } });
            status = queue.IsBusy(profile) ? "等待此账号的上一项测试结束…" : "测试进行中…"; Raise(nameof(Status));
            var result = await queue.RunAsync(profile!, () => services!.Accounts.TestCialloAsync(profile!, progress, cancel.Token, requestedEffort, requestedModel), cancel.Token);
            diagnosticDirectory = result.DiagnosticDirectory;
            foreach (var reply in result.Replies) Rows.Single(row => row.Model == reply.RequestedModel).Update(reply);
            if (result.Cancelled) foreach (var row in rows) row.CancelBeforeRequest();
            status = result.Cancelled ? "本次没判断成功" : "测试结束";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            foreach (var row in rows) row.CancelBeforeRequest();
            status = "已取消，未开始的测试不会发送请求。";
        }
        catch (Exception error) when (error is AccountException or IOException or ChunsonSpace.Core.StorageException)
        { status = "本次没判断成功"; }
        finally { cancellation = null; busy = false; elapsed.Stop(); shellBusy(false); LoadHistory(false); Raise(nameof(CanEdit)); Raise(nameof(Status)); Refresh(); }
    }
    private void Refresh() { Raise(nameof(Model)); Raise(nameof(Effort)); Raise(nameof(DiagnosticDirectory)); Raise(nameof(CanExportDiagnostics)); Raise(nameof(ErrorReport)); Raise(nameof(IsBusy)); Raise(nameof(CanSelectHistory)); StartCommand.Refresh(); CancelCommand.Refresh(); CloseCommand.Refresh(); }
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
        HideAccountHealth();
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
