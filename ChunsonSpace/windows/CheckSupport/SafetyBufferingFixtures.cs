using System.Text.Json.Nodes;

namespace ChunsonSpace.Verification;

// Synthetic protocol frames shared by parser and real-bound WPF fixtures. No network.
internal static class SafetyBufferingFixtures
{
    internal const string Svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 960 600'><rect width='960' height='600' fill='#e1f0dd'/><text x='80' y='240'>OFFLINE FIXTURE</text></svg>";
    internal static string Frame(JsonObject payload) => "data: " + payload.ToJsonString() + "\n\n";
    internal static JsonObject Created() => new() { ["type"] = "response.created", ["response"] = new JsonObject { ["id"] = "fixture-response", ["status"] = "in_progress" } };
    internal static JsonObject Completed(string text = Svg) => new() { ["type"] = "response.completed", ["response"] = new JsonObject {
        ["id"] = "fixture-response", ["status"] = "completed", ["model"] = "fixture-declared",
        ["output"] = new JsonArray(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) }) } };
    internal static string Stream(string kind, string text = Svg)
    {
        var created = Created(); var completed = Completed(text); var middle = "";
        switch (kind)
        {
            case "false": created["safety_buffering"] = false; break;
            case "true": created["safety_buffering"] = true; break;
            case "null": created["safety_buffering"] = null; break;
            case "object": created["safety_buffering"] = new JsonObject { ["opaque-private-key"] = "fixture-opaque-secret", ["access_token"] = "fixture-access", ["enabled"] = false }; break;
            case "scalar": created["safety_buffering"] = "false"; break;
            case "array": created["safety_buffering"] = new JsonArray(false, true); break;
            case "truncated":
                for (var i = 0; i < 140; i++) middle += Frame(new() { ["type"] = "response.metadata", ["safety_buffering"] = new JsonObject { ["private"] = i } });
                break;
            case "conflict": created["safety_buffering"] = false; completed["safety_buffering"] = true; break;
            case "nested": created["response"]!["safety_buffering"] = false; completed["safety_buffering"] = true; break;
            case "marker": middle = Frame(new() { ["type"] = "response.metadata", ["metadata"] = new JsonObject { ["type"] = "safety_buffering", ["opaque"] = "fixture-opaque-secret" } }); break;
            case "incomplete": created["safety_buffering"] = false; return Frame(created);
            case "missing-incomplete": return Frame(created);
            case "failed": created["safety_buffering"] = true; return Frame(created) + Frame(new() { ["type"] = "response.failed", ["message"] = "fixture raw error: detail-tail" });
            case "malformed": return Frame(created) + Frame(completed) + "data: {broken\n\n";
            case "mismatch": completed["response"]!["id"] = "other-response"; break;
            case "model-mismatch": created["response"]!["model"] = "conflicting-declared-model"; break;
            case "no-created": return Frame(completed);
            case "missing": break;
            default: throw new ArgumentException(kind);
        }
        return Frame(created) + middle + Frame(completed) + "data: [DONE]\n\n: fixture-tail\n";
    }
}
