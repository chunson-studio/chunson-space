using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Verification;

internal static class SafetyBufferingChecks
{
    internal static async Task<int> Run(string root)
    {
        Directory.CreateDirectory(root);
        var checks = new List<object>();
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed });
            File.WriteAllText(Path.Combine(root, "checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            if (!passed) throw new InvalidOperationException("safety_buffering: " + name);
        }
        var repository = new AccountRepository(root);
        repository.Profiles.CreateBoundAccount("Fixture", new("http://127.0.0.1:7890"));
        repository.Profiles.SaveAccount("Fixture", new JsonObject { ["tokens"] = new JsonObject { ["access_token"] = "fixture-access", ["account_id"] = "fixture-id" } }, overwrite: true);
        var auth = File.ReadAllBytes(Path.Combine(repository.AccountPath("Fixture"), "auth.json"));
        var transport = new Transport(); var service = new AccountService(repository, transport);
        var expected = new[] { "false", "true", "missing", "null", "object", "conflict", "nested", "marker", "incomplete", "missing-incomplete", "failed", "malformed", "mismatch", "no-created", "model-mismatch", "scalar", "array", "truncated" }
            .ToDictionary(kind => kind, kind => kind == "false" ? "账号正常" : kind == "missing" ? "账号异常" : "本次没判断成功");
        foreach (var (kind, label) in expected)
        {
            transport.Bodies.Enqueue(SafetyBufferingFixtures.Stream(kind));
            var result = await service.TestCialloAsync("Fixture", model: "gpt-6-astra");
            var reply = result.Replies.Single(); var observation = reply.SafetyBuffering!;
            Check(SafetyBufferingDisplay.Summary(observation).Contains(label), kind + " production parser label");
            var history = new AccountService(new AccountRepository(root), transport).CialloHistory("Fixture");
            var reloaded = history.Single(h => h.Result.DiagnosticDirectory == result.DiagnosticDirectory).Result.Replies.Single();
            Check(JsonSerializer.Serialize(reloaded.SafetyBuffering) == JsonSerializer.Serialize(observation), kind + " persisted per-model observation");
            var trace = JsonNode.Parse(File.ReadAllText(Path.Combine(result.DiagnosticDirectory!, "request-1", "trace-summary.json")))!;
            Check(trace["safetyBuffering"]!.ToJsonString() == JsonSerializer.SerializeToNode(observation)!.ToJsonString(), kind + " trace and result agree");
            if (kind == "object")
            {
                var safe = JsonSerializer.Serialize(observation) + SafetyBufferingDisplay.Details(observation);
                Check(!safe.Contains("fixture-opaque-secret") && !safe.Contains("fixture-access") && !safe.Contains("opaque-private-key") && safe.Contains("SHA-256"), "structured details omit private keys and values");
            }
            if (kind == "false") Check(observation.Values.Single().Boolean == false, "actual false is preserved as boolean false");
            if (kind == "failed") Check(reply.Text.Contains("detail-tail") && File.ReadAllText(Path.Combine(result.DiagnosticDirectory!, "request-1", "response.sse")).Contains("detail-tail"), "new metadata does not trim raw error");
            Check(transport.Requests.Count == history.Count, kind + " history adds no requests or retries");
        }
        // A single all-model run must keep different values attached to their requested model.
        var kinds = new[] { "false", "true", "missing", "null", "object", "conflict", "incomplete" };
        foreach (var kind in kinds.Take(CialloTest.Models.Count)) transport.Bodies.Enqueue(SafetyBufferingFixtures.Stream(kind));
        var all = await service.TestCialloAsync("Fixture");
        Check(all.Replies.Select(r => r.RequestedModel).SequenceEqual(CialloTest.Models) && all.Replies.Select((r, i) => SafetyBufferingDisplay.Summary(r.SafetyBuffering).Contains(expected[kinds[i]])).All(x => x), "all-model order retains distinct observations");
        // Actual multiline SSE framing, CRLF, trailing events and output text are handled by ReadResponses.
        var created = SafetyBufferingFixtures.Created(); created["safety_buffering"] = false;
        var raw = created.ToJsonString().Replace(",\"safety_buffering\"", ",\ndata: \"safety_buffering\"");
        transport.Bodies.Enqueue(("data: " + raw + "\n\n" + SafetyBufferingFixtures.Frame(SafetyBufferingFixtures.Completed("literal safety_buffering: true"))).Replace("\n", "\r\n"));
        var framed = (await service.TestCialloAsync("Fixture", model: "gpt-6-astra")).Replies.Single();
        Check(framed.SafetyBuffering is { Capture: "complete" } && framed.SafetyBuffering.Values.Single().Boolean == false, "multiline CRLF frame parsed once; output text is not protocol evidence");
        transport.Bodies.Enqueue(SafetyBufferingFixtures.Stream("false") + SafetyBufferingFixtures.Frame(new() { ["type"] = "response.metadata", ["safety_buffering"] = true }));
        Check(SafetyBufferingDisplay.Summary((await service.TestCialloAsync("Fixture", model: "gpt-6-astra")).Replies[0].SafetyBuffering).Contains("本次没判断成功"), "events after completion are observed through EOF");
        transport.Status = 401; transport.Bodies.Enqueue("full diagnostic " + new string('x', 5000) + " error-tail");
        var before = transport.Requests.Count; var denied = await service.TestCialloAsync("Fixture", model: "gpt-6-astra");
        Check(denied.Replies[0].SafetyBuffering is { Capture: "incomplete", Values.Count: 0 } && transport.Requests.Count == before + 1, "HTTP treatment headers never substitute SSE and 401 has no refresh");
        Check(File.ReadAllText(Path.Combine(denied.DiagnosticDirectory!, "request-1", "response.sse")).EndsWith("error-tail\n"), "HTTP raw diagnostics remain complete");
        transport.Status = 200;
        var collector = new SafetyBufferingCollector(); collector.Observe(SafetyBufferingFixtures.Created()); collector.Observe(SafetyBufferingFixtures.Completed());
        Check(SafetyBufferingDisplay.Summary(collector.Snapshot(true, truncated: true)) == "本次没判断成功", "truncation prevents absent assertion");
        var bounded = new SafetyBufferingCollector(); bounded.Observe(SafetyBufferingFixtures.Created());
        for (var i = 0; i < 140; i++) bounded.Observe(new() { ["type"] = "response.metadata", ["safety_buffering"] = new JsonObject { ["value"] = i } });
        bounded.Observe(SafetyBufferingFixtures.Completed());
        Check(bounded.Snapshot(true) is { Capture: "truncated", DetailsTruncated: true, Values.Count: 128 }, "bounded distinct values cannot claim complete capture");
        var canonical = new SafetyBufferingCollector();
        canonical.Observe(JsonNode.Parse("{\"type\":\"response.metadata\",\"safety_buffering\":{\"b\":true,\"a\":false}}")!.AsObject());
        canonical.Observe(JsonNode.Parse("{\"type\":\"response.metadata\",\"safety_buffering\":{\"a\":false,\"b\":true}}")!.AsObject());
        Check(canonical.Snapshot(false).Values is [{ Count: 2 }], "object key order does not invent conflict");
        Check(SafetyBufferingDisplay.Summary(new(1, "complete", 2, [null!])) == SafetyBufferingDisplay.InvalidHistory, "corrupt observation entries are unknown instead of crashing history");
        Check(SafetyBufferingDisplay.Summary(new(99, "complete", 2, [])) == SafetyBufferingDisplay.InvalidHistory, "unrecognized future metadata is not absence evidence");
        foreach (var corrupt in new[] { new SafetyBufferingObservation(1, "complete", 0, []), new(1, "complete", 2, [], true), new(1, "incomplete", -1, []) })
            Check(SafetyBufferingDisplay.Summary(corrupt) == SafetyBufferingDisplay.InvalidHistory, "invalid capture metadata never fabricates historical absence");
        var nestedPrivate = SafetyBufferingFixtures.Created();
        nestedPrivate["safety_buffering"] = new JsonObject { ["private-key"] = new JsonObject { ["safety_buffering"] = "private-value" } };
        transport.Bodies.Enqueue(SafetyBufferingFixtures.Frame(nestedPrivate) + SafetyBufferingFixtures.Frame(SafetyBufferingFixtures.Completed()));
        var opaque = (await service.TestCialloAsync("Fixture", model: "gpt-6-astra")).Replies.Single().SafetyBuffering!;
        Check(opaque.Values is [{ Kind: "object" }] && !JsonSerializer.Serialize(opaque).Contains("private-"), "opaque field objects are not recursively reinterpreted or exposed");
        var privateMarker = new JsonObject { ["type"] = "response.metadata", ["metadata"] = new JsonObject { ["type"] = "safety_buffering", ["safety_buffering"] = false, ["private-key"] = "private-value" } };
        transport.Bodies.Enqueue(SafetyBufferingFixtures.Frame(SafetyBufferingFixtures.Created()) + SafetyBufferingFixtures.Frame(privateMarker) + SafetyBufferingFixtures.Frame(SafetyBufferingFixtures.Completed()));
        var markerObservation = (await service.TestCialloAsync("Fixture", model: "gpt-6-astra")).Replies.Single().SafetyBuffering!;
        Check(markerObservation.Values is [{ Source: "metadata-marker", Boolean: null }] && !SafetyBufferingDisplay.Summary(markerObservation).Contains("返回结果"), "opaque metadata marker never turns private members into a boolean result");
        foreach (var middle in new[] {
            new JsonObject { ["type"] = "response.in_progress", ["response"] = new JsonObject { ["id"] = "other-response" } },
            new JsonObject { ["type"] = "response.output_text.done", ["response_id"] = "other-response" } })
        {
            transport.Bodies.Enqueue(SafetyBufferingFixtures.Frame(SafetyBufferingFixtures.Created()) + SafetyBufferingFixtures.Frame(middle) + SafetyBufferingFixtures.Frame(SafetyBufferingFixtures.Completed()));
            var mixed = (await service.TestCialloAsync("Fixture", model: "gpt-6-astra")).Replies.Single();
            Check(mixed.State == "completed" && mixed.SafetyBuffering is { Capture: "incomplete" } && !SafetyBufferingDisplay.Summary(mixed.SafetyBuffering).Contains("没有返回"), "intermediate identity mismatch only downgrades observation, not parser success");
        }
        var reordered = new SafetyBufferingCollector();
        reordered.Observe(JsonNode.Parse("{\"type\":\"response.metadata\",\"private-a\":{\"safety_buffering\":false},\"private-b\":{\"safety_buffering\":true}}")!.AsObject());
        reordered.Observe(JsonNode.Parse("{\"type\":\"response.metadata\",\"private-b\":{\"safety_buffering\":true},\"private-a\":{\"safety_buffering\":false}}")!.AsObject());
        Check(reordered.Snapshot(false).Values is [{ Count: 2 }, { Count: 2 }] && !SafetyBufferingDisplay.Summary(reordered.Snapshot(false)).Contains("不一致") && !JsonSerializer.Serialize(reordered.Snapshot(false)).Contains("private-"), "opaque path identity is stable under reordering without exposing keys");
        var wide = new SafetyBufferingCollector(); var wideEvent = SafetyBufferingFixtures.Created();
        wideEvent["data"] = new JsonArray(Enumerable.Range(0, 17000).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        wideEvent["safety_buffering"] = false; wide.Observe(wideEvent); wide.Observe(SafetyBufferingFixtures.Completed());
        Check(wide.Snapshot(true) is { Capture: "truncated", Values.Count: 0, DetailsTruncated: true }, "traversal stops at budget before reading later fields");
        var poisoned = opaque with { Values = [opaque.Values[0] with { EventType = "private-label", Path = "$.private-label" }] };
        Check(!SafetyBufferingDisplay.Details(poisoned).Contains("private-label"), "history detail labels cannot leak arbitrary private text");
        var badRange = opaque with { Values = [opaque.Values[0] with { LastEvent = 999 }] };
        Check(SafetyBufferingDisplay.Summary(badRange) == SafetyBufferingDisplay.InvalidHistory, "out of capture historical event range rejected");
        transport.FailAfterPrefix = true; transport.Bodies.Enqueue(SafetyBufferingFixtures.Stream("incomplete"));
        var disconnected = await service.TestCialloAsync("Fixture", model: "gpt-6-astra");
        Check(disconnected.Replies[0].State == "failed" && disconnected.Replies[0].SafetyBuffering is { Capture: "incomplete", Values.Count: 1 } && disconnected.Replies[0].SafetyBuffering!.Values[0].Boolean == false, "read disconnect preserves observed false with incomplete capture");
        transport.FailAfterPrefix = false;
        var legacyPath = Path.Combine(repository.AccountPath("Fixture"), "ciallo-tests", "legacy.json");
        File.WriteAllText(legacyPath, "{\"Started\":\"2020-01-01T00:00:00Z\",\"Result\":{\"Replies\":[{\"RequestedModel\":\"gpt-6-astra\",\"ResponseModel\":\"\",\"Effort\":\"high\",\"State\":\"completed\",\"Text\":\"legacy\",\"Elapsed\":\"00:00:01\"}],\"Cancelled\":false}}");
        var requestCount = transport.Requests.Count;
        Check(service.CialloHistory("Fixture").Last().Result.Replies.Single().SafetyBuffering is null && SafetyBufferingDisplay.Summary(null) == "本次没判断成功", "legacy absence of metadata remains unknown");
        Check(transport.Requests.Count == requestCount, "legacy history reading is offline");

        var runtime = new VisualRuntime(new ResponsesVisualTestRuntime(service, () => throw new InvalidOperationException("No browser")));
        var visual = new VisualTestService(repository, runtime);
        foreach (var kind in new[] { "false", "true", "null" }) transport.Bodies.Enqueue(SafetyBufferingFixtures.Stream(kind));
        var revision = await visual.RunAsync("Fixture", "gpt-6-astra", VisualTestKind.Revision, "octopus-organ");
        Check(revision.Completed && revision.Steps.Select(s => s.Stage).SequenceEqual(new[] { "初稿", "看图检查", "修改稿" }) && revision.Steps.Select(s => s.SafetyBuffering!.Values.Single().Kind).SequenceEqual(new[] { "boolean", "boolean", "null" }), "three revision stages retain independent production observations");
        Check(revision.Steps[0].SafetyBuffering!.Values[0].Boolean == false && revision.Steps[1].SafetyBuffering!.Values[0].Boolean == true, "revision never reduces different booleans to one");
        foreach (var kind in new[] { "false", "failed" }) transport.Bodies.Enqueue(SafetyBufferingFixtures.Stream(kind));
        var failure = await visual.RunAsync("Fixture", "gpt-6-astra", VisualTestKind.Revision, "octopus-organ");
        Check(failure.Steps.Count == 1 && failure.FailedRequest is { Request: 2, Stage: "看图检查", SafetyBuffering.Capture: "incomplete" } && failure.Message.Contains("detail-tail"), "failed stage saved separately without changing reply counts or error");
        requestCount = transport.Requests.Count;
        var loaded = new VisualTestService(new AccountRepository(root), runtime).History("Fixture");
        Check(loaded.Count == 2 && loaded.Any(r => r.FailedRequest?.SafetyBuffering?.Values[0].Boolean == true) && transport.Requests.Count == requestCount, "visual stage history reload has no request");
        var old = JsonSerializer.SerializeToNode(revision)!.AsObject();
        foreach (var step in old["Steps"]!.AsArray()) step!.AsObject().Remove("SafetyBuffering");
        old.Remove("FailedRequest"); File.WriteAllText(Path.Combine(revision.Directory, "result.json"), old.ToJsonString());
        Check(visual.History("Fixture").Single(r => r.Directory == revision.Directory).Steps.All(s => s.SafetyBuffering is null), "legacy visual history remains unknown per step");
        using var cancel = new CancellationTokenSource();
        transport.Hold = true;
        var pending = service.TestCialloAsync("Fixture", token: cancel.Token, model: "gpt-6-astra");
        await transport.Entered.Task; cancel.Cancel(); var cancelled = await pending;
        Check(cancelled.Cancelled && cancelled.Replies.Single().SafetyBuffering is { Capture: "incomplete" }, "cancelled active request records incomplete not absent");
        transport.Hold = false;
        repository.Profiles.SetTestsFrozen("Fixture", true);
        requestCount = transport.Requests.Count;
        try { await service.TestCialloAsync("Fixture"); Check(false, "frozen request must fail"); } catch (AccountException) { }
        Check(transport.Requests.Count == requestCount && service.CialloHistory("Fixture").Count > 0, "frozen history still readable without new requests");
        Check(File.ReadAllBytes(Path.Combine(repository.AccountPath("Fixture"), "auth.json")).SequenceEqual(auth), "credentials remain unchanged");
        return checks.Count;
    }
    private sealed class Transport : IAccountTransport
    {
        internal Queue<string> Bodies { get; } = new();
        internal List<AccountHttpRequest> Requests { get; } = [];
        internal int Status { get; set; } = 200;
        internal bool Hold { get; set; }
        internal bool FailAfterPrefix { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken token)
        {
            Requests.Add(request);
            if (Hold) { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            var bytes = Encoding.UTF8.GetBytes(Bodies.Dequeue());
            return new(Status, FailAfterPrefix ? new DisconnectedStream(bytes) : new MemoryStream(bytes), headers: new Dictionary<string, string> { ["x-codex-safety-buffering-enabled"] = "true", ["x-codex-safety-buffering-faster-model"] = "false" });
        }
    }
    private sealed class DisconnectedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            => Position == Length ? ValueTask.FromException<int>(new IOException("fixture disconnect tail")) : base.ReadAsync(buffer, token);
    }
    private sealed class VisualRuntime(IVisualTestRuntime responses) : IVisualTestRuntime
    {
        public Task<VisualReply> ReplyAsync(string profile, string model, string prompt, IReadOnlyList<string> images, string directory, CancellationToken token, string effort = ModelTestParameters.DefaultEffort)
            => responses.ReplyAsync(profile, model, prompt, images, directory, token, effort);
        public Task RenderAsync(string page, string screenshot, CancellationToken token)
        {
            File.WriteAllBytes(screenshot, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jv1kAAAAASUVORK5CYII=")); return Task.CompletedTask;
        }
    }
}
