using System.Text;
using System.Text.Json.Nodes;

namespace FAswitch.Core;

public sealed record ProfileSaveResult(bool Success, bool Exists, bool AccountLimit, string Directory, IReadOnlyList<string> RecoveryPaths, IReadOnlyList<FileIssue> Warnings, string Message);
public sealed record AccountArchiveRotation(int Kept, int Removed, IReadOnlyList<FileIssue> Warnings);
public sealed record AccountDeleteResult(bool Success, IReadOnlyList<string> Archives, AccountArchiveRotation Rotation, IReadOnlyList<FileIssue> Warnings, string Message);
public sealed record ProfileLogoutResult(bool Success, bool Recovered, string Snapshot, IReadOnlyList<string> Removed, IReadOnlyList<FileIssue> Warnings, string Message);

public sealed class ProfileMaintenance
{
    public const int AccountArchiveMaximum = 6;
    public const string AccountArchiveMarker = ".account-archive.json";
    private readonly IFileStorage files;
    public SwitchStoragePaths Paths { get; }
    private string Trash => Path.Combine(Paths.Root, "xy_clean", "CodexX-01", "Boss");
    public ProfileMaintenance(SwitchStoragePaths paths, IFileStorage? files = null)
    {
        this.files = files ?? new PhysicalFileStorage();
        Paths = new FileSwitchStorage(paths, this.files).Paths;
    }

    public ProfileSaveResult SaveCurrentAccount(string name, bool overwrite, bool isolated, bool developer = false)
    {
        name = ProfileRules.Name(name);
        var destination = Path.Combine(Paths.Root, "profiles", name);
        var recovery = new List<string>(); var warnings = new List<FileIssue>();
        var stage = Path.Combine(Trash, "profile-save-stage-" + Guid.NewGuid().ToString("N"));
        string? archive = null;
        try
        {
            var auth = JsonFiles.Read(files, Path.Combine(Paths.ActiveHome, "auth.json"));
            if (JsonFiles.Text(auth, "auth_mode") == "apikey") return SaveFailure("当前生效的是供应商，不能存成账号档案");
            if (auth["tokens"] is not JsonObject tokens || JsonFiles.Text(tokens, "account_id").Length == 0 || JsonFiles.Text(tokens, "access_token").Length == 0)
                return SaveFailure("当前没有可保存的有效账号凭据，目标档案保持不变");
            bool existed = files.DirectoryExists(destination);
            bool nonempty = existed && (files.Files(destination, false).Count > 0 || files.Directories(destination).Count > 0);
            if (nonempty && !overwrite) return new(false, true, false, destination, [], [], "账号档案已存在，请明确选择覆盖");
            files.CreateDirectory(stage); recovery.Add(stage);
            foreach (var relative in SwitchFileRules.StateFiles) CopyIfPresent(Paths.ActiveHome, stage, relative);
            if (isolated) CopySessions(Paths.ActiveHome, stage);
            var installation = existed && files.FileExists(Path.Combine(destination, "installation_id")) ? Encoding.UTF8.GetString(files.Read(Path.Combine(destination, "installation_id"))).Trim() : "";
            if (installation.Length == 0) installation = Guid.NewGuid().ToString();
            files.WriteAtomic(Path.Combine(stage, "installation_id"), Encoding.UTF8.GetBytes(installation));
            foreach (var relative in new[] { ".codex-global-state.json", ".codex-global-state.json.bak" })
            {
                var path = Path.Combine(stage, relative);
                if (!files.FileExists(path)) continue;
                var state = JsonFiles.Read(files, path); state[SwitchFileRules.InstallationKey] = installation; JsonFiles.Write(files, path, state);
            }
            if (existed)
            {
                foreach (var layer in new[] { SwitchFileRules.PackagedDesktopLayer, SwitchFileRules.WebDesktopLayer })
                    if (files.DirectoryExists(Path.Combine(destination, layer))) CopyTree(Path.Combine(destination, layer), Path.Combine(stage, layer));
                files.CreateDirectory(Trash);
                archive = UniqueArchive(name + "-" + Stamp());
                files.MoveDirectory(destination, archive); recovery.Add(archive);
            }
            files.CreateDirectory(Path.GetDirectoryName(destination)!);
            try { files.MoveDirectory(stage, destination); recovery.Remove(stage); }
            catch (Exception error) when (Io(error))
            {
                if (archive is not null && !files.DirectoryExists(destination))
                {
                    try { files.MoveDirectory(archive, destination); recovery.Remove(archive); }
                    catch (Exception restore) when (Io(restore)) { warnings.Add(Issue(archive, restore)); }
                }
                warnings.Add(Issue(stage, error));
                return SaveFailure("新档案提交失败，旧档案或恢复材料已保留");
            }
            return new(true, false, false, destination, recovery, warnings, overwrite ? "当前账号已覆盖保存，原安装身份与桌面层已保留" : "当前账号档案已保存");
        }
        catch (Exception error) when (Io(error)) { warnings.Add(Issue(stage, error)); return SaveFailure("保存当前账号失败，目标原档案与恢复材料已保留"); }
        ProfileSaveResult SaveFailure(string message) => new(false, false, false, destination, recovery, warnings, message);
    }

    public AccountDeleteResult DeleteAccount(string name)
    {
        name = ProfileRules.Name(name);
        var archives = new List<string>(); var warnings = new List<FileIssue>();
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var profile = Path.Combine(Paths.Root, "profiles", name);
        if (!files.DirectoryExists(profile)) return new(false, [], new(0, 0, []), [], "账号档案不存在");
        try
        {
            // The first authorization keeps its original profile path while Chrome
            // is open. Archive this linked profile with the account, never another account.
            var browserProfile = new AccountBrowserProfiles(Paths.Root, files).Read(name);
            var accountBrowser = Path.Combine(Paths.Root, "browser-envs", name);
            var extraBrowser = browserProfile is not null && !browserProfile.StartsWith(accountBrowser + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? new[] { (browserProfile, "-web") } : Array.Empty<(string, string)>();
            files.CreateDirectory(Trash);
            foreach (var (source, label) in new[] { (profile, ""), (accountBrowser, "-browser"), (Path.Combine(Paths.Root, "electron-envs", name), "-desktop") }.Concat(extraBrowser))
            {
                if (!files.DirectoryExists(source)) continue;
                var destination = UniqueArchive(name + label + "-" + stamp);
                try
                {
                    files.MoveDirectory(source, destination); archives.Add(destination);
                    try { JsonFiles.Write(files, Path.Combine(destination, AccountArchiveMarker), new() { ["kind"] = "account-archive", ["name"] = name, ["stamp"] = stamp, ["version"] = 1 }); }
                    catch (Exception marker) when (Io(marker)) { warnings.Add(Issue(destination, marker)); }
                }
                catch (Exception error) when (Io(error))
                {
                    warnings.Add(Issue(source, error));
                    if (source == profile) return new(false, archives, new(0, 0, []), warnings, "账号档案归档失败，未删除");
                }
            }
            var rotation = RotateAccountArchives(); warnings.AddRange(rotation.Warnings);
            return new(true, archives.Where(files.DirectoryExists).ToArray(), rotation, warnings, warnings.Count == 0 ? "账号与独立环境已移入恢复目录" : "账号已归档，部分独立环境或归档清理未完成");
        }
        catch (Exception error) when (Io(error)) { warnings.Add(Issue(Trash, error)); return new(false, archives, new(0, 0, []), warnings, "账号归档未完成"); }
    }

    public AccountArchiveRotation RotateAccountArchives(int keep = AccountArchiveMaximum)
    {
        if (keep < 0) throw new ArgumentOutOfRangeException(nameof(keep));
        var groups = new Dictionary<(string Stamp, string Name), List<string>>();
        var warnings = new List<FileIssue>(); int removed = 0;
        foreach (var directory in files.Directories(Trash))
        {
            if (!DirectTrashChild(directory)) continue;
            try
            {
                var marker = Path.Combine(directory, AccountArchiveMarker);
                if (!files.FileExists(marker)) continue;
                var data = JsonFiles.Read(files, marker);
                var stamp = JsonFiles.Text(data, "stamp"); var name = JsonFiles.Text(data, "name");
                if (JsonFiles.Text(data, "kind") != "account-archive" || stamp.Length == 0) continue;
                var key = (stamp, name);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                list.Add(directory);
            }
            catch (Exception error) when (Io(error)) { warnings.Add(Issue(directory, error)); }
        }
        foreach (var group in groups.OrderBy(p => p.Key.Stamp, StringComparer.Ordinal).ThenBy(p => p.Key.Name, StringComparer.Ordinal).Take(Math.Max(0, groups.Count - keep)))
        {
            bool complete = true;
            foreach (var directory in group.Value)
            {
                try
                {
                    if (!DirectTrashChild(directory)) throw new IOException("账号归档目录越界");
                    PhysicalFileStorage.RejectLinks(directory);
                    files.DeleteDirectory(directory, true);
                }
                catch (Exception error) when (Io(error)) { complete = false; warnings.Add(Issue(directory, error)); }
            }
            if (complete) removed++;
        }
        return new(groups.Count - removed, removed, warnings);
    }

    public ProfileLogoutResult Logout(bool clearDesktopState = false)
    {
        // No session/SQLite history is deleted. All mutation targets are copied before the first deletion.
        var removed = new List<string>(); var warnings = new List<FileIssue>();
        var snapshot = Path.Combine(Trash, "before-logout-" + Stamp() + "-" + Guid.NewGuid().ToString("N")[..8]);
        var originals = new List<(string Source, string Stored, bool Directory)>();
        var desktopSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool mutated = false;
        try
        {
            files.CreateDirectory(snapshot);
            foreach (var relative in SwitchFileRules.StateFiles.Concat(SwitchFileRules.IdentityFiles))
            {
                var path = Path.Combine(Paths.ActiveHome, relative);
                if (files.FileExists(path)) originals.Add((path, Path.Combine(snapshot, "home", relative), false));
            }
            foreach (var (path, stored) in new[] { (Path.Combine(Paths.ActiveHome, "config.toml"), "config.toml"), (Path.Combine(Paths.Root, "providers", SwitchFileRules.ProviderStateFile), "provider-state.json") })
                if (files.FileExists(path)) originals.Add((path, Path.Combine(snapshot, stored), false));
            if (clearDesktopState)
                foreach (var (root, layer) in new[] { (Paths.PackagedDesktopRoot, SwitchFileRules.PackagedDesktopLayer), (Paths.WebDesktopRoot, SwitchFileRules.WebDesktopLayer) })
                {
                    if (root is null) continue;
                    foreach (var relative in SwitchFileRules.ElectronRootDirectories.Concat(SwitchFileRules.ElectronRootFiles))
                    {
                        var path = Path.Combine(root, relative);
                        if (files.DirectoryExists(path)) { originals.Add((path, Path.Combine(snapshot, layer, relative), true)); desktopSources.Add(path); }
                        else if (files.FileExists(path)) { originals.Add((path, Path.Combine(snapshot, layer, relative), false)); desktopSources.Add(path); }
                    }
                }
            foreach (var item in originals)
                if (item.Directory) CopyTree(item.Source, item.Stored); else CopyVerified(item.Source, item.Stored);
            var manifest = new JsonArray(originals.Select(item => (JsonNode)new JsonObject { ["source"] = item.Source, ["stored"] = item.Stored, ["directory"] = item.Directory }).ToArray());
            JsonFiles.Write(files, Path.Combine(snapshot, ".logout.json"), new() { ["kind"] = "logout", ["root"] = Paths.Root, ["home"] = Paths.ActiveHome, ["items"] = manifest });
            bool wasProvider = files.FileExists(Path.Combine(Paths.ActiveHome, "auth.json")) && JsonFiles.Text(JsonFiles.Read(files, Path.Combine(Paths.ActiveHome, "auth.json")), "auth_mode") == "apikey";
            mutated = true;
            foreach (var relative in SwitchFileRules.StateFiles.Concat(SwitchFileRules.IdentityFiles))
            {
                var path = Path.Combine(Paths.ActiveHome, relative); if (!files.FileExists(path)) continue;
                files.DeleteFile(path); removed.Add(path);
            }
            if (clearDesktopState)
                foreach (var item in originals.Where(item => desktopSources.Contains(item.Source)))
                {
                    if (item.Directory) files.DeleteDirectory(item.Source, true); else files.DeleteFile(item.Source); removed.Add(item.Source);
                }
            new ProviderConfig(files, Paths.Root, Paths.ActiveHome).Leave(wasProvider);
            if (files.FileExists(Path.Combine(Paths.ActiveHome, "auth.json"))) throw new IOException("登录凭据仍然存在");
            return new(true, false, snapshot, removed, warnings, "登录态已清空，对话历史保留");
        }
        catch (Exception error) when (Io(error))
        {
            warnings.Add(Issue(snapshot, error));
            bool recovered = !mutated;
            if (mutated)
            {
                recovered = true;
                foreach (var item in originals)
                {
                    try { if (item.Directory) CopyTree(item.Stored, item.Source); else CopyVerified(item.Stored, item.Source); }
                    catch (Exception restore) when (Io(restore)) { recovered = false; warnings.Add(Issue(item.Source, restore)); }
                }
            }
            return new(false, recovered, snapshot, removed, warnings, recovered ? "登出失败，原登录态已恢复或未修改" : "登出与恢复未完整完成，恢复材料已保留");
        }
    }

    private void CopySessions(string source, string destination)
    {
        var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in SwitchFileRules.SessionDirectories)
            foreach (var file in files.Files(Path.Combine(source, directory), true))
            { var relative = Path.GetRelativePath(source, file); if (copied.Add(relative)) CopyVerified(file, Path.Combine(destination, relative)); }
        foreach (var relative in SwitchFileRules.SessionFiles)
            if (copied.Add(relative)) CopyIfPresent(source, destination, relative);
    }
    private void CopyIfPresent(string source, string destination, string relative) { var file = Path.Combine(source, relative); if (files.FileExists(file)) CopyVerified(file, Path.Combine(destination, relative)); }
    private void CopyTree(string source, string destination)
    {
        files.CreateDirectory(destination);
        foreach (var directory in files.Directories(source)) CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        foreach (var file in files.Files(source, false)) CopyVerified(file, Path.Combine(destination, Path.GetFileName(file)));
    }
    private void CopyVerified(string source, string destination) { files.CopyAtomic(source, destination); if (files.Hash(source) != files.Hash(destination)) throw new IOException("复制内容校验不一致"); }
    private string UniqueArchive(string name)
    {
        var destination = Path.Combine(Trash, name); int suffix = 1;
        while (files.DirectoryExists(destination) || files.FileExists(destination)) destination = Path.Combine(Trash, name + "-" + suffix++);
        return destination;
    }
    private bool DirectTrashChild(string path) => Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(Path.GetFullPath(Trash), StringComparison.OrdinalIgnoreCase);
    private static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
    private static bool Io(Exception error) => error is IOException or UnauthorizedAccessException or StorageException;
    private static FileIssue Issue(string path, Exception error) => error is StorageException value ? new(value.FilePath, value.Code, value.Message) : new(path, StorageError.Io, error.GetType().Name);
}
