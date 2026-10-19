using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

public sealed class SavedResultRow : ObservableObject
{
    public required string Id { get; init; }
    public required string Profile { get; init; }
    public required string Model { get; init; }
    public required DateTimeOffset Started { get; init; }
    public required string TaskType { get; init; }
    public required string Stage { get; init; }
    public required string Version { get; init; }
    public required string State { get; init; }
    public required string Prompt { get; init; }
    public required string Text { get; init; }
    public string ResponseModel { get; init; } = "";
    public string Effort { get; init; } = "";
    public SafetyBufferingObservation? Safety { get; init; }
    public VisualTestResult? Visual { get; init; }
    public int StepIndex { get; init; } = -1;
    public bool HasArtwork => Visual is not null && Visual.Kind != VisualTestKind.Animation && StepIndex >= 0 && Visual.Steps[StepIndex].Artifact is not null;
    public string Identity => $"{Model} / 思考：{ModelTestParameters.EffortLabel(Effort)} · {Profile} · {Started.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    public string TaskLabel => TaskType + (Visual?.Kind == VisualTestKind.Variation ? " · " + Visual.Variant : "") + $" · {Stage} · {State}";
    private string? promptHash;
    public string PromptHash => promptHash ??= Prompt.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Prompt))).ToLowerInvariant();
    public string VersionLabel => $"题目版本 {Version} · " + (PromptHash.Length == 0 ? "题目原文未保存" : $"题目标识 {PromptHash[..12]}");
    public string SafetySummary => AccountEvidence.Summary(new(Model, ResponseModel, Effort,
        Visual is not null ? StepIndex >= 0 : State == "已完成", Safety));
    public string SafetyDetails => SafetyBufferingDisplay.Details(Safety);
    public string ResultSummary => "";
    public string Excerpt => ResultSummary;
    public string OriginalNotice => "下面是保存的原文，可能含英文。原文没有改写，也没有另发请求翻译。";
    private BitmapSource? thumbnail;
    private bool thumbnailStarted;
    public BitmapSource? Thumbnail { get { if (HasArtwork && !thumbnailStarted) { thumbnailStarted = true; _ = LoadThumbnailAsync(); } return thumbnail; } }
    private async Task LoadThumbnailAsync()
    {
        thumbnail = await ArtworkFiles.ThumbnailAsync(Visual!, StepIndex);
        Raise(nameof(Thumbnail));
    }
    private bool selected;
    public bool Selected { get => selected; set { if (Set(ref selected, value)) SelectionChanged?.Invoke(); } }
    internal Action? SelectionChanged { get; set; }
    public DelegateCommand OpenCommand { get; internal set; } = new(_ => { });
    internal static IEnumerable<SavedResultRow> FromVisual(VisualTestResult result)
    {
        var state = result.Completed ? "已完成" : result.Cancelled ? "已取消" : "未完成";
        SavedResultRow Row(int index, string stage, string prompt, string text, SafetyBufferingObservation? safety, string response = "") => new()
        {
            Id = result.Directory + "#" + index, Profile = result.Profile, Model = result.Model, Effort = result.Effort, ResponseModel = response,
            Started = result.Started, TaskType = VisualTestCatalog.Title(result.Kind), Stage = stage, Version = result.Version,
            State = state, Prompt = prompt, Text = text, Safety = safety, Visual = result, StepIndex = index
        };
        for (var i = 0; i < result.Steps.Count; i++) { var step = result.Steps[i]; yield return Row(i, step.Stage ?? "未记录阶段", step.Prompt ?? "", step.Reply ?? "", step.SafetyBuffering, step.ResponseModel ?? ""); }
        if (result.FailedRequest is { } failed) yield return Row(-1, failed.Stage + "（请求未完成）", "", result.Message, failed.SafetyBuffering);
        else if (result.Steps.Count == 0) yield return Row(-1, "未取得回复", "", result.Message, null);
    }
}

internal static class ArtworkFiles
{
    private static readonly Dictionary<string, BitmapSource> thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private static int thumbnailGeneration;
    internal static void ClearThumbnails() { lock (thumbnails) { thumbnailGeneration++; thumbnails.Clear(); } }
    internal static string Scoped(VisualTestResult result, string name)
    {
        if (!Path.IsPathFullyQualified(result.Directory) || string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains(':') || name is "." or "..") throw new IOException("作品路径不在这条结果的目录内。");
        var root = Path.GetFullPath(result.Directory);
        PhysicalFileStorage.RejectLinks(root);
        var marker = Path.Combine(root, "result.json"); PhysicalFileStorage.RejectLinks(marker);
        if (!File.Exists(marker)) throw new IOException("保存的结果目录已移动或删除。");
        var path = Path.Combine(root, name); PhysicalFileStorage.RejectLinks(path);
        if (!File.Exists(path)) throw new IOException("作品文件已移动或删除；已保存的回复仍可查看。");
        return path;
    }
    internal static string ReadArtwork(SavedResultRow row)
    {
        if (!row.HasArtwork) throw new IOException("这一步没有保存的作品。");
        var path = Scoped(row.Visual!, row.Visual!.Steps[row.StepIndex].Artifact!);
        if (new FileInfo(path).Length > 2_000_000) throw new IOException("作品超过本地安全预览大小限制。");
        return VisualTestArtifact.Extract(File.ReadAllText(path), row.Visual.Kind == VisualTestKind.Animation) ?? throw new IOException("作品格式无效；可查看原始回复。");
    }
    private static readonly SemaphoreSlim thumbnailWorkers = new(2, 2);
    internal static async Task<BitmapSource?> ThumbnailAsync(VisualTestResult result, int index)
    {
        await thumbnailWorkers.WaitAsync();
        try { return await Task.Run(() => Thumbnail(result, index)); }
        finally { thumbnailWorkers.Release(); }
    }
    internal static BitmapSource? Thumbnail(VisualTestResult result, int index)
    {
        int generation; lock (thumbnails) generation = thumbnailGeneration;
        try
        {
            var path = Scoped(result, $"step-{index + 1}.png");
            var file = new FileInfo(path); if (file.Length > 24_000_000) return null;
            var key = path + ":" + file.LastWriteTimeUtc.Ticks + ":" + file.Length;
            lock (thumbnails) { if (thumbnails.TryGetValue(key, out var cached)) return cached; }
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 480; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            lock (thumbnails) { if (generation != thumbnailGeneration) return null; if (thumbnails.Count >= 128) thumbnails.Clear(); thumbnails[key] = bitmap; } return bitmap;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or StorageException or ArgumentException or NotSupportedException or System.IO.FileFormatException) { return null; }
    }
    internal static BitmapSource? SavedImage(VisualTestResult result, int index)
    {
        try
        {
            var path=Scoped(result,$"step-{index+1}.png"); if(new FileInfo(path).Length>24_000_000) return null;
            using var stream=File.OpenRead(path); var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.OnLoad);
            var frame=decoder.Frames[0]; if((long)frame.PixelWidth*frame.PixelHeight>32_000_000) return null;
            frame.Freeze(); return frame;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ChunsonSpace.Core.StorageException or ArgumentException or NotSupportedException) { return null; }
    }
}

public sealed record SavedResultsSnapshot(IReadOnlyList<SavedResultRow> Rows, IReadOnlyList<string> Profiles, string Warning = "");
internal sealed class ResultRows : ObservableCollection<SavedResultRow>
{
    public void Replace(IEnumerable<SavedResultRow> values)
    {
        Items.Clear(); foreach (var value in values) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
public sealed class ResultsGalleryViewModel : ObservableObject
{
    private readonly Func<SavedResultsSnapshot> read;
    private readonly Action<IReadOnlyList<SavedResultRow>> open;
    private readonly ResultRows rows = [];
    private string search = "", profile = "全部账号", type = "全部任务", state = "全部状态", order = "最新优先", status = "正在读取本机结果…";
    private bool loading;
    private bool dirty = true;
    private bool active;
    private int generation;
    public ResultsGalleryViewModel(Func<SavedResultsSnapshot> read, Action<IReadOnlyList<SavedResultRow>> open)
    {
        this.read = read; this.open = open; Items = CollectionViewSource.GetDefaultView(rows); Items.Filter = Matches;
        RefreshCommand = new(_ => { Invalidate(); Enter(); }, _ => !loading);
        CompareCommand = new(_ => open(rows.Where(r => r.Selected).ToArray()), _ => rows.Count(r => r.Selected) == 2);
        ClearCommand = new(_ => { Search = ""; Profile = "全部账号"; Type = "全部任务"; State = "全部状态"; foreach (var row in rows) row.Selected = false; });
        Sort();
    }
    internal static ResultsGalleryViewModel Create(DesktopAccountServices? services, Action<IReadOnlyList<SavedResultRow>> open) => new(() =>
    {
        if (services is null) return new([], []);
        var profiles = services.Repository.Profiles.ListAccounts().Select(a => a.Name).ToArray();
        var results = new List<SavedResultRow>(); var errors = new List<string>();
        foreach (var profile in profiles)
        {
            try
            {
                void Warn(string text) { if (!errors.Contains(profile + "：" + text)) errors.Add(profile + "：" + text); }
                results.AddRange(services.VisualTests.History(profile, Warn).SelectMany(SavedResultRow.FromVisual));
                var entries = services.Accounts.CialloHistory(profile, Warn);
                for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                {
                    var entry = entries[entryIndex];
                    for (var i = 0; i < Math.Max(1, entry.Result.Replies.Count); i++)
                    {
                        var reply = entry.Result.Replies.ElementAtOrDefault(i);
                        results.Add(new() { Id = $"{profile}/ciallo/{entry.SourceFile ?? entry.Result.DiagnosticDirectory ?? entry.Started.ToString("O") + "/" + entryIndex}/{i}", Profile = profile, Model = reply?.RequestedModel ?? "尚未请求", ResponseModel = reply?.ResponseModel ?? "", Effort = reply?.Effort ?? "", Started = entry.Started, TaskType = "Ciallo", Stage = $"模型 {i + 1}", Version = entry.Result.Version ?? "ciallo-v1", Prompt = CialloTest.Prompt, Text = reply?.Text ?? "未取得回复", Safety = reply?.SafetyBuffering, State = reply?.State == "completed" ? "已完成" : reply?.State == "cancelled" || reply is null && entry.Result.Cancelled ? "已取消" : "未完成" });
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or StorageException or AccountException or ArgumentException) { errors.Add(profile + "：" + ErrorRedaction.Scrub(e.Message)); }
        }
        return new(results, profiles, string.Join("；", errors));
    }, open);
    public ICollectionView Items { get; }
    public IReadOnlyList<string> Profiles { get; private set; } = ["全部账号"];
    public IReadOnlyList<string> Types { get; } = ["全部任务", "静态绘图", "Ciallo", "鹈鹕静态 SVG", "鹈鹕动画", "动物与机械", "检查并改进"];
    public IReadOnlyList<string> States { get; } = ["全部状态", "已完成", "未完成", "已取消"];
    public IReadOnlyList<string> Orders { get; } = ["最新优先", "最早优先"];
    public string Search { get => search; set { if (Set(ref search, value)) Filter(); } }
    public string Profile { get => profile; set { if (value is not null && Set(ref profile, value)) Filter(); } }
    private string? scopeProfile;
    internal void SetProfileScope(string? value)
    {
        var next = value ?? "全部账号";
        if (scopeProfile == value && profile == next) return;
        scopeProfile = value; profile = next; Raise(nameof(Profile)); Filter();
    }
    public string Type { get => type; set { if (value is not null && Set(ref type, value)) Filter(); } }
    public string State { get => state; set { if (value is not null && Set(ref state, value)) Filter(); } }
    public string Order { get => order; set { if (value is not null && Set(ref order, value)) Sort(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool IsLoading => loading;
    public bool IsEmpty => !loading && Items.IsEmpty;
    public string EmptyText => rows.Count == 0 ? "还没有保存的测试结果。完成测试后会自动出现在这里。" : "没有匹配结果，请更换搜索条件或清除筛选。";
    public string CountLabel => $"{Items.Cast<object>().Count()} 项 / 共 {rows.Count} 项 · 已选 {rows.Count(r => r.Selected)} 项";
    public Task CurrentLoad { get; private set; } = Task.CompletedTask;
    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand CompareCommand { get; }
    public DelegateCommand ClearCommand { get; }
    public DelegateCommand ClearHistoryCommand { get; internal set; } = new(_ => { }, _ => false);
    internal void Invalidate() { generation++; dirty = true; }
    internal void DiscardCachedResults() { Invalidate(); rows.Replace([]); Filter(); SelectionChanged(); }
    internal void Leave() => active = false;
    public void Enter() { active = true; if (!loading && dirty) CurrentLoad = LoadAsync(); }
    private bool Matches(object item) => item is SavedResultRow row && (scopeProfile is null || row.Profile == scopeProfile) && (profile == "全部账号" || row.Profile == profile) && (type == "全部任务" || row.TaskType == type) && (state == "全部状态" || row.State == state) && (string.IsNullOrWhiteSpace(search) || (row.Identity + " " + row.TaskLabel + " " + row.VersionLabel).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
    private void Filter() { Items.Refresh(); Raise(nameof(IsEmpty)); Raise(nameof(EmptyText)); Raise(nameof(CountLabel)); }
    private void Sort() { using (Items.DeferRefresh()) { Items.SortDescriptions.Clear(); Items.SortDescriptions.Add(new(nameof(SavedResultRow.Started), order == "最早优先" ? ListSortDirection.Ascending : ListSortDirection.Descending)); Items.SortDescriptions.Add(new(nameof(SavedResultRow.Id), ListSortDirection.Ascending)); } Raise(nameof(CountLabel)); }
    private async Task LoadAsync()
    {
        var requestedGeneration = generation;
        var failed = false;
        dirty = false;
        loading = true; Raise(nameof(IsLoading)); Raise(nameof(IsEmpty)); RefreshCommand.Refresh(); Status = "正在读取本机保存的结果…";
        try
        {
            var snapshot = await Task.Run(() => { var loaded = read(); foreach (var row in loaded.Rows) _ = row.PromptHash; return loaded; });
            // A clear, account removal or newer result can invalidate an in-flight
            // snapshot. Never republish that older result after the UI has cleared it.
            if (requestedGeneration != generation) return;
            var selection = rows.Where(r => r.Selected).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var row in snapshot.Rows)
            {
                row.Selected = selection.Contains(row.Id); row.SelectionChanged = SelectionChanged;
                row.OpenCommand = new(_ => open([row]));
            }
            rows.Replace(snapshot.Rows);
            Profiles = new[] { "全部账号" }.Concat(snapshot.Profiles).ToArray(); Raise(nameof(Profiles));
            if (!Profiles.Contains(profile)) Profile = "全部账号";
            Status = snapshot.Warning.Length == 0 ? "全部保存结果 · 仅本机读取；冻结账号也可查看。勾选两件作品即可并排比较。" : "有些历史记录无法读取，能读取的结果已显示。原文件保留，可刷新重试。";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or StorageException or AccountException or ArgumentException)
        { failed = true; dirty = true; Status = "本机结果暂时无法读取，请检查文件是否还在，再刷新重试。"; }
        finally
        {
            loading = false; Raise(nameof(IsLoading)); RefreshCommand.Refresh(); Filter(); SelectionChanged();
            if (dirty && active && !failed) CurrentLoad = LoadAsync();
        }
    }
    private void SelectionChanged() { CompareCommand.Refresh(); Raise(nameof(CountLabel)); }
}

public sealed partial class ShellViewModel
{
    private ResultsGalleryViewModel? resultsGallery;
    public ResultsGalleryViewModel ResultsGallery
    {
        get
        {
            if (resultsGallery is null)
            {
                resultsGallery = ResultsGalleryViewModel.Create(workspace?.Accounts, OpenSavedWorks);
                resultsGallery.ClearHistoryCommand = ClearAllTestHistoryCommand;
                resultsGallery.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ResultsGalleryViewModel.IsLoading)) { clearAllTestHistoryCommand?.Refresh(); RefreshDashboard(); } };
            }
            return resultsGallery;
        }
    }
    private DelegateCommand? clearAllTestHistoryCommand;
    internal Func<bool> ConfirmClearTestHistory { get; set; } = () => System.Windows.MessageBox.Show(
        "清除所有账号保存的测试记录和作品？账号授权、标记、冻结设置、项目与聊天保留。", "清除全部测试历史",
        System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK;
    public DelegateCommand ClearAllTestHistoryCommand => clearAllTestHistoryCommand ??= new(
        _ => CurrentOperation = ClearAllTestHistoryAsync(),
        _ => workspace is not null && !globalBusy && !accountTests.HasPending && accountTests.Accepting && resultsGallery?.IsLoading != true);
    private async Task ClearAllTestHistoryAsync()
    {
        if (!ClearAllTestHistoryCommand.CanExecute(null) || !ConfirmClearTestHistory()) return;
        // Confirmation runs a nested UI loop: recheck and close the dispatch gate before awaiting.
        if (!ClearAllTestHistoryCommand.CanExecute(null) || !accountTests.PauseWhenIdle()) return;
        SetBusy(true);
        var cleared = false;
        try { await Task.Run(() => workspace!.ProfileMaintenance.ClearAllTestHistory()); cleared = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException or ArgumentException)
        { ShowFeedback("测试历史未全部清除", "部分记录无法清除，请稍后重试。账号资料保留。", "Error"); }
        finally
        {
            // Also reload after partial deletion, so no deleted result remains selectable.
            try
            {
                CloseArtwork(); ArtworkFiles.ClearThumbnails();
                resultsGallery?.DiscardCachedResults();
                foreach (var panel in cialloPanels) panel.ReloadHistoryAfterClear();
                foreach (var panel in visualPanels) panel.ReloadHistoryAfterClear();
                retainedHealth = null; health = null; lastBackgroundPanel = null;
                Raise(nameof(AccountHealth)); Raise(nameof(IsAccountHealthOpen)); RefreshBackgroundModel();
                ResultsGallery.Enter(); await ResultsGallery.CurrentLoad;
                if (cleared) ShowFeedback("全部测试历史已清除", "账号授权、标记和冻结设置已保留。", "Success");
            }
            finally { accountTests.Accepting = !exitDraining; SetBusy(false); }
        }
    }
    public bool IsResultsPage => page == "Results";
    public bool IsWorkspacePage => !IsResultsPage;
    private ArtworkViewerViewModel? artworkViewer;
    public ArtworkViewerViewModel? ArtworkViewer => artworkViewer;
    public bool IsArtworkOpen => artworkViewer is not null;
    public void OpenSavedWorks(IReadOnlyList<SavedResultRow> rows)
    {
        CloseArtwork(); artworkViewer = new(rows, CloseArtwork); Raise(nameof(ArtworkViewer)); Raise(nameof(IsArtworkOpen)); artworkViewer.Start();
    }
    public void OpenVisualWork(VisualTestResult result, int? index)
    {
        var rows = SavedResultRow.FromVisual(result).Where(r => index is null ? r.HasArtwork : r.StepIndex == index).Take(2).ToArray();
        if (rows.Length > 0) OpenSavedWorks(rows);
    }
    public void CloseArtwork()
    {
        artworkViewer?.Stop(); artworkViewer = null; Raise(nameof(ArtworkViewer)); Raise(nameof(IsArtworkOpen));
    }
}

