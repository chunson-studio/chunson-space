using System.Text.Json.Nodes;
using FAswitch.Core;

internal static class ManagedRuntimeLinkChecks
{
    public static int Run(string root)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        void Refuses(Action action, string label)
        {
            try { action(); } catch (StorageException) { count++; return; }
            throw new InvalidOperationException(label);
        }
        var paths = new ManagedClonePaths(Path.Combine(root, "managed"));
        var storage = new ManagedCloneStorage(paths);
        var profiles = new FileProfileStore(paths.AccountRoot);
        var a = new IdentityRef(IdentityKind.Account, "A"); var b = new IdentityRef(IdentityKind.Account, "B");
        foreach (var account in new[] { a, b }) profiles.SaveAccount(account.Name,
            new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "fixture-" + account.Name, ["access_token"] = "fixture-token" } });
        storage.ApplyTarget(a, null, false);
        var original = Path.Combine(root, "original"); Directory.CreateDirectory(original);
        var sentinel = Path.Combine(original, "keep.txt"); File.WriteAllText(sentinel, "original-data");
        var plugin = Path.Combine(paths.CodexHome, "plugins", "cache", "openai-bundled", "chrome");
        var version = Path.Combine(plugin, "26.1013.31256"); var latest = Path.Combine(plugin, "latest");
        void AddAlias(bool junction = false)
        {
            Directory.CreateDirectory(version); File.WriteAllText(Path.Combine(version, "cache.txt"), "owned-cache");
            LinkFixtures.CreateDirectory(root, latest, version, "Codex bundled plugin latest alias", junction);
        }
        AddAlias(junction: true);
        Refuses(() => new PhysicalFileStorage().Files(paths.RuntimeRoot, true), "general file storage still refuses links");
        storage.Preflight(b, false);
        Check((File.GetAttributes(latest) & FileAttributes.ReparsePoint) != 0 && storage.ReadActiveIdentity() == a, "preflight accepts owned alias without removing it or changing identity");
        var foreignLink = Path.Combine(paths.RuntimeRoot, "project-link");
        LinkFixtures.CreateDirectory(root, foreignLink, original, "unrelated external link");
        Refuses(storage.ResetRuntime, "external link blocks reset even with a legitimate cache alias");
        Check((File.GetAttributes(latest) & FileAttributes.ReparsePoint) != 0 && storage.ReadActiveIdentity() == a
            && File.ReadAllText(Path.Combine(version, "cache.txt")) == "owned-cache", "all links validated before any alias or runtime data removal");
        Directory.Delete(foreignLink, false);
        storage.ResetRuntime();
        Check(storage.ReadActiveIdentity() is null && !Directory.Exists(plugin), "reset clears runtime with official latest alias");
        Check(profiles.ListAccounts().Count == 2 && File.ReadAllText(sentinel) == "original-data", "reset preserves accounts and original directory");
        storage.ApplyTarget(a, null, false); AddAlias();
        storage.ApplyTarget(b, a, false);
        Check(storage.ReadActiveIdentity() == b && !Directory.Exists(plugin), "switch also handles regenerated cache aliases");

        Directory.CreateDirectory(plugin);
        LinkFixtures.CreateDirectory(root, latest, original, "latest pointing outside plugin cache");
        Refuses(() => storage.Preflight(a, false), "latest name cannot authorize an external target");
        Refuses(storage.ResetRuntime, "reset rejects external latest target");
        Check(File.ReadAllText(sentinel) == "original-data" && storage.ReadActiveIdentity() == b, "unsafe alias leaves target and active identity intact");
        Directory.Delete(latest, false);

        LinkFixtures.CreateDirectory(root, version, original, "linked cache version");
        LinkFixtures.CreateDirectory(root, latest, version, "latest pointing to another alias");
        Refuses(storage.ResetRuntime, "linked version chain is refused");
        Directory.Delete(latest, false); Directory.Delete(version, false);
        AddAlias(); File.Delete(Path.Combine(version, "cache.txt")); Directory.Delete(version, false);
        storage.ResetRuntime();
        Check(storage.ReadActiveIdentity() is null && !Directory.Exists(plugin), "dangling internal latest alias can be removed without traversal");
        Check(File.ReadAllText(sentinel) == "original-data" && profiles.ListAccounts().Count == 2, "all cleanup cases preserve external data and account library");
        return count;
    }
}
