using System.IO;
using System.Text.Json;
using ChunsonSpace.Accounts;
using ChunsonSpace.NodeTesting;
using ChunsonSpace.Proxy;

namespace ChunsonSpace.Desktop;

internal static class ResidentialExemptionVerification
{
    public static async Task RunAsync(MainWindow window, string directory, Action<string, bool> check, Action<string> capture)
    {
        Directory.CreateDirectory(Path.Combine(directory, "accounts")); Directory.CreateDirectory(Path.Combine(directory, "home"));
        File.WriteAllText(Path.Combine(directory, "accounts", "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var workspace = new DesktopWorkspace(Path.Combine(directory, "accounts"), Path.Combine(directory, "home"), Path.Combine(directory, "desktop"), Path.Combine(directory, "web"), fixture: true);
        var model = new ShellViewModel(workspace); window.DataContext = model;
        var services = workspace.ProxyServices.NodeTesting!;
        var source = workspace.ProxyServices.FlClashRoot;
        var folder = Path.Combine(source, "profiles"); Directory.CreateDirectory(folder);
        const string yaml = "proxies:\n - {name: 美国住宅, type: socks5, server: residential.fixture.invalid, port: 443}\n - {name: 美国家宽, type: socks5, server: broadband.fixture.invalid, port: 443}\n";
        File.WriteAllText(Path.Combine(folder, "IPEqual.yaml"), yaml);
        File.WriteAllText(Path.Combine(folder, "Other.yaml"), yaml);
        var panel = new NodeTestingViewModel(services, () => (ProxyClientKind.FlClash, source));
        var network = workspace.Accounts!.OfflineTransport!;
        network.ScorePayload = "{\"ip\":\"192.0.2.10\",\"fraudScore\":16,\"isResidential\":true}";
        panel.LoadCommand.Execute(null); await panel.CurrentOperation;
        panel.ScreenCommand.Execute(null); await panel.CurrentOperation;
        var residential = panel.Items.Single(i => i.SubscriptionId == "IPEqual" && i.Name.Contains("住宅"));
        check("only IPEqual named residential is exempt at real score 84", residential.Eligible && residential.QualityScore == 84 && panel.Items.Count(i => i.Eligible) == 1 && residential.ChoiceLabel.Contains("84 分") && residential.ChoiceLabel.Contains("住宅免检"));
        check("screen and login choices share the exempt result", panel.Results.SequenceEqual(new[] { residential }) && panel.LoginChoices.SequenceEqual(panel.Results));
        check("same exit on ordinary route remains below threshold", !(await services.ProbeCachedExitAsync("http://127.0.0.1:7897", default)).Admission.Allowed);
        var json = JsonSerializer.Serialize(residential.Quality);
        check("IP cache cannot persist residential exemption", !json.Contains("ResidentialScoreExempt") && !JsonSerializer.Deserialize<IpQualityReport>(json)!.Admission.Allowed);
        var before = network.RequestCount;
        var selection = await services.PrepareDedicatedLoginAsync(residential.SubscriptionId, residential.Node, residential.Quality!, default);
        check("residential allocation reads cached score without network", network.RequestCount == before && selection.Proxy.StartsWith("http://127.0.0.1:"));
        var admitted = await services.ProbeAccountExitAsync(selection.Proxy, default);
        check("residential managed route verifies actual exit and preserves low score", admitted.Admission.Allowed && admitted.Admission.QualityScore == 84 && admitted.Ip == residential.ExitIp);
        var launch = new IpAdmissionService(services.ProbeAccountExitAsync);
        check("explicit admission accepts verified residential route", (await launch.RequireVerifiedProxyAsync(selection.Proxy, default)).Ip == residential.ExitIp);
        var cached = services.CachedLoginExit(residential.ExitIp, residential.SubscriptionId, residential.Node);
        check("CLI cached login uses same residential policy", cached.Admission.Allowed && cached.Score?.Quality == 84);
        var prior = residential.Quality!;
        residential.Apply(new(residential.Node.Id, residential.Name, residential.Type, false, prior, "connection timeout", new(ProxyLatencyState.Timeout, null, "timeout")));
        check("timeout cannot enter residential login even with old IP and score", !residential.Eligible && panel.LoginChoices.Count == 0);
        services.ResetFixtureScore(); network.ScorePayload = "{\"ip\":\"192.0.2.10\"}";
        panel.ScreenCommand.Execute(null); await panel.CurrentOperation;
        check("missing score stays visibly unknown on eligible residential route", residential.Eligible && residential.QualityScore is null && residential.ScoreStatus == "评分暂不可用" && panel.Items.Count(i => i.Eligible) == 1);
        check("CLI residential lookup can reuse verified history without fabricated score", services.CachedLoginExit(residential.ExitIp, residential.SubscriptionId, residential.Node) is { ResidentialScoreExempt: true, Score.Quality: null });
        var unscored = await services.ProbeAccountExitAsync(selection.Proxy, default);
        check("residential resume permits unknown score only with confirmed IP", unscored.Admission.Allowed && unscored.Admission.QualityScore is null);
        check("exemption with no actual IP remains rejected", !(unscored with { Ip = "" }).Admission.Allowed);
        var other = panel.Items.Single(i => i.SubscriptionId == "Other" && i.Name.Contains("住宅"));
        try { await services.PrepareDedicatedLoginAsync(other.SubscriptionId, other.Node, prior, default); throw new InvalidOperationException("forged exemption accepted"); }
        catch (InvalidOperationException error) when (error.Message != "forged exemption accepted") { check("foreign subscription cannot borrow report exemption", true); }
        File.Move(Path.Combine(folder, "IPEqual.yaml"), Path.Combine(folder, "Renamed.yaml"));
        try { await services.ProbeAccountExitAsync(selection.Proxy, default); throw new InvalidOperationException("removed subscription accepted"); }
        catch (IOException) { check("removed subscription invalidates exemption on existing port", true); }
        await workspace.Accounts.DisposeAsync();
    }
}
