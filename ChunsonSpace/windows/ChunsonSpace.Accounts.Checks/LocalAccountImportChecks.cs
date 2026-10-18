using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

internal static class LocalAccountImportChecks
{
    public static int Run(string root)
    {
        int count = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); count++; }
        var home = Path.Combine(root, "original"); Directory.CreateDirectory(home);
        var path = Path.Combine(home, "auth.json");
        JsonObject Auth(string account, string token) => new()
        {
            ["auth_mode"] = "chatgpt", ["tokens"] = new JsonObject {
                ["account_id"] = account, ["access_token"] = token,
                ["id_token"] = "fixture." + OAuthPkce.Base64Url(Encoding.UTF8.GetBytes("{\"email\":\"account-4b02bed3bb@example.invalid\"}")) + ".unsigned"
            }
        };
        var auth = Auth("local-id", "fixture-first"); File.WriteAllText(path, auth.ToJsonString());
        var original = File.ReadAllBytes(path);
        File.WriteAllText(Path.Combine(home, "config.toml"), "private = true");
        var repository = new AccountRepository(Path.Combine(root, "library"), home);
        var first = repository.ImportLocalLogin(home);
        Check(first.Created && repository.Profiles.ListAccounts().Count == 1, "local authorization creates one profile");
        Check(repository.Profiles.ReadEnvironment(first.Profile) is { FollowSystemProxy: true, Proxy: "", Timezone: "" }, "new local account follows current local network and timezone");
        Check(original.SequenceEqual(File.ReadAllBytes(path)) && !File.Exists(Path.Combine(repository.AccountPath(first.Profile), "config.toml")), "local import leaves original untouched and copies only auth");
        repository.Profiles.UpdateEnvironment(first.Profile, proxy: "http://127.0.0.1:12345", timezone: "Asia/Tokyo", locale: "en-US", followSystemProxy: false);
        var environmentPath = Path.Combine(repository.Root, "browser-envs", first.Profile, "env.json");
        var environment = File.ReadAllBytes(environmentPath);
        auth = Auth("local-id", "fixture-second"); File.WriteAllText(path, auth.ToJsonString()); original = File.ReadAllBytes(path);
        var second = repository.ImportLocalLogin(home);
        Check(!second.Created && second.Profile == first.Profile && repository.Profiles.ListAccounts().Count == 1, "repeat local import updates by account identity without duplicating");
        Check(repository.ReadAuth(first.Profile)["tokens"]?["access_token"]?.GetValue<string>() == "fixture-second", "repeat import saves current local credential");
        Check(environment.SequenceEqual(File.ReadAllBytes(environmentPath)) && original.SequenceEqual(File.ReadAllBytes(path)), "repeat import preserves independent network config bytes and original auth");
        auth = Auth("different-id", "fixture-other"); File.WriteAllText(path, auth.ToJsonString());
        var other = repository.ImportLocalLogin(home);
        Check(other.Created && other.Profile != first.Profile && repository.ReadAuth(first.Profile)["tokens"]?["account_id"]?.GetValue<string>() == "local-id", "shared email does not overwrite a different account identity");
        repository.Profiles.CreateBoundAccount("duplicate", new("http://127.0.0.1:12346", "Asia/Tokyo", "en-US"));
        repository.WriteProfileAuth("duplicate", Auth("local-id", "fixture-duplicate"));
        File.WriteAllText(path, Auth("local-id", "fixture-ambiguous").ToJsonString());
        try { repository.ImportLocalLogin(home); throw new InvalidOperationException("ambiguous account accepted"); }
        catch (AccountException error) { Check(error.Code == AccountError.IdentityMismatch && repository.Profiles.ListAccounts().Count == 3, "ambiguous local identity leaves all profiles intact"); }
        foreach (var invalid in new[] { "{", "{}", "{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"fixture\"}" })
        {
            File.WriteAllText(path, invalid);
            try { repository.ImportLocalLogin(home); throw new InvalidOperationException("invalid authorization accepted"); }
            catch (AccountException) { Check(repository.Profiles.ListAccounts().Count == 3, "invalid or API-key-only local login creates no profile"); }
        }
        File.Delete(path);
        try { repository.ImportLocalLogin(home); throw new InvalidOperationException("missing authorization accepted"); }
        catch (AccountException error) { Check(error.Code == AccountError.MissingCredentials, "missing local login reports missing credentials"); }
        return count;
    }
}
