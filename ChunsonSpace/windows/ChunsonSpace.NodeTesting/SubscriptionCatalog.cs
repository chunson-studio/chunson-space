using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ChunsonSpace.Core;
using ChunsonSpace.Proxy;

namespace ChunsonSpace.NodeTesting;

public sealed record NodeSubscription(string Id, string Name, string File, bool IsCurrent)
{
    public string Label => Name + (IsCurrent ? " · 当前使用" : "");
}
public sealed record SubscriptionCatalog(IReadOnlyList<NodeSubscription> Subscriptions, IReadOnlyList<NodeImportIssue> Issues);

public static class SubscriptionCatalogReader
{
    public static SubscriptionCatalog Read(ProxyClientKind client, string dataRoot)
    {
        var root = Path.GetFullPath(dataRoot);
        PhysicalFileStorage.RejectLinks(root);
        var subscriptions = new List<NodeSubscription>();
        var issues = new List<NodeImportIssue>();
        if (client == ProxyClientKind.FlClash)
        {
            var current = "";
            try
            {
                var outer = JsonNode.Parse(ReadFile(Path.Combine(root, "shared_preferences.json")));
                var config = outer?["flutter.config"];
                if (config is JsonValue value && value.TryGetValue<string>(out var text)) config = JsonNode.Parse(text);
                current = config?["currentProfileId"]?.ToString() ?? "";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or StorageException)
            { issues.Add(new("FlClash", "无法确认当前使用的订阅；仍显示已保存的订阅。")); }
            var dbPath = Path.Combine(root, "database.sqlite");
            if (System.IO.File.Exists(dbPath))
            {
                try
                {
                    PhysicalFileStorage.RejectLinks(dbPath);
                    foreach (var suffix in new[] { "-wal", "-shm" }) if (System.IO.File.Exists(dbPath + suffix)) PhysicalFileStorage.RejectLinks(dbPath + suffix);
                    using var db = new SubscriptionMetadataDb(dbPath);
                    var columns = db.Query("PRAGMA table_info(profiles)").Select(row => row[1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!columns.Contains("id")) throw new IOException("Unsupported profiles metadata");
                    var label = columns.Contains("label") ? "label" : columns.Contains("name") ? "name" : "id";
                    foreach (var row in db.Query($"SELECT CAST(id AS TEXT), CAST({label} AS TEXT) FROM profiles ORDER BY " + (columns.Contains("order") ? "\"order\", id" : "id")))
                    {
                        if (row[0].Length == 0 || !row[0].All(char.IsAsciiDigit)) { issues.Add(new("FlClash", "一个订阅ID格式不兼容，未读取。")); continue; }
                        subscriptions.Add(new(row[0], string.IsNullOrWhiteSpace(row[1]) ? "订阅 " + row[0] : row[1], Path.Combine(root, "profiles", row[0] + ".yaml"), row[0] == current));
                    }
                    return new(subscriptions, issues);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or StorageException)
                { subscriptions.Clear(); issues.Add(new("FlClash", "订阅名称读取失败，以下按本地缓存文件显示；未修改数据库。")); }
            }
            var folder = Path.Combine(root, "profiles");
            if (Directory.Exists(folder))
            {
                PhysicalFileStorage.RejectLinks(folder);
                foreach (var file in Directory.EnumerateFiles(folder).Where(file => Path.GetExtension(file) is ".yaml" or ".yml").Take(1000))
                    subscriptions.Add(new(Path.GetFileNameWithoutExtension(file), "本地订阅 " + Path.GetFileNameWithoutExtension(file), file, Path.GetFileNameWithoutExtension(file) == current));
            }
        }
        else if (System.IO.File.Exists(Path.Combine(root, "profiles.yaml")))
        {
            var registry = ProxyYaml.Parse(ReadFile(Path.Combine(root, "profiles.yaml")));
            var current = registry["current"]?.ToString() ?? "";
            if (registry["items"] is JsonArray items)
                foreach (var item in items.OfType<JsonObject>().Take(1000))
                {
                    var type = NodeRules.Text(item, "type");
                    if (type is not ("remote" or "local")) continue;
                    var id = NodeRules.Text(item, "uid"); var file = NodeRules.Text(item, "file");
                    if (file.Length == 0) { issues.Add(new("Clash Verge", "订阅没有本地文件，需先在客户端更新订阅。")); continue; }
                    subscriptions.Add(new(id, NodeRules.Text(item, "name") is { Length: > 0 } name ? name : id,
                        Path.Combine(root, "profiles", file), id == current));
                }
        }
        if (subscriptions.Count == 0)
        {
            var generated = Path.Combine(root, client == ProxyClientKind.FlClash ? "config.yaml" : "clash-verge.yaml");
            if (System.IO.File.Exists(generated))
            {
                subscriptions.Add(new("generated", "当前运行配置（未发现订阅目录）", generated, true));
                issues.Add(new(client.ToString(), "只找到运行配置，无法列出其他订阅；请检查客户端数据目录。"));
            }
        }
        return new(subscriptions, issues);
    }
    private static string ReadFile(string file)
    {
        PhysicalFileStorage.RejectLinks(file);
        if (new FileInfo(file).Length > ProxyYaml.MaxBytes) throw new IOException("Metadata too large");
        return System.IO.File.ReadAllText(file);
    }
}

// Only id/label metadata is selected; URLs and node credentials never enter this reader.
internal sealed class SubscriptionMetadataDb : IDisposable
{
    private IntPtr handle;
    public SubscriptionMetadataDb(string file)
    {
        if (sqlite3_open_v2(file, out handle, 1, IntPtr.Zero) != 0) { Dispose(); throw new IOException("Cannot read subscription metadata"); }
        sqlite3_busy_timeout(handle, 1500);
    }
    public List<string[]> Query(string sql)
    {
        if (sqlite3_prepare_v2(handle, sql, -1, out var statement, IntPtr.Zero) != 0) throw new IOException("Subscription metadata schema unavailable");
        try
        {
            var rows = new List<string[]>(); int status;
            while ((status = sqlite3_step(statement)) == 100)
            {
                if (rows.Count >= 1000) throw new IOException("Subscription metadata limit exceeded");
                rows.Add(Enumerable.Range(0, sqlite3_column_count(statement)).Select(i => Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i)) ?? "").ToArray());
            }
            if (status != 101) throw new IOException("Subscription metadata read failed");
            return rows;
        }
        finally { sqlite3_finalize(statement); }
    }
    public void Dispose() { if (handle != IntPtr.Zero) { sqlite3_close(handle); handle = IntPtr.Zero; } }
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
}
