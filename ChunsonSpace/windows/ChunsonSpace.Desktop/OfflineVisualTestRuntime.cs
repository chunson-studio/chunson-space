using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChunsonSpace.Accounts;

namespace ChunsonSpace.Desktop;

public sealed class OfflineVisualTestRuntime : IVisualTestRuntime
{
    public List<(string Profile, string Model, string Prompt, IReadOnlyList<string> Images, string Effort)> Requests { get; } = [];
    public Func<string, string, CancellationToken, Task<string>>? ReplyFactory { get; set; }
    public IVisualTestRuntime? ReplyRuntime { get; set; }
    public bool FailRender { get; set; }
    public int RenderCount { get; private set; }
    public const string Svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 960 600'><rect width='960' height='600' fill='#e1f0dd'/><circle cx='330' cy='430' r='100' fill='none' stroke='#375b38' stroke-width='12'/><circle cx='630' cy='430' r='100' fill='none' stroke='#375b38' stroke-width='12'/><path d='M330 430L440 270L530 430Z M440 270L610 270L630 430 M530 430L610 270' stroke='#598954' stroke-width='12' fill='none'/><ellipse cx='440' cy='210' rx='100' ry='65' fill='white'/><path d='M480 180Q440 80 520 70L610 100L520 120' fill='white' stroke='#375b38' stroke-width='8'/></svg>";
    public async Task<VisualReply> ReplyAsync(string profile, string model, string prompt, IReadOnlyList<string> images, string directory, CancellationToken token, string effort = ModelTestParameters.DefaultEffort)
    {
        Requests.Add((profile, model, prompt, images, effort));
        if (ReplyRuntime is not null) return await ReplyRuntime.ReplyAsync(profile, model, prompt, images, directory, token, effort);
        var reply = ReplyFactory is not null ? await ReplyFactory(profile, prompt, token) : prompt.StartsWith("Inspect") ? "初稿脚踏连接不够清晰；建议补全脚、曲柄与车架连接。" : prompt.Contains("HTML animation") ? "<html><body>" + Svg + "<button onclick='document.body.dataset.paused=1'>暂停</button></body></html>" : Svg;
        token.ThrowIfCancellationRequested(); return new(reply, 4864, 12.3, "", "responses", model);
    }
    public Task RenderAsync(string page, string screenshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RenderCount++;
        if (FailRender) throw new IOException("fixture render failure");
        var bitmap = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)200, 24).ToArray(), 12);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(screenshot); encoder.Save(stream);
        return Task.CompletedTask;
    }
}
