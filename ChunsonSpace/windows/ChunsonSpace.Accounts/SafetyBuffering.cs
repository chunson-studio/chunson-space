using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChunsonSpace.Accounts;

// Only protocol observations, never an account/model assessment. No raw object values are persisted.
public sealed record SafetyBufferingValue(string EventType, string Path, string Source, string Kind,
    bool? Boolean, string Fingerprint, int Members, int FirstEvent, int LastEvent, int Count);
public sealed record SafetyBufferingObservation(int Schema, string Capture, int Events,
    IReadOnlyList<SafetyBufferingValue> Values, bool DetailsTruncated = false);
public sealed record VisualRequestObservation(int Request, string Stage, SafetyBufferingObservation? SafetyBuffering);

// Fed by the production Responses parser after complete SSE data frames are parsed.
public sealed class SafetyBufferingCollector
{
    private readonly List<SafetyBufferingValue> values = [];
    private int events, created, completed;
    private string? createdId, completedId, observedId, observedModel;
    private bool invalid, limited;
    private static readonly HashSet<string> PathNames = ["response", "metadata", "output", "content", "item", "data", "error", "usage", "safety_buffering"];
    private static readonly HashSet<string> EventNames = ["response.created", "response.in_progress", "response.completed", "response.failed", "response.incomplete", "response.metadata", "response.output_text.delta", "response.output_text.done", "response.output_item.added", "response.output_item.done", "response.content_part.added", "response.content_part.done", "error"];
    public void Observe(JsonObject payload)
    {
        events++;
        var type = payload["type"] is JsonValue t && t.TryGetValue<string>(out var text) ? text : "";
        var response = payload["response"] as JsonObject;
        if (response?["model"] is { } modelNode)
        {
            if (modelNode is not JsonValue modelValue || !modelValue.TryGetValue<string>(out var model) || string.IsNullOrWhiteSpace(model)) invalid = true;
            else { if (observedModel is not null && observedModel != model) invalid = true; observedModel ??= model; }
        }
        string? Id() => response?["id"] is JsonValue id && id.TryGetValue<string>(out var s) ? s : null;
        // Mixed response identities cannot prove absence, even if the endpoints match.
        foreach (var idNode in new[] { response?["id"], payload["response_id"] })
        {
            if (idNode is null) continue;
            if (idNode is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id)) { invalid = true; continue; }
            if (observedId is not null && observedId != id) invalid = true;
            observedId ??= id;
        }
        if (type == "response.created") { created++; createdId = Id(); }
        if (type == "response.completed")
        {
            completed++; completedId = Id();
            if (created != 1 || response?["status"]?.ToString() != "completed") invalid = true;
        }
        if (type is "response.failed" or "response.incomplete" or "error") invalid = true;
        var eventType = SafeEvent(type);
        var marker = type == "response.metadata" && payload["metadata"] is JsonObject m && m["type"]?.ToString() == "safety_buffering";
        var visited = 0;
        void Walk(JsonNode? node, string path, int depth = 0)
        {
            if (++visited > 16384 || depth > 64) { limited = true; return; }
            if (node is JsonObject obj)
            {
                foreach (var pair in obj)
                {
                    if (visited >= 16384) { limited = true; break; }
                    // Stable opaque names avoid merging unrelated keys or inventing conflicts after key reordering.
                    var location = path + "." + (PathNames.Contains(pair.Key) ? pair.Key : "[属性" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Key))) + "]");
                    // Display-path expansion is not unread protocol data. Preserve a stable,
                    // distinct opaque scope and continue scanning every node under the real budgets.
                    if (location.Length > 240) location = "$.[路径" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(location))) + "]";
                    // An observed value is opaque: do not reinterpret its private members as protocol fields.
                    if (pair.Key == "safety_buffering") { visited++; Add(pair.Value, location, "field", eventType); }
                    else if (marker && ReferenceEquals(obj, payload) && pair.Key == "metadata") { visited++; Add(pair.Value, location, "metadata-marker", eventType); }
                    else Walk(pair.Value, location, depth + 1);
                }
            }
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                {
                    if (visited >= 16384) { limited = true; break; }
                    Walk(array[i], path + "[" + i + "]", depth + 1);
                }
        }
        Walk(payload, "$");
    }
    private void Add(JsonNode? value, string path, string source, string eventType)
    {
        bool? boolean = value is JsonValue scalar && scalar.TryGetValue<bool>(out var b) ? b : null;
        var kind = value is null ? "null" : boolean.HasValue ? "boolean" : value is JsonObject ? "object" : value is JsonArray ? "array" : "scalar";
        // Canonical hash distinguishes conflicting structured values without retaining opaque/private data.
        static JsonNode? Canonical(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
            JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
            _ => node?.DeepClone()
        };
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(value)?.ToJsonString() ?? "null")));
        var existing = values.FindIndex(v => v.EventType == eventType && v.Path == path && v.Source == source && v.Fingerprint == fingerprint);
        if (existing >= 0) { var v = values[existing]; values[existing] = v with { LastEvent = events, Count = v.Count + 1 }; return; }
        if (values.Count >= 128) { limited = true; return; }
        values.Add(new(eventType, path, source, kind, boolean, fingerprint, value is JsonObject o ? o.Count : value is JsonArray a ? a.Count : 0, events, events, 1));
    }
    public SafetyBufferingObservation Snapshot(bool eof, bool failed = false, bool truncated = false)
    {
        var complete = eof && !failed && !invalid && created == 1 && completed == 1
            && !string.IsNullOrWhiteSpace(createdId) && createdId == completedId;
        return new(1, truncated || limited ? "truncated" : complete ? "complete" : "incomplete", events, values.ToArray(), limited);
    }
    internal static string SafeEvent(string text) => EventNames.Contains(text) ? text : "其他事件";
}

// One interpretation shared by both result surfaces, including deserialized history.
public static class SafetyBufferingDisplay
{
    public const string Unknown = "本次没判断成功";
    public const string InvalidHistory = "本次没判断成功";
    public const string Explanation = "按当前测试规则判断；没读完整时保留原标记。";
    public static bool Valid(SafetyBufferingObservation? observation) => observation is { Schema: 1, Values: not null }
        && observation.Events >= 0 && observation.Values.Count <= 128
        && observation.Capture is "complete" or "incomplete" or "truncated"
        && (observation.Capture != "complete" || (observation.Events >= 2 && !observation.DetailsTruncated))
        && (!observation.DetailsTruncated || observation.Capture == "truncated")
        && observation.Values.All(v => v is not null && v.FirstEvent >= 1 && v.FirstEvent <= v.LastEvent && v.LastEvent <= observation.Events
            && v.Count >= 1 && v.Count <= v.LastEvent - v.FirstEvent + 1 && v.Members >= 0
            && v.Source is "field" or "metadata-marker" && v.Kind is "boolean" or "null" or "object" or "array" or "scalar"
            && (v.Kind == "boolean" ? v.Boolean.HasValue : !v.Boolean.HasValue)
            && (v.Source != "metadata-marker" || (v.Kind == "object" && v.Path == "$.metadata"))
            && v.EventType is { Length: > 0 and <= 80 } && v.Path is { Length: > 0 and <= 240 }
            && v.Fingerprint is { Length: 64 } && v.Fingerprint.All(Uri.IsHexDigit));
    private static string Bounded(string? text, int limit) => ErrorRedaction.Scrub(text is null ? "" : text[..Math.Min(text.Length, limit)]);
    private static string Value(SafetyBufferingValue v) => v.Source == "metadata-marker" ? "metadata 标记（非布尔）" : v.Kind switch
    {
        "boolean" when v.Boolean.HasValue => v.Boolean.Value ? "true" : "false",
        "null" => "null（显式）", "object" => "结构化对象", "array" => "数组", _ => "其他值"
    };
    public static string Summary(SafetyBufferingObservation? observation)
    {
        return AccountEvidence.Label(AccountEvidence.FieldQuality(observation));
    }
    public static string Details(SafetyBufferingObservation? observation)
    {
        const string note = "按多轮对照验证的当前规则判断账号状态；不证明后台型号或恢复原因。HTTP 处理头另见原始诊断；不代替本字段。对象仅保留类型、大小与 SHA-256，不展示私有值。";
        if (!Valid(observation)) return Summary(observation) + "\n没有有效的 safety_buffering 观测记录，不能据此认定字段缺失或 false。\n" + note;
        var lines = observation!.Values.Take(128).Select(v =>
            $"事件 #{v.FirstEvent}–{v.LastEvent} ×{v.Count} · {SafetyBufferingCollector.SafeEvent(v.EventType)} · {SafePath(v.Path)} · {(v.Source == "field" ? "字段" : "metadata 标记")}：{Value(v)}"
            + (v.Kind is "object" or "array" or "scalar" ? $"；成员 {v.Members}；SHA-256 {(v.Fingerprint is { Length: 64 } hash && hash.All(Uri.IsHexDigit) ? hash : "不可用")}" : ""));
        return Summary(observation) + $"\nsafety_buffering（SSE）；capture={observation.Capture}；捕获事件：{observation.Events}；范围：本次请求" + (observation.DetailsTruncated ? "；详情达到上限" : "")
            + "\n" + string.Join("\n", lines) + "\n" + note;
    }
    // Deserialized metadata is not an authority to display arbitrary private labels.
    private static string SafePath(string text) => Regex.IsMatch(text, @"^\$(\.(response|metadata|output|content|item|data|error|usage|safety_buffering|\[(属性|路径)[0-9A-F]+\])|\[[0-9]+\])*$", RegexOptions.CultureInvariant) ? Bounded(text, 240) : "位置未保存";
}
