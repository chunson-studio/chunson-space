using System.Diagnostics;
using System.Text.Json;
using FAswitch.Core;

namespace FAswitch.Accounts;

public static class CialloTest
{
    public const string Prompt = "ciallo";
    public const string Effort = "high";
    public static IReadOnlyList<string> Models { get; } = Array.AsReadOnly(new[] { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna" });
}
public sealed record CialloReply(string RequestedModel, string ResponseModel, string Effort, string State, string Text, TimeSpan Elapsed, TokenUsage? Usage = null);
public sealed record CialloResult(IReadOnlyList<CialloReply> Replies, bool Cancelled)
{
    public int Completed => Replies.Count(reply => reply.State == "completed");
}
public sealed record CialloHistoryEntry(DateTimeOffset Started, CialloResult Result, bool Finished = true)
{
    public string Label => $"{Started.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {(Result.Cancelled ? "已取消" : Finished && Result.Completed == Result.Replies.Count ? "已完成" : "未完成")} · {Result.Completed}/{Result.Replies.Count} 个模型回复";
}

public sealed partial class AccountService
{
    public IReadOnlyList<CialloHistoryEntry> CialloHistory(string profileName)
    {
        var directory = Path.Combine(repository.AccountPath(profileName), "ciallo-tests");
        if (!Directory.Exists(directory)) return [];
        var entries = new List<CialloHistoryEntry>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                if (new FileInfo(path).Length > 16_000_000 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var entry = JsonSerializer.Deserialize<CialloHistoryEntry>(File.ReadAllText(path));
                if (entry?.Result?.Replies is not null && entry.Result.Replies.All(r => r is not null)) entries.Add(entry);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return entries.OrderByDescending(e => e.Started).ToArray();
    }
    public async Task<CialloResult> TestCialloAsync(string profileName, IProgress<CialloReply>? progress = null, CancellationToken token = default)
    {
        repository.RequireTestsAllowed(profileName);
        var session = new AuthSession(repository.ReadAuth(profileName), repository.Proxy(profileName));
        _ = Credentials(session.Auth); _ = HttpAccountTransport.RequireHttpProxy(session.Proxy);
        var replies = new List<CialloReply>();
        var started = clock.UtcNow;
        var directory = Path.Combine(repository.AccountPath(profileName), "ciallo-tests");
        var path = Path.Combine(directory, started.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        CialloResult Save(bool cancelled, bool finished = true)
        {
            var result = new CialloResult(replies.ToArray(), cancelled);
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new CialloHistoryEntry(started, result, finished)));
            File.Move(temporary, path, true);
            return result;
        }
        foreach (var model in CialloTest.Models)
        {
            if (token.IsCancellationRequested) return Save(true);
            repository.RequireTestsAllowed(profileName);
            progress?.Report(new(model, "", CialloTest.Effort, "running", "", TimeSpan.Zero));
            var watch = Stopwatch.StartNew(); CialloReply reply;
            try
            {
                var result = await ReadResponses(profileName, model, session, token, CialloTest.Prompt, "You are a helpful assistant.", CialloTest.Effort,
                    requestTimeout: Timeout.InfiniteTimeSpan);
                var credentials = session.Auth["tokens"] as System.Text.Json.Nodes.JsonObject;
                reply = new(model, result.Model, CialloTest.Effort, "completed", ErrorRedaction.Scrub(result.Text,
                    AccountJson.Text(credentials, "access_token"), AccountJson.Text(credentials, "refresh_token"), AccountJson.Text(credentials, "id_token")), watch.Elapsed, TokenUsageReader.Read(result.Usage));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                reply = new(model, "", CialloTest.Effort, "cancelled", "已取消，后续模型未请求。", watch.Elapsed);
                replies.Add(reply); progress?.Report(reply); return Save(true);
            }
            catch (AccountException error) { reply = new(model, "", CialloTest.Effort, "failed", ErrorRedaction.Scrub(error.Message), watch.Elapsed); }
            catch (IOException) { reply = new(model, "", CialloTest.Effort, "failed", "连接中断，未收到完整回复。", watch.Elapsed); }
            replies.Add(reply); progress?.Report(reply); Save(false, false);
        }
        return Save(false);
    }
}
