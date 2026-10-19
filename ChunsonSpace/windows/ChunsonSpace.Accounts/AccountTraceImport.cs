using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

// Explicit local trace bridge. Pins must come from a trusted recording receipt, not assistant prose.
// Local hashes prove unchanged files, not server authenticity. No directory scanning or network access.
public static class AccountTraceImport
{
    public sealed record Result(string Outcome, string Policy, string SummarySha256, string RequestSha256, string StreamSha256);
    public sealed record Inspection(string IdentityHash, DateTimeOffset Started, AccountTestObservation Observation,
        AccountEvidenceVerdict Verdict, string SummarySha256, string RequestSha256, string StreamSha256);
    public static Result Import(FileProfileStore profiles, string profile, string directory, string summarySha, string requestSha, string streamSha)
    {
        var inspected = Inspect(directory, summarySha, requestSha, streamSha, profiles.ReadAccountIdentityHash(profile));
        var outcome = AccountEvidence.Apply(profiles, profile, inspected.IdentityHash, inspected.Started,
            AccountEvidence.Id(streamSha.ToUpperInvariant() + requestSha.ToUpperInvariant()), [inspected.Observation], source: "trace-import");
        return new(outcome, AccountEvidence.Policy, summarySha, requestSha, streamSha);
    }
    // Same pinned-source validation as import, with no profile store or write side effects.
    public static Inspection Inspect(string directory, string summarySha, string requestSha, string streamSha, string expectedIdentity)
    {
        byte[] Read(string name, string expected, int limit)
        {
            var path = Path.Combine(Path.GetFullPath(directory), name);
            PhysicalFileStorage.RejectLinks(path);
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit) || !File.Exists(path) || new FileInfo(path).Length > limit)
                throw new InvalidDataException("测试文件或校验值无效。");
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > limit || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("测试文件与指定校验值不符，未应用。");
            return bytes;
        }
        var summary = JsonNode.Parse(Read("trace-summary.json", summarySha, 1_048_576)) as JsonObject ?? throw new InvalidDataException("测试摘要无效。");
        var request = JsonNode.Parse(Read("wire-request.json", requestSha, 16_777_216)) as JsonObject ?? throw new InvalidDataException("请求记录无效。");
        var stream = Read("response.sse", streamSha, 67_108_864);
        var identity = summary["accountIdentityHash"]?.GetValue<string>() ?? "";
        if (summary["schema"]?.GetValue<int>() != 2 || identity.Length != 64 || !identity.All(Uri.IsHexDigit) || expectedIdentity != identity)
            throw new InvalidDataException("旧记录缺少身份凭据，或账号身份不一致；未应用。不能补猜身份。");
        if (!DateTimeOffset.TryParse(summary["started"]?.GetValue<string>(), out var started) || started > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("测试时间无效。");
        if (request["method"]?.GetValue<string>() != "POST" || request["endpoint"]?.GetValue<string>() != AccountEndpoints.Responses.GetLeftPart(UriPartial.Path)
            || request["body"]?["stream"]?.GetValue<bool>() != true)
            throw new InvalidDataException("记录不是受支持的模型流式测试。");
        var model = request["body"]?["model"]?.GetValue<string>() ?? "";
        var effort = request["body"]?["reasoning"]?["effort"]?.GetValue<string>() ?? "";
        var responseModel = summary["responseModel"]?.GetValue<string>() ?? "";
        var collector = new SafetyBufferingCollector(); var frame = new StringBuilder(); var lines = 0; var modelConsistent = true;
        void Consume()
        {
            if (frame.Length == 0) return;
            var raw = frame.ToString(); frame.Clear(); if (raw == "[DONE]") return;
            var payload = JsonNode.Parse(raw) as JsonObject ?? throw new InvalidDataException("流式事件无效。");
            if (payload["response"]?["model"] is { } observedModel && observedModel.GetValue<string>() != responseModel) modelConsistent = false;
            collector.Observe(payload);
        }
        using var reader = new StringReader(new UTF8Encoding(false, true).GetString(stream));
        while (reader.ReadLine() is { } line)
        {
            lines++;
            if (line.Length == 0) { Consume(); continue; }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            if (frame.Length > 0) frame.Append('\n'); frame.Append(line[5..].TrimStart(' '));
            if (frame.Length > 1_048_576) throw new InvalidDataException("流式事件超过限制。");
        }
        Consume();
        var complete = summary["readBoundary"]?.GetValue<string>() == "EOF" && summary["state"]?.GetValue<string>() == "已完成"
            && summary["httpStatus"]?.GetValue<int>() is >= 200 and < 300 && summary["truncated"]?.GetValue<bool>() == false
            && summary["bytes"]?.GetValue<long>() == stream.Length && summary["lines"]?.GetValue<int>() == lines && modelConsistent;
        var observation = collector.Snapshot(complete, !complete);
        var item = new AccountTestObservation(model, responseModel, effort, complete, observation);
        return new(identity, started, item, AccountEvidence.Classify([item]), summarySha, requestSha, streamSha);
    }
}
