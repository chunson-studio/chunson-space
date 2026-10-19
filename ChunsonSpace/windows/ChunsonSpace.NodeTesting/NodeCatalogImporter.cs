using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Core;
using ChunsonSpace.Proxy;

namespace ChunsonSpace.NodeTesting;

public sealed class NodeCatalogImporter
{
    public NodeCatalogResult Read(string configPath, string allowedRoot)
    {
        var root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar);
        var nodes = new List<NodeCandidate>(); var issues = new List<NodeImportIssue>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadLocal(configPath);
        return new(nodes, issues);
        bool ReadNodes(JsonArray entries, string source)
        {
            foreach (var entry in entries)
            {
                if (nodes.Count >= 10000) return false;
                if (entry is not JsonObject definition) { issues.Add(new(Path.GetFileName(source), "节点条目不是有效对象，已跳过。")); continue; }
                nodes.Add(new(definition, source, NodeRules.Reason(definition)));
                if (nodes.Count >= 10000) { issues.Add(new(Path.GetFileName(source), "节点数量超过本批读取限制，停止继续导入。")); return false; }
            }
            return true;
        }
        void ReadLocal(string source)
        {
            if (nodes.Count >= 10000) return;
            if (visited.Count >= 256) { issues.Add(new(Path.GetFileName(source), "本地配置引用超过 256 个文件，停止继续读取。")); return; }
            try
            {
                var path = Path.GetFullPath(source);
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("outside-root");
                PhysicalFileStorage.RejectLinks(path);
                if (!visited.Add(path)) return;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > ProxyYaml.MaxBytes) throw new IOException("unavailable-or-large");
                var document = ProxyYaml.Parse(File.ReadAllText(path, new UTF8Encoding(false, true)));
                if (document["proxies"] is JsonArray entries)
                { if (!ReadNodes(entries, path)) return; }
                else if (document["proxies"] is not null) issues.Add(new(Path.GetFileName(path), "节点列表格式不兼容。"));
                if (document["proxy-providers"] is JsonObject providers)
                    foreach (var provider in providers)
                    {
                        if (provider.Value is JsonObject inline && NodeRules.Text(inline, "type") == "inline")
                        {
                            if (inline["payload"] is JsonArray payload) { if (!ReadNodes(payload, path)) return; }
                            else issues.Add(new(Path.GetFileName(path), "本地 inline 提供器节点列表格式不兼容。"));
                            continue;
                        }
                        var relative = provider.Value is JsonObject details ? NodeRules.Text(details, "path") : "";
                        if (relative.Length == 0) { issues.Add(new(Path.GetFileName(path), "提供器没有可读取的本地缓存；未下载订阅。")); continue; }
                        ReadLocal(Path.IsPathFullyQualified(relative) ? relative : Path.Combine(Path.GetDirectoryName(path)!, relative));
                    }
                // Clash Verge profile catalog references only already-downloaded local files.
                if (document["items"] is JsonArray profiles)
                    foreach (var entry in profiles.OfType<JsonObject>())
                    {
                        var relative = NodeRules.Text(entry, "file");
                        if (relative.Length > 0) ReadLocal(Path.IsPathFullyQualified(relative) ? relative : Path.Combine(root, "profiles", relative));
                    }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException or ProxyException or ArgumentException or System.Text.DecoderFallbackException)
            { issues.Add(new(source.Contains('?') || source.Contains('@') ? "本地配置来源" : Path.GetFileName(source), "本地配置缺失、越界、链接或格式不兼容，未读取该来源；未请求订阅。")); }
        }
    }
}
