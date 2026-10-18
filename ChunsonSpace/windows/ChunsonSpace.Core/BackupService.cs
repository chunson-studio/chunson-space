using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChunsonSpace.Core;

public sealed record BackupSummary(string Name, string Time, int Count, long Bytes);
public sealed record BackupList(IReadOnlyList<BackupSummary> Items, IReadOnlyList<FileIssue> Issues);
public sealed record BackupResult(bool Ok, string Name, string Path, int Count, long Bytes, IReadOnlyList<FileIssue> Failed);
public sealed record RestoreResult(bool Ok, int Restored, string PreBackup, IReadOnlyList<FileIssue> Failed, string StagingPath = "");

public sealed class BackupService
{
    public const string BackupNote = "只含设置、账号档案(profiles)、供应商(providers)与每个账号的浏览器配置 browser-envs\\<名字>\\env.json；不含 Chrome 档案本体（几百 MB 且可重建）。";
    private readonly string root;
    private readonly IFileStorage files;
    private string Backups => Path.Combine(root, "backups");
    public BackupService(string root, IFileStorage? files = null) { this.root = Path.GetFullPath(root); this.files = files ?? new PhysicalFileStorage(); }
    public BackupResult Create(string label = "")
    {
        var tag = Regex.Replace(label, "[^0-9A-Za-z_-]+", "-").Trim('-');
        if (tag.Length > 24) tag = tag[..24];
        var name = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + (tag.Length > 0 ? "-" + tag : "");
        var original = name;
        for (var suffix = 2; files.DirectoryExists(Path.Combine(Backups, name)); suffix++) name = original + "-" + suffix;
        var dest = Path.Combine(Backups, name);
        var errors = new List<FileIssue>();
        var entries = new JsonArray();
        long bytes = 0;
        IReadOnlyList<string> sources;
        try { sources = Sources(); files.CreateDirectory(dest); }
        catch (Exception ex) when (IsIo(ex)) { return new(false, name, dest, 0, 0, [new(root, StorageError.Io, ex.Message)]); }
        foreach (var source in sources)
        {
            var relative = Path.GetRelativePath(root, source);
            try
            {
                var copied = Path.Combine(dest, relative);
                files.CopyAtomic(source, copied);
                var size = files.Length(copied);
                var hash = files.Hash(copied);
                entries.Add(new JsonObject { ["path"] = relative, ["size"] = size, ["sha256"] = hash });
                bytes += size;
            }
            catch (Exception ex) when (IsIo(ex))
            {
                entries.Add(new JsonObject { ["path"] = relative, ["error"] = ex.Message });
                errors.Add(new(relative, StorageError.Io, ex.Message));
            }
        }
        try
        {
            JsonFiles.Write(files, Path.Combine(dest, "MANIFEST.json"), new JsonObject
            {
                ["version"] = 1, ["name"] = name, ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["root"] = root, ["count"] = entries.Count, ["bytes"] = bytes, ["note"] = BackupNote, ["files"] = entries
            });
        }
        catch (StorageException ex) { errors.Add(new(ex.FilePath, ex.Code, ex.Message)); }
        return new(errors.Count == 0, name, dest, entries.Count, bytes, errors);
    }
    public BackupList List()
    {
        var items = new List<BackupSummary>();
        var issues = new List<FileIssue>();
        try
        {
            foreach (var directory in files.Directories(Backups).Reverse())
            {
                var manifest = Path.Combine(directory, "MANIFEST.json");
                if (!files.FileExists(manifest)) continue;
                try
                {
                    var value = JsonFiles.Read(files, manifest);
                    var count = Number(value["count"]);
                    items.Add(new(Path.GetFileName(directory), JsonFiles.Text(value, "time"), count is >= 0 and <= int.MaxValue ? (int)count : 0, Math.Max(0, Number(value["bytes"]))));
                }
                catch (StorageException ex) { issues.Add(new(manifest, ex.Code, ex.Message)); }
            }
        }
        catch (Exception ex) when (IsIo(ex)) { issues.Add(new(Backups, StorageError.Io, ex.Message)); }
        return new(items, issues);
    }
    public RestoreResult Restore(string name)
    {
        name = ProfileRules.Name(name);
        var directory = Path.Combine(Backups, name);
        JsonObject manifest;
        try { manifest = JsonFiles.Read(files, Path.Combine(directory, "MANIFEST.json")); }
        catch (StorageException ex) { return new(false, 0, "", [new(ex.FilePath, ex.Code, ex.Message)]); }
        if (manifest["files"] is not JsonArray entries || entries.Count == 0)
            return new(false, 0, "", [new(directory, StorageError.Corrupt, "备份清单为空或格式错误。")]);
        var errors = new List<FileIssue>();
        var verified = new List<(string Relative, string Source, string Hash)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in entries)
        {
            if (node is not JsonObject entry) { errors.Add(new(directory, StorageError.Corrupt, "清单条目必须是对象。")); continue; }
            var relative = JsonFiles.Text(entry, "path");
            if (!SafeRelative(relative) || !seen.Add(relative.Replace('\\', '/')))
            { errors.Add(new(relative, StorageError.Corrupt, "备份路径非法、越界或重复。")); continue; }
            if (JsonFiles.Text(entry, "error").Length > 0)
            { errors.Add(new(relative, StorageError.Corrupt, "此文件当初没有备份成功。")); continue; }
            var expected = JsonFiles.Text(entry, "sha256");
            if (!Regex.IsMatch(expected, "^[0-9a-fA-F]{64}$"))
            { errors.Add(new(relative, StorageError.Corrupt, "清单缺少有效 SHA-256 校验值。")); continue; }
            try
            {
                // Preflight every destination before the pre-backup and before any restore write.
                PhysicalFileStorage.RejectLinks(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                var source = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!files.FileExists(source)) { errors.Add(new(relative, StorageError.Missing, "备份文件缺失。")); continue; }
                if (!files.Hash(source).Equals(expected, StringComparison.OrdinalIgnoreCase)) { errors.Add(new(relative, StorageError.Corrupt, "备份内容校验失败。")); continue; }
                verified.Add((relative, source, expected));
            }
            catch (Exception ex) when (IsIo(ex)) { errors.Add(new(relative, StorageError.Io, ex.Message)); }
        }
        if (errors.Count > 0) return new(false, 0, "", errors);
        // Freeze verified input on disk before any live-file write, without retaining session databases in memory.
        var staging = Path.Combine(root, "xy_clean", "CodexX-01", "Boss", "backup-restore-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            files.CreateDirectory(staging);
            foreach (var entry in verified)
            {
                var copied = Path.Combine(staging, entry.Relative.Replace('/', Path.DirectorySeparatorChar));
                files.CopyAtomic(entry.Source, copied);
                if (!files.Hash(copied).Equals(entry.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new StorageException(StorageError.Corrupt, entry.Source, "备份在校验期间发生变化，尚未恢复任何文件。");
            }
        }
        catch (Exception ex) when (IsIo(ex)) { return new(false, 0, "", [new(staging, StorageError.Io, ex.Message)], staging); }
        var before = Create("restore-前");
        if (!before.Ok) return new(false, 0, before.Name, before.Failed, staging);
        var restored = 0;
        foreach (var entry in verified)
        {
            try
            {
                var copied = Path.Combine(staging, entry.Relative.Replace('/', Path.DirectorySeparatorChar));
                var destination = Path.Combine(root, entry.Relative.Replace('/', Path.DirectorySeparatorChar));
                files.CopyAtomic(copied, destination);
                if (!files.Hash(destination).Equals(entry.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new StorageException(StorageError.Corrupt, destination, "恢复文件校验失败，恢复前备份及暂存副本已保留。");
                restored++;
            }
            catch (Exception ex) when (IsIo(ex)) { errors.Add(new(entry.Relative, StorageError.Io, ex.Message)); }
        }
        if (errors.Count == 0)
        {
            try { files.DeleteDirectory(staging, true); staging = ""; }
            catch (Exception ex) when (IsIo(ex)) { /* Restoration succeeded; expose retained staging instead of claiming it was removed. */ }
        }
        return new(errors.Count == 0, restored, before.Name, errors, staging);
    }
    private IReadOnlyList<string> Sources()
    {
        var result = new List<string>();
        var settings = Path.Combine(root, "settings.json");
        if (files.FileExists(settings)) result.Add(settings);
        foreach (var folder in new[] { "profiles", "providers" }) result.AddRange(files.Files(Path.Combine(root, folder), true));
        foreach (var directory in files.Directories(Path.Combine(root, "browser-envs")))
        {
            var env = Path.Combine(directory, "env.json");
            if (files.FileExists(env)) result.Add(env);
        }
        return result;
    }
    private bool SafeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) return false;
        var segments = relative.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment is "" or "." or ".." || segment.Any(c => c < 32 || "*?\"<>|".Contains(c)))) return false;
        foreach (var segment in segments)
        {
            try { if (ProfileRules.Name(segment) != segment) return false; }
            catch (StorageException) { return false; }
        }
        // Restore only the same scope that Create backs up, never executable files or arbitrary root files.
        return relative == "settings.json" || segments.Length >= 2 && segments[0] is "profiles" or "providers" ||
            segments.Length == 3 && segments[0] == "browser-envs" && segments[2] == "env.json";
    }
    private static bool IsIo(Exception ex) => ex is IOException or UnauthorizedAccessException or StorageException;
    private static long Number(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<long>(out var number)) return number;
        if (node is JsonValue text && text.TryGetValue<string>(out var s) && long.TryParse(s, out var parsed)) return parsed;
        return 0;
    }
}
