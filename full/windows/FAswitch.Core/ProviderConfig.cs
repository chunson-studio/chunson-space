using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FAswitch.Core;

internal sealed class ProviderConfig(IFileStorage files, string root, string home)
{
    private string Config => Path.Combine(home, "config.toml");
    private string State => Path.Combine(root, "providers", SwitchFileRules.ProviderStateFile);
    public string? ReadBaseUrl() => TomlManagedKeys.ReadString(ReadText(), "openai_base_url");
    public void Enter(string name, string baseUrl, string model, bool trusted)
    {
        var text = ReadText();
        JsonObject data;
        if (files.FileExists(State)) data = JsonFiles.Read(files, State);
        else data = new();
        if (data["trusted"] is not JsonValue flag || !flag.TryGetValue<bool>(out _))
            data = new JsonObject { ["entered"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), ["trusted"] = trusted, ["record"] = trusted ? TomlManagedKeys.Record(text) : null };
        data["managed"] = model.Length > 0 ? new JsonArray("model", "openai_base_url") : new JsonArray("openai_base_url");
        data["current"] = name;
        // A failed restore-record write must leave the config file untouched.
        JsonFiles.Write(files, State, data);
        var values = new Dictionary<string, string?> { ["openai_base_url"] = JsonSerializer.Serialize(baseUrl) };
        if (model.Length > 0) values["model"] = JsonSerializer.Serialize(model);
        StoreText(TomlManagedKeys.Merge(text, values));
    }
    public void Leave(bool force)
    {
        if (!files.FileExists(State))
        {
            if (force) StoreText(TomlManagedKeys.Merge(ReadText(), new Dictionary<string, string?> { ["model"] = null, ["openai_base_url"] = null }));
            return;
        }
        JsonObject data;
        try { data = JsonFiles.Read(files, State); }
        catch (StorageException error) when (error.Code == StorageError.Corrupt) { data = new(); }
        if (JsonFiles.Boolean(data, "trusted") && data["record"] is JsonObject record)
            StoreText(TomlManagedKeys.Restore(ReadText(), record));
        else
        {
            var managed = data["managed"] as JsonArray;
            var keys = managed?.OfType<JsonValue>().Where(item => item.TryGetValue<string>(out _)).Select(item => item.GetValue<string>()).Where(item => item is "model" or "openai_base_url").ToArray() ?? ["model", "openai_base_url"];
            if (keys.Length == 0) keys = ["model", "openai_base_url"];
            StoreText(TomlManagedKeys.Merge(ReadText(), keys.ToDictionary(key => key!, _ => (string?)null)));
        }
        // Keep the record when config restoration fails; never erase the only restoration basis.
        files.DeleteFile(State);
    }
    private string ReadText() => files.FileExists(Config) ? new UTF8Encoding(false, true).GetString(files.Read(Config)) : "";
    private void StoreText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) files.DeleteFile(Config);
        else files.WriteAtomic(Config, Encoding.UTF8.GetBytes(value));
    }
}

public static partial class TomlManagedKeys
{
    private sealed record Line(string Text, string Ending);
    private static readonly string[] Keys = ["model", "openai_base_url"];
    private static List<Line> Lines(string text) => Regex.Matches(text, "[^\\r\\n]*(?:\\r\\n|\\n|\\r|\\z)")
        .Where(match => match.Length > 0).Select(match =>
        {
            var full = match.Value;
            var ending = full.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : full.EndsWith('\n') ? "\n" : full.EndsWith('\r') ? "\r" : "";
            return new Line(full[..(full.Length - ending.Length)], ending);
        }).ToList();
    private static Dictionary<string, List<int>> Managed(List<Line> lines)
    {
        var result = Keys.ToDictionary(key => key, _ => new List<int>());
        var depth = 0;
        string? quote = null;
        string? assignment = null;
        var priorOpensValue = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Text;
            var top = depth == 0 && quote is null && assignment is null;
            if (top && !priorOpensValue && Regex.IsMatch(line, "\\A\\s*\\[\\[?.+\\]\\]?\\s*(?:#.*)?\\z")) break;
            if (top)
            {
                foreach (var key in Keys)
                    if (Regex.IsMatch(line, "\\A\\s*(?:\"|')?" + key + "(?:\"|')?\\s*=")) { assignment = key; break; }
            }
            if (assignment is not null) result[assignment].Add(i);
            Scan(line, ref depth, ref quote);
            if (depth == 0 && quote is null) assignment = null;
            if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#')) priorOpensValue = line.TrimEnd().EndsWith('=');
        }
        return result;
    }
    private static void Scan(string line, ref int depth, ref string? quote)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (quote is not null)
            {
                if (line.AsSpan(i).StartsWith(quote, StringComparison.Ordinal)) { i += 2; quote = null; }
                else if (quote == "\"\"\"" && line[i] == '\\') i++;
                continue;
            }
            if (line[i] == '#') break;
            if (line.AsSpan(i).StartsWith("\"\"\"", StringComparison.Ordinal) || line.AsSpan(i).StartsWith("'''", StringComparison.Ordinal))
            { quote = line.Substring(i, 3); i += 2; continue; }
            if (line[i] is '\'' or '"')
            {
                var delimiter = line[i];
                for (i++; i < line.Length; i++)
                {
                    if (delimiter == '"' && line[i] == '\\') { i++; continue; }
                    if (line[i] == delimiter) break;
                }
                continue;
            }
            if (line[i] is '[' or '{') depth++;
            else if (line[i] is ']' or '}') depth = Math.Max(0, depth - 1);
        }
    }
    private static int TopEnd(List<Line> lines)
    {
        var depth = 0;
        string? quote = null;
        var priorOpensValue = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text;
            if (depth == 0 && quote is null && !priorOpensValue && Regex.IsMatch(text, "\\A\\s*\\[\\[?.+\\]\\]?\\s*(?:#.*)?\\z")) return i;
            Scan(text, ref depth, ref quote);
            if (!string.IsNullOrWhiteSpace(text) && !text.TrimStart().StartsWith('#')) priorOpensValue = text.TrimEnd().EndsWith('=');
        }
        return lines.Count;
    }
    public static JsonObject Record(string text)
    {
        var lines = Lines(text);
        var managed = Managed(lines);
        var result = new JsonObject();
        foreach (var key in Keys)
        {
            var indices = managed[key];
            if (indices.Count == 0) result[key] = new JsonObject { ["line"] = null, ["index"] = null };
            else result[key] = new JsonObject
            {
                ["line"] = string.Concat(indices.Select(index => lines[index].Text + (index == indices[^1] ? "" : lines[index].Ending))),
                ["index"] = indices[0], ["ending"] = lines[indices[^1]].Ending
            };
        }
        return result;
    }
    public static string Merge(string text, IReadOnlyDictionary<string, string?> values)
    {
        if (values.Keys.Any(key => !Keys.Contains(key))) throw new ArgumentException("Only provider-managed top-level keys may be modified.", nameof(values));
        var lines = Lines(text);
        var managed = Managed(lines);
        var remove = values.Keys.SelectMany(key => managed[key]).ToHashSet();
        var newline = lines.FirstOrDefault(line => line.Ending.Length > 0)?.Ending ?? "\n";
        var prefix = values.Where(pair => !string.IsNullOrEmpty(pair.Value)).Select(pair => new Line(pair.Key + " = " + pair.Value, newline)).ToArray();
        var kept = lines.Where((_, index) => !remove.Contains(index)).ToList();
        var result = prefix.Concat(kept).ToList();
        if (lines.Count > 0 && lines[^1].Ending.Length == 0 && result.Count > 0) result[^1] = result[^1] with { Ending = "" };
        return string.Concat(result.Select(line => line.Text + line.Ending));
    }
    public static string Restore(string text, JsonObject record)
    {
        var lines = Lines(text);
        var managed = Managed(lines).Values.SelectMany(indices => indices).ToHashSet();
        var kept = lines.Where((_, index) => !managed.Contains(index)).ToList();
        var newline = lines.FirstOrDefault(line => line.Ending.Length > 0)?.Ending ?? "\n";
        var records = Keys.Select(key => record[key] as JsonObject).Where(item => item is not null && JsonFiles.Text(item, "line").Length > 0)
            .OrderBy(item => item!["index"]?.GetValue<int>() ?? int.MaxValue).ToArray();
        foreach (var item in records)
        {
            var index = item!["index"]?.GetValue<int>() ?? kept.Count;
            var ending = item["ending"] is JsonValue node && node.TryGetValue<string>(out var raw) ? raw : newline;
            var restored = Lines(JsonFiles.Text(item, "line") + ending);
            var insertion = Math.Clamp(index, 0, TopEnd(kept));
            if (insertion > 0 && kept[insertion - 1].Ending.Length == 0) kept[insertion - 1] = kept[insertion - 1] with { Ending = newline };
            if (insertion < kept.Count && restored.Count > 0 && restored[^1].Ending.Length == 0) restored[^1] = restored[^1] with { Ending = newline };
            kept.InsertRange(insertion, restored);
        }
        return string.Concat(kept.Select(line => line.Text + line.Ending));
    }
    public static string? ReadString(string text, string key)
    {
        if (!Keys.Contains(key)) return null;
        var record = Record(text)[key]!.AsObject();
        var line = JsonFiles.Text(record, "line");
        if (line.Length == 0) return null;
        var right = line[(line.IndexOf('=') + 1)..].TrimStart();
        if (right.Length < 2 || right[0] is not ('"' or '\'')) return null;
        var delimiter = right[0];
        var end = 1;
        for (; end < right.Length; end++)
        {
            if (delimiter == '"' && right[end] == '\\') { end++; continue; }
            if (right[end] == delimiter) break;
        }
        if (end >= right.Length) return null;
        if (delimiter == '\'') return right[1..end];
        try { return JsonSerializer.Deserialize<string>(right[..(end + 1)]); }
        catch (JsonException) { return null; }
    }
}
