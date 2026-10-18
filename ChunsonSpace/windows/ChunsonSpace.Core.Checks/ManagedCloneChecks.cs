using System.Text.Json.Nodes;
using ChunsonSpace.Core;

internal static class ManagedCloneChecks
{
    public static int Run(string root)
    {
        int count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        void Fails(Action action, string label) { try { action(); } catch (StorageException) { count++; return; } throw new InvalidOperationException(label); }
        var paths = new ManagedClonePaths(Path.Combine(root, "managed"));
        var physical = new PhysicalFileStorage();
        var fault = new FaultStorage(physical);
        var managed = new ManagedCloneStorage(paths, fault);
        var profiles = new FileProfileStore(paths.AccountRoot);
        JsonObject Auth(string id, string token) => new() { ["tokens"] = new JsonObject { ["account_id"] = id, ["access_token"] = token, ["refresh_token"] = "fixture-refresh-" + token } };
        var a = new IdentityRef(IdentityKind.Account, "A"); var b = new IdentityRef(IdentityKind.Account, "B");
        profiles.SaveAccount(a.Name, Auth("id-a", "a-old")); profiles.SaveAccount(b.Name, Auth("id-b", "b"));
        File.WriteAllText(Path.Combine(profiles.ProfileDirectory(a), "config.toml"), "model = \"fixture-a\"\n");
        File.WriteAllText(Path.Combine(profiles.ProfileDirectory(b), "config.toml"), "model = \"fixture-b\"\n");
        profiles.SaveEnvironment("A", new("http://127.0.0.1:7897")); profiles.SaveEnvironment("B", new(FollowSystemProxy: true));
        var originalSibling = Path.Combine(root, "original-home"); Directory.CreateDirectory(originalSibling);
        File.WriteAllText(Path.Combine(originalSibling, "auth.json"), "original-fixture");
        managed.ApplyTarget(a, null, false);
        Check(managed.ReadActiveIdentity() == a, "managed first identity confirmed");
        File.WriteAllText(Path.Combine(paths.CodexHome, "auth.json"), Auth("id-a", "a-refreshed").ToJsonString());
        Directory.CreateDirectory(Path.Combine(paths.CodexHome, "sessions"));
        File.WriteAllText(Path.Combine(paths.CodexHome, "sessions", "old.jsonl"), "fixture-chat");
        Directory.CreateDirectory(Path.Combine(paths.DesktopRoot, "Default"));
        File.WriteAllText(Path.Combine(paths.DesktopRoot, "Default", "Cookies"), "fixture-cookie");
        var snapshot = managed.Snapshot("switch", true);
        Check(!Directory.Exists(Path.Combine(snapshot.Path, "sessions")) && !Directory.Exists(Path.Combine(snapshot.Path, "electron")), "snapshot contains no chats or desktop cache");
        Check(managed.ArchiveCurrent(snapshot, a, true), "old credentials archived before clearing");
        managed.ApplyTarget(b, a, false);
        Check(managed.ReadActiveIdentity() == b && !Directory.Exists(Path.Combine(paths.CodexHome, "sessions")) && !Directory.Exists(Path.Combine(paths.DesktopRoot, "Default")), "switch cleans runtime even when isolation preference false");
        Check(profiles.ReadAccountCredentials("A")["tokens"]?["refresh_token"]?.GetValue<string>() == "fixture-refresh-a-refreshed", "runtime rotated refresh credential preserved in account library");
        Check(!Directory.Exists(Path.Combine(profiles.ProfileDirectory(a), "sessions")) && !Directory.Exists(Path.Combine(profiles.ProfileDirectory(a), "electron")), "account library stores no runtime history");
        Check(File.ReadAllText(Path.Combine(paths.CodexHome, "config.toml")).Contains("fixture-b", StringComparison.Ordinal), "target account config loaded");
        Check(File.ReadAllText(Path.Combine(originalSibling, "auth.json")) == "original-fixture", "original home unaffected");
        Check(managed.Complete(snapshot, true).SnapshotKept == false && !Directory.Exists(snapshot.Path), "successful credentials snapshot cleaned");
        var bSnapshot = managed.Snapshot("switch", false); managed.ArchiveCurrent(bSnapshot, b, false);
        fault.FailDeleteDirectory = path => path == paths.DesktopRoot;
        try { managed.ApplyTarget(a, b, true); throw new InvalidOperationException("delete failure allowed"); } catch (IOException) { count++; }
        Check(managed.ReadActiveIdentity() == b, "failed runtime deletion never loads other identity");
        fault.FailDeleteDirectory = null;
        managed.ApplyTarget(a, b, true); managed.Restore(bSnapshot, false);
        Check(managed.ReadActiveIdentity() == b, "rollback rebuilds clean previous identity");
        File.WriteAllText(Path.Combine(bSnapshot.Path, "auth.json"), "{}");
        Fails(() => managed.Restore(bSnapshot, false), "tampered snapshot rejected before runtime changes");
        Check(managed.ReadActiveIdentity() == b, "tampered recovery keeps current identity");
        File.WriteAllText(Path.Combine(profiles.ProfileDirectory(b), "config.toml"), "model = \"new-library-config\"\n");
        var configSnapshot = managed.Snapshot("switch", false); managed.ArchiveCurrent(configSnapshot, b, false);
        Check(File.ReadAllText(Path.Combine(profiles.ProfileDirectory(b), "config.toml")).Contains("new-library-config", StringComparison.Ordinal), "running old config cannot overwrite modified library config baseline");
        managed.Complete(configSnapshot, true);
        managed.ApplyTarget(b, b, false);
        File.WriteAllText(Path.Combine(paths.CodexHome, "config.toml"), "model = \"runtime-edit\"\n");
        var runtimeConfigSnapshot = managed.Snapshot("switch", false); managed.ArchiveCurrent(runtimeConfigSnapshot, b, false);
        Check(File.ReadAllText(Path.Combine(profiles.ProfileDirectory(b), "config.toml")).Contains("runtime-edit", StringComparison.Ordinal), "runtime config edits persist when library baseline unchanged");
        managed.Complete(runtimeConfigSnapshot, true);
        var newer = Auth("id-b", "new-login"); newer["last_refresh"] = "2026-10-13T12:00:00Z";
        profiles.SaveAccount("B", newer, overwrite: true);
        var freshSnapshot = managed.Snapshot("switch", false); managed.ArchiveCurrent(freshSnapshot, b, false);
        Check(profiles.ReadAccountCredentials("B")["tokens"]?["access_token"]?.GetValue<string>() == "new-login", "older running auth cannot overwrite a newer explicit authorization");
        managed.Complete(freshSnapshot, true);
        managed.ResetRuntime();
        Check(managed.ReadActiveIdentity() is null && File.Exists(Path.Combine(profiles.ProfileDirectory(b), "auth.json")) && File.Exists(Path.Combine(profiles.ProfileDirectory(b), "config.toml")), "manual clone reset retains every account authorization and config");
        Check(profiles.ReadEnvironment("B").FollowSystemProxy, "runtime reset retains account routing");
        File.WriteAllText(Path.Combine(paths.RuntimeRoot, ManagedCloneStorage.RuntimeMarker), "{}");
        Fails(() => managed.ResetRuntime(), "wrong runtime owner marker refuses clear");
        var foreign = new ManagedClonePaths(Path.Combine(root, "foreign")); Directory.CreateDirectory(foreign.RuntimeRoot); File.WriteAllText(Path.Combine(foreign.RuntimeRoot, "keep.txt"), "fixture");
        Fails(() => _ = new ManagedCloneStorage(foreign), "nonempty unowned runtime cannot be adopted");
        Check(File.ReadAllText(Path.Combine(foreign.RuntimeRoot, "keep.txt")) == "fixture", "unowned runtime preserved");
        var linked = new ManagedClonePaths(Path.Combine(root, "linked")); var linkStorage = new ManagedCloneStorage(linked);
        LinkFixtures.CreateDirectory(root, Path.Combine(linked.RuntimeRoot, "project-link"), originalSibling, "runtime descendant link");
        Fails(() => linkStorage.ResetRuntime(), "runtime descendant link rejects recursive clear");
        Check(File.ReadAllText(Path.Combine(originalSibling, "auth.json")) == "original-fixture", "linked user project remains unaffected");
        Directory.Delete(Path.Combine(linked.RuntimeRoot, "project-link"));
        count += RecoveryRegressionChecks(Path.Combine(root, "review-regressions"));
        count += ManagedRuntimeLinkChecks.Run(Path.Combine(root, "runtime-cache-links"));
        return count;
    }

    private static int RecoveryRegressionChecks(string root)
    {
        int count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        (ManagedClonePaths Paths, ManagedCloneStorage Storage, FileProfileStore Profiles, IdentityRef A, IdentityRef B) Accounts(string name)
        {
            var paths = new ManagedClonePaths(Path.Combine(root, name));
            var storage = new ManagedCloneStorage(paths); var profiles = new FileProfileStore(paths.AccountRoot);
            var a = new IdentityRef(IdentityKind.Account, "A"); var b = new IdentityRef(IdentityKind.Account, "B");
            foreach (var identity in new[] { a, b }) profiles.SaveAccount(identity.Name,
                new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "fixture-" + identity.Name, ["access_token"] = "fixture-token" } });
            File.WriteAllText(Path.Combine(profiles.ProfileDirectory(a), "config.toml"), "# fixture-original");
            storage.ApplyTarget(a, null, false);
            return (paths, storage, profiles, a, b);
        }
        void ExpectIo(Action action, string label) { try { action(); } catch (IOException) { count++; return; } throw new InvalidOperationException(label); }
        var locked = Accounts("locked-partial-runtime");
        var sessions = Path.Combine(locked.Paths.CodexHome, "sessions"); Directory.CreateDirectory(sessions);
        var deletedFile = Path.Combine(sessions, "a-first.jsonl"); File.WriteAllText(deletedFile, "fixture-history");
        var lockedFile = Path.Combine(sessions, "z-locked.jsonl"); File.WriteAllText(lockedFile, "fixture-locked-history");
        var snapshot = locked.Storage.Snapshot("switch", false); locked.Storage.ArchiveCurrent(snapshot, locked.A, false);
        using (var handle = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            ExpectIo(() => locked.Storage.ApplyTarget(locked.B, locked.A, false), "actual locked runtime file refuses target load");
            Check(!File.Exists(deletedFile) && File.Exists(lockedFile), "actual recursive cleanup failure includes partial deletion");
            Check(File.Exists(Path.Combine(locked.Paths.RuntimeRoot, ManagedCloneStorage.RuntimeMarker))
                && locked.Storage.ReadActiveIdentity() == locked.A, "partial cleanup retains ownership and old credentials/identity");
            Check(new ManagedCloneStorage(locked.Paths).ReadActiveIdentity() == locked.A, "manager storage can reopen while locked cleanup is incomplete");
        }
        locked.Storage.Restore(snapshot, false);
        Check(locked.Storage.ReadActiveIdentity() == locked.A && new ManagedCloneStorage(locked.Paths).ReadActiveIdentity() == locked.A,
            "snapshot restores after real lock release and storage reopens");
        locked.Storage.ApplyTarget(locked.B, locked.A, false);
        Check(locked.Storage.ReadActiveIdentity() == locked.B, "retry after partial cleanup can load target");

        var canceled = Accounts("archive-then-cancel");
        var runtimeConfig = Path.Combine(canceled.Paths.CodexHome, "config.toml");
        var libraryConfig = Path.Combine(canceled.Profiles.ProfileDirectory(canceled.A), "config.toml");
        File.WriteAllText(runtimeConfig, "# fixture-first-edit");
        snapshot = canceled.Storage.Snapshot("switch", false); canceled.Storage.ArchiveCurrent(snapshot, canceled.A, false);
        // Cancellation after archive leaves this same live runtime in place.
        File.WriteAllText(runtimeConfig, "# fixture-second-edit");
        var next = canceled.Storage.Snapshot("switch", false); canceled.Storage.ArchiveCurrent(next, canceled.A, false);
        canceled.Storage.ApplyTarget(canceled.B, canceled.A, false); canceled.Storage.Complete(next, true);
        Check(File.ReadAllText(libraryConfig) == "# fixture-second-edit", "archive then cancellation advances baseline and preserves later runtime edit");

        var rolledBack = Accounts("archive-then-rollback");
        runtimeConfig = Path.Combine(rolledBack.Paths.CodexHome, "config.toml");
        libraryConfig = Path.Combine(rolledBack.Profiles.ProfileDirectory(rolledBack.A), "config.toml");
        File.WriteAllText(runtimeConfig, "# fixture-first-edit");
        snapshot = rolledBack.Storage.Snapshot("switch", false); rolledBack.Storage.ArchiveCurrent(snapshot, rolledBack.A, false);
        rolledBack.Storage.ApplyTarget(rolledBack.B, rolledBack.A, false); rolledBack.Storage.Restore(snapshot, false);
        File.WriteAllText(runtimeConfig, "# fixture-after-rollback-edit");
        next = rolledBack.Storage.Snapshot("switch", false); rolledBack.Storage.ArchiveCurrent(next, rolledBack.A, false);
        rolledBack.Storage.ApplyTarget(rolledBack.B, rolledBack.A, false); rolledBack.Storage.Complete(next, true);
        Check(File.ReadAllText(libraryConfig) == "# fixture-after-rollback-edit", "rollback snapshot rebases saved matching config and preserves later runtime edit");

        var independent = Accounts("external-config-after-archive");
        runtimeConfig = Path.Combine(independent.Paths.CodexHome, "config.toml");
        libraryConfig = Path.Combine(independent.Profiles.ProfileDirectory(independent.A), "config.toml");
        File.WriteAllText(runtimeConfig, "# fixture-first-edit");
        snapshot = independent.Storage.Snapshot("switch", false); independent.Storage.ArchiveCurrent(snapshot, independent.A, false);
        File.WriteAllText(libraryConfig, "# fixture-independent-library-edit");
        independent.Storage.ApplyTarget(independent.B, independent.A, false); independent.Storage.Restore(snapshot, false);
        File.WriteAllText(runtimeConfig, "# fixture-later-runtime-edit");
        next = independent.Storage.Snapshot("switch", false); independent.Storage.ArchiveCurrent(next, independent.A, false);
        Check(File.ReadAllText(libraryConfig) == "# fixture-independent-library-edit", "rollback does not rebase over independently changed library configuration");

        var providerPaths = new ManagedClonePaths(Path.Combine(root, "provider-key-updates"));
        var providerStorage = new ManagedCloneStorage(providerPaths); var providers = new FileProfileStore(providerPaths.AccountRoot);
        var provider = new IdentityRef(IdentityKind.Provider, "provider"); var other = new IdentityRef(IdentityKind.Provider, "other");
        providers.SaveProvider(provider.Name, "https://fixture.invalid/v1", "fixture-old-key"); providers.SaveProvider(other.Name, "https://fixture.invalid/v1", "fixture-other-key");
        providerStorage.ApplyTarget(provider, null, false);
        var markerPath = Path.Combine(providerPaths.RuntimeRoot, ".chunson-active.json");
        var marker = JsonNode.Parse(File.ReadAllText(markerPath))!.AsObject(); marker.Remove("provider_key_baseline"); File.WriteAllText(markerPath, marker.ToJsonString());
        providerStorage.PrepareProviderUpdate(provider);
        Check(!File.ReadAllText(markerPath).Contains("fixture-old-key", StringComparison.Ordinal) && JsonNode.Parse(File.ReadAllText(markerPath))!["provider_key_baseline"]!.GetValue<string>().Length == 64,
            "legacy verified provider receives only key hash baseline");
        providers.UpdateProvider(provider.Name, "https://fixture.invalid/v2", "fixture-new-key");
        Check(providerStorage.ReadActiveIdentity() == provider, "saved provider key update leaves owned old runtime identity readable");
        snapshot = providerStorage.Snapshot("switch", false); providerStorage.ArchiveCurrent(snapshot, provider, false);
        var savedAuth = Path.Combine(providers.ProfileDirectory(provider), "auth.json");
        Check(File.ReadAllText(savedAuth).Contains("fixture-new-key", StringComparison.Ordinal), "archiving old provider runtime never overwrites newly saved key");
        providerStorage.ApplyTarget(other, provider, false); providerStorage.ApplyTarget(provider, other, false);
        Check(File.ReadAllText(Path.Combine(providerPaths.CodexHome, "auth.json")).Contains("fixture-new-key", StringComparison.Ordinal), "rebuilding provider loads newly saved key");
        providers.UpdateProvider(provider.Name, "https://fixture.invalid/v3", "fixture-newest-key"); providerStorage.ResetRuntime();
        Check(providerStorage.ReadActiveIdentity() is null && File.ReadAllText(savedAuth).Contains("fixture-newest-key", StringComparison.Ordinal), "reset after provider edit retains newest key in library");
        providerStorage.ApplyTarget(provider, null, false);
        marker = JsonNode.Parse(File.ReadAllText(markerPath))!.AsObject(); marker.Remove("provider_key_baseline"); File.WriteAllText(markerPath, marker.ToJsonString());
        providers.UpdateProvider(provider.Name, "https://fixture.invalid/v4", "fixture-unknown-key");
        try { providerStorage.PrepareProviderUpdate(provider); throw new InvalidOperationException("unverified legacy provider received baseline"); }
        catch (StorageException) { count++; }
        Check(JsonNode.Parse(File.ReadAllText(markerPath))!["provider_key_baseline"] is null, "mismatched legacy runtime cannot receive ownership baseline");
        return count;
    }
}
