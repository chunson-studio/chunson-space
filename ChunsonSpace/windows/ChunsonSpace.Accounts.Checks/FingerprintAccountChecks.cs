using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Fingerprint;

internal static class FingerprintAccountChecks
{
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); count++; }
        var repository = new AccountRepository(root);
        repository.Profiles.CreateBoundAccount("指纹", new("http://127.0.0.1:7897"));
        repository.Profiles.SaveAccount("指纹", new JsonObject { ["auth_mode"] = "chatgpt", ["tokens"] = new JsonObject
        { ["access_token"] = "fingerprint-access", ["refresh_token"] = "fingerprint-refresh", ["id_token"] = "fingerprint-id", ["account_id"] = "fingerprint-account" } }, overwrite: true);
        var clock = new FingerprintClock();
        var questions = FingerprintEngine.Default.GenerateChallenges(new PredictableRandom());
        Assert(questions.Count == 3 && questions.Select(q => q.Id).Distinct().Count() == 3, "original engine three distinct challenges");
        string Vector(int length, int offset) => string.Join(", ", Enumerable.Range(0, length).Select(i => (i * 73 + offset) % 355 + 1));
        string Payload(string text, string model = "upstream-response-model", int cached = 8) => "data: " + new JsonObject
        {
            ["type"] = "response.completed", ["response"] = new JsonObject { ["status"] = "completed", ["model"] = model,
            ["output"] = new JsonArray(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) }),
            ["usage"] = new JsonObject { ["cached_input_tokens"] = cached } }
        }.ToJsonString() + "\n\n";
        var network = new FingerprintTransport();
        var service = new AccountService(repository, network, clock);
        var progress = new CapturedProgress();
        for (var i = 0; i < 3; i++) network.Text(200, Payload(Vector(questions[i].ExpectedCount, i)));
        var authPath = Path.Combine(root, "profiles", "指纹", "auth.json");
        var envPath = Path.Combine(root, "browser-envs", "指纹", "env.json");
        var beforeAuth = File.ReadAllBytes(authPath); var beforeEnv = File.ReadAllBytes(envPath);
        var result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions, progress);
        Assert(result.Completed && result.Responses.Count == 3 && result.Analysis?.UsedOutputs == 3 && result.Failure is null, "three explicit replies analyzed by real engine");
        Assert(result.Analysis?.Status == FingerprintAnalysisStatus.Valid && result.Analysis.Results.Count == FingerprintEngine.Default.ModelIds.Count, "engine nearest candidates returned");
        Assert(network.Requests.Count == 3 && network.Requests.All(r => r.RequireProxy && r.Proxy == "http://127.0.0.1:7897"), "exactly three requests through selected account explicit proxy");
        Assert(result.Responses.All(r => r.RequestedModel == "gpt-6-astra" && r.ResponseModel == "upstream-response-model"), "requested versus actual response model recorded separately");
        Assert(result.Responses.All(r => r.Text.Length > 220 && r.Usage.CachedInputTokens == 8), "complete raw output retained, cached usage factual only");
        Assert(File.ReadAllBytes(authPath).SequenceEqual(beforeAuth) && File.ReadAllBytes(envPath).SequenceEqual(beforeEnv), "ordinary fingerprint test doesn't rewrite auth or proxy");
        Assert(progress.Items.Select(p => p.Stage).SequenceEqual(new[] { FingerprintTestStage.Starting, FingerprintTestStage.Completed, FingerprintTestStage.Starting, FingerprintTestStage.Completed, FingerprintTestStage.Starting, FingerprintTestStage.Completed }), "serial per-question progress");
        for (var i = 0; i < network.Requests.Count; i++)
        {
            var body = JsonNode.Parse(network.Requests[i].Body!)!.AsObject();
            Assert(body["instructions"]!.GetValue<string>() == "" && body["reasoning"]?["effort"]?.GetValue<string>() == "high" && !body.ContainsKey("temperature"), "fingerprint uses high effort with empty instructions and default temperature");
            Assert(body["input"]!.AsArray().Count == 1 && body["input"]![0]!["content"]![0]!["text"]!.GetValue<string>() == questions[i].Prompt &&
                body["tools"]!.AsArray().Count == 0 && body["tool_choice"]!.GetValue<string>() == "none" && !body.ContainsKey("previous_response_id") && !body["store"]!.GetValue<bool>(), "independent prompt no history no tools");
        }
        Assert(network.Requests.Select(r => r.Headers["session-id"]).Distinct().Count() == 3, "independent request sessions");
        Assert(result.Warnings.Any(w => w.Contains("预算差异", StringComparison.Ordinal)), "sampling condition caveat recorded");
        network = new(); service = new(repository, network, clock);
        for (var i = 0; i < 3; i++) network.Text(200, Payload(Vector(questions[i].ExpectedCount, i)));
        try { await service.TestFingerprintAsync("指纹", "not-in-bank", questions); throw new InvalidOperationException("unsupported model accepted"); }
        catch (ArgumentException) { Assert(network.Requests.Count == 0, "unsupported hidden fingerprint model rejected before transport"); }
        network = new(); service = new(repository, network, clock);
        network.Text(200, Payload(Vector(questions[0].ExpectedCount, 0))); network.Text(404, "unsupported fingerprint-access");
        result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions);
        Assert(!result.Completed && result.Failure is { Index: 2, Error: AccountError.Http } && result.Responses.Count == 1 && result.Analysis is null && network.Requests.Count == 2, "failure on second question retains first and never switches model or fills third");
        Assert(!result.Failure!.Message.Contains("fingerprint-access", StringComparison.Ordinal), "question error secrets scrubbed");
        network = new(); service = new(repository, network, clock); network.Stream(503, () => new FingerprintBrokenStream());
        result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions);
        Assert(result.Failure is { Index: 1, Error: AccountError.Network } && result.Responses.Count == 0 && network.Requests.Count == 1, "error body disconnect is specific to the failed question and stops requests");
        network = new(); service = new(repository, network, clock); network.Text(200, "data: {\"type\":\"response.output_text.delta\",\"delta\":\"1,2,3\"}\n\ndata: [DONE]\n\n");
        result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions);
        Assert(result.Failure is { Index: 1, Error: AccountError.Network } && result.Responses.Count == 0 && network.Requests.Count == 1, "SSE without completion not fingerprint success");
        network = new(); service = new(repository, network, clock); network.Text(200, "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"incomplete\"}}\n\n");
        result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions);
        Assert(!result.Completed && result.Failure?.Index == 1 && network.Requests.Count == 1, "incomplete completed event rejected");
        network = new(); service = new(repository, network, clock); network.Text(200, Payload("no numerical output")); network.Text(200, Payload("no numerical output")); network.Text(200, Payload("no numerical output"));
        result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions);
        Assert(result.Completed && result.Analysis?.Status == FingerprintAnalysisStatus.NoUsableOutputs && result.Analysis.Results.Count == 0 && result.Warnings.Any(w => w.Contains("解析条件", StringComparison.Ordinal)), "request completion distinct from usable fingerprint score");
        network = new(); service = new(repository, network, clock); network.Text(401, "expired"); network.Json(200, new JsonObject { ["access_token"] = "fingerprint-renewed" });
        for (var i = 0; i < 3; i++) network.Text(200, Payload(Vector(questions[i].ExpectedCount, i)));
        result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions);
        Assert(result.Completed && network.Requests.Count == 5 && network.Requests.Count(r => r.Url == AccountEndpoints.Token) == 1, "one ordinary refresh retry within fingerprint workflow");
        Assert(JsonNode.Parse(network.Requests[0].Body!)!["input"]![0]!["content"]![0]!["text"]!.GetValue<string>() == JsonNode.Parse(network.Requests[2].Body!)!["input"]![0]!["content"]![0]!["text"]!.GetValue<string>() &&
            JsonNode.Parse(network.Requests[2].Body!)?["reasoning"]?["effort"]?.GetValue<string>() == "high", "401 retry retains challenge and high effort");
        Assert(network.Requests[2].Headers["authorization"] == "Bearer fingerprint-renewed" && repository.ReadAuth("指纹")["tokens"]!["refresh_token"]!.GetValue<string>() == "fingerprint-refresh", "refresh semantics reused without losing refresh token");
        network = new(); service = new(repository, network, clock); network.Text(401, "expired"); network.Json(200, new JsonObject { ["access_token"] = "fingerprint-second" }); network.Text(401, "still expired");
        result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions);
        Assert(result.Failure is { Index: 1, Error: AccountError.Http } && network.Requests.Count == 3, "repeated 401 stops without refresh loop");
        network = new(); service = new(repository, network, clock); network.Text(200, Payload(Vector(questions[0].ExpectedCount, 1)));
        using (var cancelled = new CancellationTokenSource())
        {
            progress = new(p => { if (p.Stage == FingerprintTestStage.Completed) cancelled.Cancel(); });
            result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions, progress, cancelled.Token);
            Assert(result.Failure is { Index: 2, Error: AccountError.Cancelled } && result.Responses.Count == 1 && network.Requests.Count == 1, "cancel between questions doesn't fire remaining requests");
        }
        network = new(); service = new(repository, network, clock);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions, cancellationToken: cancelled.Token);
            Assert(result.Failure?.Error == AccountError.Cancelled && network.Requests.Count == 0, "pre-cancel never requests");
        }
        network = new(); service = new(repository, network, clock) { FingerprintQuestionTimeout = TimeSpan.FromMilliseconds(20) };
        network.Stream(200, () => new FingerprintSlowStream());
        result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions);
        Assert(result.Failure is { Index: 1, Error: AccountError.Timeout } && network.Requests.Count == 1 && !result.Completed, "whole-question timeout covers body and doesn't proceed");
        network = new(); service = new(repository, network, clock) { FingerprintQuestionTimeout = TimeSpan.FromMilliseconds(20) };
        network.Text(401, "expired"); network.DelayUntilCancelled();
        result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions);
        Assert(result.Failure?.Error == AccountError.Timeout && network.Requests.Count == 2, "question deadline also covers refresh without adding 25 seconds");
        network = new(); service = new(repository, network, clock);
        network.Text(200, Payload(Vector(questions[0].ExpectedCount, 1) + "\nfingerprint-second"));
        network.Text(200, Payload(Vector(questions[1].ExpectedCount, 2))); network.Text(200, Payload(Vector(questions[2].ExpectedCount, 3)));
        result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions);
        Assert(result.Responses[0].Text.Length > 220 && !result.Responses[0].Text.Contains("fingerprint-second", StringComparison.Ordinal), "known tokens redacted without losing original numerical text");
        network = new(); service = new(repository, network, clock);
        try { await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions.Take(2).ToArray()); throw new InvalidOperationException("missing question accepted"); }
        catch (ArgumentException) { Assert(network.Requests.Count == 0, "no implicit question generation or filling"); }
        repository.Profiles.UpdateEnvironment("指纹", proxy: "");
        result = await service.TestFingerprintAsync("指纹", "gpt-6-astra", questions);
        Assert(result.Failure?.Error == AccountError.ExitUnavailable && network.Requests.Count == 0, "no proxy cannot use system or direct route");
        Console.WriteLine($"PASS {count} isolated fingerprint account checks; fixture root: {root}");
        return count;
    }
    private sealed class FingerprintTransport : IAccountTransport
    {
        private readonly Queue<Func<CancellationToken, Task<AccountHttpResponse>>> replies = new();
        public List<AccountHttpRequest> Requests { get; } = [];
        public void Text(int status, string text) => Stream(status, () => new MemoryStream(Encoding.UTF8.GetBytes(text)));
        public void Json(int status, JsonObject value) => Text(status, value.ToJsonString());
        public void Stream(int status, Func<Stream> stream) => replies.Enqueue(_ => Task.FromResult(new AccountHttpResponse(status, stream())));
        public void DelayUntilCancelled() => replies.Enqueue(async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException("fixture wait unexpectedly ended"); });
        public Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); return replies.Dequeue()(cancellationToken); }
    }
    private sealed class CapturedProgress(Action<FingerprintTestProgress>? onReport = null) : IProgress<FingerprintTestProgress>
    {
        public List<FingerprintTestProgress> Items { get; } = [];
        public void Report(FingerprintTestProgress value) { Items.Add(value); onReport?.Invoke(value); }
    }
    private sealed class PredictableRandom : IFingerprintRandom
    {
        private int next;
        public int NextIndex(int length) => 0;
        public Guid NextGuid() => new(++next, 0, 0, new byte[8]);
    }
    private sealed class FingerprintClock : IAccountClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.Delay(duration, cancellationToken);
    }
    private sealed class FingerprintSlowStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    }
    private sealed class FingerprintBrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("fixture disconnect fingerprint-access"));
    }
}
