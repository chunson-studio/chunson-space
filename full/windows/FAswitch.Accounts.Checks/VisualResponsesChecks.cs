using System.Text;
using System.Text.Json.Nodes;
using FAswitch.Accounts;

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
        Task<VisualReply> Reply(string name = "A", IReadOnlyList<string>? images = null, CancellationToken token = default)
            => runtime.ReplyAsync(name, "gpt-6-astra", "fixture prompt", images ?? [], Path.Combine(root, "request-" + ++run), token);
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
        var png = Path.Combine(root, "preview.png");
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jv1kAAAAASUVORK5CYII=");
        File.WriteAllBytes(png, bytes); transport.Body = Completed(128); await Reply("B", [png]);
        var imageRequest = transport.Requests[^1]; var image = JsonNode.Parse(imageRequest.Body!)!["input"]![0]!["content"]![1]!;
        Check(image["type"]!.ToString() == "input_image" && image["image_url"]!.ToString() == "data:image/png;base64," + Convert.ToBase64String(bytes), "review sends actual rendered PNG bytes");
        Check(imageRequest.Proxy.EndsWith(":7891") && imageRequest.Headers["chatgpt-account-id"] == "fixture-id-B", "second account retains its own route and authorization");
        var before = transport.Requests.Count;
        try { await Reply(images: [Path.Combine(root, "missing.png")]); Check(false, "missing image must stop request"); } catch (IOException) { }
        Check(transport.Requests.Count == before, "missing preview never silently becomes text-only review");
        transport.Status = 401; transport.Body = "unauthorized";
        try { await Reply(); Check(false, "401 must remain failure"); } catch (AccountException e) { Check(e.Status == 401, "401 remains explicit"); }
        Check(transport.Requests.Count == before + 1 && File.ReadAllText(Path.Combine(repository.AccountPath("A"), "auth.json")) == auth, "no authentication retry or source auth modification");
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
            return new(Status, new MemoryStream(Encoding.UTF8.GetBytes(Body)));
        }
    }
}
