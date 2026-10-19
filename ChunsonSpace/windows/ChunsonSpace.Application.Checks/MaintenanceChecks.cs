using System.Text.Json.Nodes;
using ChunsonSpace.Application;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;

internal static class MaintenanceChecks
{
    public static async Task<int> RunAsync(string fixtureRoot)
    {
        int count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); count++; Console.WriteLine("PASS " + name); }
        var directory = Path.Combine(fixtureRoot, "maintenance"); var root = Path.Combine(directory, "store"); var home = Path.Combine(directory, "active");
        var packaged = Path.Combine(directory, "packaged"); var web = Path.Combine(directory, "roaming", "Codex", "web", "Codex");
        Directory.CreateDirectory(root); Directory.CreateDirectory(home);
        void Write(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        var accountAuth = "{\"tokens\":{\"account_id\":\"fixture-original\",\"access_token\":\"fixture-token\"}}";
        Write(Path.Combine(home, "auth.json"), accountAuth);
        Write(Path.Combine(home, ".codex-global-state.json"), "{\"fixture\":1}");
        Write(Path.Combine(home, "sessions", "nested", "history.jsonl"), "fixture history");
        Write(Path.Combine(home, "state_5.sqlite"), "fixture database");
        var paths = new SwitchStoragePaths(root, home, packaged, web);
        var maintenance = new ProfileMaintenance(paths);
        var saved = maintenance.SaveCurrentAccount("保存", false, true);
        Check(saved.Success && File.ReadAllText(Path.Combine(saved.Directory, "auth.json")) == accountAuth, "save current copies valid account credentials");
        Check(File.Exists(Path.Combine(saved.Directory, "sessions", "nested", "history.jsonl")) && File.Exists(Path.Combine(saved.Directory, "state_5.sqlite")), "save current copies session directory and SQLite state");
        var installation = File.ReadAllText(Path.Combine(saved.Directory, "installation_id"));
        Check(JsonNode.Parse(File.ReadAllText(Path.Combine(saved.Directory, ".codex-global-state.json")))![SwitchFileRules.InstallationKey]!.GetValue<string>() == installation, "saved account installation identity is internally aligned");
        Directory.CreateDirectory(Path.Combine(saved.Directory, "electron", "Default", "empty"));
        Write(Path.Combine(saved.Directory, "electron", "Default", "state"), "old device");
        Write(Path.Combine(saved.Directory, "electron-web-state", "Default", "state"), "old web device");
        var duplicate = maintenance.SaveCurrentAccount("保存", false, true);
        Check(!duplicate.Success && duplicate.Exists, "save current requires explicit overwrite for nonempty profile");
        var markStore = new FileProfileStore(root);
        markStore.SetAccountMarks("保存", AccountQuality.SuspectedDegraded, true, true); markStore.SetTestsFrozen("保存", true);
        Write(Path.Combine(saved.Directory, "ciallo-tests", "SYNTHETIC-history.json"), "synthetic test history");
        var marksBefore = File.ReadAllBytes(Path.Combine(saved.Directory, FileProfileStore.AccountMarksFile));
        saved = maintenance.SaveCurrentAccount("保存", true, true);
        Check(marksBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(saved.Directory, FileProfileStore.AccountMarksFile))) && markStore.AreTestsFrozen("保存")
            && File.ReadAllText(Path.Combine(saved.Directory, "ciallo-tests", "SYNTHETIC-history.json")) == "synthetic test history", "same-account overwrite preserves marks freeze and test history byte for byte");
        Write(Path.Combine(home, "auth.json"), "{\"tokens\":{\"account_id\":\"SYNTHETIC-other\",\"access_token\":\"fixture\"}}");
        Check(!maintenance.SaveCurrentAccount("保存", true, true).Success && File.ReadAllText(Path.Combine(saved.Directory, "auth.json")) == accountAuth
            && marksBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(saved.Directory, FileProfileStore.AccountMarksFile))), "different identity cannot overwrite another account and inherit its marks");
        Write(Path.Combine(home, "auth.json"), accountAuth);
        Check(saved.Success && File.ReadAllText(Path.Combine(saved.Directory, "installation_id")) == installation, "overwrite retains original installation identity");
        Check(File.ReadAllText(Path.Combine(saved.Directory, "electron", "Default", "state")) == "old device" && Directory.Exists(Path.Combine(saved.Directory, "electron", "Default", "empty")), "overwrite retains original desktop layer and empty directories");
        Check(File.ReadAllText(Path.Combine(saved.Directory, "electron-web-state", "Default", "state")) == "old web device" && saved.RecoveryPaths.Any(Directory.Exists), "overwrite retains web layer and original profile archive");
        Check(saved.RecoveryPaths.All(path => !File.Exists(Path.Combine(path, ProfileMaintenance.AccountArchiveMarker))), "overwrite recovery archive does not join deletion rotation");
        Write(Path.Combine(home, "auth.json"), "{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"fixture-key\"}");
        saved = maintenance.SaveCurrentAccount("禁止", false, true);
        Check(!saved.Success && !Directory.Exists(saved.Directory), "supplier API key cannot be saved as account");
        Write(Path.Combine(home, "auth.json"), "{\"tokens\":{\"account_id\":\"fixture\"}}");
        saved = maintenance.SaveCurrentAccount("无令牌", false, true);
        Check(!saved.Success && !Directory.Exists(saved.Directory), "incomplete current credentials preserve target");
        Write(Path.Combine(home, "auth.json"), accountAuth);
        var faults = new FaultFiles { FailCopySuffix = "auth.json" };
        saved = new ProfileMaintenance(paths, faults).SaveCurrentAccount("保存", true, true);
        Check(!saved.Success && File.ReadAllText(Path.Combine(root, "profiles", "保存", "installation_id")) == installation && saved.RecoveryPaths.Any(Directory.Exists), "save copy failure preserves existing profile and staging evidence");
        faults = new FaultFiles { FailMoveDestination = Path.Combine(root, "profiles", "保存") };
        saved = new ProfileMaintenance(paths, faults).SaveCurrentAccount("保存", true, true);
        Check(!saved.Success && saved.RecoveryPaths.Any(Directory.Exists), "save commit failure retains old archive and staging evidence");
        // Restore the manually retained archive for subsequent independent fixture tests.
        if (!Directory.Exists(Path.Combine(root, "profiles", "保存")))
            Directory.Move(saved.RecoveryPaths.First(path => !Path.GetFileName(path).StartsWith("profile-save-stage-")), Path.Combine(root, "profiles", "保存"));
        var trash = Path.Combine(root, "xy_clean", "CodexX-01", "Boss");
        var unmarked = Path.Combine(trash, "keep-unmarked"); Directory.CreateDirectory(unmarked); Write(Path.Combine(unmarked, "evidence"), "fixture");
        for (int index = 0; index < 7; index++)
        {
            var name = "删除" + index;
            Write(Path.Combine(root, "profiles", name, "auth.json"), accountAuth);
            Write(Path.Combine(root, "browser-envs", name, "env.json"), "{}");
            Write(Path.Combine(root, "electron-envs", name, "state"), "fixture");
            var deleted = maintenance.DeleteAccount(name);
            Check(deleted.Success && deleted.Archives.Count == 0 && !Directory.Exists(Path.Combine(root, "profiles", name)), "delete removes local account without archives " + index);
        }
        var rotation = maintenance.RotateAccountArchives();
        Check(rotation.Kept == 0 && Directory.Exists(unmarked), "permanent deletion leaves no new archive events and preserves unmarked evidence");
        var archiveMarkers = Directory.GetFiles(trash, ProfileMaintenance.AccountArchiveMarker, SearchOption.AllDirectories);
        Check(archiveMarkers.Length == 0, "permanent deletion writes no recovery markers");
        var nested = Path.Combine(unmarked, "nested-marked"); Directory.CreateDirectory(nested); Write(Path.Combine(nested, ProfileMaintenance.AccountArchiveMarker), "{\"kind\":\"account-archive\",\"name\":\"nested\",\"stamp\":\"0000\"}");
        maintenance.RotateAccountArchives();
        Check(Directory.Exists(nested), "rotation never searches or deletes nested marked evidence");
        Write(Path.Combine(home, "auth.json"), accountAuth); Write(Path.Combine(home, "installation_id"), "fixture-active-id");
        Write(Path.Combine(packaged, "Default", "Network", "Cookies"), "fixture-cookie");
        Write(Path.Combine(web, "Default", "Network", "Cookies"), "fixture-web-cookie");
        var loggedOut = maintenance.Logout(true);
        Check(loggedOut.Success && !File.Exists(Path.Combine(home, "auth.json")) && !File.Exists(Path.Combine(home, "installation_id")), "logout removes credentials and installation identity");
        Check(File.Exists(Path.Combine(home, "sessions", "nested", "history.jsonl")) && File.Exists(Path.Combine(home, "state_5.sqlite")), "logout retains dialogue and SQLite history");
        Check(!Directory.Exists(Path.Combine(packaged, "Default")) && !Directory.Exists(Path.Combine(web, "Default")) && Directory.Exists(loggedOut.Snapshot), "logout clears desktop login layers with retained recovery snapshot");
        Write(Path.Combine(home, "auth.json"), accountAuth); Write(Path.Combine(home, "installation_id"), "fixture-active-id");
        faults = new FaultFiles { FailDeleteFileSuffix = "installation_id" };
        loggedOut = new ProfileMaintenance(paths, faults).Logout();
        Check(!loggedOut.Success && loggedOut.Recovered && File.ReadAllText(Path.Combine(home, "auth.json")) == accountAuth, "partial logout failure restores original credentials");
        var unsafeRoot = false;
        try { _ = new ProfileMaintenance(paths with { ActiveHome = Path.Combine(root, "profiles", "nested") }); }
        catch (StorageException) { unsafeRoot = true; }
        Check(unsafeRoot, "profile maintenance reuses active/profile overlap rejection");
        var runtime = new FakeRuntime([]) { StopFailsAt = 1 };
        var result = await new MaintenanceService(runtime).SaveCurrentAccountAsync(maintenance, "停止失败", false, true);
        Check(!result.Success && result.Save is null && !Directory.Exists(Path.Combine(root, "profiles", "停止失败")), "save current stop failure performs no profile write");
        runtime = new FakeRuntime([]) { Pending = new(LaunchStatus.Pending, false, null, "fake", "pending") };
        result = await new MaintenanceService(runtime).LogoutAsync(maintenance);
        Check(!result.Success && File.Exists(Path.Combine(home, "auth.json")), "pending launch prevents logout mutation");

        // Rebuild has its own roots to keep prior maintenance fixture evidence independent.
        var rebuildDirectory = Path.Combine(fixtureRoot, "rebuild"); var rebuildRoot = Path.Combine(rebuildDirectory, "tool"); var rebuildHome = Path.Combine(rebuildDirectory, "home"); var local = Path.Combine(rebuildDirectory, "local");
        var rebuildWeb = Path.Combine(rebuildDirectory, "roaming", "Codex", "web", "Codex");
        var rebuildPackage = Path.Combine(rebuildDirectory, "package");
        Write(Path.Combine(rebuildHome, "auth.json"), accountAuth); Write(Path.Combine(rebuildHome, "config.toml"), "fixture = true");
        Write(Path.Combine(rebuildHome, "plugins", "fixture"), "plugin"); Write(Path.Combine(rebuildHome, "skills", "fixture"), "skill");
        Write(Path.Combine(rebuildHome, "sessions", "history"), "history");
        Directory.CreateDirectory(Path.Combine(rebuildHome, "empty-state"));
        Write(Path.Combine(rebuildWeb, "Default", "Network", "Cookies"), "cookie");
        Write(Path.Combine(rebuildPackage, "Default", "Network", "Cookies"), "cookie");
        Write(Path.Combine(local, "OpenAI", "Codex", "app", "Codex.exe"), "fixture-runtime");
        Write(Path.Combine(local, "OpenAI", "extension", "state"), "extension");
        Write(Path.Combine(local, "Codex", "log"), "log");
        Write(Path.Combine(rebuildRoot, "electron-envs", "state"), "old desktop");
        var rebuildPaths = new SwitchStoragePaths(rebuildRoot, rebuildHome, rebuildPackage, rebuildWeb);
        var rebuild = new EnvironmentRebuild(rebuildPaths, local);
        var preview = rebuild.Preview();
        Check(preview.Items.Count == 8 && preview.Kept.Count == 4, "rebuild previews state and retained config plugins skills runtime");
        Check(preview.Items.All(item => item.Path != Path.Combine(rebuildHome, "config.toml") && !item.Path.Contains(Path.Combine(local, "OpenAI", "Codex"))), "rebuild excludes configuration and installation runtime targets");
        var preflightFault = new FaultFiles { FailManifestWriteAt = 1 };
        var rebuilt = new EnvironmentRebuild(rebuildPaths, local, preflightFault).Execute();
        Check(!rebuilt.Success && !rebuilt.ManifestCurrent && !rebuilt.Partial && File.Exists(Path.Combine(rebuildHome, "auth.json")), "rebuild initial manifest failure prevents every move");
        var movingFault = new FaultFiles { FailManifestWriteAt = 2 };
        rebuilt = new EnvironmentRebuild(rebuildPaths, local, movingFault).Execute();
        Check(!rebuilt.Success && rebuilt.Partial && !rebuilt.ManifestCurrent && rebuilt.Planned.Count(item => item.Status == "moved") == 1 && rebuilt.Planned.Any(item => item.Status == "pending"), "rebuild manifest update failure stops remaining moves");
        // Recreate any moved fixture target for a full separate execution.
        foreach (var item in rebuilt.Planned.Where(item => item.Status == "moved"))
        {
            if (Directory.Exists(item.To)) Directory.Move(item.To, item.Path); else File.Move(item.To, item.Path);
        }
        var copyFault = new FaultFiles { FailCopySourceSuffix = "auth.json" };
        rebuilt = new EnvironmentRebuild(rebuildPaths, local, copyFault).Execute();
        Check(!rebuilt.Success && rebuilt.Partial && rebuilt.ManifestCurrent && File.Exists(Path.Combine(rebuildHome, "auth.json")) && rebuilt.Planned.Any(item => item.Status == "failed"), "rebuild partial copy failure preserves source and reports per-item state");
        Check(File.Exists(Path.Combine(rebuildHome, "config.toml")) && File.Exists(Path.Combine(rebuildHome, "plugins", "fixture")) && File.Exists(Path.Combine(rebuildHome, "skills", "fixture")) && File.Exists(Path.Combine(local, "OpenAI", "Codex", "app", "Codex.exe")), "rebuild partial execution preserves all kept content");
        rebuilt = rebuild.Execute();
        Check(rebuilt.Success && !File.Exists(Path.Combine(rebuildHome, "auth.json")) && File.Exists(Path.Combine(rebuilt.Destination, EnvironmentRebuild.Marker)), "rebuild remaining state execution persists completed recovery manifest");
        runtime = new FakeRuntime([]) { ConfirmFails = true };
        result = await new MaintenanceService(runtime).RebuildAsync(rebuild);
        Check(!result.Success && result.Rebuild is null, "rebuild refuses execution after reopened-process recheck");
        return count;
    }
}

internal sealed class FaultFiles : IFileStorage
{
    private readonly PhysicalFileStorage physical = new();
    public string? FailCopySuffix { get; init; }
    public string? FailCopySourceSuffix { get; init; }
    public string? FailMoveDestination { get; init; }
    public string? FailDeleteFileSuffix { get; init; }
    public int? FailManifestWriteAt { get; init; }
    private int manifestWrites;
    public bool FileExists(string path) => physical.FileExists(path);
    public bool DirectoryExists(string path) => physical.DirectoryExists(path);
    public byte[] Read(string path) => physical.Read(path);
    public long Length(string path) => physical.Length(path);
    public void WriteAtomic(string path, byte[] content)
    {
        if (path.EndsWith(EnvironmentRebuild.Marker) && ++manifestWrites == FailManifestWriteAt) throw new IOException("fixture manifest");
        physical.WriteAtomic(path, content);
    }
    public void CopyAtomic(string source, string destination)
    {
        if (FailCopySuffix is { } suffix && destination.EndsWith(suffix) || FailCopySourceSuffix is { } sourceSuffix && source.EndsWith(sourceSuffix)) throw new IOException("fixture copy");
        physical.CopyAtomic(source, destination);
    }
    public string Hash(string path) => physical.Hash(path);
    public void CreateDirectory(string path) => physical.CreateDirectory(path);
    public IReadOnlyList<string> Files(string path, bool recursive) => physical.Files(path, recursive);
    public IReadOnlyList<string> Directories(string path) => physical.Directories(path);
    public void MoveDirectory(string source, string destination) { if (destination == FailMoveDestination) throw new IOException("fixture commit"); physical.MoveDirectory(source, destination); }
    public void DeleteFile(string path) { if (FailDeleteFileSuffix is { } suffix && path.EndsWith(suffix)) throw new IOException("fixture delete"); physical.DeleteFile(path); }
    public void DeleteDirectory(string path, bool recursive) => physical.DeleteDirectory(path, recursive);
}
