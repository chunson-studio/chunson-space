using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

public sealed class ResponsesVisualTestRuntime(AccountService accounts, Func<string> findBrowser) : IVisualTestRuntime
{
    public Task<VisualReply> ReplyAsync(string profile, string model, string prompt, IReadOnlyList<string> images, string directory, CancellationToken token, string effort = ModelTestParameters.DefaultEffort)
        => accounts.RequestVisualAsync(profile, model, prompt, images, directory, token, effort);
    public Task RenderAsync(string page, string screenshot, CancellationToken token)
        => new LocalVisualRenderer(findBrowser).RenderAsync(page, screenshot, token);
}

public sealed partial class AccountService
{
    internal async Task<VisualReply> RequestVisualAsync(string profile, string model, string prompt, IReadOnlyList<string> images, string directory, CancellationToken token, string effort = ModelTestParameters.DefaultEffort)
    {
        token.ThrowIfCancellationRequested();
        repository.RequireTestsAllowed(profile);
        ModelTestParameters.RequireEffort(effort);
        ModelTestParameters.RequireModel(model);
        var session = new AuthSession(repository.ReadAuth(profile), repository.Proxy(profile));
        _ = Credentials(session.Auth); _ = HttpAccountTransport.RequireHttpProxy(session.Proxy);
        var secrets = (session.Auth["tokens"] as JsonObject ?? []).Select(p => p.Value?.ToString() ?? "").Where(s => s.Length > 0).ToArray();
        var watch = Stopwatch.StartNew();
        VisualReply? reply = null;
        string? failure = null;
        var identity = FileProfileStore.AccountIdentityHash(Credentials(session.Auth).Account);
        Directory.CreateDirectory(directory);
        using var trace = new ModelRequestTrace(directory, secrets, identity);
        try
        {
            var result = await ReadResponses(profile, model, session, token, prompt, BuiltInTestInstructions.Helpful, effort,
                images, Timeout.InfiniteTimeSpan, refreshAuthorization: false, trace: trace);
            var text = result.Text;
            // Redact literal credentials without changing SVG/JavaScript assignments or URLs.
            foreach (var secret in secrets.OrderByDescending(s => s.Length))
            {
                text = text.Replace(secret, "<已隐去>", StringComparison.Ordinal);
                text = text.Replace(JsonSerializer.Serialize(secret)[1..^1], "<已隐去>", StringComparison.Ordinal);
            }
            reply = new(text, TokenUsageReader.Read(result.Usage).CachedInputTokens, watch.Elapsed.TotalSeconds, "", "responses", result.Model, trace.SafetyBuffering);
            AccountEvidence.RecordIssue(repository.Profiles, profile, AccountIssue.None, identity);
            return reply;
        }
        catch (Exception e) when (e is AccountException or IOException or OperationCanceledException)
        {
            trace.Fail(e, token.IsCancellationRequested); e.Data[nameof(SafetyBufferingObservation)] = trace.SafetyBuffering; failure = ErrorRedaction.Scrub(e.Message, secrets);
            if (e is not OperationCanceledException) AccountEvidence.RecordIssue(repository.Profiles, profile, AccountEvidence.FailureIssue(e), identity);
            throw;
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(new
            {
                transport = "responses", requestedModel = model, effort, imageCount = images.Count,
                requestTimeoutSeconds = (int?)null, automaticRetries = 0, completed = reply is not null,
                responseModel = reply?.ResponseModel, cachedTokens = reply?.CachedTokens,
                seconds = watch.Elapsed.TotalSeconds, error = failure, safetyBuffering = trace.SafetyBuffering
            }));
        }
    }
}

