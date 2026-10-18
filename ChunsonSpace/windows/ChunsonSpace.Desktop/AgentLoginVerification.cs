using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Proxy;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

internal static class AgentLoginVerification
{
    public static async Task RunAsync(string evidence, Action<string, bool> check)
    {
        var root = Path.Combine(evidence, "workspace"); var home = Path.Combine(evidence, "active");
        Directory.CreateDirectory(root); Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(root, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var workspace = new DesktopWorkspace(root, home, null, null, fixture: true);
        var accounts = workspace.Accounts!; var nodes = workspace.ProxyServices.NodeTesting!;
        var network = accounts.OfflineTransport!; var browser = accounts.OfflineNewAccountBrowser!;
        var profiles = Path.Combine(workspace.ProxyServices.FlClashRoot, "profiles"); Directory.CreateDirectory(profiles);
        var source = "proxies:\n - {name: 家宽验证, type: socks5, server: fixture.invalid, port: 443}\n";
        var sourcePath = Path.Combine(profiles, "account-route.yaml"); File.WriteAllText(sourcePath, source);
        var sub = nodes.Subscriptions(ProxyClientKind.FlClash, workspace.ProxyServices.FlClashRoot).Subscriptions.Single();
        var node = nodes.LoadSubscription(sub, workspace.ProxyServices.FlClashRoot).Nodes.Single();
        async Task<(int Code, JsonNode Report)> Run(string name, AgentRequest request)
        {
            var input = Path.Combine(evidence, name + "-request.json"); var output = Path.Combine(evidence, name + "-report.json");
            File.WriteAllText(input, JsonSerializer.Serialize(request));
            var code = await AgentCli.RunAsync(workspace, input, output);
            return (code, JsonNode.Parse(File.ReadAllText(output))!);
        }
        try
        {
            var protectionRunner = workspace.ProxyServices.FixtureProtectionRunner!;
            var protectionNetworkBefore = network.RequestCount;
            var dnsBefore = workspace.ProxyServices.FixtureFlClashRunner!.Invocations.Count;
            var udpBefore = workspace.ProxyServices.FixtureBrowserUdp!.Calls;
            protectionRunner.PreviewStatus = DirectConnectionProtectionStatus.ProtectionStale;
            var retiredBefore = protectionRunner.Actions.Count;
            foreach (var retiredCommand in new[] { "protection-preview", "protection-apply" })
            {
                var retired = await Run("retired-" + retiredCommand, new(retiredCommand));
                check("retired protection command does not call firewall " + retiredCommand, retired.Code != 0 && protectionRunner.Actions.Count == retiredBefore);
            }
            var inspected = await Run("protection-status", new("protection-status"));
            check("CLI status observes the same configured rules without an account request", inspected.Code == 0
                && inspected.Report["data"]?["settingsChangeAttempted"]?.GetValue<bool>() == false);
            var removed = await Run("protection-disabled", new("protection-disable"));
            var reread = await Run("protection-status-disabled", new("protection-status"));
            check("CLI disable and readback share the firewall service", removed.Code == 0 && reread.Code == 0
                && reread.Report["data"]?["protection"]?["status"]?.GetValue<string>() == "Disabled"
                && protectionRunner.Actions.TakeLast(2).SequenceEqual(new[] { DirectConnectionProtectionAction.Restore, DirectConnectionProtectionAction.Inspect }));
            check("protection CLI never changes DNS browsers or accounts and sends no account requests",
                workspace.ProxyServices.FixtureFlClashRunner.Invocations.Count == dnsBefore && workspace.ProxyServices.FixtureBrowserUdp.Calls == udpBefore
                && network.RequestCount == protectionNetworkBefore && browser.OpenCount == 0 && workspace.Profiles.ListAccounts().Count == 0);
            await nodes.ProbeScoredExitAsync("http://127.0.0.1:7890", default);
            foreach (var item in new[] {
                ("missing-selection", new AgentRequest("login", SubscriptionId: sub.Id)),
                ("unknown-score", new AgentRequest("login", sub.Id, node.Id, ExpectedIp: "203.0.113.99")),
                ("invalid-ip", new AgentRequest("login", sub.Id, node.Id, ExpectedIp: "not-an-ip")),
                ("changed-node", new AgentRequest("login", sub.Id, "missing", ExpectedIp: "192.0.2.10")) })
            {
                var before = network.RequestCount;
                var result = await Run(item.Item1, item.Item2);
                check("CLI dedicated " + item.Item1 + " refuses without network, browser or main-route fallback",
                    result.Code == 1 && network.RequestCount == before && browser.OpenCount == 0
                    && !File.Exists(Path.Combine(root, "managed-node-routes.json")));
            }
            network.ScorePayload = "{\"ip\":\"192.0.2.10\",\"fraudScore\":16}"; nodes.ResetFixtureScore();
            await nodes.ProbeScoredExitAsync("http://127.0.0.1:7890", default);
            var beforeLow = network.RequestCount;
            var low = await Run("84-rejected", new("login", sub.Id, node.Id, ExpectedIp: "192.0.2.10"));
            check("CLI cached 84 rejects without rescoring or opening browser", low.Code == 1 && network.RequestCount == beforeLow && browser.OpenCount == 0);

            network.ScorePayload = "{\"ip\":\"192.0.2.10\",\"fraudScore\":15}"; nodes.ResetFixtureScore();
            await nodes.ProbeScoredExitAsync("http://127.0.0.1:7890", default);
            var beforeScore = network.Requests.Count(r => NetCoffeeService.IsLookup(r.Url));
            var dedicated = await Run("85-dedicated", new("login", sub.Id, node.Id, ExpectedIp: "192.0.2.10"));
            var proxy = dedicated.Report["data"]?["route"]?["proxy"]?.GetValue<string>();
            var account = workspace.Profiles.ListAccounts().Single();
            check("CLI dedicated 85 allocates a loopback port and saves the same route after fixture authorization",
                dedicated.Code == 0 && proxy == browser.Proxy && proxy != browser.SystemProxy
                && workspace.Profiles.ReadEnvironment(account.Name).Proxy == proxy && account.HasCredentials);
            check("CLI dedicated login reuses score and preserves source subscription",
                network.Requests.Count(r => NetCoffeeService.IsLookup(r.Url)) == beforeScore && File.ReadAllText(sourcePath) == source);
            check("CLI dedicated login cleans its isolated browser", browser.CleanupCalls > 0 && browser.DisposeCalls > 0);

            var current = await Run("current-empty-fields", new("login", SubscriptionId: "", NodeId: "", ExpectedIp: ""));
            check("CLI wrapper's empty optional fields retain current-network login", current.Code == 0 && browser.Proxy == browser.SystemProxy
                && network.Requests.Count(r => NetCoffeeService.IsLookup(r.Url)) == beforeScore);
            var beforeLaunchCheckRequests = network.RequestCount;
            var localCheck = await Run("launch-check", new("launch-check", Profile: account.Name));
            check("CLI local launch check does not send account requests or launch", localCheck.Code == 0 && network.RequestCount == beforeLaunchCheckRequests
                && localCheck.Report["data"]?["launched"]?.GetValue<bool>() == false
                && localCheck.Report["data"]?["networkVerified"]?.GetValue<bool>() == false);
            var comparison = await Run("ciallo", new("ciallo", Profile: account.Name));
            check("CLI Ciallo uses shared high-effort comparison", comparison.Code == 0 && comparison.Report["data"]?["completed"]?.GetValue<int>() == CialloTest.Models.Count);
            var screened = await Run("screen", new("screen"));
            var requestsBeforePending = network.RequestCount;
            var pending = await Run("screen-pending", new("screen-pending"));
            check("CLI incremental screening skips existing complete nodes", screened.Code == 0 && pending.Code == 0 && network.RequestCount == requestsBeforePending && pending.Report["data"]?["tested"]?.GetValue<int>() == 0);
        }
        finally { await accounts.DisposeAsync(); }
    }
}
