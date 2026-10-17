using System.IO;
using FAswitch.Accounts;

namespace FAswitch.Desktop;

public sealed class VisualTestStepViewModel
{
    public VisualTestStepViewModel(VisualTestResult result, VisualTestStep step, int index, Action<string> report)
    {
        Step = step;
        var shot = Path.Combine(result.Directory, "step-" + (index + 1) + ".png");
        Thumbnail = File.Exists(shot) ? shot : null;
        OpenCommand = new(_ =>
        {
            try
            {
                VisualTestViewer.Open(result, index);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { report("无法打开作品：" + e.Message); }
        }, _ => step.Artifact is not null && step.Preview is not null);
    }
    public VisualTestStep Step { get; }
    public string? Thumbnail { get; }
    public bool HasThumbnail => Thumbnail is not null;
    public string Usage => $"{Step.Seconds:N1} 秒 · 缓存 token：{Step.CachedTokens?.ToString() ?? "未返回"}";
    public DelegateCommand OpenCommand { get; }
}
public sealed class VisualTestBatchRow(string profile, Action<VisualTestResult> show, Func<bool> canShow) : ObservableObject
{
    private VisualTestResult? result;
    private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    public bool IsFinished { get; private set; }
    public string Profile { get; } = profile;
    public VisualTestResult? Result => result;
    public string State => result is null ? "等待测试" : result.Cancelled ? "已取消" : result.Completed ? "已完成" : result.Message;
    public string Detail => $"{result?.Steps.Count ?? 0} 次回复 · 用时 {TestElapsedClock.Format(elapsed.Elapsed.TotalSeconds)}";
    public void Show() { if (result is not null && canShow()) show(result); }
    public void Update(VisualTestResult value) { result = value; Raise(nameof(State)); Raise(nameof(Detail)); Raise(nameof(Result)); }
    public void Finish(VisualTestResult value) { IsFinished = true; elapsed.Stop(); Update(value); }
    public void Tick() { if (!IsFinished) Raise(nameof(Detail)); }
}
public sealed record VisualTestHistoryRow(VisualTestResult Result)
{
    public string Label => $"{Result.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {VisualTestCatalog.Title(Result.Kind)} · {Result.Model} · {VisualTestCatalog.TransportLabel(Result.Version)} · {(Result.Completed ? "已完成" : Result.Cancelled ? "已取消" : "未完成")}";
}
public sealed class VisualTestPanelViewModel : ObservableObject
{
    private readonly DesktopAccountServices? services;
    private readonly Action<bool> shellBusy;
    private readonly AccountTestQueue queue;
    private readonly TestElapsedClock elapsed;
    private string? profile;
    private string model = "gpt-6-astra";
    private VisualTestVariant variant = VisualTestCatalog.Variants[0];
    private bool busy;
    private string status = "选择账号后开始测试，也可以查看已保存的结果。";
    private CancellationTokenSource? cancellation;
    private VisualTestResult? result;
    private IReadOnlyList<VisualTestBatchRow> batch = [];
    private IReadOnlyList<VisualTestBatchRow> runRows = [];
    private IReadOnlyList<VisualTestStepViewModel> steps = [];
    private IReadOnlyList<VisualTestHistoryRow> history = [];
    private VisualTestHistoryRow? selectedHistory;
    public VisualTestPanelViewModel(DesktopAccountServices? services, string? profile, VisualTestKind kind, Action<bool> shellBusy, Action close, IReadOnlyList<string> profiles, AccountTestQueue? queue = null)
    {
        this.services = services; this.shellBusy = shellBusy; this.queue = queue ?? new(); Kind = kind; Profiles = profiles;
        elapsed = new(RefreshProgress);
        this.profile = profiles.Contains(profile) ? profile : profiles.FirstOrDefault();
        StartCommand = new(_ => CurrentOperation = RunAsync(false), _ => CanRun);
        StartAllCommand = new(_ => CurrentOperation = RunAsync(true), _ => CanRunAll);
        CancelCommand = new(_ => cancellation?.Cancel(), _ => busy);
        CloseCommand = new(_ => close());
        ShowBatchCommand = new(value => { if (!busy && value is VisualTestBatchRow row) row.Show(); }, _ => !busy);
        OpenGalleryCommand = new(_ =>
        {
            try { VisualTestViewer.Open(result!); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { SetStatus("无法打开作品对比：" + e.Message); }
        }, _ => result is not null && result.Steps.Count > 0);
        this.queue.Changed += Refresh;
        Load();
    }
    public VisualTestKind Kind { get; private set; }
    public bool IsPelican => Kind is VisualTestKind.Static or VisualTestKind.Animation;
    public bool Animated
    {
        get => Kind == VisualTestKind.Animation;
        set
        {
            if (busy || !IsPelican || value == Animated) return;
            Kind = value ? VisualTestKind.Animation : VisualTestKind.Static;
            batch = []; Load();
            foreach (var name in new[] { nameof(Animated), nameof(Title), nameof(RequestDescription), nameof(DurationHint) }) Raise(name);
        }
    }
    public string DurationHint => Kind == VisualTestKind.Animation ? "动画通常更慢，可能等待十多分钟；不设请求超时，可随时取消。"
        : Kind == VisualTestKind.Revision ? "静态初稿、看图检查、修改稿依次执行，保留每一步结果。" : "静态 SVG 通常更快；实际耗时取决于模型响应。";
    public string Title => VisualTestCatalog.Title(Kind);
    public IReadOnlyList<string> Profiles { get; }
    public string? SelectedProfile { get => profile; set { if (!busy && value is not null && Profiles.Contains(value) && Set(ref profile, value)) Load(); } }
    public string Model { get => model; set { if (!busy && Set(ref model, value)) Load(); } }
    public IReadOnlyList<VisualTestVariant> Variants => VisualTestCatalog.Variants;
    public VisualTestVariant Variant { get => variant; set { if (!busy && value is not null && Set(ref variant, value)) Load(); } }
    public bool IsVariation => Kind == VisualTestKind.Variation;
    public string Prompt => result?.Steps.FirstOrDefault()?.Prompt ?? VisualTestCatalog.Prompt(Kind, variant.Id);
    public bool IsBusy => busy;
    public bool CanEdit => !busy;
    private IReadOnlyList<string> EligibleProfiles => Profiles.Where(name => services is not null && !services.Repository.Profiles.AreTestsFrozen(name)).ToArray();
    private bool CanRunAll => !busy && queue.Accepting && services is not null && EligibleProfiles.Count > 0 && VisualTestCatalog.ValidModel(model);
    private bool CanRun => CanRunAll && profile is not null && EligibleProfiles.Contains(profile);
    public string RequestDescription => $"通用请求 · high · 不设请求总超时 · 每账号 {VisualTestCatalog.Requests(Kind)} 次请求；全部 {EligibleProfiles.Count * VisualTestCatalog.Requests(Kind)} 次。" + (Profiles.Count > EligibleProfiles.Count ? $" 冻结 {Profiles.Count - EligibleProfiles.Count} 个，自动跳过。" : "");
    public bool IsSelectedFrozen => profile is not null && services?.Repository.Profiles.AreTestsFrozen(profile) == true;
    public string Status => status;
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
            model = saved.Model; Kind = saved.Kind;
            variant = VisualTestCatalog.Variants.FirstOrDefault(v => v.Id == saved.Variant) ?? variant;
            batch = []; runRows = []; elapsed.Reset(); Show(saved); SetStatus($"历史结果（{VisualTestCatalog.TransportLabel(saved.Version)}）· " + saved.Message);
        }
    }
    public VisualTestResult? Result => result;
    public Task CurrentOperation { get; private set; } = Task.CompletedTask;
    public DelegateCommand StartCommand { get; }
    public DelegateCommand StartAllCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DelegateCommand CloseCommand { get; }
    public DelegateCommand ShowBatchCommand { get; }
    public DelegateCommand OpenGalleryCommand { get; }
    private void Load()
    {
        runRows = []; elapsed.Reset();
        result = profile is null || services is null ? null : services.VisualTests.Load(profile, model, Kind, variant.Id);
        LoadHistory();
        steps = result?.Steps.Select((step, index) => new VisualTestStepViewModel(result, step, index, SetStatus)).ToArray() ?? [];
        status = result is null ? "当前配置暂无结果；可从历史记录选择，或点击测试。" : $"历史结果（{VisualTestCatalog.TransportLabel(result.Version)}）· " + result.Message;
        Refresh();
    }
    private void LoadHistory()
    {
        history = profile is null || services is null ? [] : services.VisualTests.History(profile).Select(r => new VisualTestHistoryRow(r)).ToArray();
        selectedHistory = history.FirstOrDefault(r => r.Result.Directory == result?.Directory);
    }
    private void Show(VisualTestResult value)
    {
        var changedProfile = profile != value.Profile;
        result = value; profile = value.Profile;
        if (changedProfile) LoadHistory();
        selectedHistory = history.FirstOrDefault(r => r.Result.Directory == value.Directory);
        steps = value.Steps.Select((step, index) => new VisualTestStepViewModel(value, step, index, SetStatus)).ToArray();
        status = value.Message; Refresh();
    }
    private void SetStatus(string value) { status = value; Raise(nameof(Status)); }
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
        var requestedModel = model; var requestedVariant = variant.Id;
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
                        var waiting = new VisualTestResult(name, requestedModel, Kind, requestedVariant, VisualTestCatalog.Version, DateTimeOffset.UtcNow, "", [], false, false, "等待此账号的上一项测试结束…");
                        row.Update(waiting); if (profile == name) Show(waiting);
                    }
                    var value = await queue.RunAsync(name, () => services!.VisualTests.RunAsync(name, requestedModel, Kind, requestedVariant, updates, cancel.Token), cancel.Token);
                    finished.Add(name);
                    row.Finish(value); RefreshProgress(); if (profile == name) Show(value);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    finished.Add(name);
                    var value = new VisualTestResult(name, requestedModel, Kind, requestedVariant, VisualTestCatalog.Version, DateTimeOffset.UtcNow, "", [], false, true, "已取消，未开始的测试不会发送请求。");
                    row.Finish(value); RefreshProgress(); if (profile == name) Show(value);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or AccountException or FAswitch.Core.StorageException or InvalidOperationException or ArgumentException)
                {
                    finished.Add(name);
                    var value = new VisualTestResult(name, requestedModel, Kind, requestedVariant, VisualTestCatalog.Version, DateTimeOffset.UtcNow, "", [], false, false, ErrorRedaction.Scrub(e.Message));
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
        RefreshProgress();
        foreach (var property in new[] { nameof(SelectedProfile), nameof(Steps), nameof(Result), nameof(Summary), nameof(Status), nameof(CanEdit), nameof(IsBusy), nameof(Prompt), nameof(BatchRows), nameof(HasBatchResults), nameof(History), nameof(SelectedHistory), nameof(HistoryHint), nameof(CanSelectHistory), nameof(Model), nameof(Variant), nameof(Animated), nameof(Title), nameof(IsPelican), nameof(IsVariation), nameof(RequestDescription), nameof(DurationHint) }) Raise(property);
        foreach (var command in new[] { StartCommand, StartAllCommand, CloseCommand, CancelCommand, ShowBatchCommand, OpenGalleryCommand }) command.Refresh();
    }
    public void RefreshTestEligibility() => Refresh();
}
