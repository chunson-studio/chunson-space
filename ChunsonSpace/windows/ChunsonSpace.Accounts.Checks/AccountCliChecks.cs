using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

static class AccountCliChecks
{
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Assert(bool value, string label) { if (!value) throw new InvalidOperationException("CLI: " + label); count++; }
        var output = new StringWriter();
        var help = await AccountCli.TryRunWithoutServicesAsync(["--help"], output);
        Assert(help == 0 && output.ToString().Contains("--timezone=", StringComparison.Ordinal) && !Directory.Exists(root), "help uses no account/system/browser services");
        Assert(await AccountCli.TryRunWithoutServicesAsync(["--unknown"], output) == 2, "invalid parsing requires no services");
        var network = new FakeTransport();
        var clock = new FakeClock();
        var repository = new AccountRepository(Path.Combine(root, "data"));
        var callback = new FakeCallbackFactory(clock);
        var browser = new FakeBrowser();
        var login = new LoginService(repository, network, callback, browser, clock);
        var egress = new EgressService(repository, network);
        var cli = new AccountCli(repository, login, egress, output, developer: true);
        Assert(await cli.TryRunAsync([]) is null, "no arguments means GUI");
        Assert(await cli.TryRunAsync(["--list", "--set", "A", "--proxy=x"]) == 2, "list mutually exclusive");
        Assert(await cli.TryRunAsync(["--set", "A", "--new", "B", "--proxy=x"]) == 2, "set and new mutually exclusive");
        Assert(await cli.TryRunAsync(["--new", "A"]) == 2, "new requires proxy flag");
        Assert(await cli.TryRunAsync(["--new", "CON", "--proxy=http://127.0.0.1:7897"]) == 2, "new invalid name usage error");
        Assert(await cli.TryRunAsync(["--launch", "--set", "A", "--proxy=x"]) == 2, "launch only with new");
        Assert(await cli.TryRunAsync(["--set", "missing", "--proxy=http://127.0.0.1:7897"]) == 3, "missing account exit 3");
        Assert(await cli.TryRunAsync(["--new", "NoProxy", "--proxy="]) == 4 && !repository.Exists("NoProxy"), "empty proxy refuses creation");
        Assert(await cli.TryRunAsync(["--new", "Socks", "--proxy=socks5://127.0.0.1:7897"]) == 4 && !repository.Exists("Socks"), "SOCKS5 refuses account creation");
        Assert(await cli.TryRunAsync(["--new", "A", "--proxy=http://127.0.0.1:7897", "--timezone=Asia/Tokyo"]) == 0, "new bound profile saved");
        repository.Profiles.UpdateEnvironment("A", locale: "ja-JP");
        Assert(await cli.TryRunAsync(["--set", "A", "--proxy=http://127.0.0.1:7898"]) == 0 && repository.Profiles.ReadEnvironment("A") is { Timezone: "Asia/Tokyo", Locale: "ja-JP" }, "set unspecified fields unchanged");
        Assert(await cli.TryRunAsync(["--set", "A", "--timezone="]) == 0 && repository.Profiles.ReadEnvironment("A").Timezone == "", "equals empty timezone clears");
        Assert(await cli.TryRunAsync(["--set", "A", "--proxy", ""]) == 0 && repository.Profiles.ReadEnvironment("A").Proxy == "", "separate empty proxy clears");
        Assert(await cli.TryRunAsync(["--set", "A", "--timezone", "auto"]) == 2, "auto invalid for set");
        Assert(await cli.TryRunAsync(["--set", "A"]) == 2, "set needs specified field");
        Assert(await cli.TryRunAsync(["--new", "A", "--proxy=http://127.0.0.1:7897"]) == 4, "new never overwrites");
        network.Text(200, "ip=192.0.2.2\nloc=US\n");
        var callbacksBefore = callback.Starts;
        Assert(await cli.TryRunAsync(["--new", "多时区", "--proxy=http://127.0.0.1:7897", "--timezone=auto", "--launch"]) == 5, "multizone candidate exit 5");
        Assert(repository.Exists("多时区") && repository.Profiles.ReadEnvironment("多时区").Timezone == "" && callback.Starts == callbacksBefore && output.ToString().Contains("America/Chicago", StringComparison.Ordinal), "multizone keeps saved profile, prints candidates, launches no browser");
        network.Text(200, "ip=192.0.2.3\nloc=JP\n");
        Assert(await cli.TryRunAsync(["--new", "单时区", "--proxy=http://127.0.0.1:7897", "--timezone=auto"]) == 0 && repository.Profiles.ReadEnvironment("单时区").Timezone == "Asia/Tokyo", "single candidate auto timezone saved");
        network.Text(200, "ip=192.0.2.4\nloc=XX\n");
        Assert(await cli.TryRunAsync(["--new", "未知时区", "--proxy=http://127.0.0.1:7897", "--timezone=auto"]) == 5, "unknown country no timezone guessed");
        var token = new JsonObject { ["id_token"] = "fixture." + OAuthPkce.Base64Url(Encoding.UTF8.GetBytes(new JsonObject { ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = "fixture-cli" } }.ToJsonString())) + ".unsigned", ["access_token"] = "fixture-at", ["refresh_token"] = "fixture-rt" };
        network.Json(200, token);
        Assert(await cli.TryRunAsync(["--new", "登录", "--proxy=http://127.0.0.1:7897", "--launch"]) == 0 && repository.ReadAuth("登录")["tokens"]!["account_id"]!.GetValue<string>() == "fixture-cli", "launch awaits human callback and persists isolated login");
        callback.Kind = CallbackKind.Cancelled;
        Assert(await cli.TryRunAsync(["--new", "取消", "--proxy=http://127.0.0.1:7897", "--launch"]) == 4 && !File.Exists(Path.Combine(repository.AccountPath("取消"), "auth.json")), "cancelled launch exit 4 and no auth");
        network.Text(200, "loc=JP\nip=192.0.2.5\n");
        Assert(await cli.TryRunAsync(["--new", "自动后取消", "--proxy=http://127.0.0.1:7897", "--timezone=auto", "--launch"]) == 4, "resolved auto then cancelled login is failure 4, not choice 5");
        Assert(await cli.TryRunAsync(["--list"]) == 0 && output.ToString().Contains("账号", StringComparison.Ordinal), "list prints account environment table");
        Console.WriteLine($"PASS {count} isolated CLI checks; fixture root: {root}");
        return count;
    }
}
