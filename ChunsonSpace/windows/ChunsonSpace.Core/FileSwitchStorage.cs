using System.Text;
using System.Text.Json.Nodes;

namespace ChunsonSpace.Core;

public sealed class FileSwitchStorage : ISwitchStorage
{
    private readonly IFileStorage files;
    private readonly ProviderConfig providerConfig;
    private readonly FileProfileStore profiles;
    public SwitchStoragePaths Paths { get; }
    private string Trash => Path.Combine(Paths.Root, "xy_clean", "CodexX-01", "Boss");
    private string ProviderState => Path.Combine(Paths.Root, "providers", SwitchFileRules.ProviderStateFile);
    private string Config => Path.Combine(Paths.ActiveHome, "config.toml");
    public FileSwitchStorage(SwitchStoragePaths paths, IFileStorage? files = null)
    {
        Paths = Normalize(paths);
        this.files = files ?? new PhysicalFileStorage();
        providerConfig = new(this.files, Paths.Root, Paths.ActiveHome);
        profiles = new(Paths.Root, this.files);
        foreach (var path in new[] { Paths.Root, Paths.ActiveHome, Paths.PackagedDesktopRoot, Paths.WebDesktopRoot }.Where(path => path is not null)) PhysicalFileStorage.RejectLinks(path!);
        ValidateRoots();
    }
    public IdentityRef? ReadActiveIdentity()
    {
        var authPath = Path.Combine(Paths.ActiveHome, "auth.json");
        if (!files.FileExists(authPath)) return null;
        JsonObject auth;
        try { auth = JsonFiles.Read(files, authPath); }
        catch (StorageException error) when (error.Code == StorageError.Corrupt) { return null; }
        if (JsonFiles.Text(auth, "auth_mode") == "apikey")
        {
            var key = JsonFiles.Text(auth, "OPENAI_API_KEY");
            if (key.Length == 0) return null;
            var baseUrl = providerConfig.ReadBaseUrl();
            var candidates = new List<IdentityRef>();
            string current = "";
            if (files.FileExists(ProviderState))
                try { current = JsonFiles.Text(JsonFiles.Read(files, ProviderState), "current"); }
                catch (StorageException error) when (error.Code == StorageError.Corrupt) { current = ""; }
            foreach (var directory in files.Directories(Path.Combine(Paths.Root, "providers")))
            {
                var path = Path.Combine(directory, "provider.json");
                if (!files.FileExists(path)) continue;
                JsonObject data;
                try { data = JsonFiles.Read(files, path); }
                catch (StorageException error) when (error.Code == StorageError.Corrupt) { continue; }
                if (JsonFiles.Text(data, "api_key") != key) continue;
                var matchesUrl = baseUrl is not null && JsonFiles.Text(data, "base_url").Trim() == baseUrl;
                var identity = new IdentityRef(IdentityKind.Provider, Path.GetFileName(directory));
                if (identity == new IdentityRef(IdentityKind.Provider, current) && (baseUrl is null || matchesUrl)) return identity;
                if (matchesUrl) candidates.Add(identity);
            }
            return candidates.Count == 1 ? candidates[0] : null;
        }
        var accountId = JsonFiles.Text(auth["tokens"] as JsonObject ?? new(), "account_id");
        if (accountId.Length == 0) return null;
        var accounts = new List<IdentityRef>();
        foreach (var directory in files.Directories(Path.Combine(Paths.Root, "profiles")))
        {
            var path = Path.Combine(directory, "auth.json");
            if (!files.FileExists(path)) continue;
            JsonObject data;
            try { data = JsonFiles.Read(files, path); }
            catch (StorageException error) when (error.Code == StorageError.Corrupt) { continue; }
            if (JsonFiles.Text(data["tokens"] as JsonObject ?? new(), "account_id") == accountId) accounts.Add(new(IdentityKind.Account, Path.GetFileName(directory)));
        }
        if (accounts.Count == 1) return accounts[0];
        var installationPath = Path.Combine(Paths.ActiveHome, "installation_id");
        if (accounts.Count > 1 && files.FileExists(installationPath))
        {
            var installation = Encoding.UTF8.GetString(files.Read(installationPath)).Trim();
            if (installation.Length > 0)
            {
                var matching = accounts.Where(account =>
                {
                    var candidate = Path.Combine(profiles.ProfileDirectory(account), "installation_id");
                    return files.FileExists(candidate) && Encoding.UTF8.GetString(files.Read(candidate)).Trim() == installation;
                }).ToArray();
                if (matching.Length == 1) return matching[0];
            }
        }
        return null;
    }
    public void Preflight(IdentityRef target, bool isolated)
    {
        var source = profiles.ProfileDirectory(target);
        if (!files.DirectoryExists(source)) throw new StorageException(StorageError.Missing, source, "目标档案不存在。");
        var authPath = Path.Combine(source, "auth.json");
        if (target.Kind == IdentityKind.Account && !files.FileExists(authPath)) throw new StorageException(StorageError.LoginRequired, authPath, "账号档案尚未登录，请先登录一次。");
        var auth = JsonFiles.Read(files, authPath);
        if (target.Kind == IdentityKind.Provider)
        {
            var data = JsonFiles.Read(files, Path.Combine(source, "provider.json"));
            ProfileRules.BaseUrl(JsonFiles.Text(data, "base_url"));
            if (JsonFiles.Text(data, "api_key").Length == 0 || JsonFiles.Text(auth, "auth_mode") != "apikey" || JsonFiles.Text(auth, "OPENAI_API_KEY") != JsonFiles.Text(data, "api_key"))
                throw new StorageException(StorageError.Corrupt, source, "供应商配置与凭据不一致。");
        }
        else if (auth["tokens"] is not JsonObject tokens || JsonFiles.Text(tokens, "account_id").Length == 0)
            throw new StorageException(StorageError.Corrupt, source, "账号凭据缺少 account_id。");
        // Fully enumerate relevant sources now; linked or inaccessible input fails before mutations.
        foreach (var relative in LoginFiles(false)) if (files.FileExists(Path.Combine(source, relative))) _ = files.Hash(Path.Combine(source, relative));
        if (isolated)
        {
            foreach (var relative in SessionFiles(source)) _ = files.Hash(Path.Combine(source, relative));
            foreach (var layer in DesktopRoots()) if (layer.Root is not null && files.DirectoryExists(layer.Root)) _ = ElectronFiles(layer.Root);
            var owner = Owner(target);
            foreach (var layer in DesktopRoots()) if (files.DirectoryExists(Path.Combine(owner, layer.Name))) _ = ElectronFiles(Path.Combine(owner, layer.Name));
        }
    }
    public SwitchSnapshot Snapshot(string action, bool isolated)
    {
        action = ProfileRules.Name(action);
        var directory = Path.Combine(Trash, "before-" + action + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            files.CreateDirectory(directory);
            CopyLogin(Paths.ActiveHome, directory, isolated);
            if (isolated) CopySessions(Paths.ActiveHome, directory);
            var marks = new JsonObject
            {
                ["version"] = 1, ["root"] = Paths.Root, ["active_home"] = Paths.ActiveHome, ["isolated"] = isolated,
                ["config"] = files.FileExists(Config), ["provider_state"] = files.FileExists(ProviderState)
            };
            var desktop = new JsonObject();
            if (isolated)
            {
                foreach (var layer in DesktopRoots())
                {
                    if (layer.Root is null) continue;
                    var present = files.DirectoryExists(layer.Root);
                    desktop[layer.Name] = new JsonObject { ["root"] = layer.Root, ["present"] = present };
                    if (present) CopyElectron(layer.Root, Path.Combine(directory, layer.Name));
                }
            }
            marks["desktop"] = desktop;
            if (files.FileExists(Config)) Copy(Config, Path.Combine(directory, "config.toml"));
            if (files.FileExists(ProviderState)) Copy(ProviderState, Path.Combine(directory, SwitchFileRules.ProviderStateFile));
            var manifest = new JsonObject();
            foreach (var file in files.Files(directory, true)) manifest[Path.GetRelativePath(directory, file)] = files.Hash(file);
            marks["manifest"] = manifest;
            JsonFiles.Write(files, Path.Combine(directory, "_snapshot.json"), marks);
            return new(directory, Paths);
        }
        catch (Exception error) when (StorageFailure(error)) { throw Error(directory, "创建切换快照失败，已保留现有恢复材料。", error); }
    }
    public bool ArchiveCurrent(SwitchSnapshot snapshot, IdentityRef? current, bool isolated)
    {
        _ = ValidateSnapshot(snapshot, isolated);
        if (current is null) return false;
        var owner = Owner(current);
        try
        {
            CopyLogin(snapshot.Path, owner, false);
            if (isolated)
            {
                ReplaceSessions(snapshot.Path, owner, clearTransient: false);
                foreach (var layer in DesktopRoots())
                {
                    var stored = Path.Combine(snapshot.Path, layer.Name);
                    if (files.DirectoryExists(stored)) ReplaceElectronArchive(stored, Path.Combine(owner, layer.Name));
                }
            }
            return true;
        }
        catch (Exception error) when (StorageFailure(error)) { throw Error(snapshot.Path, "当前身份归档失败；活动数据未切换，快照已保留。", error); }
    }
    public SwitchApplyResult ApplyTarget(IdentityRef target, IdentityRef? current, bool isolated)
    {
        Preflight(target, isolated);
        var source = profiles.ProfileDirectory(target);
        var warnings = new List<FileIssue>();
        var recoveryPaths = new List<string>();
        try
        {
            if (target.Kind == IdentityKind.Provider) EnsureProviderHome(source);
            if (isolated) warnings.AddRange(ReplaceSessions(source, Paths.ActiveHome, clearTransient: true));
            if (target.Kind == IdentityKind.Provider)
            {
                var data = JsonFiles.Read(files, Path.Combine(source, "provider.json"));
                var activeAuth = JsonFiles.Read(files, Path.Combine(Paths.ActiveHome, "auth.json"), true);
                providerConfig.Enter(ProfileRules.Name(target.Name), JsonFiles.Text(data, "base_url"), JsonFiles.Text(data, "model"), JsonFiles.Text(activeAuth, "auth_mode") != "apikey" && current?.Kind != IdentityKind.Provider);
            }
            else EnsureInstallationId(source);
            CopyLogin(source, Paths.ActiveHome, false);
            SyncInstallation(Paths.ActiveHome);
            if (isolated)
            {
                foreach (var layer in DesktopRoots())
                {
                    if (layer.Root is null) continue;
                    var rootPresent = files.DirectoryExists(layer.Root);
                    if (rootPresent)
                    {
                        // Always preserve a verified copy before clearing. Unknown owners retain it permanently.
                        var recovery = Path.Combine(Trash, "electron-" + (current is null ? "orphan-" : "clear-") + layer.Name + "-" + Guid.NewGuid().ToString("N"));
                        CopyElectron(layer.Root, recovery);
                        recoveryPaths.Add(recovery);
                        warnings.AddRange(ClearElectron(layer.Root, recoveryPaths));
                    }
                    var stored = Path.Combine(Owner(target), layer.Name);
                    if (files.DirectoryExists(stored)) CopyElectron(stored, layer.Root);
                }
            }
            if (target.Kind == IdentityKind.Account) providerConfig.Leave(current?.Kind == IdentityKind.Provider);
            var active = ReadActiveIdentity();
            if (active != target) throw new StorageException(StorageError.Corrupt, Paths.ActiveHome, "目标凭据已写入，但无法确认活动身份；必须从快照恢复。");
            return new(active, warnings, recoveryPaths);
        }
        catch (Exception error) when (StorageFailure(error)) { throw Error(Paths.ActiveHome, "应用目标档案失败，调用方必须从原快照恢复。", error); }
    }
    public void Restore(SwitchSnapshot snapshot, bool isolated)
    {
        var marks = ValidateSnapshot(snapshot, isolated);
        try
        {
            if (isolated) _ = ReplaceSessions(snapshot.Path, Paths.ActiveHome, clearTransient: true);
            CopyLogin(snapshot.Path, Paths.ActiveHome, isolated);
            SyncInstallation(Paths.ActiveHome);
            RestorePresence(snapshot.Path, "config.toml", Config, JsonFiles.Boolean(marks, "config"));
            RestorePresence(snapshot.Path, SwitchFileRules.ProviderStateFile, ProviderState, JsonFiles.Boolean(marks, "provider_state"));
            if (isolated && marks["desktop"] is JsonObject desktop)
            {
                foreach (var layer in DesktopRoots())
                {
                    if (layer.Root is null || desktop[layer.Name] is not JsonObject entry) continue;
                    if (files.DirectoryExists(layer.Root))
                    {
                        var recovery = Path.Combine(Trash, "electron-restore-" + layer.Name + "-" + Guid.NewGuid().ToString("N"));
                        CopyElectron(layer.Root, recovery);
                        var warnings = ClearElectron(layer.Root, []);
                        if (warnings.Count > 0) throw new StorageException(StorageError.Io, layer.Root, "恢复桌面数据时缓存仍被占用，未完整恢复。");
                    }
                    if (JsonFiles.Boolean(entry, "present")) CopyElectron(Path.Combine(snapshot.Path, layer.Name), layer.Root);
                }
            }
        }
        catch (Exception error) when (StorageFailure(error)) { throw Error(snapshot.Path, "恢复失败，原快照和新增恢复材料已保留。", error); }
    }
    public SwitchCompleteResult Complete(SwitchSnapshot snapshot, bool archived, IReadOnlyList<string>? recoveryPaths = null)
    {
        if (!archived) return new(true, []);
        try
        {
            _ = ValidateSnapshot(snapshot, null);
            var warnings = new List<FileIssue>();
            var deleted = new List<string>();
            foreach (var path in (recoveryPaths ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Same(Path.GetDirectoryName(Path.GetFullPath(path))!, Trash))
                { warnings.Add(new(path, StorageError.InvalidValue, "恢复材料不在本次允许清理的恢复目录中。")); continue; }
                var name = Path.GetFileName(path);
                if (name.StartsWith("electron-orphan-", StringComparison.Ordinal)) continue;
                if (!name.StartsWith("electron-clear-", StringComparison.Ordinal) && !name.StartsWith("electron-cache-", StringComparison.Ordinal))
                { warnings.Add(new(path, StorageError.InvalidValue, "此恢复材料不允许自动清理。")); continue; }
                try { files.DeleteDirectory(path, true); deleted.Add(path); }
                catch (Exception error) when (StorageFailure(error)) { warnings.Add(new(path, StorageError.Io, "恢复材料清理失败：" + error.Message)); }
            }
            if (warnings.Count > 0) return new(true, warnings, deleted);
            files.DeleteDirectory(snapshot.Path, true);
            deleted.Add(snapshot.Path);
            return new(false, [], deleted);
        }
        catch (Exception error) when (StorageFailure(error)) { return new(true, [new(snapshot.Path, StorageError.Io, "暂存清理失败，切换已完成，快照仍保留：" + error.Message)]); }
    }
    private JsonObject ValidateSnapshot(SwitchSnapshot snapshot, bool? isolated)
    {
        if (Normalize(snapshot.Paths) != Paths || !Within(snapshot.Path, Trash) || !Path.GetFileName(snapshot.Path).StartsWith("before-", StringComparison.Ordinal))
            throw new StorageException(StorageError.InvalidValue, snapshot.Path, "快照路径与当前显式存储位置不一致。");
        var marks = JsonFiles.Read(files, Path.Combine(snapshot.Path, "_snapshot.json"));
        if (!Same(JsonFiles.Text(marks, "root"), Paths.Root) || !Same(JsonFiles.Text(marks, "active_home"), Paths.ActiveHome) ||
            isolated is not null && JsonFiles.Boolean(marks, "isolated") != isolated)
            throw new StorageException(StorageError.Corrupt, snapshot.Path, "快照存储位置或隔离设置不一致。");
        if (marks["manifest"] is not JsonObject manifest) throw new StorageException(StorageError.Corrupt, snapshot.Path, "快照缺少校验清单。");
        var actualFiles = files.Files(snapshot.Path, true).Select(path => Path.GetRelativePath(snapshot.Path, path)).Where(relative => relative != "_snapshot.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualFiles.SetEquals(manifest.Select(pair => pair.Key))) throw new StorageException(StorageError.Corrupt, snapshot.Path, "快照实际文件与校验清单不一致。");
        foreach (var required in new[] { (Key: "config", File: "config.toml"), (Key: "provider_state", File: SwitchFileRules.ProviderStateFile) })
            if (marks[required.Key] is not JsonValue boolean || !boolean.TryGetValue<bool>(out var present) || present != (manifest[required.File] is not null))
                throw new StorageException(StorageError.Corrupt, snapshot.Path, "快照原文件标记与清单不一致：" + required.File);
        foreach (var target in new[] { Paths.Root, Paths.ActiveHome, Paths.PackagedDesktopRoot, Paths.WebDesktopRoot }.Where(target => target is not null)) PhysicalFileStorage.RejectLinks(target!);
        foreach (var relative in LoginFiles(true).Concat(SwitchFileRules.SessionDirectories).Concat(SwitchFileRules.SessionFiles).Append("config.toml")) PhysicalFileStorage.RejectLinks(Path.Combine(Paths.ActiveHome, relative));
        PhysicalFileStorage.RejectLinks(ProviderState);
        foreach (var pair in manifest)
        {
            var path = Path.Combine(snapshot.Path, pair.Key);
            if (!SafeSnapshotRelative(pair.Key) || !Within(path, snapshot.Path) || !files.FileExists(path) || files.Hash(path) != pair.Value?.GetValue<string>())
                throw new StorageException(StorageError.Corrupt, snapshot.Path, "快照文件缺失或校验失败：" + pair.Key);
        }
        if (marks["desktop"] is JsonObject desktop)
        {
            foreach (var pair in desktop)
            {
                var actual = DesktopRoots().FirstOrDefault(layer => layer.Name == pair.Key).Root;
                if (pair.Value is not JsonObject entry || actual is null || !Same(JsonFiles.Text(entry, "root"), actual))
                    throw new StorageException(StorageError.Corrupt, snapshot.Path, "桌面数据位置已变化，未写入未知目录。");
                if (JsonFiles.Boolean(entry, "present") && !files.DirectoryExists(Path.Combine(snapshot.Path, pair.Key)))
                    throw new StorageException(StorageError.Corrupt, snapshot.Path, "快照缺少桌面数据层：" + pair.Key);
            }
        }
        return marks;
    }
    private void CopyLogin(string source, string destination, bool extra)
    {
        files.CreateDirectory(destination);
        foreach (var relative in LoginFiles(extra))
        {
            var sourceFile = Path.Combine(source, relative);
            var target = Path.Combine(destination, relative);
            if (files.FileExists(sourceFile)) Copy(sourceFile, target);
            else files.DeleteFile(target);
        }
    }
    private static IEnumerable<string> LoginFiles(bool extra) => SwitchFileRules.StateFiles.Concat(SwitchFileRules.IdentityFiles).Concat(extra ? SwitchFileRules.CleanExtraFiles : []).Distinct(StringComparer.Ordinal);
    private IEnumerable<string> SessionFiles(string source)
    {
        foreach (var directory in SwitchFileRules.SessionDirectories)
            foreach (var path in files.Files(Path.Combine(source, directory), true)) yield return Path.GetRelativePath(source, path);
        foreach (var relative in SwitchFileRules.SessionFiles) if (files.FileExists(Path.Combine(source, relative))) yield return relative;
    }
    private void CopySessions(string source, string destination)
    {
        foreach (var directory in SwitchFileRules.SessionDirectories)
            if (files.DirectoryExists(Path.Combine(source, directory))) CopyTree(Path.Combine(source, directory), Path.Combine(destination, directory));
        foreach (var relative in SwitchFileRules.SessionFiles) if (files.FileExists(Path.Combine(source, relative))) Copy(Path.Combine(source, relative), Path.Combine(destination, relative));
    }
    private IReadOnlyList<FileIssue> ReplaceSessions(string source, string destination, bool clearTransient)
    {
        foreach (var relative in SwitchFileRules.SessionDirectories.Concat(SwitchFileRules.SessionFiles)) DeleteItem(Path.Combine(destination, relative));
        CopySessions(source, destination);
        var warnings = new List<FileIssue>();
        if (clearTransient)
        {
            var transient = SwitchFileRules.TransientItems.Select(relative => Path.Combine(destination, relative)).ToList();
            transient.AddRange(files.Files(Path.Combine(destination, ".sandbox"), false).Where(path => Path.GetExtension(path) == ".log"));
            foreach (var path in transient)
                try { DeleteItem(path); }
                catch (Exception error) when (StorageFailure(error)) { warnings.Add(new(path, StorageError.Io, error.Message)); }
        }
        return warnings;
    }
    private void CopyTree(string source, string destination)
    {
        files.CreateDirectory(destination);
        foreach (var path in files.Files(source, false)) Copy(path, Path.Combine(destination, Path.GetFileName(path)));
        foreach (var directory in files.Directories(source)) CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
    private List<string> ElectronFiles(string source)
    {
        var result = new List<string>();
        void Walk(string directory)
        {
            foreach (var path in files.Files(directory, false)) result.Add(Path.GetRelativePath(source, path));
            foreach (var child in files.Directories(directory)) if (!SwitchFileRules.ElectronCacheDirectories.Contains(Path.GetFileName(child))) Walk(child);
        }
        foreach (var directory in SwitchFileRules.ElectronRootDirectories)
            if (files.DirectoryExists(Path.Combine(source, directory))) Walk(Path.Combine(source, directory));
        foreach (var relative in SwitchFileRules.ElectronRootFiles) if (files.FileExists(Path.Combine(source, relative))) result.Add(relative);
        return result;
    }
    private void CopyElectron(string source, string destination)
    {
        files.CreateDirectory(destination);
        foreach (var directory in SwitchFileRules.ElectronRootDirectories)
            if (files.DirectoryExists(Path.Combine(source, directory))) CopyEmptyElectronDirectories(source, destination, Path.Combine(source, directory));
        foreach (var relative in ElectronFiles(source)) Copy(Path.Combine(source, relative), Path.Combine(destination, relative));
    }
    private void CopyEmptyElectronDirectories(string source, string destination, string directory)
    {
        files.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var child in files.Directories(directory)) if (!SwitchFileRules.ElectronCacheDirectories.Contains(Path.GetFileName(child))) CopyEmptyElectronDirectories(source, destination, child);
    }
    private void ReplaceElectronArchive(string source, string destination)
    {
        var staging = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        var old = destination + ".old-" + Guid.NewGuid().ToString("N");
        CopyElectron(source, staging);
        var hadOld = files.DirectoryExists(destination);
        if (hadOld) files.MoveDirectory(destination, old);
        try { files.MoveDirectory(staging, destination); }
        catch
        {
            if (hadOld) files.MoveDirectory(old, destination);
            throw;
        }
        if (hadOld) files.DeleteDirectory(old, true);
    }
    private IReadOnlyList<FileIssue> ClearElectron(string root, List<string> recoveryPaths)
    {
        var warnings = new List<FileIssue>();
        var cacheBackup = Path.Combine(Trash, "electron-cache-" + Guid.NewGuid().ToString("N"));
        void MoveCaches(string directory)
        {
            foreach (var child in files.Directories(directory))
            {
                if (Path.GetFileName(child) == "Cache")
                {
                    var target = Path.Combine(cacheBackup, Path.GetRelativePath(root, child));
                    try
                    {
                        files.CreateDirectory(Path.GetDirectoryName(target)!);
                        files.MoveDirectory(child, target);
                        if (!recoveryPaths.Contains(cacheBackup)) recoveryPaths.Add(cacheBackup);
                    }
                    catch (Exception error) when (StorageFailure(error)) { warnings.Add(new(child, StorageError.Io, error.Message)); }
                }
                else MoveCaches(child);
            }
        }
        foreach (var relative in SwitchFileRules.ElectronRootDirectories)
            if (files.DirectoryExists(Path.Combine(root, relative))) MoveCaches(Path.Combine(root, relative));
        foreach (var relative in ElectronFiles(root)) files.DeleteFile(Path.Combine(root, relative));
        // Remove empty state directories; pure caches and occupied HTTP caches remain in place.
        void RemoveEmpty(string directory)
        {
            foreach (var child in files.Directories(directory)) if (!SwitchFileRules.ElectronCacheDirectories.Contains(Path.GetFileName(child))) RemoveEmpty(child);
            if (files.Files(directory, false).Count == 0 && files.Directories(directory).Count == 0) files.DeleteDirectory(directory, false);
        }
        foreach (var relative in SwitchFileRules.ElectronRootDirectories)
            if (files.DirectoryExists(Path.Combine(root, relative))) RemoveEmpty(Path.Combine(root, relative));
        return warnings;
    }
    private void EnsureProviderHome(string directory)
    {
        foreach (var relative in SwitchFileRules.StateFiles.Concat(SwitchFileRules.IdentityFiles).Where(relative => relative != "auth.json"))
            if (!files.FileExists(Path.Combine(directory, relative)) && files.FileExists(Path.Combine(Paths.ActiveHome, relative))) Copy(Path.Combine(Paths.ActiveHome, relative), Path.Combine(directory, relative));
    }
    private void EnsureInstallationId(string directory)
    {
        var path = Path.Combine(directory, "installation_id");
        if (files.FileExists(path) && Encoding.UTF8.GetString(files.Read(path)).Trim().Length > 0) return;
        files.WriteAtomic(path, Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
    }
    private void SyncInstallation(string directory)
    {
        var installation = Path.Combine(directory, "installation_id");
        if (!files.FileExists(installation)) return;
        var value = Encoding.UTF8.GetString(files.Read(installation)).Trim();
        foreach (var relative in SwitchFileRules.StateFiles.Where(relative => relative != "auth.json"))
        {
            var path = Path.Combine(directory, relative);
            if (!files.FileExists(path)) continue;
            var data = JsonFiles.Read(files, path);
            data[SwitchFileRules.InstallationKey] = value;
            JsonFiles.Write(files, path, data);
        }
    }
    private void RestorePresence(string source, string relative, string target, bool present)
    {
        if (present)
        {
            if (!files.FileExists(Path.Combine(source, relative))) throw new StorageException(StorageError.Corrupt, source, "快照缺少原文件：" + relative);
            Copy(Path.Combine(source, relative), target);
        }
        else files.DeleteFile(target);
    }
    private void DeleteItem(string path) { if (files.DirectoryExists(path)) files.DeleteDirectory(path, true); else files.DeleteFile(path); }
    private void Copy(string source, string destination) => files.CopyAtomic(source, destination);
    private string Owner(IdentityRef identity) => identity.Kind == IdentityKind.Provider ? Path.Combine(Paths.Root, "provider-layer") : profiles.ProfileDirectory(identity);
    private IEnumerable<(string Name, string? Root)> DesktopRoots()
    { yield return ("electron", Paths.PackagedDesktopRoot); yield return ("electron-web-state", Paths.WebDesktopRoot); }
    private static bool SafeSnapshotRelative(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) return false;
        foreach (var segment in relative.Replace('\\', '/').Split('/'))
            try { if (ProfileRules.Name(segment) != segment) return false; }
            catch (StorageException) { return false; }
        return true;
    }
    private static SwitchStoragePaths Normalize(SwitchStoragePaths paths) => new(Path.GetFullPath(paths.Root), Path.GetFullPath(paths.ActiveHome), paths.PackagedDesktopRoot is null ? null : Path.GetFullPath(paths.PackagedDesktopRoot), paths.WebDesktopRoot is null ? null : Path.GetFullPath(paths.WebDesktopRoot));
    private void ValidateRoots()
    {
        var runtimeRoots = new[] { Paths.ActiveHome, Paths.PackagedDesktopRoot, Paths.WebDesktopRoot }.Where(path => path is not null).Select(path => path!).ToArray();
        var ownedRoots = new[] { "profiles", "providers", "provider-layer", "browser-envs", "electron-envs", "xy_clean", "backups" }.Select(folder => Path.Combine(Paths.Root, folder)).ToArray();
        for (var i = 0; i < runtimeRoots.Length; i++)
        {
            if (Same(runtimeRoots[i], Paths.Root) || Within(Paths.Root, runtimeRoots[i]) || ownedRoots.Any(owned => Overlaps(owned, runtimeRoots[i])))
                throw new StorageException(StorageError.InvalidValue, runtimeRoots[i], "活动数据根目录不能与档案、供应商、备份或恢复材料重叠。");
            for (var j = i + 1; j < runtimeRoots.Length; j++)
                if (Overlaps(runtimeRoots[i], runtimeRoots[j])) throw new StorageException(StorageError.InvalidValue, runtimeRoots[i], "活动 Codex 与两层桌面数据目录不能重叠。");
        }
    }
    private static bool Overlaps(string left, string right) => Same(left, right) || Within(left, right) || Within(right, left);
    private static bool Same(string left, string right) => left.Length > 0 && right.Length > 0 && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar));
    private static bool Within(string path, string directory) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or StorageException or DecoderFallbackException;
    private static StorageException Error(string path, string message, Exception error) => new(error is StorageException storage ? storage.Code : StorageError.Io, path, message + " " + error.Message, error);
}
