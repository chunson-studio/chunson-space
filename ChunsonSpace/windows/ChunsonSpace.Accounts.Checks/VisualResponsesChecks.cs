using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;

internal static class VisualResponsesChecks
{
    public static async Task<int> Run(string root)
    {
        int count = 0, run = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        var repository = new AccountRepository(root);
        foreach (var name in new[] { "A", "B" })
        {
            repository.Profiles.CreateBoundAccount(name, new(name == "A" ? "http://127.0.0.1:7890" : "http://127.0.0.1:7891"));
            repository.Profiles.SaveAccount(name, new JsonObject { ["tokens"] = new JsonObject { ["access_token"] = "fixture-access-" + name, ["account_id"] = "fixture-id-" + name } }, overwrite: true);
        }
        var auth = File.ReadAllText(Path.Combine(repository.AccountPath("A"), "auth.json"));
        var transport = new FixtureTransport();
        var runtime = new ResponsesVisualTestRuntime(new AccountService(repository, transport), () => throw new InvalidOperationException("Browser must not start for model replies"));
        Task<VisualReply> Reply(string name = "A", IReadOnlyList<string>? images = null, CancellationToken token = default, string effort = "high")
            => runtime.ReplyAsync(name, "gpt-6-astra", "fixture prompt", images ?? [], Path.Combine(root, "request-" + ++run), token, effort);
        string Completed(long? cached) => "data: " + new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject {
            ["status"] = "completed", ["model"] = "declared-model", ["usage"] = cached is null ? null : new JsonObject { ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = cached } },
            ["output"] = new JsonArray(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "<svg>" + new string('a', 500) + " state=1 fixture-access-A</svg>" }) }) } }.ToJsonString() + "\n\n";
        foreach (var cached in new long?[] { 0, 4864, null })
        {
            transport.Body = Completed(cached); var reply = await Reply();
            Check(reply.CachedTokens == cached && reply.Transport == "responses" && reply.CliVersion == "" && reply.ResponseModel == "declared-model", "direct response preserves cache zero/nonzero/missing and records transport/model");
            Check(reply.Text.Length > 500 && reply.Text.Contains("state=1") && !reply.Text.Contains("fixture-access-A"), "artwork remains complete while literal credential is redacted");
        }
        var request = transport.Requests[0]; var body = JsonNode.Parse(request.Body!)!;
        Check(request.Url == AccountEndpoints.Responses && request.RequireProxy && request.Proxy == "http://127.0.0.1:7890" && request.Headers["chatgpt-account-id"] == "fixture-id-A", "uses selected account and existing explicit route");
        Check(body["model"]!.ToString() == "gpt-6-astra" && body["reasoning"]!["effort"]!.ToString() == "high" && body["tools"]!.AsArray().Count == 0 && body["tool_choice"]!.ToString() == "none" && body["store"]!.ToString() == "false", "same model/high with no tools or server storage");
        var diagnostic = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "request-1", "request.json")))!;
        Check(diagnostic["requestTimeoutSeconds"] is null && diagnostic["automaticRetries"]!.GetValue<int>() == 0 && !diagnostic.ToJsonString().Contains("fixture-access"), "diagnostics report no total timeout/retries and omit secrets");
        transport.Body = Completed(42) + "data: [DONE]\n\n: fixture-tail\n";
        await Reply(effort: "high");
        var captured = Path.Combine(root, "request-" + run);
        var wire = JsonNode.Parse(File.ReadAllText(Path.Combine(captured, "wire-request.json")))!;
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(captured, "trace-summary.json")))!;
        var stream = File.ReadAllText(Path.Combine(captured, "response.sse"));
        Check(JsonNode.Parse(transport.Requests[^1].Body!)!["reasoning"]!["effort"]!.ToString() == "high" && wire["body"]!["reasoning"]!["effort"]!.ToString() == "high", "selected effort is actually sent and captured");
        Check(stream.Contains("[DONE]") && stream.Contains("fixture-tail") && summary["readBoundary"]!.ToString() == "EOF" && !summary["truncated"]!.GetValue<bool>(), "capture consumes response tail through EOF without silent truncation");
        Check(summary["bytes"]!.GetValue<long>() == new FileInfo(Path.Combine(captured, "response.sse")).Length && !stream.Contains('\r'), "trace byte count matches UTF-8 file length with normalized LF endings");
        Check(!Directory.GetFiles(captured).Select(File.ReadAllText).Any(s => s.Contains("fixture-access-A") || s.Contains("fixture-id-A") || s.Contains("fixture-cookie") || s.Contains("fixture-turn-state")), "trace files redact authorization account response cookies and session state");
        var responseHeaders = JsonNode.Parse(File.ReadAllText(Path.Combine(captured, "response-headers.json")))!;
        var stateMetadata = responseHeaders["opaqueHeaderMetadata"]!["x-codex-turn-state"]!;
        Check(stateMetadata["length"]!.GetValue<int>() == "fixture-turn-state".Length && stateMetadata["sha256"]!.ToString() == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("fixture-turn-state"))), "opaque routing state retains only verifiable length and hash, never its value");
        Check(stream.Contains("state=1") && summary["usage"]!["input_tokens_details"]!["cached_tokens"]!.GetValue<int>() == 42, "full trace preserves artwork and raw usage");
        var priorInvalid = transport.Requests.Count;
        try { await Reply(effort: "bad-effort"); Check(false, "invalid effort refused"); } catch (ArgumentException) { }
        Check(transport.Requests.Count == priorInvalid, "invalid effort never reaches transport");
        var png = Path.Combine(root, "preview.png");
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jv1kAAAAASUVORK5CYII=");
        File.WriteAllBytes(png, bytes); transport.Body = Completed(128); await Reply("B", [png]);
        var imageRequest = transport.Requests[^1]; var image = JsonNode.Parse(imageRequest.Body!)!["input"]![0]!["content"]![1]!;
        Check(image["type"]!.ToString() == "input_image" && image["image_url"]!.ToString() == "data:image/png;base64," + Convert.ToBase64String(bytes), "review sends actual rendered PNG bytes");
        Check(imageRequest.Proxy.EndsWith(":7891") && imageRequest.Headers["chatgpt-account-id"] == "fixture-id-B", "second account retains its own route and authorization");
        var before = transport.Requests.Count;
        try { await Reply(images: [Path.Combine(root, "missing.png")]); Check(false, "missing image must stop request"); } catch (IOException) { }
        Check(transport.Requests.Count == before, "missing preview never silently becomes text-only review");
        transport.Status = 401; transport.Body = "unauthorized " + new string('x', 5000) + " fixture-error-tail";
        try { await Reply(); Check(false, "401 must remain failure"); } catch (AccountException e) { Check(e.Status == 401, "401 remains explicit"); }
        Check(transport.Requests.Count == before + 1 && File.ReadAllText(Path.Combine(repository.AccountPath("A"), "auth.json")) == auth, "no authentication retry or source auth modification");
        Check(File.ReadAllText(Path.Combine(root, "request-" + run, "response.sse")).Contains("fixture-error-tail"), "diagnostic capture retains full HTTP error beyond displayed excerpt");
        transport.Status = 200; transport.Body = "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"Selected model is at capacity\"}}}\n\n";
        try { await Reply(); Check(false, "capacity failure must remain failure"); } catch (AccountException e) { Check(e.Message.Contains("at capacity"), "structured service reason remains visible"); }
        transport.Body = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n";
        try { await Reply(); Check(false, "partial stream must not succeed"); } catch (AccountException e) { Check(e.Message.Contains("response.completed"), "stream completion is required"); }
        transport.Hold = true;
        using (var cancel = new CancellationTokenSource())
        {
            var pending = Reply(token: cancel.Token);
            await transport.Entered.Task;
            Check(!pending.IsCompleted, "request remains pending until explicitly cancelled"); cancel.Cancel();
            try { await pending; Check(false, "cancellation must stop request"); } catch (OperationCanceledException) { Check(true, "user cancellation reaches direct request"); }
        }
        Check(File.ReadAllText(Path.Combine(repository.AccountPath("A"), "auth.json")) == auth, "all direct runtime scenarios preserve source authorization");
        var bounded = Path.Combine(root, "trace-limit");
        using (var limitTrace = new ModelRequestTrace(bounded, []))
        {
            var line = new string('x', 1024 * 1024 - 1);
            for (var i = 0; i < 64; i++) limitTrace.Line(line);
            limitTrace.Line("overflow"); limitTrace.EndOfStream();
        }
        var limitedSummary = JsonNode.Parse(File.ReadAllText(Path.Combine(bounded, "trace-summary.json")))!;
        Check(limitedSummary["truncated"]!.GetValue<bool>() && limitedSummary["bytes"]!.GetValue<long>() == 64L * 1024 * 1024
            && new FileInfo(Path.Combine(bounded, "response.sse")).Length == 64L * 1024 * 1024 && limitedSummary["lines"]!.GetValue<int>() == 64,
            "trace accepts exactly 64 MiB and flags later data without exceeding physical file limit");
        Check(limitedSummary["safetyBuffering"]!["Capture"]!.ToString() == "truncated", "physical trace truncation propagates to safety capture metadata");
        var unicode = Path.Combine(root, "trace-unicode");
        using (var unicodeTrace = new ModelRequestTrace(unicode, [])) { unicodeTrace.Line("猫"); unicodeTrace.Line(""); }
        var unicodeSummary = JsonNode.Parse(File.ReadAllText(Path.Combine(unicode, "trace-summary.json")))!;
        Check(unicodeSummary["bytes"]!.GetValue<long>() == 5 && File.ReadAllBytes(Path.Combine(unicode, "response.sse")).SequenceEqual(Encoding.UTF8.GetBytes("猫\n\n")),
            "trace byte count includes multibyte text and empty line separators without BOM");
        return count;
    }
    private sealed class FixtureTransport : IAccountTransport
    {
        public List<AccountHttpRequest> Requests { get; } = [];
        public string Body { get; set; } = "";
        public int Status { get; set; } = 200;
        public bool Hold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken token)
        {
            Requests.Add(request);
            if (Hold) { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return new(Status, new MemoryStream(Encoding.UTF8.GetBytes(Body)), headers: new Dictionary<string,string> { ["set-cookie"] = "fixture-cookie", ["x-request-id"] = "fixture-request-id", ["x-codex-turn-state"] = "fixture-turn-state" });
        }
    }
}
