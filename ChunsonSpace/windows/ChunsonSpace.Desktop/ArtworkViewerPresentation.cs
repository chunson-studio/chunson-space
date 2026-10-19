using System.IO;
using System.Windows.Media.Imaging;
using ChunsonSpace.Accounts;

namespace ChunsonSpace.Desktop;

public sealed class ArtworkPaneViewModel : ObservableObject
{
    private readonly CancellationTokenSource stopped = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string?> browser;
    private LocalArtworkSession? session;
    private BitmapSource? image;
    private string status = "正在打开保存的作品…";
    private string errorDetails = "";
    private int requestedScale = 2, renderedScale;
    public ArtworkPaneViewModel(SavedResultRow row, Func<string?> browser)
    {
        Row = row; this.browser = browser;
        ExternalCommand = new(_ =>
        {
            try { _ = ArtworkFiles.ReadArtwork(Row); VisualTestViewer.OpenExternal(Row.Visual!, Row.StepIndex); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or ChunsonSpace.Core.StorageException or System.ComponentModel.Win32Exception) { ErrorDetails = ErrorRedaction.Scrub(e.Message); Status = "外部打开没有成功，作品仍保留；可展开查看错误详情。"; }
        }, _ => row.HasArtwork);
    }
    public SavedResultRow Row { get; }
    public BitmapSource? Image { get => image; private set => Set(ref image, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string ErrorDetails { get => errorDetails; private set => Set(ref errorDetails, value); }
    private bool animated;
    public bool IsInteractive => animated && session is not null;
    public long FrameCount { get; private set; }
    public Task Ready => ready.Task;
    public Task Operation { get; private set; } = Task.CompletedTask;
    public DelegateCommand ExternalCommand { get; }
    public void Start() => Operation = RunAsync();
    public void Stop() { stopped.Cancel(); session?.Stop(); }
    public void RequestScale(double zoom) => requestedScale = Math.Clamp((int)Math.Ceiling(zoom), 2, 4);
    public async Task ClickAsync(double x, double y)
    {
        try { if (IsInteractive) await session!.ClickAsync(x, y, stopped.Token); }
        catch (Exception e) when (e is AccountException or OperationCanceledException) { if (!stopped.IsCancellationRequested) { ErrorDetails = ErrorRedaction.Scrub(e.Message); Status = "动画操作没有完成，请关闭作品后重试；错误详情可展开查看。"; } }
    }
    private async Task RunAsync()
    {
        try
        {
            if (!Row.HasArtwork) { Status = "保存的文字回复 · 原文保留；下方可展开题目和详细信息。"; return; }
            var content = await Task.Run(() => ArtworkFiles.ReadArtwork(Row), stopped.Token);
            animated = VisualTestArtifact.IsAnimated(content);
            var executable = browser();
            if (executable is null)
            {
                Image = await Task.Run(() => ArtworkFiles.SavedImage(Row.Visual!,Row.StepIndex),stopped.Token);
                Status = "本机缺少作品显示组件。这里只显示保存的图片；可使用右侧外部打开。"; return;
            }
            session = await LocalArtworkSession.OpenAsync(executable, content, stopped.Token);
            Raise(nameof(IsInteractive));
            do
            {
                stopped.Token.ThrowIfCancellationRequested();
                if (animated || renderedScale != requestedScale)
                {
                    var scale = animated ? 1 : requestedScale;
                    // Interactive frames retain a 1:1 CSS-pixel surface. Chromium's OOPIF
                    // hit testing can lag a scaled capture; static SVGs may safely render at 2–4x.
                    var bytes = await session.CaptureAsync(scale, stopped.Token);
                    Image = await Task.Run(() => Decode(bytes), stopped.Token); renderedScale = scale; FrameCount++;
                    Status = animated ? "动画作品本地显示 · 可点击作品内控件，滚轮缩放，中键拖动" : "矢量原稿本地显示 · 滚轮缩放，中键拖动 · 放大可看清细节";
                    ready.TrySetResult();
                }
                await Task.Delay(180, stopped.Token);
            } while (!stopped.IsCancellationRequested);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or AccountException or ChunsonSpace.Core.StorageException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            if (!stopped.IsCancellationRequested) { Image ??= await Task.Run(() => Row.Visual is null ? null : ArtworkFiles.SavedImage(Row.Visual,Row.StepIndex)); ErrorDetails = ErrorRedaction.Scrub(e.Message); Status = "作品文件无法读取、格式无效或显示组件未能启动。这里只显示保存的图片；错误详情可展开查看。"; }
        }
        finally
        {
            ready.TrySetResult();
            if (session is not null) { await session.DisposeAsync(); session = null; Raise(nameof(IsInteractive)); }
        }
    }
    internal static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
}

public sealed class ArtworkViewerViewModel : ObservableObject
{
    private double zoom = 1;
    public ArtworkViewerViewModel(IReadOnlyList<SavedResultRow> rows, Action close, Func<string?>? browser = null)
    {
        Panes = rows.Take(2).Select(r => new ArtworkPaneViewModel(r, browser ?? (() => InstalledBrowsers.Find().FirstOrDefault()?.Executable))).ToArray();
        CloseCommand = new(_ => close()); FitCommand = new(_ => Zoom = 1);
        ZoomInCommand = new(_ => Zoom = Math.Min(4, zoom * 1.25)); ZoomOutCommand = new(_ => Zoom = Math.Max(0.5, zoom / 1.25));
    }
    public IReadOnlyList<ArtworkPaneViewModel> Panes { get; }
    public int Columns => Panes.Count;
    public string Title => Panes.Count == 2 ? "并排比较" : Panes.FirstOrDefault()?.Row.HasArtwork == true ? "作品大图" : "文字结果";
    public string Comparison => Panes.Count != 2 ? "保存的作品 · 查看不发送模型请求" : Panes[0].Row.PromptHash.Length == 0 || Panes[1].Row.PromptHash.Length == 0 || Panes[0].Row.Version != Panes[1].Row.Version || Panes[0].Row.PromptHash != Panes[1].Row.PromptHash
        ? "题目版本或阶段内容不同 · 仅并排查看，不视为同题评分；每侧显示独立版本与题目标识。" : "相同题目版本与原文 · 两侧同步缩放、平移；不自动评分。";
    public double Zoom { get => zoom; set { if (Set(ref zoom, Math.Clamp(value, .5, 4))) { Raise(nameof(ZoomLabel)); foreach (var pane in Panes) pane.RequestScale(zoom); } } }
    public string ZoomLabel => $"{zoom:P0}";
    public DelegateCommand CloseCommand { get; }
    public DelegateCommand FitCommand { get; }
    public DelegateCommand ZoomInCommand { get; }
    public DelegateCommand ZoomOutCommand { get; }
    public void Start() { foreach (var pane in Panes) pane.Start(); }
    public void Stop() { foreach (var pane in Panes) pane.Stop(); }
}
