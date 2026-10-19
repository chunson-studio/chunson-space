using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace ChunsonSpace.Proxy;

public static class ProxyYaml
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public static JsonObject Parse(string yaml)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > MaxBytes) throw Invalid();
        try
        {
            var parser = new Parser(new StringReader(yaml));
            var documents = 0;
            while (parser.MoveNext())
            {
                if (parser.Current is DocumentStart && ++documents > 1) throw Invalid();
                if (parser.Current is AnchorAlias || parser.Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                    throw new ProxyException(ProxyError.InvalidConfig, "配置含 YAML 锚点、别名或标签，请先转换为普通配置；未修改文件。");
            }
            var value = new DeserializerBuilder().WithDuplicateKeyChecking().WithAttemptingUnquotedStringTypeDeserialization().Build().Deserialize<object>(yaml);
            var json = new SerializerBuilder().JsonCompatible().Build().Serialize(value);
            var result = JsonNode.Parse(json) as JsonObject ?? throw Invalid();
            RejectMerge(result);
            return result;
        }
        catch (ProxyException) { throw; }
        catch (Exception e) when (e is YamlException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        { throw Invalid(); }
    }
    public static string Serialize(JsonObject value)
    {
        // JSON is a YAML subset. It preserves scalar types and every unrelated setting without interpreting templates.
        return value.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
    internal static string Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : "";
    internal static bool Bool(JsonNode? value) => value is JsonValue v && v.TryGetValue<bool>(out var flag) && flag;
    internal static JsonObject Object(JsonObject value, string key, bool create = false)
    {
        if (value[key] is JsonObject found) return found;
        if (value.ContainsKey(key) && value[key] is not null) throw Invalid();
        if (!create) return new();
        var result = new JsonObject(); value[key] = result; return result;
    }
    internal static void RejectMerge(JsonNode? node)
    {
        if (node is JsonObject map) foreach (var pair in map) { if (pair.Key == "<<") throw Invalid(); RejectMerge(pair.Value); }
        else if (node is JsonArray list) foreach (var child in list) RejectMerge(child);
    }
    internal static ProxyException Invalid() => new(ProxyError.InvalidConfig, "配置格式不兼容或损坏；未修改文件。");
}
