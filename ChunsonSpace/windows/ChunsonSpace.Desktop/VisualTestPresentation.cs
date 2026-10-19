using System.IO;
using ChunsonSpace.Accounts;

namespace ChunsonSpace.Desktop;

public sealed class VisualTestStepViewModel
{
    public VisualTestStepViewModel(VisualTestResult result, VisualTestStep step, int index, Action<string> report, Action<VisualTestResult, int?>? open = null)
    {
        Step = step;
        SafetyBufferingSummary = AccountEvidence.Summary(new(result.Model, step.ResponseModel, result.Effort, true, step.SafetyBuffering));
        try { Thumbnail = ArtworkFiles.Scoped(result, "step-" + (index + 1) + ".png"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or ChunsonSpace.Core.StorageException) { Thumbnail = null; }
        OpenCommand = new(_ =>
        {
            try
            {
                open?.Invoke(result, index);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { report("无法打开作品：" + e.Message); }
        }, _ => step.Artifact is not null);
    }
    public VisualTestStep Step { get; }
    public string SafetyBufferingSummary { get; }
    public string SafetyBufferingDetails => SafetyBufferingDisplay.Details(Step.SafetyBuffering);
    public string? Thumbnail { get; }
    public bool HasThumbnail => Thumbnail is not null;
    public string Usage => $"{Step.Seconds:N1} 秒 · 缓存用量：{Step.CachedTokens?.ToString() ?? "未返回"}";
    public DelegateCommand OpenCommand { get; }
}
public sealed class VisualTestBatchRow(string profile, Action<VisualTestResult> show, Func<bool> canShow) : ObservableObject
{
    private VisualTestResult? result;
    private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    public bool IsFinished { get; private set; }
    public string Profile { get; } = profile;
    public VisualTestResult? Result => result;
    public string State => result is null ? "等待测试" : result.Cancelled ? "已取消" : result.Completed ? "已完成" : IsFinished ? "未完成，已收到的内容已保存" : result.Message;
    public string Detail => $"{result?.Steps.Count ?? 0} 次回复 · 用时 {TestElapsedClock.Format(elapsed.Elapsed.TotalSeconds)}";
    public void Show() { if (result is not null && canShow()) show(result); }
    public void Update(VisualTestResult value) { result = value; Raise(nameof(State)); Raise(nameof(Detail)); Raise(nameof(Result)); }
    public void Finish(VisualTestResult value) { IsFinished = true; elapsed.Stop(); Update(value); }
    public void Tick() { if (!IsFinished) Raise(nameof(Detail)); }
}
public sealed record VisualTestHistoryRow(VisualTestResult Result)
{
    public string Label => $"{Result.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {VisualTestCatalog.Title(Result.Kind)} · {Result.Model} / 思考：{ModelTestParameters.EffortLabel(Result.Effort)} · {VisualTestCatalog.TransportLabel(Result.Version)} · {(Result.Completed ? "已完成" : Result.Cancelled ? "已取消" : "未完成")}";
}
public sealed class VisualTestPanelViewModel : ObservableObject
{
    private readonly DesktopAccountServices? services;
    private readonly Action<bool> shellBusy;
    private readonly AccountTestQueue queue;
    private readonly TestElapsedClock elapsed;
    private readonly Action<VisualTestResult, int?>? openArtwork;
    private string? profile;
    private string model = "gpt-6-astra";
    private string effort = ModelTestParameters.DefaultEffort;
    private string drawingContent = "";
    public string DrawingContent { get => drawingContent; set { if (!busy && Set(ref drawingContent, value)) Refresh(); } }
    private bool busy;
    private string status = "选择账号后开始测试，也可以查看已保存的结果。";
    private CancellationTokenSource? cancellation;
    private VisualTestResult? result;
    private IReadOnlyList<VisualTestBatchRow> batch = [];
    private IReadOnlyList<VisualTestBatchRow> runRows = [];
    private IReadOnlyList<VisualTestStepViewModel> steps = [];
    private IReadOnlyList<VisualTestHistoryRow> history = [];
    private VisualTestHistoryRow? selectedHistory;
    public VisualTestPanelViewModel(DesktopAccountServices? services, string? profile, VisualTestKind kind, Action<bool> shellBusy, Action close, IReadOnlyList<string> profiles, AccountTestQueue? queue = null, Action<VisualTestResult, int?>? openArtwork = null)
    {
        this.services = services; this.shellBusy = shellBusy; this.queue = queue ?? new(); Kind = VisualTestKind.Static; Profiles = profiles;
        this.openArtwork = openArtwork;
        elapsed = new(RefreshProgress);
        this.profile = profiles.Contains(profile) ? profile : profiles.FirstOrDefault();
        StartCommand = new(_ => CurrentOperation = RunAsync(false), _ => CanRun);
        StartAllCommand = new(_ => CurrentOperation = RunAsync(true), _ => CanRunAll);
        CancelCommand = new(_ => cancellation?.Cancel(), _ => busy);
        CloseCommand = new(_ => close());
        ShowBatchCommand = new(value => { if (!busy && value is VisualTestBatchRow row) row.Show(); }, _ => !busy);
        OpenGalleryCommand = new(_ =>
        {
            try { openArtwork?.Invoke(result!, null); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { SetStatus("无法打开作品对比：" + e.Message); }
        }, _ => result is not null && result.Steps.Count > 0);
        this.queue.Changed += Refresh;
        Load();
    }
    public VisualTestKind Kind { get; private set; }
    public string DurationHint => "静态 SVG · 内容由你输入，可随时取消。";
    public string Title => VisualTestCatalog.Title(Kind);
    public IReadOnlyList<string> Profiles { get; }
    public bool HasMultipleProfiles => Profiles.Count > 1;
    public string? SelectedProfile { get => profile; set { if (!busy && value is not null && Profiles.Contains(value) && Set(ref profile, value)) Load(); } }
    public string Model { get => model; set { if (!busy && Set(ref model, ModelTestParameters.AllowedModel(value) ? value : "gpt-6-astra")) Load(); } }
    public IReadOnlyList<string> Models => CialloTest.Models;
    public IReadOnlyList<string> Efforts => ModelTestParameters.Efforts;
    public string Effort { get => effort; set { if (!busy && value == ModelTestParameters.DefaultEffort && Set(ref effort, value)) Load(); } }
    public string? DiagnosticDirectory => result?.Directory;
    public bool CanExportDiagnostics => !busy && TestDiagnosticsExport.HasData(DiagnosticDirectory);
    public string Prompt => result?.Steps.FirstOrDefault()?.Prompt ?? DrawingContent;
    public bool IsBusy => busy;
    public bool CanEdit => !busy;
    private IReadOnlyList<string> EligibleProfiles => Profiles.Where(name => services is not null && !services.Repository.Profiles.AreTestsFrozen(name)).ToArray();
    private bool CanRunAll => !busy && !string.IsNullOrWhiteSpace(drawingContent) && drawingContent.Length <= 12000 && queue.Accepting && services is not null && EligibleProfiles.Count > 0 && ModelTestParameters.AllowedModel(model) && effort == ModelTestParameters.DefaultEffort;
    private bool CanRun => CanRunAll && profile is not null && EligibleProfiles.Contains(profile);
    public string RequestDescription => $"通用请求 · 思考：{ModelTestParameters.EffortLabel(effort)} · 不设请求总超时 · 每账号 {VisualTestCatalog.Requests(Kind)} 次请求；全部 {EligibleProfiles.Count * VisualTestCatalog.Requests(Kind)} 次。" + (Profiles.Count > EligibleProfiles.Count ? $" 冻结 {Profiles.Count - EligibleProfiles.Count} 个，自动跳过。" : "");
    public bool IsSelectedFrozen => profile is not null && services?.Repository.Profiles.AreTestsFrozen(profile) == true;
    public string Status => !busy && result is not null ? result.Completed ? "测试结束" : "本次没判断成功" : status;
    public string ErrorReport => SupportReport.Create(Title,
        $"模型：{result?.Model ?? model} / {result?.Effort ?? effort}\n调用方式：{VisualTestCatalog.TransportLabel(result?.Version ?? VisualTestCatalog.Version)}\n题目：{result?.Variant ?? drawingContent}\n状态：{Summary}\n{ProgressSummary}\n错误：{result?.Message ?? status}\n显示信息：{status}", result?.Started, profile);
    public string Summary => result is null ? (busy ? "测试准备中" : "尚无结果") : $"{(result.Completed ? "已完成" : result.Cancelled ? "已取消" : busy && runRows.Any(r => r.Profile == profile && !r.IsFinished) ? "进行中" : "未完成")} · {result.Steps.Count}/{VisualTestCatalog.Requests(Kind)} 次回复 · {result.Started.ToLocalTime():MM-dd HH:mm}";
    public int CompletedReplies => elapsed.Started ? runRows.Sum(r => r.Result?.Steps.Count ?? 0) : result?.Steps.Count ?? 0;
    public int ExpectedReplies => Math.Max(1, elapsed.Started ? runRows.Count : 1) * VisualTestCatalog.Requests(Kind);
    public string ProgressSummary => IsSelectedFrozen ? "账号已冻结静置，不能发起测试；可查看历史结果。" : elapsed.Started
        ? $"回复进度 {CompletedReplies}/{ExpectedReplies} · {elapsed.Text}" + (runRows.Count > 1 ? $" · 账号已结束 {runRows.Count(r => r.IsFinished)}/{runRows.Count}" : "")
        : result is null ? "开始后显示实时用时" : result.ElapsedSeconds is { } seconds ? "已记录用时 " + TestElapsedClock.Format(seconds) : $"回复累计 {result.Steps.Sum(s => s.Seconds):N1} 秒（旧记录无总用时）";
    public IReadOnlyList<VisualTestStepViewModel> Steps => steps;
    public IReadOnlyList<VisualTestBatchRow> BatchRows => batch;
    public bool HasBatchResults => batch.Count > 0;
    public IReadOnlyList<VisualTestHistoryRow> History => history;
    public string HistoryHint => history.Count == 0 ? "暂无历史记录" : $"{history.Count} 条记录 · 选择日期查看，不会重新测试";
    public bool CanSelectHistory => !busy && history.Count > 0;
    public VisualTestHistoryRow? SelectedHistory
    {
        get => selectedHistory;
        set
        {
            if (busy || value is null || !history.Contains(value) || !Set(ref selectedHistory, value)) return;
            var saved = value.Result;
            model = ModelTestParameters.AllowedModel(saved.Model) ? saved.Model : "gpt-6-astra"; effort = ModelTestParameters.DefaultEffort; Kind = VisualTestKind.Static;
            batch = []; runRows = []; elapsed.Reset(); Show(saved); SetStatus($"历史结果（{VisualTestCatalog.TransportLabel(saved.Version)}）· " + saved.Message);
        }
    }
    public VisualTestResult? Result => result;
    public Task CurrentOperation { get; private set; } = Task.CompletedTask;
    public bool HasFailedRequest => result?.FailedRequest is not null;
    public string FailedRequestSafetySummary => result?.FailedRequest is not null ? "本次没判断成功" : "";
    public string FailedRequestSafetyDetails => SafetyBufferingDisplay.Details(result?.FailedRequest?.SafetyBuffering);
    public DelegateCommand StartCommand { get; }
    public DelegateCommand StartAllCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DelegateCommand CloseCommand { get; }
    public DelegateCommand ShowBatchCommand { get; }
    public DelegateCommand OpenGalleryCommand { get; }
    internal void ReloadHistoryAfterClear()
    {
        if (busy) return;
        batch = []; runRows = []; result = null; steps = []; history = []; selectedHistory = null;
        elapsed.Reset();
        try { Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ChunsonSpace.Core.StorageException)
        { status = "历史记录暂时无法读取。"; }
        Refresh();
    }
    private void Load()
    {
        runRows = []; elapsed.Reset();
        result = profile is null || services is null ? null : services.VisualTests.Load(profile, model, Kind, drawingContent, effort);
        LoadHistory();
        steps = result?.Steps.Select((step, index) => new VisualTestStepViewModel(result, step, index, SetStatus, openArtwork)).ToArray() ?? [];
        status = result is null ? "当前配置暂无结果；可从历史记录选择，或点击测试。" : $"历史结果（{VisualTestCatalog.TransportLabel(result.Version)}）· " + result.Message;
        Refresh();
    }
    private void LoadHistory()
    {
        history = profile is null || services is null ? [] : services.VisualTests.History(profile).Where(r => r.Kind == VisualTestKind.Static).Select(r => new VisualTestHistoryRow(r)).ToArray();
        selectedHistory = history.FirstOrDefault(r => r.Result.Directory == result?.Directory);
    }
    private void Show(VisualTestResult value)
    {
        var changedProfile = profile != value.Profile;
        result = value; profile = value.Profile;
        if (changedProfile) LoadHistory();
        selectedHistory = history.FirstOrDefault(r => r.Result.Directory == value.Directory);
        steps = value.Steps.Select((step, index) => new VisualTestStepViewModel(value, step, index, SetStatus, openArtwork)).ToArray();
        status = value.Message; Refresh();
    }
    private void SetStatus(string value) { status = value; Raise(nameof(Status)); Raise(nameof(ErrorReport)); }
    private void RefreshProgress()
    {
        foreach (var row in runRows) row.Tick();
        Raise(nameof(ProgressSummary)); Raise(nameof(CompletedReplies)); Raise(nameof(ExpectedReplies));
    }
    private async Task RunAsync(bool all)
    {
        if (all ? !CanRunAll : !CanRun) return;
        var targets = all ? EligibleProfiles.ToArray() : [profile!];
        if (all && !targets.Contains(profile)) profile = targets[0];
        var requestedModel = model; var requestedVariant = drawingContent; var requestedEffort = effort;
        runRows = targets.Select(name => new VisualTestBatchRow(name, Show, () => !busy)).ToArray();
        batch = all ? runRows : [];
        elapsed.Start();
        busy = true; shellBusy(true); result = null; steps = []; Refresh();
        using var cancel = new CancellationTokenSource(); cancellation = cancel;
        SetStatus("测试进行中；账号之间并行，同一账号内串行。");
        try
        {
            var finished = new HashSet<string>(StringComparer.Ordinal);
            async Task RunAccount(string name)
            {
                var row = runRows.Single(r => r.Profile == name);
                var updates = new Progress<VisualTestResult>(value =>
                {
                    if (cancellation != cancel || !busy || finished.Contains(name)) return;
                    row.Update(value); RefreshProgress(); if (profile == name) Show(value);
                });
                try
                {
                    if (queue.IsBusy(name))
                    {
                        var waiting = new VisualTestResult(name, requestedModel, Kind, requestedVariant, VisualTestCatalog.Version, DateTimeOffset.UtcNow, "", [], false, false, "等待此账号的上一项测试结束…", Effort: requestedEffort);
                        row.Update(waiting); if (profile == name) Show(waiting);
                    }
                    var value = await queue.RunAsync(name, () => services!.VisualTests.RunAsync(name, requestedModel, Kind, requestedVariant, updates, cancel.Token, requestedEffort), cancel.Token);
                    finished.Add(name);
                    row.Finish(value); RefreshProgress(); if (profile == name) Show(value);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    finished.Add(name);
                    var value = new VisualTestResult(name, requestedModel, Kind, requestedVariant, VisualTestCatalog.Version, DateTimeOffset.UtcNow, "", [], false, true, "已取消，未开始的测试不会发送请求。", Effort: requestedEffort);
                    row.Finish(value); RefreshProgress(); if (profile == name) Show(value);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or AccountException or ChunsonSpace.Core.StorageException or InvalidOperationException or ArgumentException)
                {
                    finished.Add(name);
                    var value = new VisualTestResult(name, requestedModel, Kind, requestedVariant, VisualTestCatalog.Version, DateTimeOffset.UtcNow, "", [], false, false, ErrorRedaction.Scrub(e.Message), Effort: requestedEffort);
                    row.Finish(value); RefreshProgress(); if (profile == name) Show(value);
                }
            }
            await Task.WhenAll(targets.Select(RunAccount));
            if (all) SetStatus($"{(cancel.IsCancellationRequested ? "已取消" : "全部测试结束")} · 完成 {batch.Count(r => r.Result?.Completed == true)}/{targets.Length} 个账号。点击账号查看作品。");
        }
        finally { cancellation = null; busy = false; elapsed.Stop(); shellBusy(false); LoadHistory(); Refresh(); }
    }
    private void Refresh()
    {
        Raise(nameof(HasFailedRequest)); Raise(nameof(FailedRequestSafetySummary)); Raise(nameof(FailedRequestSafetyDetails));
        Raise(nameof(ErrorReport));
        RefreshProgress();
        foreach (var property in new[] { nameof(SelectedProfile), nameof(Steps), nameof(Result), nameof(Summary), nameof(Status), nameof(CanEdit), nameof(IsBusy), nameof(Prompt), nameof(BatchRows), nameof(HasBatchResults), nameof(History), nameof(SelectedHistory), nameof(HistoryHint), nameof(CanSelectHistory), nameof(Model), nameof(Effort), nameof(DiagnosticDirectory), nameof(CanExportDiagnostics), nameof(Title), nameof(RequestDescription), nameof(DurationHint) }) Raise(property);
        foreach (var command in new[] { StartCommand, StartAllCommand, CloseCommand, CancelCommand, ShowBatchCommand, OpenGalleryCommand }) command.Refresh();
    }
    public void RefreshTestEligibility() => Refresh();
}
