using System.Text;
using System.Text.Json.Nodes;

namespace ChunsonSpace.Core;

/// <summary>Rebuild account runtime independently, without importing custom tools.</summary>
public sealed class ManagedCloneStorage : ISwitchStorage
{
    public const string OwnerMarker = ".chunson-switch-managed.json";
    public const string RuntimeMarker = ".chunson-managed-runtime.json";
    private const string IdentityMarker = ".chunson-active.json";
    private static readonly string[] KeptFiles = ["auth.json", "config.toml", "installation_id"];
    private readonly IFileStorage files;
    private readonly FileProfileStore profiles;
    public ManagedClonePaths ClonePaths { get; }
    public SwitchStoragePaths Paths => ClonePaths.StoragePaths;

    public ManagedCloneStorage(ManagedClonePaths paths, IFileStorage? files = null)
    {
        ClonePaths = paths;
        this.files = files ?? new PhysicalFileStorage();
        profiles = new(paths.AccountRoot, this.files);
        EnsureOwnership();
    }

    public IdentityRef? ReadActiveIdentity()
    {
        ValidateOwnership();
        var marker = Path.Combine(ClonePaths.RuntimeRoot, IdentityMarker);
        if (!files.FileExists(marker)) return null;
        var data = JsonFiles.Read(files, marker);
        if (!Enum.TryParse<IdentityKind>(JsonFiles.Text(data, "kind"), out var kind) || !Enum.IsDefined(kind))
            throw new StorageException(StorageError.Corrupt, marker, "受管分身身份标记无效。");
        var identity = new IdentityRef(kind, ProfileRules.Name(JsonFiles.Text(data, "name")));
        var active = JsonFiles.Read(files, Path.Combine(Paths.ActiveHome, "auth.json"));
        var stored = JsonFiles.Read(files, Path.Combine(profiles.ProfileDirectory(identity), "auth.json"));
        if (!MatchesRuntimeIdentity(active, stored, kind, data)) throw new StorageException(StorageError.Corrupt, marker, "受管分身凭据与身份标记不一致，未确认活动账号。");
        return identity;
    }

    public void PrepareProviderUpdate(IdentityRef identity)
    {
        if (identity.Kind != IdentityKind.Provider || ReadActiveIdentity() != identity) return;
        var marker = Path.Combine(ClonePaths.RuntimeRoot, IdentityMarker);
        var value = JsonFiles.Read(files, marker);
        if (JsonFiles.Text(value, "provider_key_baseline").Length > 0) return;
        // Old runtimes receive a baseline only after their auth still matches the saved profile.
        value["provider_key_baseline"] = ProviderKeyHash(JsonFiles.Read(files, Path.Combine(Paths.ActiveHome, "auth.json")));
        JsonFiles.Write(files, marker, value);
    }

    public void Preflight(IdentityRef target, bool isolated)
    {
        ValidateOwnership();
        if (files.FileExists(Path.Combine(Paths.ActiveHome, "auth.json")) && ReadActiveIdentity() is null)
            throw new StorageException(StorageError.Corrupt, Paths.ActiveHome, "受管分身存在无法确认归属的凭据，未清理或切换。");
        new FileSwitchStorage(Paths, files).Preflight(target, false);
        var source = profiles.ProfileDirectory(target);
        if (target.Kind == IdentityKind.Account)
        {
            var auth = JsonFiles.Read(files, Path.Combine(source, "auth.json"));
            if (JsonFiles.Text(auth["tokens"] as JsonObject ?? new(), "access_token").Length == 0)
                throw new StorageException(StorageError.LoginRequired, source, "账号授权缺少 access_token，请重新登录。");
        }
        foreach (var name in KeptFiles) if (files.FileExists(Path.Combine(source, name))) _ = files.Hash(Path.Combine(source, name));
        _ = ManagedRuntimeLinks.Inspect(ClonePaths, files); // Validate cache aliases without following or removing them.
    }

    public SwitchSnapshot Snapshot(string action, bool isolated)
    {
        ValidateOwnership();
        var destination = Path.Combine(ClonePaths.RecoveryRoot, "credentials-" + ProfileRules.Name(action) + "-" + Guid.NewGuid().ToString("N"));
        files.CreateDirectory(destination);
        CopyKept(Paths.ActiveHome, destination);
        CopyIfPresent(Path.Combine(ClonePaths.RuntimeRoot, IdentityMarker), Path.Combine(destination, IdentityMarker));
        var manifest = new JsonObject();
        foreach (var path in files.Files(destination, false)) manifest[Path.GetFileName(path)] = files.Hash(path);
        JsonFiles.Write(files, Path.Combine(destination, "snapshot.json"), new() { ["runtime"] = ClonePaths.RuntimeRoot, ["accounts"] = ClonePaths.AccountRoot, ["manifest"] = manifest });
        return new(destination, Paths);
    }

    public bool ArchiveCurrent(SwitchSnapshot snapshot, IdentityRef? current, bool isolated)
    {
        ValidateSnapshot(snapshot);
        if (current is null) return false;
        var auth = JsonFiles.Read(files, Path.Combine(snapshot.Path, "auth.json"));
        var destination = profiles.ProfileDirectory(current);
        var stored = JsonFiles.Read(files, Path.Combine(destination, "auth.json"));
        var identityMarker = JsonFiles.Read(files, Path.Combine(snapshot.Path, IdentityMarker));
        if (!MatchesRuntimeIdentity(auth, stored, current.Kind, identityMarker))
            throw new StorageException(StorageError.Corrupt, destination, "旧账号身份不一致，未覆盖账号库。");
        // Save runtime-refreshed tokens before removing any runtime data.
        var newerStoredAuth = current.Kind == IdentityKind.Account
            && DateTimeOffset.TryParse(JsonFiles.Text(stored, "last_refresh"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var storedTime)
            && (!DateTimeOffset.TryParse(JsonFiles.Text(auth, "last_refresh"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var runtimeTime) || storedTime >= runtimeTime);
        var providerKeyUpdated = current.Kind == IdentityKind.Provider && !SameIdentity(auth, stored, current.Kind);
        var savedConfig = Path.Combine(destination, "config.toml");
        var storedConfigHash = files.FileExists(savedConfig) ? files.Hash(savedConfig) : "absent";
        var configBaseline = JsonFiles.Text(identityMarker, "config_baseline");
        // Only the configuration loaded into this runtime may be replaced by its edits.
        // A changed account-library configuration remains authoritative.
        var saveConfig = storedConfigHash == configBaseline || configBaseline.Length == 0 && storedConfigHash == "absent";
        foreach (var name in KeptFiles)
            if (name != "config.toml" && (name != "auth.json" || !newerStoredAuth && !providerKeyUpdated)) CopyIfPresent(Path.Combine(snapshot.Path, name), Path.Combine(destination, name));
        if (saveConfig)
        {
            var sourceConfig = Path.Combine(snapshot.Path, "config.toml");
            var savedText = files.FileExists(savedConfig) ? Encoding.UTF8.GetString(files.Read(savedConfig)) : "";
            var runtimeText = files.FileExists(sourceConfig) ? Encoding.UTF8.GetString(files.Read(sourceConfig)) : "";
            var merged = TomlManagedKeys.MergeCleanManagedRuntime(savedText, runtimeText);
            if (merged.Length == 0) files.DeleteFile(savedConfig); else files.WriteAtomic(savedConfig, Encoding.UTF8.GetBytes(merged));
        }
        if (saveConfig) UpdateConfigBaseline(current);
        return true;
    }

    public SwitchApplyResult ApplyTarget(IdentityRef target, IdentityRef? current, bool isolated)
    {
        Preflight(target, false);
        RecreateRuntime();
        CopyKept(profiles.ProfileDirectory(target), Paths.ActiveHome);
        RemoveToolConfiguration();
        if (target.Kind == IdentityKind.Provider)
        {
            var provider = profiles.ReadProvider(target.Name);
            var config = Path.Combine(Paths.ActiveHome, "config.toml");
            var text = files.FileExists(config) ? Encoding.UTF8.GetString(files.Read(config)) : "";
            var values = new Dictionary<string, string?> { ["openai_base_url"] = System.Text.Json.JsonSerializer.Serialize(provider.BaseUrl) };
            if (provider.Model.Length > 0) values["model"] = System.Text.Json.JsonSerializer.Serialize(provider.Model);
            files.WriteAtomic(config, Encoding.UTF8.GetBytes(TomlManagedKeys.Merge(text, values)));
        }
        var savedConfig = Path.Combine(profiles.ProfileDirectory(target), "config.toml");
        JsonFiles.Write(files, Path.Combine(ClonePaths.RuntimeRoot, IdentityMarker), new() { ["kind"] = target.Kind.ToString(), ["name"] = target.Name,
            ["config_baseline"] = files.FileExists(savedConfig) ? files.Hash(savedConfig) : "absent",
            ["provider_key_baseline"] = target.Kind == IdentityKind.Provider ? ProviderKeyHash(JsonFiles.Read(files, Path.Combine(Paths.ActiveHome, "auth.json"))) : null });
        if (ReadActiveIdentity() != target) throw new StorageException(StorageError.Corrupt, Paths.ActiveHome, "受管分身目标身份未确认。");
        return new(target, [], []);
    }

    public void Restore(SwitchSnapshot snapshot, bool isolated)
    {
        ValidateSnapshot(snapshot);
        RecreateRuntime();
        CopyKept(snapshot.Path, Paths.ActiveHome);
        CopyIfPresent(Path.Combine(snapshot.Path, IdentityMarker), Path.Combine(ClonePaths.RuntimeRoot, IdentityMarker));
        RemoveToolConfiguration();
        var restored = ReadActiveIdentity();
        if (restored is not null)
        {
            var config = Path.Combine(Paths.ActiveHome, "config.toml");
            var savedConfig = Path.Combine(profiles.ProfileDirectory(restored), "config.toml");
            if (FileHashOrAbsent(config) == FileHashOrAbsent(savedConfig)) UpdateConfigBaseline(restored);
        }
    }

    public SwitchCompleteResult Complete(SwitchSnapshot snapshot, bool archived, IReadOnlyList<string>? recoveryPaths = null)
    {
        ValidateSnapshot(snapshot);
        files.DeleteDirectory(snapshot.Path, true);
        return new(false, [], [snapshot.Path]);
    }

    public void ResetRuntime()
    {
        var current = ReadActiveIdentity();
        _ = ManagedRuntimeLinks.Inspect(ClonePaths, files);
        var snapshot = Snapshot("reset", false);
        ArchiveCurrent(snapshot, current, false);
        RecreateRuntime();
        Complete(snapshot, current is not null);
    }

    private void RemoveToolConfiguration()
    {
        files.DeleteFile(Path.Combine(Paths.ActiveHome, "installation_id"));
        var path = Path.Combine(Paths.ActiveHome, "config.toml");
        if (!files.FileExists(path)) return;
        var text = Encoding.UTF8.GetString(files.Read(path)).TrimStart('\uFEFF');
        var cleaned = TomlManagedKeys.ForCleanManagedRuntime(text);
        if (cleaned != text) files.WriteAtomic(path, Encoding.UTF8.GetBytes(cleaned));
    }

    private void RecreateRuntime()
    {
        ValidateOwnership();
        var aliases = ManagedRuntimeLinks.Inspect(ClonePaths, files);
        ManagedRuntimeLinks.RemoveAliases(ClonePaths, aliases);
        _ = files.Files(ClonePaths.RuntimeRoot, true);
        // Keep the verified root marker throughout cleanup. Delete caches before credentials/identity.
        foreach (var path in files.Files(ClonePaths.RuntimeRoot, false))
            if (Path.GetFileName(path) is not (RuntimeMarker or IdentityMarker)) files.DeleteFile(path);
        foreach (var path in files.Directories(ClonePaths.RuntimeRoot))
            if (!Same(path, Paths.ActiveHome)) files.DeleteDirectory(path, true);
        foreach (var path in files.Directories(Paths.ActiveHome)) files.DeleteDirectory(path, true);
        foreach (var path in files.Files(Paths.ActiveHome, false))
            if (!KeptFiles.Contains(Path.GetFileName(path), StringComparer.Ordinal)) files.DeleteFile(path);
        InitializeRuntime();
        foreach (var name in new[] { "installation_id", "config.toml", "auth.json" }) files.DeleteFile(Path.Combine(Paths.ActiveHome, name));
        files.DeleteFile(Path.Combine(ClonePaths.RuntimeRoot, IdentityMarker));
    }
    private void EnsureOwnership()
    {
        PhysicalFileStorage.RejectLinks(ClonePaths.DataRoot);
        var marker = Path.Combine(ClonePaths.DataRoot, OwnerMarker);
        if (!files.FileExists(marker))
        {
            if (files.DirectoryExists(ClonePaths.RuntimeRoot) && (files.Files(ClonePaths.RuntimeRoot, false).Count > 0 || files.Directories(ClonePaths.RuntimeRoot).Count > 0))
                throw new StorageException(StorageError.Conflict, ClonePaths.RuntimeRoot, "未标记的运行目录非空，拒绝接管或清理。");
            JsonFiles.Write(files, marker, OwnerDocument());
        }
        ValidateOwnerMarker(marker);
        if (!files.DirectoryExists(ClonePaths.RuntimeRoot)) InitializeRuntime();
        else if (!files.FileExists(Path.Combine(ClonePaths.RuntimeRoot, RuntimeMarker)))
        {
            if (files.Files(ClonePaths.RuntimeRoot, false).Count > 0 || files.Directories(ClonePaths.RuntimeRoot).Count > 0)
                throw new StorageException(StorageError.Conflict, ClonePaths.RuntimeRoot, "运行归属标记缺失，拒绝接管非空目录。");
            InitializeRuntime();
        }
        ValidateOwnership();
    }
    private JsonObject OwnerDocument() => new() { ["kind"] = "chunson-switch-managed", ["version"] = 1, ["root"] = ClonePaths.DataRoot, ["runtime"] = ClonePaths.RuntimeRoot, ["accounts"] = ClonePaths.AccountRoot };
    private void InitializeRuntime()
    {
        files.CreateDirectory(ClonePaths.RuntimeRoot);
        JsonFiles.Write(files, Path.Combine(ClonePaths.RuntimeRoot, RuntimeMarker), OwnerDocument());
        foreach (var path in new[] { Paths.ActiveHome, ClonePaths.DesktopRoot, ClonePaths.LaunchStateRoot }) files.CreateDirectory(path);
    }
    public void ValidateOwnership()
    {
        foreach (var path in new[] { ClonePaths.DataRoot, ClonePaths.AccountRoot, ClonePaths.RuntimeRoot, Paths.ActiveHome, ClonePaths.DesktopRoot, ClonePaths.LaunchStateRoot, ClonePaths.RecoveryRoot }) PhysicalFileStorage.RejectLinks(path);
        ValidateOwnerMarker(Path.Combine(ClonePaths.DataRoot, OwnerMarker));
        ValidateOwnerMarker(Path.Combine(ClonePaths.RuntimeRoot, RuntimeMarker));
    }
    private void ValidateOwnerMarker(string path)
    {
        var value = JsonFiles.Read(files, path);
        if (JsonFiles.Text(value, "kind") != "chunson-switch-managed" || value["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != 1
            || !Same(JsonFiles.Text(value, "root"), ClonePaths.DataRoot) || !Same(JsonFiles.Text(value, "runtime"), ClonePaths.RuntimeRoot) || !Same(JsonFiles.Text(value, "accounts"), ClonePaths.AccountRoot))
            throw new StorageException(StorageError.Corrupt, path, "受管分身路径归属标记不一致，拒绝清理。");
    }
    private void ValidateSnapshot(SwitchSnapshot snapshot)
    {
        ValidateOwnership();
        if (snapshot.Paths != Paths || !Same(Path.GetDirectoryName(Path.GetFullPath(snapshot.Path))!, ClonePaths.RecoveryRoot) || !Path.GetFileName(snapshot.Path).StartsWith("credentials-", StringComparison.Ordinal))
            throw new StorageException(StorageError.InvalidValue, snapshot.Path, "凭据快照位置无效。");
        var value = JsonFiles.Read(files, Path.Combine(snapshot.Path, "snapshot.json"));
        if (!Same(JsonFiles.Text(value, "runtime"), ClonePaths.RuntimeRoot) || !Same(JsonFiles.Text(value, "accounts"), ClonePaths.AccountRoot) || value["manifest"] is not JsonObject manifest)
            throw new StorageException(StorageError.Corrupt, snapshot.Path, "凭据快照归属无效。");
        var actual = files.Files(snapshot.Path, true).Select(path => Path.GetRelativePath(snapshot.Path, path)).Where(name => name != "snapshot.json").ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(manifest.Select(item => item.Key))) throw new StorageException(StorageError.Corrupt, snapshot.Path, "凭据快照文件不一致。");
        foreach (var entry in manifest)
            if (!KeptFiles.Append(IdentityMarker).Contains(entry.Key, StringComparer.Ordinal) || files.Hash(Path.Combine(snapshot.Path, entry.Key)) != entry.Value?.GetValue<string>())
                throw new StorageException(StorageError.Corrupt, snapshot.Path, "凭据快照校验失败。");
    }
    private static bool Same(string left, string right) => Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static bool SameIdentity(JsonObject left, JsonObject right, IdentityKind kind)
    {
        if (kind == IdentityKind.Provider) return JsonFiles.Text(left, "auth_mode") == "apikey" && JsonFiles.Text(left, "OPENAI_API_KEY").Length > 0 && JsonFiles.Text(left, "OPENAI_API_KEY") == JsonFiles.Text(right, "OPENAI_API_KEY");
        var id = JsonFiles.Text(left["tokens"] as JsonObject ?? new(), "account_id");
        return id.Length > 0 && id == JsonFiles.Text(right["tokens"] as JsonObject ?? new(), "account_id");
    }
    private static string ProviderKeyHash(JsonObject auth)
    {
        var key = JsonFiles.Text(auth, "OPENAI_API_KEY");
        return JsonFiles.Text(auth, "auth_mode") == "apikey" && key.Length > 0
            ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key))) : "";
    }
    private static bool MatchesRuntimeIdentity(JsonObject active, JsonObject stored, IdentityKind kind, JsonObject marker)
    {
        var baseline = JsonFiles.Text(marker, "provider_key_baseline");
        return kind == IdentityKind.Provider && baseline.Length > 0
            ? ProviderKeyHash(active) == baseline && ProviderKeyHash(stored).Length > 0
            : SameIdentity(active, stored, kind);
    }
    private string FileHashOrAbsent(string path) => files.FileExists(path) ? files.Hash(path) : "absent";
    private void UpdateConfigBaseline(IdentityRef identity)
    {
        var marker = Path.Combine(ClonePaths.RuntimeRoot, IdentityMarker);
        var value = JsonFiles.Read(files, marker);
        if (JsonFiles.Text(value, "kind") != identity.Kind.ToString() || JsonFiles.Text(value, "name") != identity.Name)
            throw new StorageException(StorageError.Corrupt, marker, "归档后运行身份标记不一致，配置基线未更新。");
        value["config_baseline"] = FileHashOrAbsent(Path.Combine(profiles.ProfileDirectory(identity), "config.toml"));
        JsonFiles.Write(files, marker, value);
    }
    private void CopyKept(string source, string destination) { foreach (var name in KeptFiles) CopyIfPresent(Path.Combine(source, name), Path.Combine(destination, name)); }
    private void CopyIfPresent(string source, string destination) { if (files.FileExists(source)) files.CopyAtomic(source, destination); }
}
