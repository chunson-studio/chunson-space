using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunsonSpace.Core;

namespace ChunsonSpace.Proxy;

public sealed class ClashVergeDnsService(IFileStorage? files = null, IClashVergeState? client = null, DnsPolicy? policy = null)
{
    private readonly IFileStorage files = files ?? new PhysicalFileStorage();
    private readonly IClashVergeState client = client ?? new WindowsClashVergeState();
    private readonly DnsPolicyService rules = new(policy);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ProxyInspection Inspect(string dataRoot) => Guard(() => Load(dataRoot).Inspection);

    public ClashVergeRepairPlan Preview(ClashVergeRepairRequest request) => Guard(() =>
    {
        var loaded = Load(request.DataRoot);
        var root = loaded.Root;
        var group = request.ProxyGroup ?? loaded.Inspection.SuggestedGroup
            ?? throw new ProxyException(ProxyError.GroupRequired, "有多个或无法确认的代理分组，请选择分组后预览。");
        MihomoInspector.RequireGroup(loaded.Inspection, group);
        var observed = loaded.Observed;
        byte[]? Read(string relative, bool required = false) => ReadObserved(root, relative, observed, required);
        var items = loaded.Registry["items"] as JsonArray ?? throw ProxyYaml.Invalid();
        var scripts = items.OfType<JsonObject>().Where(i => ProxyYaml.Text(i["uid"]) == "Script").ToArray();
        if (scripts.Length > 1) throw ProxyYaml.Invalid();
        var global = scripts.SingleOrDefault();
        if (global is not null && ProxyYaml.Text(global["type"]) != "script") throw new ProxyException(ProxyError.Conflict, "全局 Script 条目类型不兼容；未修改文件。");
        var scriptFile = global is null ? "Script.js" : SafeFile(ProxyYaml.Text(global["file"]), ".js");
        var relativeScript = "profiles/" + scriptFile;
        var beforeScript = Read(relativeScript);
        if (global is null && beforeScript is not null) throw new ProxyException(ProxyError.Conflict, "未绑定的 Script.js 已存在，保留该文件；请先在 Clash Verge 检查全局脚本。");
        if (beforeScript is not null && !ClashVergeScript.CanReplace(Decode(beforeScript)))
            throw new ProxyException(ProxyError.Conflict, "已有自定义全局脚本，保留原脚本；请先在 Clash Verge 合并或移除冲突。", PathAt(root, relativeScript));
        CheckOtherOverrides(root, loaded.Current, items, observed);
        _ = rules.Apply(ProxyYaml.Parse(loaded.SourceYaml), group, request.EnableTun);
        var planned = new List<PlannedFile>();
        planned.Add(new(relativeScript, beforeScript, Utf8.GetBytes(ClashVergeScript.Generate(group, request.EnableTun, rules.Policy))));
        if (global is null)
        {
            var entry = new JsonObject { ["uid"] = "Script", ["type"] = "script", ["file"] = scriptFile,
                ["name"] = "Chunson Space DNS", ["updated"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            items.Add(entry);
            planned.Add(new("profiles.yaml", loaded.RegistryBytes, Utf8.GetBytes(ProxyYaml.Serialize(loaded.Registry)),
                [new(["$item", "Script"], false, null, entry.ToJsonString())]));
        }
        var vergeBefore = Read("verge.yaml");
        var verge = vergeBefore is null ? new JsonObject() : ProxyYaml.Parse(Decode(vergeBefore));
        var vergeFields = new List<FieldChange>();
        Set(verge, ["enable_dns_settings"], JsonValue.Create(false)!, vergeFields);
        var currentId = ProxyYaml.Text(loaded.Current["uid"]);
        if (verge["profile_dns_settings"] is JsonObject settings && settings[currentId] is JsonObject currentDns)
        {
            if (currentDns.ContainsKey("enabled")) Set(verge, ["profile_dns_settings", currentId, "enabled"], JsonValue.Create(false)!, vergeFields);
        }
        else if (verge["profile_dns_settings"] is not null and not JsonObject) throw ProxyYaml.Invalid();
        if (request.EnableTun) Set(verge, ["enable_tun_mode"], JsonValue.Create(true)!, vergeFields);
        if (vergeFields.Count > 0) planned.Add(new("verge.yaml", vergeBefore, Utf8.GetBytes(ProxyYaml.Serialize(verge)), vergeFields));
        var clashBefore = Read("clash.yaml");
        var clash = clashBefore is null ? new JsonObject() : ProxyYaml.Parse(Decode(clashBefore));
        var clashFields = new List<FieldChange>();
        Set(clash, ["mode"], JsonValue.Create("rule")!, clashFields);
        Set(clash, ["tun", "strict-route"], JsonValue.Create(true)!, clashFields);
        if (request.EnableTun)
        {
            Set(clash, ["tun", "auto-route"], JsonValue.Create(true)!, clashFields);
            var tun = ProxyYaml.Object(clash, "tun", true);
            if (tun["dns-hijack"] is not null and not JsonArray) throw ProxyYaml.Invalid();
            var hijack = tun["dns-hijack"]?.DeepClone() as JsonArray ?? new();
            foreach (var value in rules.Policy.TunDnsHijack) if (!hijack.Any(n => ProxyYaml.Text(n) == value)) hijack.Add(value);
            Set(clash, ["tun", "dns-hijack"], hijack, clashFields);
        }
        if (clashFields.Count > 0) planned.Add(new("clash.yaml", clashBefore, Utf8.GetBytes(ProxyYaml.Serialize(clash)), clashFields));
        var changes = new List<DnsChange>
        {
            new("profiles/" + scriptFile, "持久全局脚本：Codex 与本程序优先走纯代理分组、不可转发时拒绝；加密 DNS 与国内分流，保留无关规则和端口。"),
            new("verge.yaml", "关闭设置页 DNS 覆盖，让全局脚本 DNS 生效。"),
            new("clash.yaml", "规则模式与严格路由；保留原端口。")
        };
        if (request.EnableTun) changes.Add(new("TUN", "开启 TUN 自动路由，并保留原 DNS 劫持项。"));
        var warnings = loaded.Inspection.Warnings.Concat(new[]
        {
            "应用及还原前必须完全退出 Clash Verge；保存后请重新打开。",
            "只确认持久设置已保存，尚未验证 DNS、网络出口或运行配置。",
            "全局脚本影响所有订阅；其他订阅需包含所选分组，独立 DNS 设置可能覆盖脚本。",
            "修改的设置文件可能转为等价的 JSON/YAML 表示；无关设置值保留，原始文件保存在备份中。"
        }).ToArray();
        return new ClashVergeRepairPlan(request with { DataRoot = root }, loaded.Inspection, group, changes, warnings, planned, observed);
    });

    public ProxySaveResult Apply(ClashVergeRepairPlan plan, string backupRoot) => Guard<ProxySaveResult>(() =>
    {
        Stopped();
        var root = Root(plan.Request.DataRoot);
        var backupBase = Root(backupRoot);
        if (Inside(root, backupBase) || Inside(backupBase, root)) throw new ProxyException(ProxyError.Conflict, "备份目录与 Clash Verge 数据目录不能重叠。");
        foreach (var pair in plan.Observed)
            if (HashOrNull(PathAt(root, pair.Key)) != pair.Value) throw new ProxyException(ProxyError.StalePreview, "预览后配置已变化，请重新预览；未修改文件。");
        MihomoInspector.RequireGroup(Load(root).Inspection, plan.ProxyGroup);
        var backup = Path.Combine(backupBase, "clash-verge-dns-" + Guid.NewGuid().ToString("N"));
        files.CreateDirectory(backup);
        var records = new List<BackupFile>();
        for (var i = 0; i < plan.Files.Count; i++)
        {
            var file = plan.Files[i];
            var name = i + ".before";
            if (file.Before is not null) files.WriteAtomic(Path.Combine(backup, name), file.Before);
            records.Add(new(file.RelativePath, file.Before is not null, name, file.Before is null ? null : Hash(file.Before), Hash(file.After), file.Fields));
        }
        var manifest = new BackupManifest(1, root, records);
        files.WriteAtomic(Path.Combine(backup, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        Stopped();
        // A second complete read catches client/user rewrites while the backup was being created.
        foreach (var pair in plan.Observed)
            if (HashOrNull(PathAt(root, pair.Key)) != pair.Value) throw new ProxyException(ProxyError.StalePreview, "备份期间配置已变化，请重新预览。", recoveryPath: backup);
        try
        {
            foreach (var file in plan.Files) files.WriteAtomic(PathAt(root, file.RelativePath), file.After);
        }
        catch (Exception e) when (Io(e))
        {
            var failed = Rollback(plan.Files.Select(f => (PathAt(root, f.RelativePath), f.Before)));
            throw new ProxyException(ProxyError.Io, failed ? "保存失败，部分恢复也失败；请保留备份。" : "保存失败，已恢复应用前文件。", recoveryPath: backup);
        }
        return new(true, backup, plan.Warnings);
    });

    public ProxySaveResult Restore(string backupPath) => Guard<ProxySaveResult>(() =>
    {
        Stopped();
        var backup = Root(backupPath);
        BackupManifest manifest;
        try { manifest = JsonSerializer.Deserialize<BackupManifest>(ReadBytes(Path.Combine(backup, "manifest.json")), JsonOptions) ?? throw ProxyYaml.Invalid(); }
        catch (JsonException) { throw ProxyYaml.Invalid(); }
        var root = Root(manifest.DataRoot);
        if (manifest.Version != 1 || manifest.Files is null || manifest.Files.Count == 0 || manifest.Files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count || Inside(root, backup) || Inside(backup, root)) throw ProxyYaml.Invalid();
        var changes = new List<(string Path, byte[]? Before, byte[]? After)>();
        foreach (var record in manifest.Files)
        {
            ValidateRecord(record);
            var target = PathAt(root, record.RelativePath);
            var original = record.BeforeExists ? ReadBytes(PathAt(backup, record.BeforeFile)) : null;
            if (original is not null && Hash(original) != record.BeforeHash) throw new ProxyException(ProxyError.InvalidConfig, "备份校验失败；未还原任何文件。");
            var current = files.FileExists(target) ? ReadBytes(target) : null;
            if (record.Fields is null)
            {
                if (current is null || Hash(current) != record.AfterHash) throw new ProxyException(ProxyError.RestoreConflict, "全局脚本已被修改或删除，保留当前脚本；未还原任何文件。", target);
                changes.Add((target, current, original));
            }
            else
            {
                if (current is null) throw new ProxyException(ProxyError.RestoreConflict, "设置文件已被删除；未还原任何文件。", target);
                var value = ProxyYaml.Parse(Decode(current));
                foreach (var field in record.Fields)
                {
                    var slot = Slot(value, field.Keys);
                    if (!slot.Exists || !Equal(slot.Value, JsonNode.Parse(field.AfterJson))) throw new ProxyException(ProxyError.RestoreConflict, "本工具接管的设置已被修改，保留后续修改；未还原任何文件。", target);
                }
                foreach (var field in record.Fields.Reverse()) RestoreField(value, field);
                // Newly created settings files are deleted only if no later unrelated settings remain.
                var restored = original is null && value.Count == 0 ? null : Utf8.GetBytes(ProxyYaml.Serialize(value));
                changes.Add((target, current, restored));
            }
        }
        // All destination roots/ancestors are checked before any write, including later missing files.
        foreach (var change in changes) PhysicalFileStorage.RejectLinks(change.Path);
        Stopped();
        foreach (var change in changes)
            if (HashOrNull(change.Path) != (change.Before is null ? null : Hash(change.Before)))
                throw new ProxyException(ProxyError.RestoreConflict, "还原准备期间配置已变化；未还原任何文件。", change.Path, backup);
        try
        {
            foreach (var change in changes) if (change.After is null) files.DeleteFile(change.Path); else files.WriteAtomic(change.Path, change.After);
        }
        catch (Exception e) when (Io(e))
        {
            var failed = Rollback(changes.Select(c => (c.Path, c.Before)));
            throw new ProxyException(ProxyError.Io, failed ? "还原失败，部分回滚也失败；请保留备份。" : "还原失败，已恢复还原前文件。", recoveryPath: backup);
        }
        return new(true, backup, ["持久设置已还原；请重新打开 Clash Verge。尚未验证运行效果。"]);
    });

    private Loaded Load(string dataRoot)
    {
        var root = Root(dataRoot);
        var observed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var registryBytes = ReadObserved(root, "profiles.yaml", observed, true)!;
        var registry = ProxyYaml.Parse(Decode(registryBytes));
        var currentId = ProxyYaml.Text(registry["current"]);
        if (registry["items"] is not JsonArray items || items.Any(i => i is not JsonObject)) throw ProxyYaml.Invalid();
        var ids = items.OfType<JsonObject>().Select(i => ProxyYaml.Text(i["uid"])).ToArray();
        if (ids.Any(string.IsNullOrEmpty) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw ProxyYaml.Invalid();
        var current = items.OfType<JsonObject>().SingleOrDefault(i => ProxyYaml.Text(i["uid"]) == currentId) ?? throw ProxyYaml.Invalid();
        if (ProxyYaml.Text(current["type"]) is not "remote" and not "local") throw ProxyYaml.Invalid();
        var source = Decode(ReadObserved(root, "profiles/" + SafeFile(ProxyYaml.Text(current["file"]), ".yaml", ".yml"), observed, true)!);
        var runtimeBytes = ReadObserved(root, "clash-verge.yaml", observed);
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        if (current["selected"] is JsonArray choices)
            foreach (var choice in choices.OfType<JsonObject>())
                if (ProxyYaml.Text(choice["name"]).Length > 0) selected[ProxyYaml.Text(choice["name"])] = ProxyYaml.Text(choice["now"]);
        var runtime = runtimeBytes is null ? null : Decode(runtimeBytes);
        var providers = ReadProviders(root, runtime ?? source, observed);
        var inspection = MihomoInspector.Inspect(source, selected, runtime, providers.Yamls);
        inspection = inspection with { Warnings = inspection.Warnings.Concat(providers.Warnings).ToArray() };
        return new(root, registry, registryBytes, current, source, inspection, observed);
    }
    private (Dictionary<string, string> Yamls, List<string> Warnings) ReadProviders(string root, string yaml, Dictionary<string, string?> observed)
    {
        var config = ProxyYaml.Parse(yaml);
        var groups = config["proxy-groups"] as JsonArray ?? new();
        var declarations = ProxyYaml.Object(config, "proxy-providers");
        var references = groups.OfType<JsonObject>().SelectMany(g => (g["use"] as JsonArray ?? new()).Select(ProxyYaml.Text)).ToHashSet(StringComparer.Ordinal);
        if (groups.OfType<JsonObject>().Any(g => ProxyYaml.Bool(g["include-all"]) || ProxyYaml.Bool(g["include-all-providers"])))
            foreach (var declaration in declarations) references.Add(declaration.Key);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var name in references)
        {
            if (declarations[name] is not JsonObject provider) { warnings.Add("存在未声明的代理提供器，分组不能确认。"); continue; }
            if (provider["filter"] is not null || provider["exclude-filter"] is not null || provider["exclude-type"] is not null ||
                provider["override"] is JsonObject over && over.Any(p => p.Key is "proxy-name" or "override-expr"))
            { warnings.Add("提供器包含动态筛选或名称表达式，分组不能离线确认。"); continue; }
            JsonObject cached;
            if (ProxyYaml.Text(provider["type"]) == "inline" && provider["payload"] is JsonArray payload)
                cached = new JsonObject { ["proxies"] = payload.DeepClone() };
            else
            {
                var pathText = ProxyYaml.Text(provider["path"]);
                if (pathText.Length == 0) { warnings.Add("提供器没有显式缓存路径，未搜索或下载缓存。"); continue; }
                if (pathText.Contains("://", StringComparison.Ordinal)) throw new ProxyException(ProxyError.InvalidConfig, "提供器缓存路径不能是网络地址。");
                var full = Path.GetFullPath(Path.Combine(root, pathText.Replace('/', Path.DirectorySeparatorChar)));
                if (!Inside(root, full) || string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
                    throw new ProxyException(ProxyError.InvalidConfig, "提供器缓存位于数据根之外，未读取外部文件。");
                PhysicalFileStorage.RejectLinks(full);
                var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                var bytes = ReadObserved(root, relative, observed);
                if (bytes is null) { warnings.Add("提供器缓存缺失，分组不能确认；未发起下载。"); continue; }
                if (ProxyYaml.Text(provider["type"]) == "http")
                {
                    var interval = provider["interval"] is JsonValue number && number.TryGetValue<long>(out var seconds) ? seconds : 0;
                    var stamp = File.GetLastWriteTimeUtc(full);
                    if (interval <= 0 || interval > 31536000 || stamp.AddSeconds(interval) < DateTime.UtcNow)
                    { warnings.Add("提供器缓存已过期或缺少有效刷新周期，分组不能确认；未发起下载。"); continue; }
                }
                cached = ProxyYaml.Parse(Decode(bytes));
            }
            if (cached["proxies"] is not JsonArray entries || entries.Any(n => n is not JsonObject)) throw ProxyYaml.Invalid();
            var prefix = provider["override"] is JsonObject options ? ProxyYaml.Text(options["additional-prefix"]) : "";
            var suffix = provider["override"] is JsonObject suffixOptions ? ProxyYaml.Text(suffixOptions["additional-suffix"]) : "";
            foreach (var node in entries.OfType<JsonObject>()) node["name"] = prefix + ProxyYaml.Text(node["name"]) + suffix;
            result[name] = ProxyYaml.Serialize(cached);
        }
        return (result, warnings);
    }
    private void CheckOtherOverrides(string root, JsonObject current, JsonArray items, Dictionary<string, string?> observed)
    {
        var option = ProxyYaml.Object(current, "option");
        var scriptId = ProxyYaml.Text(option["script"]);
        if (scriptId.Length > 0 && scriptId != "Script")
        {
            var item = items.OfType<JsonObject>().SingleOrDefault(i => ProxyYaml.Text(i["uid"]) == scriptId) ?? throw ProxyYaml.Invalid();
            if (ProxyYaml.Text(item["type"]) != "script") throw ProxyYaml.Invalid();
            var path = "profiles/" + SafeFile(ProxyYaml.Text(item["file"]), ".js");
            var code = Decode(ReadObserved(root, path, observed, true)!);
            // A per-profile tool script could intentionally contradict the new global policy.
            if (ClashVergeScript.Normalize(code) != ClashVergeScript.Normalize(ClashVergeScript.DefaultScript) &&
                ClashVergeScript.Normalize(code) != ClashVergeScript.Normalize("function main(config) { return config; }"))
                throw new ProxyException(ProxyError.Conflict, "当前订阅存在独立自定义脚本，可能覆盖全局策略；原脚本已保留。", PathAt(root, path));
        }
        foreach (var uid in new[] { "Merge", ProxyYaml.Text(option["merge"]) }.Where(s => s.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var item = items.OfType<JsonObject>().SingleOrDefault(i => ProxyYaml.Text(i["uid"]) == uid);
            if (item is null) continue;
            if (ProxyYaml.Text(item["type"]) != "merge") throw ProxyYaml.Invalid();
            var path = "profiles/" + SafeFile(ProxyYaml.Text(item["file"]), ".yaml", ".yml");
            var config = ProxyYaml.Parse(Decode(ReadObserved(root, path, observed, true)!));
            if (config.Any(pair => pair.Key is "dns" or "tun" or "rules" or "mode" or "find-process-mode"))
                throw new ProxyException(ProxyError.Conflict, "已有扩展覆写涉及 DNS、路由或规则，请先检查冲突；未修改文件。", PathAt(root, path));
        }
    }
    private byte[]? ReadObserved(string root, string relative, Dictionary<string, string?> observed, bool required = false)
    {
        var path = PathAt(root, relative);
        if (!files.FileExists(path)) { observed[relative] = null; if (required) throw new ProxyException(ProxyError.InvalidConfig, "缺少必要的 Clash Verge 配置文件。", path); return null; }
        var bytes = ReadBytes(path); observed[relative] = Hash(bytes); return bytes;
    }
    private byte[] ReadBytes(string path) { PhysicalFileStorage.RejectLinks(path); if (files.Length(path) > ProxyYaml.MaxBytes) throw ProxyYaml.Invalid(); return files.Read(path); }
    private string? HashOrNull(string path) => files.FileExists(path) ? files.Hash(path) : null;
    private static string Decode(byte[] bytes) { try { return Utf8.GetString(bytes).TrimStart('\uFEFF'); } catch (DecoderFallbackException) { throw ProxyYaml.Invalid(); } }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Root(string path) { var full = Path.GetFullPath(path); PhysicalFileStorage.RejectLinks(full); return Path.TrimEndingDirectorySeparator(full); }
    private static string PathAt(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(p => p is "" or "." or "..")) throw ProxyYaml.Invalid();
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Inside(root, path) || string.Equals(root, path, StringComparison.OrdinalIgnoreCase)) throw ProxyYaml.Invalid();
        PhysicalFileStorage.RejectLinks(path); return path;
    }
    private static bool Inside(string root, string path) => string.Equals(root, path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string SafeFile(string value, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['/', '\\', ':']) >= 0 || value.Contains("..", StringComparison.Ordinal) ||
            value.Any(char.IsControl) || value.EndsWith(' ') || !extensions.Contains(Path.GetExtension(value), StringComparer.OrdinalIgnoreCase)) throw ProxyYaml.Invalid();
        return value;
    }
    private void Stopped()
    {
        var state = client.Read();
        if (state != ProxyClientState.Stopped) throw new ProxyException(ProxyError.Conflict, state == ProxyClientState.Running ? "请先完全退出 Clash Verge，再应用或还原。" : "无法确认 Clash Verge 已退出，未修改任何配置。");
    }
    private bool Rollback(IEnumerable<(string Path, byte[]? Content)> originals)
    {
        var failed = false;
        foreach (var original in originals) try { if (original.Content is null) files.DeleteFile(original.Path); else files.WriteAtomic(original.Path, original.Content); } catch (Exception e) when (Io(e)) { failed = true; }
        return failed;
    }
    private static bool Io(Exception error) => error is IOException or UnauthorizedAccessException or StorageException;
    private static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (ProxyException) { throw; }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw ProxyYaml.Invalid(); }
        catch (Exception e) when (Io(e) || e is ArgumentException or NotSupportedException) { throw new ProxyException(ProxyError.Io, "无法访问代理配置文件；未确认保存成功。"); }
    }
    private static bool Equal(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);
    private static (bool Exists, JsonNode? Value) Slot(JsonObject value, string[] keys)
    {
        if (keys.Length == 2 && keys[0] == "$item")
        {
            var item = (value["items"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault(i => ProxyYaml.Text(i["uid"]) == keys[1]);
            return (item is not null, item);
        }
        JsonObject parent = value;
        foreach (var key in keys[..^1]) { if (parent[key] is not JsonObject next) return (false, null); parent = next; }
        return (parent.ContainsKey(keys[^1]), parent[keys[^1]]);
    }
    private static void Set(JsonObject value, string[] keys, JsonNode after, List<FieldChange> changes)
    {
        var before = Slot(value, keys);
        if (before.Exists && Equal(before.Value, after)) return;
        changes.Add(new(keys, before.Exists, before.Value?.ToJsonString(), after.ToJsonString()));
        JsonObject parent = value;
        foreach (var key in keys[..^1]) parent = ProxyYaml.Object(parent, key, true);
        parent[keys[^1]] = after.DeepClone();
    }
    private static void RestoreField(JsonObject value, FieldChange field)
    {
        if (field.Keys[0] == "$item")
        {
            var list = value["items"] as JsonArray ?? throw ProxyYaml.Invalid();
            var item = list.OfType<JsonObject>().Single(i => ProxyYaml.Text(i["uid"]) == field.Keys[1]); list.Remove(item); return;
        }
        JsonObject parent = value;
        var parents = new List<(JsonObject Map, string Key)>();
        foreach (var key in field.Keys[..^1]) { parents.Add((parent, key)); parent = parent[key] as JsonObject ?? throw ProxyYaml.Invalid(); }
        if (field.BeforeExists) parent[field.Keys[^1]] = field.BeforeJson is null ? null : JsonNode.Parse(field.BeforeJson); else parent.Remove(field.Keys[^1]);
        foreach (var (map, key) in parents.AsEnumerable().Reverse()) if (map[key] is JsonObject { Count: 0 }) map.Remove(key);
    }
    private static void ValidateRecord(BackupFile file)
    {
        if (file.RelativePath is null || file.BeforeFile is null || !System.Text.RegularExpressions.Regex.IsMatch(file.BeforeFile, @"\A[0-9]+\.before\z") ||
            !System.Text.RegularExpressions.Regex.IsMatch(file.AfterHash ?? "", @"\A[a-f0-9]{64}\z") ||
            file.BeforeExists && !System.Text.RegularExpressions.Regex.IsMatch(file.BeforeHash ?? "", @"\A[a-f0-9]{64}\z")) throw ProxyYaml.Invalid();
        if (file.Fields is null) { if (!file.RelativePath.StartsWith("profiles/", StringComparison.Ordinal)) throw ProxyYaml.Invalid(); _ = SafeFile(file.RelativePath[9..], ".js"); return; }
        if (file.Fields.Count == 0) throw ProxyYaml.Invalid();
        foreach (var field in file.Fields)
        {
            if (field.Keys is null || field.Keys.Length == 0 || field.Keys.Any(string.IsNullOrEmpty)) throw ProxyYaml.Invalid();
            var ok = file.RelativePath switch
            {
                "profiles.yaml" => field.Keys.SequenceEqual(["$item", "Script"]) && !field.BeforeExists,
                "verge.yaml" => field.Keys.SequenceEqual(["enable_dns_settings"]) || field.Keys.SequenceEqual(["enable_tun_mode"]) ||
                    field.Keys is ["profile_dns_settings", _, "enabled"],
                "clash.yaml" => field.Keys.SequenceEqual(["mode"]) || field.Keys is ["tun", "strict-route" or "auto-route" or "dns-hijack"],
                _ => false
            };
            if (!ok) throw ProxyYaml.Invalid();
            _ = JsonNode.Parse(field.AfterJson);
            if (field.BeforeJson is not null) _ = JsonNode.Parse(field.BeforeJson);
        }
    }
    private sealed record Loaded(string Root, JsonObject Registry, byte[] RegistryBytes, JsonObject Current, string SourceYaml,
        ProxyInspection Inspection, Dictionary<string, string?> Observed);
    private sealed record BackupManifest(int Version, string DataRoot, IReadOnlyList<BackupFile> Files);
    private sealed record BackupFile(string RelativePath, bool BeforeExists, string BeforeFile, string? BeforeHash, string AfterHash, IReadOnlyList<FieldChange>? Fields);
}
