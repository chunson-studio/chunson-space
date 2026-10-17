using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FAswitch.Core;

namespace FAswitch.Accounts;

public sealed class ResponsesVisualTestRuntime(AccountService accounts, Func<string> findBrowser) : IVisualTestRuntime
{
    public Task<VisualReply> ReplyAsync(string profile, string model, string prompt, IReadOnlyList<string> images, string directory, CancellationToken token)
        => accounts.RequestVisualAsync(profile, model, prompt, images, directory, token);
    public Task RenderAsync(string page, string screenshot, CancellationToken token)
        => new LocalVisualRenderer(findBrowser).RenderAsync(page, screenshot, token);
}

public sealed partial class AccountService
{
    internal async Task<VisualReply> RequestVisualAsync(string profile, string model, string prompt, IReadOnlyList<string> images, string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!VisualTestCatalog.ValidModel(model)) throw new ArgumentException("模型名称无效。");
        var session = new AuthSession(repository.ReadAuth(profile), repository.Proxy(profile));
        _ = Credentials(session.Auth); _ = HttpAccountTransport.RequireHttpProxy(session.Proxy);
        var secrets = (session.Auth["tokens"] as JsonObject ?? []).Select(p => p.Value?.ToString() ?? "").Where(s => s.Length > 0).ToArray();
        var watch = Stopwatch.StartNew();
        VisualReply? reply = null;
        string? failure = null;
        Directory.CreateDirectory(directory);
        try
        {
            var result = await ReadResponses(profile, model, session, token, prompt, "You are a helpful assistant.", "high",
                images, Timeout.InfiniteTimeSpan, refreshAuthorization: false);
            var text = result.Text;
            // Redact literal credentials without changing SVG/JavaScript assignments or URLs.
            foreach (var secret in secrets.OrderByDescending(s => s.Length))
            {
                text = text.Replace(secret, "<已隐去>", StringComparison.Ordinal);
                text = text.Replace(JsonSerializer.Serialize(secret)[1..^1], "<已隐去>", StringComparison.Ordinal);
            }
            reply = new(text, TokenUsageReader.Read(result.Usage).CachedInputTokens, watch.Elapsed.TotalSeconds, "", "responses", result.Model);
            return reply;
        }
        catch (Exception e) when (e is AccountException or IOException or OperationCanceledException)
        { failure = ErrorRedaction.Scrub(e.Message, secrets); throw; }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(new
            {
                transport = "responses", requestedModel = model, effort = "high", imageCount = images.Count,
                requestTimeoutSeconds = (int?)null, automaticRetries = 0, completed = reply is not null,
                responseModel = reply?.ResponseModel, cachedTokens = reply?.CachedTokens,
                seconds = watch.Elapsed.TotalSeconds, error = failure
            }));
        }
    }
}
