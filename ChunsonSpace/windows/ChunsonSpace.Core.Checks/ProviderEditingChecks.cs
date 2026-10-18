using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Core;

static class ProviderEditingChecks
{
    public static int Run(string root)
    {
        var count = 0;
        void Assert(bool value, string label) { if (!value) throw new InvalidOperationException("provider edit: " + label); count++; }
        var data = Path.Combine(root, "data");
        var home = Path.Combine(root, "home");
        var files = new PhysicalFileStorage();
        var fault = new FaultStorage(files);
        var store = new FileProfileStore(data, fault);
        store.SaveProvider("P", "https://first.invalid/v1", "first-key", "first-model");
        var pdir = store.ProfileDirectory(new(IdentityKind.Provider, "P"));
        Directory.CreateDirectory(Path.Combine(pdir, "sessions"));
        File.WriteAllText(Path.Combine(pdir, "sessions", "history.jsonl"), "fixture history");
        var metadataPath = Path.Combine(pdir, "provider.json");
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
        metadata["custom"] = "kept";
        File.WriteAllText(metadataPath, metadata.ToJsonString());
        var created = store.ReadProvider("P").Created;
        var edited = store.UpdateProvider("P", "https://edited.invalid/v1", "", "edited-model");
        Assert(!edited.ActiveUpdated && store.ReadProvider("P").Created == created && Directory.Exists(edited.RecoveryPath), "inactive edit preserves creation time and recovery copy");
        Assert(File.ReadAllText(Path.Combine(pdir, "sessions", "history.jsonl")) == "fixture history" && JsonNode.Parse(File.ReadAllText(metadataPath))!["custom"]!.GetValue<string>() == "kept", "edit preserves sessions and unknown metadata");
        Assert(JsonNode.Parse(File.ReadAllText(Path.Combine(pdir, "auth.json")))!["OPENAI_API_KEY"]!.GetValue<string>() == "first-key", "blank editing key keeps prior value");
        var overwritten = store.SaveProvider("P", "https://replacement.invalid/v1", "replacement-key", "replacement-model", true);
        Assert(overwritten.ArchivedPath is not null && File.ReadAllText(Path.Combine(overwritten.ArchivedPath, "sessions", "history.jsonl")) == "fixture history", "explicit overwrite archives whole old directory");
        Assert(!Directory.Exists(Path.Combine(pdir, "sessions")), "new overwritten slot starts without old sessions");
        store.SaveAccount("A", JsonNode.Parse("{\"tokens\":{\"account_id\":\"fixture-account\"}}")!.AsObject());
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "auth.json"), "{\"tokens\":{\"account_id\":\"fixture-account\"}}");
        File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"user-model\"\nother = true\n[table]\nmodel = \"nested\"\n");
        var storage = new FileSwitchStorage(new(data, home), fault);
        var original = storage.Snapshot("provider-edit", false);
        storage.ArchiveCurrent(original, new(IdentityKind.Account, "A"), false);
        storage.ApplyTarget(new(IdentityKind.Provider, "P"), new(IdentityKind.Account, "A"), false);
        var activeEdited = store.UpdateProvider("p", "https://active-edited.invalid/v1", "new-active-key", "new-active-model", home);
        Assert(activeEdited.ActiveUpdated && activeEdited.Warnings.Count == 1 && storage.ReadActiveIdentity() == new IdentityRef(IdentityKind.Provider, "P"), "current provider edit confirmed from auth/config facts with Windows case semantics");
        Assert(JsonNode.Parse(File.ReadAllText(Path.Combine(home, "auth.json")))!["OPENAI_API_KEY"]!.GetValue<string>() == "new-active-key" && File.ReadAllText(Path.Combine(home, "config.toml")).Contains("new-active-model", StringComparison.Ordinal), "active key and managed config synchronized");
        Assert(File.ReadAllText(Path.Combine(home, "config.toml")).Contains("[table]\nmodel = \"nested\"", StringComparison.Ordinal), "active edit preserves other TOML fields");
        var before = new[] { metadataPath, Path.Combine(pdir, "auth.json"), Path.Combine(home, "auth.json"), Path.Combine(home, "config.toml"), Path.Combine(data, "providers", ".state.json") }.ToDictionary(path => path, File.ReadAllText);
        fault.FailWrite = path => path == Path.Combine(home, "config.toml");
        try { store.UpdateProvider("P", "https://failed.invalid/v1", "failed-key", "failed-model", home); throw new InvalidOperationException("update fault not thrown"); }
        catch (StorageException error) { Assert(error.Code == StorageError.Io && Directory.Exists(error.FilePath), "active edit failure exposes retained recovery directory"); }
        Assert(before.All(pair => File.ReadAllText(pair.Key) == pair.Value), "active edit failure restores all affected files");
        fault.FailWrite = null;
        var oldUrl = store.ReadProvider("P").BaseUrl;
        fault.FailMove = path => Path.GetFileName(path).StartsWith("provider-new-", StringComparison.Ordinal);
        try { store.SaveProvider("P", "https://failed-overwrite.invalid/v1", "failed-key", overwrite: true); throw new InvalidOperationException("move fault not thrown"); }
        catch (StorageException) { count++; }
        Assert(store.ReadProvider("P").BaseUrl == oldUrl, "overwrite destination move failure restores old directory");
        fault.FailMove = null;
        storage.Restore(original, false);
        Assert(storage.ReadActiveIdentity() == new IdentityRef(IdentityKind.Account, "A"), "provider edit does not break snapshot restore");
        try { store.UpdateProvider("P", "https://bad-home.invalid", "fixture", activeHome: pdir); throw new InvalidOperationException("unsafe active home accepted"); }
        catch (StorageException error) { Assert(error.Code == StorageError.InvalidValue, "active provider edit reuses root overlap safeguards"); }
        Console.WriteLine($"PASS {count} provider editing checks; fixture root: {root}");
        return count;
    }
}
