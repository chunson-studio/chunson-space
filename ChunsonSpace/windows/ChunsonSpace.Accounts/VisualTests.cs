using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ChunsonSpace.Accounts;

public enum VisualTestKind { Animation, Variation, Revision, Static }
public static class VisualTestCatalog
{
    public const string Version = "visual-v5-original";
    public static string TransportLabel(string version) => version == "visual-v1" ? "Codex CLI" : "通用请求";
    public static string Title(VisualTestKind kind) => kind switch { VisualTestKind.Static => "静态绘图", VisualTestKind.Animation => "鹈鹕动画", VisualTestKind.Variation => "动物与机械", _ => "检查并改进" };
    public static int Requests(VisualTestKind kind) => kind == VisualTestKind.Revision ? 3 : 1;
    public static string Prompt(VisualTestKind kind, string content)
    {
        if (kind != VisualTestKind.Static) throw new ArgumentException("仅支持静态绘图。");
        if (string.IsNullOrWhiteSpace(content) || content.Length > 12000) throw new ArgumentException("请填写绘图内容（最多 12000 字）。");
        return "Create a static SVG for the following description. Use a viewBox of 0 0 960 600. Return only SVG vector elements, without animation, scripts, embedded images, external resources or tools.\n" + content.Trim();
    }
    public static bool ValidModel(string model) => Regex.IsMatch(model, "^(gpt-|codex|o[134](?:-|$))[a-zA-Z0-9._-]*$", RegexOptions.CultureInvariant) && model.Length < 200;
}

public sealed record VisualReply(string Text, long? CachedTokens, double Seconds, string CliVersion, string Transport = "codex-cli", string ResponseModel = "", SafetyBufferingObservation? SafetyBuffering = null);
public interface IVisualTestRuntime
{
    Task<VisualReply> ReplyAsync(string profile, string model, string prompt, IReadOnlyList<string> images, string directory, CancellationToken token, string effort = ModelTestParameters.DefaultEffort);
    Task RenderAsync(string page, string screenshot, CancellationToken token);
}
public sealed record VisualTestStep(string Stage, string Prompt, string Reply, long? CachedTokens, double Seconds,
    string CliVersion, string? Artifact, string? Preview, string Validation, string Transport = "codex-cli", string ResponseModel = "", SafetyBufferingObservation? SafetyBuffering = null);
public sealed record VisualTestResult(string Profile, string Model, VisualTestKind Kind, string Variant, string Version,
    DateTimeOffset Started, string Directory, IReadOnlyList<VisualTestStep> Steps, bool Completed, bool Cancelled, string Message, double? ElapsedSeconds = null, string Effort = ModelTestParameters.DefaultEffort, VisualRequestObservation? FailedRequest = null);

public sealed class VisualTestService(AccountRepository repository, IVisualTestRuntime runtime)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private string CacheDirectory(string profile, string model, VisualTestKind kind, string variant, string version = VisualTestCatalog.Version, string effort = ModelTestParameters.DefaultEffort)
    {
        var id = repository.ReadAuth(profile)["tokens"]?["account_id"]?.GetValue<string>() ?? throw new IOException("账号缺少授权身份。");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + "\n" + model + "\n" + kind + "\n" + variant + "\n" + version + (effort == ModelTestParameters.DefaultEffort ? "" : "\n" + effort))));
        return Path.Combine(repository.AccountPath(profile), "visual-tests", key);
    }
    public VisualTestResult? Load(string profile, string model, VisualTestKind kind, string variant, string effort = ModelTestParameters.DefaultEffort)
    {
        try
        {
            var directory = CacheDirectory(profile, model, kind, variant, effort: effort);
            var path = Path.Combine(directory, "latest.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 16_000_000) return null;
            var result = JsonSerializer.Deserialize<VisualTestResult>(File.ReadAllText(path), Json);
            return result is not null && result.Model == model && result.Kind == kind && result.Variant == variant && result.Version == VisualTestCatalog.Version && result.Effort == effort
                && Path.GetFullPath(result.Directory).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? result with { Profile = profile } : null;
        }
        catch (Exception e) when (e is IOException or JsonException or AccountException or ChunsonSpace.Core.StorageException) { return null; }
    }
    public IReadOnlyList<VisualTestResult> History(string profile, Action<string>? warning = null)
    {
        var results = new List<VisualTestResult>();
        var root = Path.Combine(repository.AccountPath(profile), "visual-tests");
        ChunsonSpace.Core.PhysicalFileStorage.RejectLinks(root);
        if (!Directory.Exists(root)) return results;
        foreach (var cache in Directory.EnumerateDirectories(root).Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0))
        foreach (var run in Directory.EnumerateDirectories(cache).Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0))
        {
            try
            {
                var path = Path.Combine(run, "result.json");
                if (!File.Exists(path)) continue;
                if (new FileInfo(path).Length > 16_000_000 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { warning?.Invoke("部分作品历史无法读取，原文件已保留。"); continue; }
                var result = JsonSerializer.Deserialize<VisualTestResult>(File.ReadAllText(path), Json);
                if (result is null || result.Steps is null || result.Steps.Any(s => s is null) || result.Message is null || result.Version is null || !Enum.IsDefined(result.Kind)
                    || !VisualTestCatalog.ValidModel(result.Model ?? "") || result.Variant is null || !ModelTestParameters.ValidEffort(result.Effort)
                    || !string.Equals(Path.GetFullPath(result.Directory), Path.GetFullPath(run), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(CacheDirectory(profile, result.Model!, result.Kind, result.Variant, result.Version, result.Effort), cache, StringComparison.OrdinalIgnoreCase)) { warning?.Invoke("部分作品历史无法读取，原文件已保留。"); continue; }
                results.Add(result with { Profile = profile });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or AccountException or ChunsonSpace.Core.StorageException or ArgumentException) { warning?.Invoke("部分作品历史无法读取，原文件已保留。"); }
        }
        return results.OrderByDescending(r => r.Started).ThenByDescending(r => r.Directory, StringComparer.Ordinal).ToArray();
    }
    public async Task<VisualTestResult> RunAsync(string profile, string model, VisualTestKind kind, string variant,
        IProgress<VisualTestResult>? progress = null, CancellationToken token = default, string effort = ModelTestParameters.DefaultEffort)
    {
        repository.RequireTestsAllowed(profile);
        ModelTestParameters.RequireModel(model);
        ModelTestParameters.RequireEffort(effort);
        var prompt = VisualTestCatalog.Prompt(kind, variant);
        var cache = CacheDirectory(profile, model, kind, variant, effort: effort);
        var directory = Path.Combine(cache, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var steps = new List<VisualTestStep>();
        var started = DateTimeOffset.UtcNow;
        var identity = repository.Profiles.ReadAccountIdentityHash(profile);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        VisualRequestObservation? failedRequest = null;
        VisualTestResult Snapshot(bool complete, bool cancelled, string message) => new(profile, model, kind, variant, VisualTestCatalog.Version, started, directory, steps.ToArray(), complete, cancelled, message, elapsed.Elapsed.TotalSeconds, effort, failedRequest);
        void Save(VisualTestResult result)
        {
            var json = JsonSerializer.Serialize(result, Json);
            File.WriteAllText(Path.Combine(directory, "result.json"), json);
            File.WriteAllText(Path.Combine(directory, "gallery.html"), VisualTestArtifact.Gallery(result));
            var temp = Path.Combine(cache, "latest-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, json); File.Move(temp, Path.Combine(cache, "latest.json"), true);
            progress?.Report(result);
        }
        async Task Add(string stage, string question, IReadOnlyList<string> images, bool artwork)
        {
            token.ThrowIfCancellationRequested();
            repository.RequireTestsAllowed(profile);
            Save(Snapshot(false, false, $"第 {steps.Count + 1}/{VisualTestCatalog.Requests(kind)} 步 · {stage}：等待模型回复…"));
            VisualReply reply;
            try { reply = await runtime.ReplyAsync(profile, model, question, images, Path.Combine(directory, "request-" + (steps.Count + 1)), token, effort); }
            catch (Exception e)
            {
                // Preserve the original exception and successful reply/progress counts.
                failedRequest = new(steps.Count + 1, stage, e.Data[nameof(SafetyBufferingObservation)] as SafetyBufferingObservation);
                throw;
            }
            string? artifact = null, preview = null;
            var validation = artwork ? "未取得有效作品" : "检查意见；不是独立评分";
            if (artwork)
            {
                var content = VisualTestArtifact.Extract(reply.Text, false);
                if (content is not null)
                {
                    artifact = "step-" + (steps.Count + 1) + ".svg";
                    preview = "step-" + (steps.Count + 1) + "-preview.html";
                    File.WriteAllText(Path.Combine(directory, artifact), content);
                    File.WriteAllText(Path.Combine(directory, preview), VisualTestArtifact.Wrap(content));
                    validation = "结构检查通过；画面与动作请查看作品";
                }
            }
            steps.Add(new(stage, question, reply.Text, reply.CachedTokens, reply.Seconds, reply.CliVersion, artifact, preview, validation, reply.Transport, reply.ResponseModel, reply.SafetyBuffering));
            Save(Snapshot(false, false, stage + "已返回"));
            if (artwork && artifact is null) throw new InvalidDataException("模型回复不包含有效作品，已保存原文，不自动重试。");
        }
        try
        {
            await Add("生成静态作品", prompt, [], true);
            Save(Snapshot(false, false, "回复已完成，正在生成本地预览…"));
            try { await runtime.RenderAsync(Path.Combine(directory, steps[^1].Preview!), Path.Combine(directory, "step-" + steps.Count + ".png"), token); }
            catch (IOException e) { throw new IOException("模型回复和作品已保存，本地预览未完成：" + e.Message, e); }
            var result = Snapshot(true, false, "测试完成 · 作品已保存，尚未进行独立视觉评分。"); Save(result);
            AccountEvidence.Apply(repository.Profiles, profile, identity, started, AccountEvidence.Id(directory),
                steps.Select(s => new AccountTestObservation(model, s.ResponseModel, effort, s.Transport == "responses", s.SafetyBuffering)).ToArray(), result.Completed);
            return result;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or AccountException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            var result = Snapshot(false, e is OperationCanceledException, e is OperationCanceledException ? "已取消，已完成的作品与回复保留。" : ErrorRedaction.Scrub(e.Message));
            Save(result);
            AccountEvidence.Apply(repository.Profiles, profile, identity, started, AccountEvidence.Id(directory),
                steps.Select(s => new AccountTestObservation(model, s.ResponseModel, effort, s.Transport == "responses", s.SafetyBuffering)).ToArray(), result.Completed);
            return result;
        }
    }
}

public static class VisualTestArtifact
{
    public static string Gallery(VisualTestResult result)
    {
        var cards = string.Join("", result.Steps.OrderBy(s => s.Artifact is null).Select(step =>
            "<article><h2>" + WebUtility.HtmlEncode(step.Stage) + "</h2><p>" + step.Seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s · 缓存 token " + (step.CachedTokens?.ToString() ?? "未返回") + "</p>" +
            (step.Artifact is not null && Extract(step.Reply, false) is { } content
                ? "<iframe sandbox='' referrerpolicy='no-referrer' srcdoc=\"" + WebUtility.HtmlEncode(Wrap(content)) + "\"></iframe>"
                : "<pre>" + WebUtility.HtmlEncode(step.Reply) + "</pre>") + "</article>"));
        return "<!doctype html><html lang='zh'><meta charset='utf-8'><title>作品对比</title><style>body{font:16px system-ui;background:#f2f7ec;color:#22362a;padding:24px}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(400px,1fr));gap:18px}article{background:white;border:1px solid #cfddc8;border-radius:18px;padding:18px}h1{font-size:24px}h2{font-size:18px}p{color:#526a50}iframe{border:0;width:100%;height:440px}pre{white-space:pre-wrap}</style><h1>" + WebUtility.HtmlEncode(VisualTestCatalog.Title(result.Kind) + " · " + result.Profile) + "</h1><p>" + WebUtility.HtmlEncode(result.Model) + " / " + WebUtility.HtmlEncode(result.Effort) + " · 保存的作品；查看不会发送模型请求。检查意见来自受测模型，不是独立评分。</p><main>" + cards + "</main></html>";
    }
    public static string? Extract(string text, bool html)
    {
        if (html || text.Length > 2_000_000 || Regex.IsMatch(text, @"animation\s*:|@keyframes", RegexOptions.IgnoreCase)) return null;
        var start = text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        const string endTag = "</svg>";
        var end = text.LastIndexOf(endTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < start) return null;
        var result = text[start..(end + endTag.Length)];
        try
        {
            using var reader = XmlReader.Create(new StringReader(result), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
            var svg = XDocument.Load(reader);
            return svg.Root?.Name.LocalName == "svg" && svg.Descendants().All(e => e.Name.LocalName is not ("script" or "foreignObject" or "image" or "animate" or "animateMotion" or "animateTransform" or "set") && !e.Attributes().Any(a => a.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))) ? result : null;
        }
        catch (XmlException) { return null; }
    }
    public static string Wrap(string content)
    {
        const string policy = "default-src 'none'; script-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src 'none'; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
        var document = "<!doctype html><html><head><meta http-equiv='Content-Security-Policy' content=\"" + policy + "\"><style>html,body{margin:0;width:100%;height:100%;overflow:hidden}svg{max-width:100%;max-height:100%}</style></head><body>" + content + "</body></html>";
        return "<!doctype html><html><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; frame-src about:; style-src 'unsafe-inline'; script-src 'none'; base-uri 'none'; form-action 'none'\"><title>模型测试作品</title><style>html,body{margin:0;height:100%;background:#f3f7ef}iframe{width:100%;height:100%;border:0}</style></head><body><iframe sandbox='' referrerpolicy='no-referrer' srcdoc=\"" + WebUtility.HtmlEncode(document) + "\"></iframe></body></html>";
    }
}
