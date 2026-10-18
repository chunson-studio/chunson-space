using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

internal static class CialloChecks
{
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        var repository = new AccountRepository(root);
        repository.Profiles.CreateBoundAccount("Ciallo", new("http://127.0.0.1:7890", "Asia/Tokyo", "en-US"));
        repository.Profiles.SaveAccount("Ciallo", new JsonObject { ["tokens"] = new JsonObject { ["access_token"] = "fixture-access", ["account_id"] = "fixture-id" } }, overwrite: true);
        var transport = new FakeTransport(); var service = new AccountService(repository, transport);
        for (var i = 0; i < CialloTest.Models.Count; i++)
        {
            if (i == 1) transport.Text(404, "model unavailable");
            else transport.Text(200, "data: " + new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject {
                ["status"] = "completed", ["model"] = "declared-fixture", ["usage"] = new JsonObject { ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = i == 0 ? 0 : 4096 } }, ["output"] = new JsonArray(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "reply-" + i }) }) } }.ToJsonString() + "\n\n");
        }
        var result = await service.TestCialloAsync("Ciallo");
        Check(result.Version == CialloTest.Version && JsonNode.Parse(transport.Requests[0].Body!)!["instructions"]!.ToString() == BuiltInTestInstructions.Helpful, "new Ciallo instructions and persisted version agree");
        var legacy = System.Text.Json.JsonSerializer.Deserialize<CialloResult>("{\"Replies\":[],\"Cancelled\":false}")!;
        Check(legacy.Version == "ciallo-v1", "old missing-version Ciallo keeps original instruction identity");
        Check(result.Completed == CialloTest.Models.Count - 1 && result.Replies[1].State == "failed", "unsupported model remains a failure; later models still run");
        Check(transport.Requests.Count == CialloTest.Models.Count, "one request per model with no fallback or extra scoring");
        var bodies = transport.Requests.Select(request => JsonNode.Parse(request.Body!)!).ToArray();
        Check(bodies.Select(body => body["model"]!.GetValue<string>()).SequenceEqual(CialloTest.Models), "all requested models retained in order");
        Check(bodies.All(body => body["reasoning"]?["effort"]?.GetValue<string>() == "high" && body["input"]!.ToJsonString().Contains("ciallo")), "every model gets high effort and ciallo");
        Check(result.Replies[0].Text == "reply-0" && result.Replies[0].ResponseModel == "declared-fixture", "raw text and declared model remain separate");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Check((await service.TestCialloAsync("Ciallo", token: cancel.Token)).Cancelled && transport.Requests.Count == CialloTest.Models.Count, "cancel prevents all further model requests");
        var history = new AccountService(new AccountRepository(root), transport).CialloHistory("Ciallo");
        Check(history.Count == 2 && history[0].Result.Cancelled && history[1].Result.Replies[0].Text == "reply-0", "Ciallo history persists cancelled and completed responses across service restart");
        Check(transport.Requests.Count == CialloTest.Models.Count && history.All(entry => entry.Finished), "history reads do not send requests and final records are marked finished");
        Check(history[1].Result.Replies[0].Usage?.CachedInputTokens == 0, "history preserves real zero and nonzero cached token readings");
        File.WriteAllText(Path.Combine(repository.AccountPath("Ciallo"), "ciallo-tests", "broken.json"), "{invalid");
        Check(service.CialloHistory("Ciallo").Count == 2, "corrupt Ciallo record does not hide other history");
        repository.Profiles.CreateBoundAccount("Other", new("http://127.0.0.1:7891"));
        Check(service.CialloHistory("Other").Count == 0, "Ciallo history is scoped to the selected account");
        transport.Text(200, "data: {\"type\":\"response.completed\",\"response\":{\"model\":\"declared-fixture\",\"status\":\"completed\",\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"" + new string('x', 500) + " fixture-access\"}]}]}}\n\n");
        var single = await service.TestCialloAsync("Ciallo", effort: "high", model: "gpt-6-astra");
        Check(single.Completed == 1 && single.Replies[0].Effort == "high" && transport.Requests.Count == CialloTest.Models.Count + 1, "single selected model sends exactly one request");
        Check(JsonNode.Parse(transport.Requests[^1].Body!)!["reasoning"]!["effort"]!.ToString() == "high" && File.Exists(Path.Combine(single.DiagnosticDirectory!, "request-1", "trace-summary.json")), "Ciallo effort and complete diagnostics are retained");
        Check(single.Replies[0].Text.Length > 500 && !single.Replies[0].Text.Contains("fixture-access"), "Ciallo saves complete reply without error-message truncation and still redacts credentials");
        var report = new IpQualityReport(true, "203.0.113.3", "United States", "", "America/New_York", "", "", null, null, null, null, null, DateTimeOffset.UtcNow, "fixture", "fixture") { CountryCode = "US" };
        var egress = new EgressService(repository, transport, (_, _) => Task.FromResult(report));
        var matched = await egress.MatchEnvironmentAsync("Ciallo");
        Check(matched.Egress.Ok && matched.Environment.Timezone == "America/New_York" && matched.Environment.Locale == "en-US", "combined environment action updates timezone while retaining explicit language");
        var before = repository.Profiles.ReadEnvironment("Ciallo");
        var failed = new EgressService(repository, transport, (_, _) => Task.FromResult(report with { Success = false, Ip = "", Timezone = "" }));
        Check(!(await failed.MatchEnvironmentAsync("Ciallo")).Egress.Ok && repository.Profiles.ReadEnvironment("Ciallo") == before, "failed region match preserves the complete profile");
        return count;
    }
}
