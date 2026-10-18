using System.Text.Json.Nodes;

namespace ChunsonSpace.Core;

public sealed record RebuildItem(string Label, string Path, long Bytes, int Files);
public sealed record RebuildPlan(IReadOnlyList<RebuildItem> Items, IReadOnlyList<RebuildItem> Kept, long Bytes, int Files);
public sealed record RebuildMove(string Label, string Path, string To, long Bytes, int Files, string Status, string Message = "");
public sealed record RebuildResult(bool Success, bool Partial, bool ManifestCurrent, string Destination, IReadOnlyList<RebuildMove> Planned, IReadOnlyList<FileIssue> Failed, string Message);

public sealed class EnvironmentRebuild
{
    public const string Marker = ".codex-rebuild.json";
    private readonly IFileStorage files;
    private readonly string localAppData;
    public SwitchStoragePaths Paths { get; }
    private string Trash => Path.Combine(Paths.Root, "xy_clean", "CodexX-01", "Boss");
    public EnvironmentRebuild(SwitchStoragePaths paths, string localAppData = "", IFileStorage? files = null)
    {
        this.files = files ?? new PhysicalFileStorage();
        Paths = new FileSwitchStorage(paths, this.files).Paths;
        this.localAppData = string.IsNullOrWhiteSpace(localAppData) ? "" : Path.GetFullPath(localAppData);
    }
    public RebuildPlan Preview()
    {
        var targets = Targets().Select(Describe).ToArray();
        var kept = new List<RebuildItem>();
        foreach (var name in new[] { "config.toml", "plugins", "skills" })
        {
            var path = Path.Combine(Paths.ActiveHome, name);
            if (Exists(path)) kept.Add(Describe(("保留 " + name, path)));
        }
        if (localAppData.Length > 0 && files.DirectoryExists(Path.Combine(localAppData, "OpenAI", "Codex"))) kept.Add(Describe(("桌面安装运行时", Path.Combine(localAppData, "OpenAI", "Codex"))));
        return new(targets, kept, targets.Sum(item => item.Bytes), targets.Sum(item => item.Files));
    }

    public RebuildResult Execute()
    {
        var planned = new List<RebuildMove>(); var failed = new List<FileIssue>();
        var destination = Path.Combine(Trash, "codex-rebuild-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        bool manifestCurrent = false, incompleteCopy = false;
        try
        {
            var preview = Preview();
            if (preview.Items.Count == 0) return new(true, false, true, "", [], [], "当前环境没有需要重建的状态");
            files.CreateDirectory(destination);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Marker };
            foreach (var item in preview.Items)
            {
                var name = Path.GetFileName(item.Path); var original = name; int suffix = 1;
                while (!reserved.Add(name)) name = original + "." + suffix++;
                var target = Path.Combine(destination, name);
                if (Within(destination, item.Path) || Same(destination, item.Path) || !Same(Path.GetDirectoryName(target)!, destination)) throw new IOException("重建暂存路径越界或位于目标内部");
                planned.Add(new(item.Label, item.Path, target, item.Bytes, item.Files, "pending"));
            }
            Persist(false);
            manifestCurrent = true;
        }
        catch (Exception error) when (Io(error))
        {
            failed.Add(Issue(destination, error));
            return Result("重建清单未保存，尚未移动任何状态");
        }
        for (var index = 0; index < planned.Count; index++)
        {
            var item = planned[index];
            try
            {
                if (Exists(item.To)) throw new IOException("重建目的地已经存在");
                MoveVerified(item.Path, item.To);
                planned[index] = item with { Status = "moved" };
            }
            catch (Exception error) when (Io(error))
            {
                incompleteCopy |= Exists(item.To);
                planned[index] = item with { Status = "failed", Message = error.GetType().Name };
                failed.Add(Issue(item.Path, error));
            }
            try { Persist(planned.All(row => row.Status == "moved")); }
            catch (Exception error) when (Io(error))
            {
                manifestCurrent = false; failed.Add(Issue(Path.Combine(destination, Marker), error)); break;
            }
        }
        return Result(manifestCurrent ? failed.Count == 0 ? "环境状态已移入恢复目录，配置/插件/技能/运行时保留" : "环境重建部分失败，已移动状态与恢复清单已保留" : "清单更新失败，已停止后续移动，按原路径与目的地核对");

        void Persist(bool complete)
        {
            var rows = new JsonArray(planned.Select(item => (JsonNode)new JsonObject { ["label"] = item.Label, ["path"] = item.Path, ["to"] = item.To, ["bytes"] = item.Bytes, ["files"] = item.Files, ["status"] = item.Status, ["message"] = item.Message }).ToArray());
            var moved = new JsonArray(rows.OfType<JsonObject>().Where(row => JsonFiles.Text(row, "status") == "moved").Select(row => row.DeepClone()).ToArray());
            var errors = new JsonArray(failed.Select(error => (JsonNode)new JsonObject { ["path"] = error.Path, ["message"] = error.Message, ["stage"] = "move-or-manifest" }).ToArray());
            JsonFiles.Write(files, Path.Combine(destination, Marker), new() { ["kind"] = "codex-rebuild", ["at"] = DateTimeOffset.Now.ToString("O"), ["note"] = "原状态保留，可按清单搬回", ["planned"] = rows, ["moved"] = moved, ["failed"] = errors, ["complete"] = complete });
        }
        RebuildResult Result(string message) => new(manifestCurrent && failed.Count == 0 && planned.All(item => item.Status == "moved"), planned.Any(item => item.Status == "moved") || incompleteCopy, manifestCurrent, destination, planned.ToArray(), failed.ToArray(), message);
    }

    private IReadOnlyList<(string Label, string Path)> Targets()
    {
        var candidates = new List<(string Label, string Path)>();
        foreach (var path in files.Files(Paths.ActiveHome, false).Concat(files.Directories(Paths.ActiveHome)))
        {
            var name = Path.GetFileName(path);
            if (name == "config.toml" || name is "plugins" or "skills" && files.DirectoryExists(path)) continue;
            candidates.Add(("Codex " + name, path));
        }
        if (Paths.WebDesktopRoot is { } web) candidates.Add(("桌面 userData", Directory.GetParent(Directory.GetParent(web)!.FullName)!.FullName));
        if (Paths.PackagedDesktopRoot is { } packaged) candidates.Add(("MSIX 桌面 userData", packaged));
        if (localAppData.Length > 0)
        {
            candidates.Add(("桌面日志", Path.Combine(localAppData, "Codex")));
            candidates.Add(("桌面扩展状态", Path.Combine(localAppData, "OpenAI", "extension")));
        }
        foreach (var name in new[] { "electron-envs", "electron-web-state" }) candidates.Add(("工具桌面状态 " + name, Path.Combine(Paths.Root, name)));
        var existing = candidates.Where(item => Exists(item.Path)).DistinctBy(item => Path.GetFullPath(item.Path), StringComparer.OrdinalIgnoreCase).ToArray();
        var result = existing.Where(item => !existing.Any(parent => !Same(parent.Path, item.Path) && Within(item.Path, parent.Path))).ToArray();
        foreach (var item in result)
        {
            PhysicalFileStorage.RejectLinks(item.Path);
            if (Same(item.Path, Paths.Root) || Within(Paths.Root, item.Path) || Within(Trash, item.Path) || Same(item.Path, Trash)) throw new StorageException(StorageError.InvalidValue, item.Path, "重建目标不能包含工具数据或恢复材料");
            foreach (var kept in new[] { "config.toml", "plugins", "skills" }.Select(name => Path.Combine(Paths.ActiveHome, name)))
                if (Exists(kept) && (Same(item.Path, kept) || Within(kept, item.Path))) throw new StorageException(StorageError.InvalidValue, item.Path, "重建目标不能包含需要保留的配置/插件/技能");
            if (localAppData.Length > 0)
            {
                var installedRuntime = Path.Combine(localAppData, "OpenAI", "Codex");
                if (Same(item.Path, installedRuntime) || Within(item.Path, installedRuntime) || Within(installedRuntime, item.Path)) throw new StorageException(StorageError.InvalidValue, item.Path, "重建目标不能包含已安装运行时");
            }
        }
        return result;
    }
    private RebuildItem Describe((string Label, string Path) entry)
    {
        var list = files.DirectoryExists(entry.Path) ? files.Files(entry.Path, true) : [entry.Path];
        return new(entry.Label, entry.Path, list.Sum(path => files.Length(path)), list.Count);
    }
    private void MoveVerified(string source, string destination)
    {
        if (!files.DirectoryExists(source))
        {
            var hash = files.Hash(source); files.CopyAtomic(source, destination);
            if (files.Hash(source) != hash || files.Hash(destination) != hash) throw new IOException("重建文件复制校验失败");
            files.DeleteFile(source); return;
        }
        var paths = files.Files(source, true); var hashes = paths.ToDictionary(path => path, files.Hash, StringComparer.OrdinalIgnoreCase);
        CopyTree(source, destination);
        var now = files.Files(source, true);
        if (now.Count != paths.Count || now.Any(path => !hashes.TryGetValue(path, out var hash) || files.Hash(path) != hash || files.Hash(Path.Combine(destination, Path.GetRelativePath(source, path))) != hash)) throw new IOException("重建目录内容在复制期间发生变化");
        files.DeleteDirectory(source, true);
    }
    private void CopyTree(string source, string destination)
    {
        files.CreateDirectory(destination);
        foreach (var directory in files.Directories(source)) CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        foreach (var path in files.Files(source, false)) files.CopyAtomic(path, Path.Combine(destination, Path.GetFileName(path)));
    }
    private bool Exists(string path) => files.DirectoryExists(path) || files.FileExists(path);
    private static bool Same(string left, string right) => Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string path, string directory) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Io(Exception error) => error is IOException or UnauthorizedAccessException or StorageException;
    private static FileIssue Issue(string path, Exception error) => error is StorageException value ? new(value.FilePath, value.Code, value.Message) : new(path, StorageError.Io, error.GetType().Name);
}
