using System.Text.Json.Nodes;
using FAswitch.Core;

internal static class ManagedToolIsolationChecks
{
    public static int Run(string root)
    {
        var count = 0;
        void Check(bool passed, string label) { if (!passed) throw new InvalidOperationException(label); count++; }
        void Put(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        var paths = new ManagedClonePaths(Path.Combine(root, "clone"));
        var storage = new ManagedCloneStorage(paths);
        var profiles = new FileProfileStore(paths.AccountRoot);
        var a = new IdentityRef(IdentityKind.Account, "A"); var b = new IdentityRef(IdentityKind.Account, "B");
        foreach (var identity in new[] { a, b }) profiles.SaveAccount(identity.Name,
            new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "fixture-" + identity.Name, ["access_token"] = "fixture-access-" + identity.Name } });
        var config = """
            model = "account-model"
            model_reasoning_effort = "high"
            mcp_servers.inline = { command = "old-tool" }
            [mcp_servers.stale]
            command = "old-server"
            ["plugins".'fixture@example']
            enabled = true
            [[skills.config]]
            path = "old-skill"
            enabled = true
            [marketplaces.old]
            source = "old-catalog"
            [features]
            fixture = true
            [projects.'fixture']
            trust_level = "trusted"
            """;
        var savedConfig = Path.Combine(profiles.ProfileDirectory(a), "config.toml");
        Put(savedConfig, config);
        Put(Path.Combine(profiles.ProfileDirectory(a), "skills", "old", "SKILL.md"), "not inherited");
        storage.ApplyTarget(a, null, false);
        var runtimeConfig = Path.Combine(paths.CodexHome, "config.toml");
        var cleaned = File.ReadAllText(runtimeConfig);
        Check(cleaned.Contains("account-model") && cleaned.Contains("high") && cleaned.Contains("[features]") && cleaned.Contains("trust_level"), "account model and unrelated config survive");
        Check(!cleaned.Contains("mcp_servers") && !cleaned.Contains("plugins") && !cleaned.Contains("skills.config") && !cleaned.Contains("marketplaces"), "all custom tool config forms removed");
        Check(File.ReadAllText(savedConfig) == config, "activation leaves saved account config untouched");
        Check(!Directory.Exists(Path.Combine(paths.CodexHome, "skills")) && !Directory.Exists(Path.Combine(paths.CodexHome, "plugins")), "account activation imports no tool assets");
        Check(storage.ReadActiveIdentity() == a && File.ReadAllText(Path.Combine(paths.CodexHome, "auth.json")).Contains("fixture-A"), "selected account still activates normally");
        // A snapshot made by an older runtime may still contain inherited tool settings.
        Put(runtimeConfig, cleaned + "\n[mcp_servers.legacy]\ncommand='old'\n");
        Put(Path.Combine(paths.CodexHome, "skills", "legacy", "SKILL.md"), "old copied skill");
        Put(Path.Combine(paths.CodexHome, "plugins", "cache", "legacy", "plugin.json"), "{}");
        var snapshot = storage.Snapshot("isolation", false);
        storage.ApplyTarget(b, a, false);
        Check(!Directory.Exists(Path.Combine(paths.CodexHome, "skills")) && !Directory.Exists(Path.Combine(paths.CodexHome, "plugins")), "switch removes previously inherited runtime assets");
        Check(!File.Exists(runtimeConfig) && storage.ReadActiveIdentity() == b, "account without config stays free of tool config");
        storage.Restore(snapshot, false);
        Check(storage.ReadActiveIdentity() == a && !File.ReadAllText(runtimeConfig).Contains("mcp_servers.legacy") && File.ReadAllText(runtimeConfig).Contains("account-model"), "recovery retains account but strips legacy inherited tools");
        Check(!Directory.Exists(Path.Combine(paths.CodexHome, "skills")) && !Directory.Exists(Path.Combine(paths.CodexHome, "plugins")), "recovery does not restore tool assets");
        storage.Complete(snapshot, true);
        storage.ResetRuntime(); storage.ApplyTarget(a, null, false);
        Check(!Directory.Exists(Path.Combine(paths.CodexHome, "skills")) && !File.ReadAllText(runtimeConfig).Contains("mcp_servers"), "rebuild cannot reintroduce saved tool settings");
        var tricky = "model='account'\nnotes='''\n[mcp_servers.not_a_table]\n'''\nmcp_servers.inline={command='old'}\n[\"plugins\".'quoted@test']\nenabled=true\n[[skills.config]]\npath='old'\n[projects.'kept']\ntrust_level='trusted'\n";
        var stripped = TomlManagedKeys.WithoutTools(tricky);
        Check(stripped.Contains("notes='''\n[mcp_servers.not_a_table]\n'''") && stripped.Contains("[projects.'kept']"), "multiline text and unrelated tables preserved");
        Check(!stripped.Contains("mcp_servers.inline") && !stripped.Contains("quoted@test") && !stripped.Contains("[[skills.config]]"), "quoted and inline tools stripped");
        Check(TomlManagedKeys.WithoutTools(stripped) == stripped, "tool removal is idempotent");
        Check(TomlManagedKeys.WithoutTools("model='account'") == "model='account'", "tool-free configuration bytes stay unchanged");
        return count;
    }
}
