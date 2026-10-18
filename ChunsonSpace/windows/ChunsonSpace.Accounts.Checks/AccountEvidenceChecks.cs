using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.Verification;

internal static class AccountEvidenceChecks
{
    internal static async Task<int> Run(string root)
    {
        Directory.CreateDirectory(root); var facts = new List<object>();
        void Check(bool passed, string name)
        { facts.Add(new { name, passed }); File.WriteAllText(Path.Combine(root, "checks.json"), JsonSerializer.Serialize(facts)); if (!passed) throw new InvalidOperationException(name); }
        AccountRepository Repository(string folder)
        {
            var repository = new AccountRepository(Path.Combine(root, folder));
            foreach (var name in new[] { "SYNTHETIC-A", "SYNTHETIC-B" })
            {
                repository.Profiles.CreateBoundAccount(name, new("http://127.0.0.1:7890"));
                repository.Profiles.SaveAccount(name, new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "synthetic-id-" + name, ["access_token"] = "synthetic-access" } }, overwrite: true);
            }
            return repository;
        }
        const string target = "SYNTHETIC-A"; const string model = "gpt-6-astra";
        var repo = Repository("software"); var clock = new Clock(); var transport = new FakeTransport(); var service = new AccountService(repo, transport, clock: clock);
        var auth = File.ReadAllBytes(Path.Combine(repo.AccountPath(target), "auth.json"));
        Check(repo.Profiles.ReadAccountMarks(target) is { Quality: AccountQuality.Unmarked, Mode: AccountMarkMode.Automatic }, "legacy account starts neutral automatic without requests");
        string Stream(string kind, string requested = model, string text = "Raw ASCII\n中文原文\nstate=ready code=42\n  ")
        {
            var result = SafetyBufferingFixtures.Stream(kind).Replace("fixture-declared", requested, StringComparison.Ordinal);
            return result.Replace(JsonSerializer.Serialize(SafetyBufferingFixtures.Svg), JsonSerializer.Serialize(text), StringComparison.Ordinal);
        }
        async Task<CialloResult> Run(string kind, string requested = model, string effort = "high")
        { clock.Advance(); transport.Text(200, Stream(kind, requested)); return await service.TestCialloAsync(target, model: requested, effort: effort); }
        var missing = await Run("missing");
        Check(repo.Profiles.ReadAccountMarks(target) is { Quality: AccountQuality.SuspectedDegraded, Recovered: false, QualitySource: "software" }, "complete absent SSE becomes abnormal only for supported model");
        var normal = await Run("false");
        Check(repo.Profiles.ReadAccountMarks(target) is { Quality: AccountQuality.Normal, Recovered: true, RecoverySource: "signal" }, "automatic abnormal to normal records signal recovery");
        Check(normal.Replies[0].Text == "Raw ASCII\n中文原文\nstate=ready code=42\n  ", "successful model reply is byte-equivalent text including language whitespace and ordinary code assignments");
        var sent = JsonNode.Parse(transport.Requests[^1].Body!)!;
        Check(sent["instructions"]!.ToString() == "You are a helpful assistant." && sent["input"]![0]!["content"]![0]!["text"]!.ToString() == "ciallo", "actual outgoing body preserves original instruction and probe without language direction");
        foreach (var kind in new[] { "incomplete", "missing-incomplete", "no-created", "mismatch", "model-mismatch", "conflict", "null", "true", "object", "malformed", "truncated", "failed" })
        {
            await Run(kind);
            Check(repo.Profiles.ReadAccountMarks(target) is { Quality: AccountQuality.Normal, Inconclusive: true }, kind + " retains last valid classification");
        }
        foreach (var unsupported in new[] { "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol", "gpt-unknown" })
        {
            var countBefore = transport.Requests.Count;
            try { await service.TestCialloAsync(target, model: unsupported); Check(false, "unsupported should fail"); } catch (ArgumentException) { }
            Check(transport.Requests.Count == countBefore, unsupported + " blocked before any request");
            Check(AccountEvidence.Classify([new(unsupported, unsupported, "high", true, new(1, "complete", 2, []))]).Quality is null, unsupported + " historical import cannot classify");
        }
        foreach (var invalidEffort in new[] { "none", "low", "medium", "xhigh", "max", "ultra" })
        {
            var countBefore = transport.Requests.Count;
            try { await service.TestCialloAsync(target, model: model, effort: invalidEffort); Check(false, "invalid effort should fail"); } catch (ArgumentException) { }
            var runtime = new ResponsesVisualTestRuntime(service, () => throw new InvalidOperationException());
            try { await runtime.ReplyAsync(target, model, "User supplied 中文 prompt", [], Path.Combine(root, "must-not-create"), default, invalidEffort); Check(false, "visual invalid effort should fail"); } catch (ArgumentException) { }
            Check(transport.Requests.Count == countBefore && !Directory.Exists(Path.Combine(root, "must-not-create")), invalidEffort + " blocked at both request boundaries");
        }
        // Conflicting supported models in one batch must not depend on their enumeration order. Excluded models do not vote.
        clock.Advance(); foreach (var requested in CialloTest.Models) transport.Text(200, Stream(requested == "gpt-6.1-sol" ? "missing" : "false", requested));
        await service.TestCialloAsync(target);
        Check(repo.Profiles.ReadAccountMarks(target) is { Quality: AccountQuality.Normal, Inconclusive: true }, "conflicting supported batch does not choose last model");
        clock.Advance(); foreach (var requested in CialloTest.Models) transport.Text(200, Stream(AccountEvidence.Supported(requested) ? "false" : "missing", requested));
        await service.TestCialloAsync(target);
        Check(repo.Profiles.ReadAccountMarks(target) is { Quality: AccountQuality.Normal, Inconclusive: false }, "excluded models do not contradict unanimous supported evidence");
        var identity = repo.Profiles.ReadAccountIdentityHash(target); var before = repo.Profiles.ReadAccountMarks(target);
        Check(repo.Profiles.ApplyAccountEvidence(target, identity, clock.UtcNow.AddMinutes(-2), AccountEvidence.Id("old"), AccountQuality.SuspectedDegraded, "software", model, "旧记录").Contains("旧记录")
            && repo.Profiles.ReadAccountMarks(target) == before, "stale result cannot overwrite a later completed test");
        repo.Profiles.SetAccountMarks(target, AccountQuality.SuspectedDegraded, true, false, clock.UtcNow);
        await Run("false");
        Check(repo.Profiles.ReadAccountMarks(target) is { Quality: AccountQuality.SuspectedDegraded, Mode: AccountMarkMode.Manual, Problem: true, Recovered: false }, "automatic results never replace manual choice or invent recovery");
        repo.Profiles.SetAccountMarks(target, AccountQuality.Unmarked, false, false, clock.UtcNow, AccountMarkMode.Automatic);
        Check(repo.Profiles.ReadAccountMarks(target).Quality == AccountQuality.Unmarked, "enabling automatic mode does not replay history");
        await Run("false");
        Check(repo.Profiles.ReadAccountMarks(target) is { Quality: AccountQuality.Normal, Mode: AccountMarkMode.Automatic, Recovered: false }, "new result after enable classifies without guessed recovery");
        Check(repo.Profiles.ReadAccountMarks("SYNTHETIC-B").Quality == AccountQuality.Unmarked && auth.SequenceEqual(File.ReadAllBytes(Path.Combine(repo.AccountPath(target), "auth.json"))), "exact account targeting preserves other row and credentials");
        var requests = transport.Requests.Count; _ = new AccountService(new AccountRepository(repo.Root), transport).CialloHistory(target);
        Check(requests == transport.Requests.Count && new FileProfileStore(repo.Root).ReadAccountMarks(target) == repo.Profiles.ReadAccountMarks(target), "reload preserves marks and history without requests");
        // A second account store receives an explicitly selected, hash-pinned production recording.
        var imported = Repository("import"); var trace = Path.Combine(normal.DiagnosticDirectory!, "request-1");
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(trace, file))));
        var pins = new[] { Hash("trace-summary.json"), Hash("wire-request.json"), Hash("response.sse") };
        var import = AccountTraceImport.Import(imported.Profiles, target, trace, pins[0], pins[1], pins[2]);
        Check(import.Outcome == "自动标记已更新" && imported.Profiles.ReadAccountMarks(target) is { Quality: AccountQuality.Normal, QualitySource: "trace-import" }, "explicit conversation trace bridge reparses production SSE using shared classifier");
        before = imported.Profiles.ReadAccountMarks(target);
        Check(AccountTraceImport.Import(imported.Profiles, target, trace, pins[0], pins[1], pins[2]).Outcome.Contains("重复") && imported.Profiles.ReadAccountMarks(target) == before, "duplicate import is idempotent");
        void Rejected(Action action, string name)
        { try { action(); Check(false, name); } catch (InvalidDataException) { Check(true, name); } }
        Rejected(() => AccountTraceImport.Import(imported.Profiles, "SYNTHETIC-B", trace, pins[0], pins[1], pins[2]), "import cannot target another account identity");
        Rejected(() => AccountTraceImport.Import(imported.Profiles, target, trace, pins[0], pins[1], new string('0', 64)), "unverified or changed stream rejected before persistence");
        var originalTrace = trace;
        trace = Path.Combine(missing.DiagnosticDirectory!, "request-1");
        Check(AccountTraceImport.Import(imported.Profiles, target, trace, Hash("trace-summary.json"), Hash("wire-request.json"), Hash("response.sse")).Outcome.Contains("旧记录")
            && imported.Profiles.ReadAccountMarks(target) == before, "older imported abnormal trace cannot overwrite newer normal result");
        var incomplete = await Run("incomplete"); trace = Path.Combine(incomplete.DiagnosticDirectory!, "request-1");
        AccountTraceImport.Import(imported.Profiles, target, trace, Hash("trace-summary.json"), Hash("wire-request.json"), Hash("response.sse"));
        Check(imported.Profiles.ReadAccountMarks(target) is { Quality: AccountQuality.Normal, Inconclusive: true }, "imported incomplete false retains normal and records inconclusive");
        imported.Profiles.SetAccountMarks(target, AccountQuality.SuspectedDegraded, true, false);
        Check(AccountTraceImport.Import(imported.Profiles, target, originalTrace, pins[0], pins[1], pins[2]).Outcome == "保留手动标记"
            && imported.Profiles.ReadAccountMarks(target).Quality == AccountQuality.SuspectedDegraded, "trace import respects manual override");
        var legacyTrace = Path.Combine(root, "legacy-unbound-trace"); Directory.CreateDirectory(legacyTrace);
        foreach (var file in new[] { "wire-request.json", "trace-summary.json", "response.sse" }) File.Copy(Path.Combine(originalTrace, file), Path.Combine(legacyTrace, file));
        var legacySummary = JsonNode.Parse(File.ReadAllText(Path.Combine(legacyTrace, "trace-summary.json")))!.AsObject(); legacySummary.Remove("accountIdentityHash"); legacySummary.Remove("schema");
        File.WriteAllText(Path.Combine(legacyTrace, "trace-summary.json"), legacySummary.ToJsonString()); trace = legacyTrace;
        Rejected(() => AccountTraceImport.Import(imported.Profiles, target, trace, Hash("trace-summary.json"), Hash("wire-request.json"), Hash("response.sse")), "legacy audit output without recorded identity cannot become machine evidence");
        var native = Repository("native-import");
        File.WriteAllText(Path.Combine(root, "native-import-fixture.json"), JsonSerializer.Serialize(new { accountRoot = native.Root, profile = target, directory = originalTrace, pins, synthetic = true }));
        foreach (var invalidMarks in new[] { "{broken", "{\"version\":99,\"quality\":\"Normal\"}" })
        {
            var damaged = Repository("damaged-" + facts.Count); var damagedTransport = new FakeTransport();
            var damagedService = new AccountService(damaged, damagedTransport);
            var damagedPath = Path.Combine(damaged.AccountPath(target), FileProfileStore.AccountMarksFile);
            File.WriteAllText(damagedPath, invalidMarks); damagedTransport.Text(200, Stream("false"));
            var completed = await damagedService.TestCialloAsync(target, model: model);
            Check(completed.Completed == 1 && completed.Replies[0].Text == "Raw ASCII\n中文原文\nstate=ready code=42\n  "
                && damagedService.CialloHistory(target).Single().Result.Completed == 1, "unreadable optional marks do not discard completed Ciallo reply/history");
            Check(File.ReadAllText(damagedPath) == invalidMarks && damagedTransport.Requests.Count == 1 && damaged.Profiles.ReadAccountMarks(target).ReadFailed,
                "unreadable marks retained and surfaced without retry");
            damagedTransport.Text(200, Stream("false"));
            var damagedRuntime = new ResponsesVisualTestRuntime(damagedService, () => throw new InvalidOperationException());
            var raw = await damagedRuntime.ReplyAsync(target, model, "User prompt 中文", [], Path.Combine(root, "damaged-visual-" + facts.Count), default);
            Check(raw.Text == completed.Replies[0].Text && File.ReadAllText(damagedPath) == invalidMarks, "optional annotation failure cannot turn successful artwork response into failure");
        }
        var exhausted = Repository("connectivity-rejected"); var rejectedTransport = new FakeTransport();
        exhausted.Profiles.ApplyAccountEvidence(target, exhausted.Profiles.ReadAccountIdentityHash(target), DateTimeOffset.UtcNow.AddDays(-1), AccountEvidence.Id("prior-connectivity"), AccountQuality.Normal, "software", model, "旧的完整结果");
        foreach (var unused in ModelTestParameters.Models) rejectedTransport.Text(400, "SYNTHETIC request rejected");
        try { await new AccountService(exhausted, rejectedTransport).TestAccountAsync(target); } catch (AccountException) { }
        Check(exhausted.Profiles.ReadAccountMarks(target) is { Quality: AccountQuality.Normal, Inconclusive: true }, "all supported connectivity attempts rejected leaves truthful inconclusive with prior quality");
        var changed = Repository("identity-changed"); var changedTransport = new FakeTransport();
        changedTransport.Text(200, Stream("false", "gpt-6.1-sol"));
        var changedService = new AccountService(changed, changedTransport);
        var progress = new ImmediateProgress(reply => {
            if (reply.State == "completed") File.WriteAllText(Path.Combine(changed.AccountPath(target), "auth.json"), "{\"tokens\":{\"account_id\":\"SYNTHETIC-new-id\",\"access_token\":\"synthetic-new-token\"}}");
        });
        var changedResult = await changedService.TestCialloAsync(target, progress);
        Check(changedTransport.Requests.Count == 1 && changedResult.Completed == 1 && changed.Profiles.ReadAccountMarks(target).Quality == AccountQuality.Unmarked,
            "changed identity stops next batch request and cannot label replacement account");
        var boundaryRepo = Repository("request-boundaries"); var boundaryTransport = new FakeTransport(); var boundaryService = new AccountService(boundaryRepo, boundaryTransport);
        var boundaryRuntime = new ResponsesVisualTestRuntime(boundaryService, () => throw new InvalidOperationException("No browser"));
        var cliLookups = 0;
        var nativeRuntime = new CodexVisualTestRuntime(boundaryRepo, () => { cliLookups++; throw new InvalidOperationException("No real CLI"); }, () => throw new InvalidOperationException("No browser"));
        foreach (var unsupported in new[] { "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol", "gpt-arbitrary-alias" })
        {
            try { await boundaryRuntime.ReplyAsync(target, unsupported, "Original task", [], Path.Combine(root, "rejected-runtime"), default); Check(false, "HTTP runtime rejects model"); } catch (ArgumentException) { }
            try { await nativeRuntime.ReplyAsync(target, unsupported, "Original task", [], Path.Combine(root, "rejected-native"), default); Check(false, "native runtime rejects model"); } catch (ArgumentException) { }
            try { _ = CodexVisualTestRuntime.BuildStartInfo("unused", "unused", "http://127.0.0.1:7890", unsupported, []); Check(false, "native builder rejects model"); } catch (ArgumentException) { }
            Check(boundaryTransport.Requests.Count == 0 && cliLookups == 0, unsupported + " cannot pass alternate request builders");
        }
        boundaryRepo.Profiles.SetTestsFrozen(target, true);
        try { await nativeRuntime.ReplyAsync(target, model, "Original task", [], Path.Combine(root, "frozen-native"), default); Check(false, "native frozen guard"); } catch (AccountException error) { Check(error.Code == AccountError.Busy && cliLookups == 0, "callable native path respects frozen account before CLI lookup"); }
        boundaryRepo.Profiles.SetTestsFrozen(target, false);
        using (var goldenStream = typeof(AccountEvidenceChecks).Assembly.GetManifestResourceStream("EnglishPromptProvenance")!)
        {
            var golden = JsonNode.Parse(goldenStream)!;
            foreach (var entry in golden["prompts"]!.AsObject().Where(p => !p.Key.StartsWith("instructions/")))
            {
                var exactPrompt = entry.Value!["text"]!.ToString();
                boundaryTransport.Text(200, Stream("false"));
                var raw = await boundaryRuntime.ReplyAsync(target, model, exactPrompt, [], Path.Combine(root, "outgoing-" + boundaryTransport.Requests.Count), default);
                var body = JsonNode.Parse(boundaryTransport.Requests[^1].Body!)!;
                Check(body["input"]![0]!["content"]![0]!["text"]!.ToString() == exactPrompt && body["instructions"]!.ToString() == golden["prompts"]!["instructions/helpful"]!["text"]!.ToString()
                    && body["reasoning"]!["effort"]!.ToString() == "high" && raw.Text == "Raw ASCII\n中文原文\nstate=ready code=42\n  ", entry.Key + " exact approved English body and unaltered multilingual raw reply");
            }
        }
        return facts.Count;
    }
    private sealed class ImmediateProgress(Action<CialloReply> callback) : IProgress<CialloReply>
    { public void Report(CialloReply value) => callback(value); }
    private sealed class Clock : IAccountClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow.AddDays(-1);
        public void Advance() => UtcNow = UtcNow.AddMinutes(1);
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
