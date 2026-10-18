using System.Diagnostics;
using System.Text.Json;
using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

public static class CialloTest
{
    public const string Version = "ciallo-v3-original";
    public const string Prompt = "ciallo";
    public const string Effort = "high";
    public static IReadOnlyList<string> Models => ModelTestParameters.Models;
}
public sealed record CialloReply(string RequestedModel, string ResponseModel, string Effort, string State, string Text, TimeSpan Elapsed, TokenUsage? Usage = null, SafetyBufferingObservation? SafetyBuffering = null);
public sealed record CialloResult(IReadOnlyList<CialloReply> Replies, bool Cancelled, string? DiagnosticDirectory = null, string Version = "ciallo-v1", string MarkOutcome = "")
{
    public int Completed => Replies.Count(reply => reply.State == "completed");
}
public sealed record CialloHistoryEntry(DateTimeOffset Started, CialloResult Result, bool Finished = true)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SourceFile { get; init; }
    public string Label => $"{Started.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {(Result.Replies.Count == 1 ? Result.Replies[0].RequestedModel : "多模型")} / 思考：{ModelTestParameters.EffortLabel(Result.Replies.FirstOrDefault()?.Effort ?? ModelTestParameters.DefaultEffort)} · {(Result.Cancelled ? "已取消" : Finished && Result.Completed == Result.Replies.Count ? "已完成" : "未完成")} · {Result.Completed}/{Result.Replies.Count} 个模型回复";
}

public sealed partial class AccountService
{
    public IReadOnlyList<CialloHistoryEntry> CialloHistory(string profileName, Action<string>? warning = null)
    {
        var directory = Path.Combine(repository.AccountPath(profileName), "ciallo-tests");
        PhysicalFileStorage.RejectLinks(directory);
        if (!Directory.Exists(directory)) return [];
        var entries = new List<CialloHistoryEntry>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                if (new FileInfo(path).Length > 16_000_000 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { warning?.Invoke("部分问候测试历史无法读取，原文件已保留。"); continue; }
                var entry = JsonSerializer.Deserialize<CialloHistoryEntry>(File.ReadAllText(path));
                if (entry?.Result?.Replies is not null && entry.Result.Replies.All(r => r is not null)
                    && (entry.Result.DiagnosticDirectory is null || Path.GetFullPath(entry.Result.DiagnosticDirectory) == Path.ChangeExtension(Path.GetFullPath(path), null))) entries.Add(entry with { SourceFile = Path.GetFullPath(path) });
                else warning?.Invoke("部分问候测试历史无法读取，原文件已保留。");
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { warning?.Invoke("部分 Ciallo 历史无法读取，原文件已保留。"); }
        }
        return entries.OrderByDescending(e => e.Started).ToArray();
    }
    public async Task<CialloResult> TestCialloAsync(string profileName, IProgress<CialloReply>? progress = null, CancellationToken token = default, string effort = ModelTestParameters.DefaultEffort, string? model = null)
    {
        repository.RequireTestsAllowed(profileName);
        ModelTestParameters.RequireEffort(effort);
        if (model is not null) ModelTestParameters.RequireModel(model);
        var models = model is null ? CialloTest.Models : new[] { model };
        var session = new AuthSession(repository.ReadAuth(profileName), repository.Proxy(profileName));
        _ = Credentials(session.Auth); _ = HttpAccountTransport.RequireHttpProxy(session.Proxy);
        var replies = new List<CialloReply>();
        var started = clock.UtcNow;
        var identity = FileProfileStore.AccountIdentityHash(Credentials(session.Auth).Account);
        var directory = Path.Combine(repository.AccountPath(profileName), "ciallo-tests");
        var path = Path.Combine(directory, started.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        CialloResult Save(bool cancelled, bool finished = true)
        {
            var result = new CialloResult(replies.ToArray(), cancelled, Path.ChangeExtension(path, null), CialloTest.Version);
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new CialloHistoryEntry(started, result, finished)));
            File.Move(temporary, path, true);
            if (finished) result = result with { MarkOutcome = AccountEvidence.Apply(repository.Profiles, profileName, identity, started, AccountEvidence.Id(path),
                replies.Select(r => new AccountTestObservation(r.RequestedModel, r.ResponseModel, r.Effort, r.State == "completed", r.SafetyBuffering)).ToArray(), !cancelled) };
            return result;
        }
        foreach (var requestedModel in models)
        {
            if (token.IsCancellationRequested) return Save(true);
            repository.RequireTestsAllowed(profileName);
            progress?.Report(new(requestedModel, "", effort, "running", "", TimeSpan.Zero));
            var watch = Stopwatch.StartNew(); CialloReply reply;
            using var trace = new ModelRequestTrace(Path.Combine(Path.ChangeExtension(path, null), "request-" + (replies.Count + 1)),
                (session.Auth["tokens"] as System.Text.Json.Nodes.JsonObject ?? []).Select(p => p.Value?.ToString() ?? ""), identity);
            try
            {
                var result = await ReadResponses(profileName, requestedModel, session, token, CialloTest.Prompt, BuiltInTestInstructions.Helpful, effort,
                    requestTimeout: Timeout.InfiniteTimeSpan, refreshAuthorization: false, trace: trace);
                var credentials = session.Auth["tokens"] as System.Text.Json.Nodes.JsonObject;
                reply = new(requestedModel, result.Model, effort, "completed", ErrorRedaction.ModelReply(result.Text,
                    AccountJson.Text(credentials, "access_token"), AccountJson.Text(credentials, "refresh_token"), AccountJson.Text(credentials, "id_token"), AccountJson.Text(credentials, "account_id")), watch.Elapsed, TokenUsageReader.Read(result.Usage));
            }
            catch (OperationCanceledException error) when (token.IsCancellationRequested)
            {
                trace.Fail(error, callerCancellationRequested: true);
                reply = new(requestedModel, "", effort, "cancelled", "已取消，后续模型未请求。", watch.Elapsed, SafetyBuffering: trace.SafetyBuffering);
                replies.Add(reply); progress?.Report(reply); return Save(true);
            }
            catch (AccountException error) { trace.Fail(error); AccountEvidence.RecordIssue(repository.Profiles, profileName, AccountEvidence.FailureIssue(error), identity); reply = new(requestedModel, "", effort, "failed", ErrorRedaction.Scrub(error.Message), watch.Elapsed); }
            catch (IOException error) { trace.Fail(error); AccountEvidence.RecordIssue(repository.Profiles, profileName, AccountIssue.TemporaryConnection, identity); reply = new(requestedModel, "", effort, "failed", "连接中断，未收到完整回复。", watch.Elapsed); }
            reply = reply with { SafetyBuffering = trace.SafetyBuffering };
            replies.Add(reply); progress?.Report(reply); Save(false, false);
        }
        if (replies.All(r => r.State == "completed")) AccountEvidence.RecordIssue(repository.Profiles, profileName, AccountIssue.None, identity);
        return Save(false);
    }
}

