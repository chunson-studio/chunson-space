using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

internal static class AccountPreparationChecks
{
    public static async Task<int> Run(string root)
    {
        int count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        var clock = new FakeClock(); var network = new FakeTransport();
        var repository = new AccountRepository(root);
        repository.Profiles.CreateBoundAccount("fixture", new("http://127.0.0.1:7897"));
        var target = new IdentityRef(IdentityKind.Account, "fixture");
        var service = new AccountService(repository, network, clock);
        string Jwt(JsonObject claims) => "fixture." + OAuthPkce.Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString())) + ".unsigned";
        void Save(string access) => repository.SaveLogin(target.Name, JwtClaims.ToAuth(new()
        {
            ["id_token"] = Jwt(new() { ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = "fixture-id" } }),
            ["access_token"] = access, ["refresh_token"] = "fixture-refresh"
        }, clock.UtcNow));
        var fresh = Jwt(new() { ["exp"] = clock.UtcNow.AddHours(1).ToUnixTimeSeconds() });
        Save(fresh); var original = repository.ReadAuth(target.Name).ToJsonString();
        Check((await service.PrepareWithProxyAsync(target, "http://127.0.0.1:7898", default)).Count == 0
            && network.Requests.Count == 0 && repository.ReadAuth(target.Name).ToJsonString() == original,
            "fresh access skips launch refresh and preserves credentials");
        Check((await service.PrepareAsync(target, default)).Count == 0 && network.Requests.Count == 0,
            "both preparation paths reuse a fresh credential");
        Check((await service.PrepareWithProxyAsync(target, "", default)).Count > 0 && network.Requests.Count == 0,
            "fresh credential does not bypass explicit proxy validation");
        foreach (var access in new[] {
            Jwt(new() { ["exp"] = clock.UtcNow.AddMinutes(2).ToUnixTimeSeconds() }),
            Jwt(new() { ["exp"] = clock.UtcNow.AddSeconds(-1).ToUnixTimeSeconds() }),
            Jwt(new()), Jwt(new() { ["exp"] = "invalid" }), "opaque-fixture-token", "bad.???.token" })
        {
            Save(access); var before = network.Requests.Count;
            network.Json(200, new() { ["access_token"] = "refreshed-fixture-token" });
            Check((await service.PrepareWithProxyAsync(target, "http://127.0.0.1:7898", default)).Count == 0
                && network.Requests.Count == before + 1 && network.Requests[^1].Proxy == "http://127.0.0.1:7898",
                "near-expiry, expired or unreadable expiry refreshes once on resolved route");
        }
        Save(fresh); var beforeExplicit = network.Requests.Count;
        network.Json(200, new() { ["access_token"] = "explicit-refresh-fixture" });
        await service.RefreshAsync(target.Name);
        Check(network.Requests.Count == beforeExplicit + 1, "explicit refresh still refreshes fresh credentials");
        Save(fresh); var beforeCancel = network.Requests.Count;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.PrepareWithProxyAsync(target, "http://127.0.0.1:7898", cancelled.Token); Check(false, "cancel ignored"); }
        catch (OperationCanceledException) { Check(network.Requests.Count == beforeCancel, "cancelled preparation performs no request"); }
        return count;
    }
}
