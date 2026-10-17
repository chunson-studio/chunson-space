using System.Text;
using System.Text.Json.Nodes;
using FAswitch.Core;

static class SwitchStorageChecks
{
    public static int Run(string root)
    {
        var count = 0;
        void Assert(bool value, string label) { if (!value) throw new InvalidOperationException("switch: " + label); count++; }
        void Expect(Action action, string label)
        {
            try { action(); throw new InvalidOperationException("switch expected failure: " + label); }
            catch (StorageException) { count++; }
        }
        void Write(string path, string text)
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
        string Read(string path) => File.ReadAllText(path);
        var paths = new SwitchStoragePaths(Path.Combine(root, "data"), Path.Combine(root, "home"), Path.Combine(root, "package"), Path.Combine(root, "web"));
        var actual = new PhysicalFileStorage();
        var fault = new FaultStorage(actual);
        var storage = new FileSwitchStorage(paths, fault);
        var profiles = new FileProfileStore(paths.Root, actual);
        var a = new IdentityRef(IdentityKind.Account, "账号A");
        var b = new IdentityRef(IdentityKind.Account, "账号B");
        var p = new IdentityRef(IdentityKind.Provider, "供应商P");
        var q = new IdentityRef(IdentityKind.Provider, "供应商Q");
        var aHome = profiles.ProfileDirectory(a);
        var bHome = profiles.ProfileDirectory(b);
        var authA = JsonNode.Parse("{\"auth_mode\":\"chatgpt\",\"tokens\":{\"account_id\":\"fixture-A\",\"access_token\":\"fixture-only\"}}")!.AsObject();
        var authB = JsonNode.Parse("{\"auth_mode\":\"chatgpt\",\"tokens\":{\"account_id\":\"fixture-B\",\"access_token\":\"fixture-only\"}}")!.AsObject();
        profiles.SaveAccount(a.Name, authA);
        profiles.SaveAccount(b.Name, authB);
        Write(Path.Combine(paths.ActiveHome, "auth.json"), authA.ToJsonString());
        Write(Path.Combine(paths.ActiveHome, "installation_id"), "fixture-install-A");
        foreach (var global in new[] { ".codex-global-state.json", ".codex-global-state.json.bak" })
            Write(Path.Combine(paths.ActiveHome, global), new JsonObject { [SwitchFileRules.InstallationKey] = "fixture-install-A", ["custom"] = 9 }.ToJsonString());
        Write(Path.Combine(bHome, ".codex-global-state.json"), "{\"custom\":7}");
        foreach (var relative in SwitchFileRules.SessionDirectories) Write(Path.Combine(paths.ActiveHome, relative, "中文", "fixture.txt"), "A:" + relative);
        foreach (var relative in SwitchFileRules.SessionFiles) Write(Path.Combine(paths.ActiveHome, relative), "A:" + relative);
        Write(Path.Combine(bHome, "sessions", "B.jsonl"), "B history");
        Write(Path.Combine(bHome, "state_5.sqlite"), "B db");
        foreach (var relative in SwitchFileRules.TransientItems) Write(Path.Combine(paths.ActiveHome, relative, "fixture.txt"), "trace");
        Write(Path.Combine(paths.ActiveHome, ".sandbox", "one.log"), "transient log");
        Write(Path.Combine(paths.ActiveHome, ".sandbox", "deny_read_acl_state.json"), "fixture ACL state");
        var originalConfig = "# 说明\r\nmodel = \"original-model\" # preserve comment\r\nother = \"原样\"\r\nopenai_base_url = \"https://original.invalid/v1\"\r\ntext = '''\r\n[fake]\r\nmodel = \"inside-string\"\r\n'''\r\narray = [\r\n  [1, 2],\r\n]\r\n[section]\r\nmodel = \"section-model\"\r\nopenai_base_url = \"section-url\"\r\n";
        Write(Path.Combine(paths.ActiveHome, "config.toml"), originalConfig);
        foreach (var desktop in new[] { paths.PackagedDesktopRoot!, paths.WebDesktopRoot! })
        {
            Write(Path.Combine(desktop, "Default", "Network", "Cookies"), "A cookie:" + desktop);
            Write(Path.Combine(desktop, "statsig-state.json"), "A statsig");
            Write(Path.Combine(desktop, "Default", "Cache", "cached"), "HTTP cache");
            Write(Path.Combine(desktop, "Default", "Code Cache", "code"), "pure cache");
            Write(Path.Combine(desktop, "unmanaged.txt"), "unmanaged file");
        }
        Write(Path.Combine(bHome, "electron", "Default", "Network", "Cookies"), "B package cookie");
        Write(Path.Combine(bHome, "electron-web-state", "Default", "Network", "Cookies"), "B web cookie");
        Assert(storage.ReadActiveIdentity() == a, "active inferred from account id");
        Write(Path.Combine(paths.Root, "profiles", "坏档案", "auth.json"), "{");
        Assert(storage.ReadActiveIdentity() == a, "corrupt unrelated account does not block inference");
        Directory.Delete(Path.Combine(paths.Root, "profiles", "坏档案"), true);
        foreach (var badPaths in new[]
        {
            paths with { ActiveHome = aHome }, paths with { ActiveHome = Path.Combine(paths.Root, "xy_clean") },
            paths with { ActiveHome = paths.Root }, paths with { WebDesktopRoot = paths.ActiveHome },
            paths with { WebDesktopRoot = Path.Combine(paths.PackagedDesktopRoot!, "nested") },
            paths with { PackagedDesktopRoot = Path.Combine(paths.Root, "provider-layer") }
        }) Expect(() => _ = new FileSwitchStorage(badPaths, actual), "overlapping business roots rejected");
        var missingAuth = Path.Combine(paths.Root, "profiles", "待登录");
        Directory.CreateDirectory(missingAuth);
        try { storage.Preflight(new(IdentityKind.Account, "待登录"), true); throw new InvalidOperationException("missing credentials accepted"); }
        catch (StorageException error) { Assert(error.Code == StorageError.LoginRequired, "new account needs login typed"); }
        var beforePreflight = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllText);
        storage.Preflight(b, true);
        Assert(beforePreflight.All(pair => Read(pair.Key) == pair.Value) && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == beforePreflight.Count, "preflight has no writes");
        var snapshot = storage.Snapshot("switch", true);
        Assert(Directory.Exists(snapshot.Path), "snapshot material exists");
        foreach (var relative in SwitchFileRules.StateFiles.Concat(SwitchFileRules.IdentityFiles).Concat(SwitchFileRules.SessionFiles))
            Assert(File.Exists(Path.Combine(snapshot.Path, relative)), "snapshot file " + relative);
        foreach (var relative in SwitchFileRules.SessionDirectories)
            Assert(File.Exists(Path.Combine(snapshot.Path, relative, "中文", "fixture.txt")), "snapshot directory " + relative);
        Assert(!File.Exists(Path.Combine(snapshot.Path, "electron", "Default", "Cache", "cached")), "snapshot excludes cache");
        Assert(storage.ArchiveCurrent(snapshot, a, true), "archive known identity");
        profiles.SaveAccount("同账号不同档案", authA, developer: true);
        Write(Path.Combine(paths.Root, "profiles", "同账号不同档案", "installation_id"), "fixture-other-install");
        Assert(storage.ReadActiveIdentity() == a, "duplicate account id resolved using installation identity");
        Write(Path.Combine(paths.Root, "profiles", "同账号不同档案", "installation_id"), "fixture-install-A");
        Assert(storage.ReadActiveIdentity() is null, "same account and installation remains ambiguous");
        Directory.Delete(Path.Combine(paths.Root, "profiles", "同账号不同档案"), true);
        Assert(Read(Path.Combine(aHome, "electron-web-state", "statsig-state.json")) == "A statsig", "archive web layer");
        var apply = storage.ApplyTarget(b, a, true);
        Assert(apply.ActiveIdentity == b && storage.ReadActiveIdentity() == b, "target activation typed and fact checked");
        Assert(!File.Exists(Path.Combine(paths.ActiveHome, ".codex-global-state.json.bak")), "missing login source deletes stale state");
        Assert(JsonNode.Parse(Read(Path.Combine(paths.ActiveHome, ".codex-global-state.json")))![SwitchFileRules.InstallationKey]!.GetValue<string>() == Read(Path.Combine(paths.ActiveHome, "installation_id")), "installation identity synchronized");
        Assert(Read(Path.Combine(paths.ActiveHome, "sessions", "B.jsonl")) == "B history" && !Directory.Exists(Path.Combine(paths.ActiveHome, "memories")), "session replace removes absent target data");
        Assert(!File.Exists(Path.Combine(paths.ActiveHome, "logs_2.sqlite")), "isolated extras removed by session replacement");
        Assert(!File.Exists(Path.Combine(paths.ActiveHome, ".sandbox", "one.log")) && File.Exists(Path.Combine(paths.ActiveHome, ".sandbox", "deny_read_acl_state.json")), "transient logs cleared and ACL state kept");
        Assert(Read(Path.Combine(paths.PackagedDesktopRoot!, "Default", "Network", "Cookies")) == "B package cookie" && Read(Path.Combine(paths.WebDesktopRoot!, "Default", "Network", "Cookies")) == "B web cookie", "both desktop layers switched");
        Assert(Read(Path.Combine(paths.WebDesktopRoot!, "Default", "Code Cache", "code")) == "pure cache" && Read(Path.Combine(paths.WebDesktopRoot!, "unmanaged.txt")) == "unmanaged file", "pure caches and unmanaged data untouched");
        Assert(!Directory.Exists(Path.Combine(paths.WebDesktopRoot!, "Default", "Cache")) && apply.RecoveryPaths.Count >= 4, "HTTP caches moved with recovery locations");
        Assert(Directory.Exists(snapshot.Path), "switch does not clean snapshot before confirmed launch");
        storage.Restore(snapshot, true);
        Assert(storage.ReadActiveIdentity() == a && Read(Path.Combine(paths.ActiveHome, "config.toml")) == originalConfig, "restore identity and exact config");
        Assert(Read(Path.Combine(paths.ActiveHome, "sessions", "中文", "fixture.txt")) == "A:sessions" && !File.Exists(Path.Combine(paths.ActiveHome, "sessions", "B.jsonl")), "restore exact session scope");
        Assert(Read(Path.Combine(paths.WebDesktopRoot!, "statsig-state.json")) == "A statsig" && Read(Path.Combine(paths.PackagedDesktopRoot!, "statsig-state.json")) == "A statsig", "restore both desktop layers");
        Assert(!File.Exists(Path.Combine(paths.Root, "providers", ".state.json")), "restore originally absent provider state");
        var completed = storage.Complete(snapshot, true, apply.RecoveryPaths);
        Assert(!completed.SnapshotKept && !Directory.Exists(snapshot.Path), "confirmed known archive allows cleanup");
        Assert(apply.RecoveryPaths.All(path => !Directory.Exists(path)), "confirmed success clears only this operation recoverable copies");
        Assert(completed.DeletedPaths?.Count == apply.RecoveryPaths.Count + 1, "cleanup returns exact removed paths for UI");
        var shared = storage.Snapshot("shared", false);
        Assert(!Directory.Exists(Path.Combine(shared.Path, "sessions")) && !Directory.Exists(Path.Combine(shared.Path, "electron")), "shared snapshot excludes isolated data");
        var aSession = Read(Path.Combine(paths.ActiveHome, "sessions", "中文", "fixture.txt"));
        storage.ArchiveCurrent(shared, a, false);
        storage.ApplyTarget(b, a, false);
        Assert(Read(Path.Combine(paths.ActiveHome, "sessions", "中文", "fixture.txt")) == aSession && Read(Path.Combine(paths.WebDesktopRoot!, "statsig-state.json")) == "A statsig", "isolation disabled shares sessions and desktop");
        storage.Restore(shared, false);
        Assert(storage.ReadActiveIdentity() == a, "shared restore reinstates login");
        profiles.SaveProvider(p.Name, "https://provider-one.invalid/v1", "fixture-shared-key", "provider-model");
        profiles.SaveProvider(q.Name, "https://provider-two.invalid/v1", "fixture-shared-key", "");
        var beforeProvider = storage.Snapshot("provider", true);
        storage.ArchiveCurrent(beforeProvider, a, true);
        storage.ApplyTarget(p, a, true);
        var providerConfig = Read(Path.Combine(paths.ActiveHome, "config.toml"));
        Assert(storage.ReadActiveIdentity() == p && providerConfig.Contains("model = \"provider-model\"", StringComparison.Ordinal), "provider activation and model");
        Assert(providerConfig.Contains("model = \"inside-string\"\r\n", StringComparison.Ordinal) && providerConfig.Contains("[section]\r\nmodel = \"section-model\"\r\n", StringComparison.Ordinal), "only top-level TOML keys managed");
        Assert(!providerConfig.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n'), "CRLF preserved");
        Assert(!File.Exists(Path.Combine(paths.WebDesktopRoot!, "Default", "Network", "Cookies")), "new provider shared desktop starts fresh");
        Write(Path.Combine(paths.WebDesktopRoot!, "Default", "Network", "Cookies"), "provider shared cookie");
        var beforeQ = storage.Snapshot("provider-Q", true);
        storage.ArchiveCurrent(beforeQ, p, true);
        storage.ApplyTarget(q, p, true);
        Assert(storage.ReadActiveIdentity() == q && Read(Path.Combine(paths.WebDesktopRoot!, "Default", "Network", "Cookies")) == "provider shared cookie", "providers share layer without account crossover");
        var originalRecord = JsonNode.Parse(Read(Path.Combine(paths.Root, "providers", ".state.json")))!["record"]!.AsObject();
        Assert(originalRecord["model"]!["line"]!.GetValue<string>().Contains("original-model", StringComparison.Ordinal), "provider-to-provider retains original trusted record");
        var beforeReturn = storage.Snapshot("return-account", true);
        storage.ArchiveCurrent(beforeReturn, q, true);
        storage.ApplyTarget(a, q, true);
        Assert(storage.ReadActiveIdentity() == a && Read(Path.Combine(paths.ActiveHome, "config.toml")) == originalConfig, "leave provider restores original lines and positions");
        Assert(!File.Exists(Path.Combine(paths.Root, "providers", ".state.json")), "provider record removed only after config restored");
        storage.Restore(beforeReturn, true);
        Assert(storage.ReadActiveIdentity() == q && File.Exists(Path.Combine(paths.Root, "providers", ".state.json")), "rollback restores provider activity record");
        storage.Restore(beforeProvider, true);
        Assert(storage.ReadActiveIdentity() == a, "rollback returns from provider to account");
        Write(Path.Combine(paths.Root, "providers", "坏供应商", "provider.json"), "{");
        storage.ApplyTarget(p, a, true);
        Assert(storage.ReadActiveIdentity() == p, "corrupt unrelated provider does not block inference");
        Write(Path.Combine(paths.Root, "providers", ".state.json"), "{");
        Assert(storage.ReadActiveIdentity() == p, "corrupt provider record falls back to matching key and URL");
        var corruptStateSnapshot = storage.Snapshot("corrupt-provider-state", true);
        storage.ArchiveCurrent(corruptStateSnapshot, p, true);
        storage.ApplyTarget(a, p, true);
        var untrustedLeft = Read(Path.Combine(paths.ActiveHome, "config.toml"));
        Assert(storage.ReadActiveIdentity() == a && !File.Exists(Path.Combine(paths.Root, "providers", ".state.json")) && untrustedLeft.Contains("model = \"section-model\"", StringComparison.Ordinal) && !untrustedLeft.Contains("provider-model", StringComparison.Ordinal), "corrupt provider record allows leaving and preserves nested keys");
        storage.Restore(beforeProvider, true);
        Directory.Delete(Path.Combine(paths.Root, "providers", "坏供应商"), true);
        var configBeforeFailure = Read(Path.Combine(paths.ActiveHome, "config.toml"));
        var forFailure = storage.Snapshot("failure", true);
        storage.ArchiveCurrent(forFailure, a, true);
        fault.FailWrite = path => path == Path.Combine(paths.Root, "providers", ".state.json");
        Expect(() => storage.ApplyTarget(p, a, true), "provider record fault");
        Assert(Read(Path.Combine(paths.ActiveHome, "config.toml")) == configBeforeFailure, "record write failure never edits config");
        fault.FailWrite = null;
        storage.Restore(forFailure, true);
        fault.FailWrite = path => path == Path.Combine(paths.ActiveHome, "auth.json");
        Expect(() => storage.ApplyTarget(b, a, true), "auth copy fault");
        fault.FailWrite = null;
        storage.Restore(forFailure, true);
        Assert(storage.ReadActiveIdentity() == a && Directory.Exists(forFailure.Path), "copy fault recovers from retained snapshot");
        fault.FailDeleteFile = path => path.EndsWith("Cookies", StringComparison.Ordinal);
        Expect(() => storage.ApplyTarget(b, a, true), "desktop deletion fault");
        fault.FailDeleteFile = null;
        storage.Restore(forFailure, true);
        Assert(storage.ReadActiveIdentity() == a, "desktop fault restores login and layers");
        Write(Path.Combine(paths.WebDesktopRoot!, "Default", "Cache", "cached"), "occupied HTTP cache fixture");
        fault.FailMove = path => Path.GetFileName(path) == "Cache";
        var cacheWarning = storage.ApplyTarget(b, a, true);
        Assert(cacheWarning.ActiveIdentity == b && cacheWarning.Warnings.Count == 1 && File.Exists(Path.Combine(paths.WebDesktopRoot!, "Default", "Cache", "cached")), "occupied HTTP cache stays and warning propagated");
        fault.FailMove = null;
        storage.Restore(forFailure, true);
        fault.FailWrite = path => path.Contains("before-failed-snapshot", StringComparison.Ordinal) && path.EndsWith("auth.json", StringComparison.Ordinal);
        Expect(() => storage.Snapshot("failed-snapshot", true), "snapshot fault");
        Assert(storage.ReadActiveIdentity() == a, "snapshot failure does not alter active state");
        fault.FailWrite = null;
        var corrupt = storage.Snapshot("corrupt", true);
        Write(Path.Combine(corrupt.Path, "auth.json"), "damaged");
        var homeBeforeCorrupt = Read(Path.Combine(paths.ActiveHome, "auth.json"));
        Expect(() => storage.Restore(corrupt, true), "corrupt snapshot");
        Assert(Read(Path.Combine(paths.ActiveHome, "auth.json")) == homeBeforeCorrupt, "corrupt snapshot blocked before writes");
        var missingMarked = storage.Snapshot("missing-marked", true);
        var marksPath = Path.Combine(missingMarked.Path, "_snapshot.json");
        var missingMarks = JsonNode.Parse(Read(marksPath))!.AsObject();
        missingMarks["manifest"]!.AsObject().Remove("config.toml");
        File.Delete(Path.Combine(missingMarked.Path, "config.toml"));
        Write(marksPath, missingMarks.ToJsonString());
        Expect(() => storage.Restore(missingMarked, true), "marked missing config rejected before any writes");
        Assert(Read(Path.Combine(paths.ActiveHome, "auth.json")) == homeBeforeCorrupt, "missing marked file leaves login intact");
        var modifiedPaths = new FileSwitchStorage(paths with { WebDesktopRoot = Path.Combine(root, "different-web") }, actual);
        Expect(() => modifiedPaths.Restore(forFailure, true), "changed storage roots");
        fault.FailDeleteDirectory = path => path == forFailure.Path;
        var cleanupFail = storage.Complete(forFailure, true);
        Assert(cleanupFail.SnapshotKept && cleanupFail.Warnings.Count == 1 && storage.ReadActiveIdentity() == a, "cleanup failure warns without rolling back success");
        fault.FailDeleteDirectory = null;
        Write(Path.Combine(paths.ActiveHome, "auth.json"), "{\"tokens\":{\"account_id\":\"fixture-unknown\"}}");
        Assert(storage.ReadActiveIdentity() is null, "unknown owner remains unknown");
        var unknown = storage.Snapshot("unknown", true);
        Assert(!storage.ArchiveCurrent(unknown, null, true), "unknown owner not falsely archived");
        var unknownApply = storage.ApplyTarget(b, null, true);
        Assert(storage.Complete(unknown, false, unknownApply.RecoveryPaths).SnapshotKept && Directory.Exists(unknown.Path) && unknownApply.RecoveryPaths.All(Directory.Exists), "unknown original snapshot and recovery materials never cleaned");
        storage.Restore(unknown, true);
        Assert(storage.ReadActiveIdentity() is null, "unknown restored as unknown");
        Write(Path.Combine(paths.ActiveHome, "auth.json"), "{");
        Assert(storage.ReadActiveIdentity() is null, "corrupt current credentials are unknown");
        var unknownCorrupt = storage.Snapshot("unknown-corrupt", true);
        Assert(!storage.ArchiveCurrent(unknownCorrupt, null, true), "corrupt unknown snapshot retained");
        storage.ApplyTarget(b, null, true);
        storage.Restore(unknownCorrupt, true);
        Assert(Read(Path.Combine(paths.ActiveHome, "auth.json")) == "{" && storage.ReadActiveIdentity() is null, "corrupt original bytes can be recovered without certification");
        var noConfig = "# comments only\n[table]\nmodel = \"nested\"\n";
        Assert(TomlManagedKeys.Restore(TomlManagedKeys.Merge(noConfig, new Dictionary<string, string?> { ["model"] = "\"p\"", ["openai_base_url"] = "\"url\"" }), TomlManagedKeys.Record(noConfig)) == noConfig, "absent managed keys restored without touching table");
        var multiline = "values = [\n  [1, 2],\n]\ntext = \"\"\"\nmodel = \"in-string\"\n[not-table]\n\"\"\"\nmodel = \"before\"\n[real]\nmodel = \"nested\"";
        var merged = TomlManagedKeys.Merge(multiline, new Dictionary<string, string?> { ["model"] = "\"after\"" });
        Assert(merged.Contains("model = \"in-string\"", StringComparison.Ordinal) && merged.EndsWith("model = \"nested\"", StringComparison.Ordinal), "multiline scanner and no final newline");
        Assert(TomlManagedKeys.Restore(merged, TomlManagedKeys.Record(multiline)) == multiline, "exact multiline original restored");
        var beforeChanged = "first = 1\nsecond = 2\nmodel = \"original\"\n[table]\nmodel = \"nested\"\n";
        var afterChanged = "model = \"provider\"\n[table]\nmodel = \"nested\"\n";
        Assert(TomlManagedKeys.Restore(afterChanged, TomlManagedKeys.Record(beforeChanged)) == "model = \"original\"\n[table]\nmodel = \"nested\"\n", "removed top-level lines never move restored key into table");
        var addedTop = "model = \"provider\"\nadded = 3\n[table]\nmodel = \"nested\"\n";
        var restoredTop = TomlManagedKeys.Restore(addedTop, TomlManagedKeys.Record(beforeChanged));
        Assert(restoredTop.StartsWith("added = 3\nmodel = \"original\"\n[table]", StringComparison.Ordinal), "non-managed user edits preserved while restoring top-level keys");
        var streamSource = Path.Combine(root, "stream-source.bin");
        var streamTarget = Path.Combine(root, "stream-target.bin");
        using (var stream = new FileStream(streamSource, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.SetLength(8 * 1048576 + 17); stream.Position = stream.Length - 1; stream.WriteByte(123); }
        actual.CopyAtomic(streamSource, streamTarget);
        Assert(new FileInfo(streamTarget).Length == 8 * 1048576 + 17 && actual.Hash(streamSource) == actual.Hash(streamTarget), "streamed copy and incrementally hashed multi-buffer fixture");
        using (var lockedTarget = new FileStream(streamTarget, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { actual.CopyAtomic(streamSource, streamTarget); throw new InvalidOperationException("locked atomic replace accepted"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { count++; }
        }
        Assert(actual.Hash(streamSource) == actual.Hash(streamTarget) && !Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "streamed copy failure preserves destination and clears temporary file");
        Console.WriteLine($"PASS {count} switch storage checks; fixture root: {root}");
        return count;
    }
}
