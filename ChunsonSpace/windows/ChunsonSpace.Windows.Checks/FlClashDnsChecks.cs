using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunsonSpace.Windows;

internal static class FlClashDnsChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var runner = new FixtureRunner();
        var assets = FlClashDnsAssets.FromApplicationRoot(@"D:\fixture's\app");
        var service = new FlClashDnsService(runner, assets);
        var request = new FlClashDnsRequest(@"D:\fixture's\data", @"D:\fixture's\FlClash.exe", "Proxy's $variable;exit 99", OfflineFixture: true);
        runner.Response = Response("Preview", "PreviewReady", 0);
        var result = await service.PreviewAsync(request);
        check(result.Status == FlClashDnsStatus.PreviewReady && !result.SettingsSaved && !result.NetworkVerified && runner.Last!.Action == FlClashDnsAction.Preview, "FlClash preview preserves explicit action and no network-success claim");
        var optionalReport = JsonNode.Parse(runner.Response.ReportJson!)!.AsObject();
        optionalReport["scope"] = null; optionalReport["subscriptionCount"] = null;
        runner.Response = runner.Response with { ReportJson = optionalReport.ToJsonString() };
        result = await service.PreviewAsync(request);
        check(result.Status == FlClashDnsStatus.PreviewReady && result.Scope is null && result.SubscriptionCount is null,
            "legacy or null optional coverage does not imply configured all-subscription DNS");
        check(result.ProxyPorts!.Single().BindAddress is null && result.ProxyPorts!.Single().RequiresAuthentication is null, "FlClash unknown binding and authentication are not assumed safe for account proxy");
        check(result.ConfigurationStatus == FlClashConfigurationStatus.Unknown && result.ApplicationDiscoveryStatus == FlClashApplicationDiscoveryStatus.Unknown, "older FlClash schema reports retain groups with optional discovery fields unknown");
        runner.Response = WithDiscovery(Response("Preview", "PreviewReady", 0), "Ready", "NotFound");
        result = await service.PreviewAsync(request);
        check(result.Status == FlClashDnsStatus.PreviewReady && result.ConfigurationStatus == FlClashConfigurationStatus.Ready && result.ApplicationDiscoveryStatus == FlClashApplicationDiscoveryStatus.NotFound && result.Groups.Count == 2 && result.SelectedGroup == "A" && result.ProfileId == 1, "FlClash configuration and chosen group remain usable when executable discovery is absent");
        runner.Response = WithDiscovery(Response("Preview", "Failed", 1), "Ready", "Found");
        result = await service.PreviewAsync(request);
        check(result.Status == FlClashDnsStatus.Failed && result.ConfigurationStatus == FlClashConfigurationStatus.Ready && result.Groups.Count == 2 && result.ApplicationPath == @"D:\fixture\FlClash.exe", "repair conflict never discards already-read FlClash groups and installation metadata");
        runner.Response = Response("Apply", "SettingsSaved", 0, saved: true);
        result = await service.ApplyAsync(request);
        check(result.Completed && result.SettingsSaved && !result.ReloadConfirmed && !result.GeneratedConfigVerified, "FlClash saved settings remain separate from reload and generated validation");
        runner.Response = Response("Apply", "ReloadUnconfirmed", 0, saved: true);
        check((await service.ApplyAsync(request)).Status == FlClashDnsStatus.ReloadUnconfirmed, "FlClash unconfirmed reload is retained as an explicit status");
        runner.Response = Response("Apply", "GeneratedConfigVerified", 0, saved: true, reloaded: true, verified: true);
        result = await service.ApplyAsync(request);
        check(result.ReloadConfirmed && result.GeneratedConfigVerified && !result.NetworkVerified, "FlClash generated configuration is never DNS operational proof");
        runner.Response = Response("Restore", "Restored", 0, saved: true);
        check((await service.RestoreAsync(request)).Status == FlClashDnsStatus.Restored, "FlClash restore action has its own coherent result");
        runner.Response = Response("Preview", "SelectionRequired", 2);
        result = await service.PreviewAsync(request);
        check(result.Status == FlClashDnsStatus.SelectionRequired && result.Groups.Count == 2 && !result.Completed, "FlClash candidates are structured without interactive prompts");
        runner.Response = Response("Apply", "SettingsSaved", 1, saved: true);
        check((await service.ApplyAsync(request)).Status == FlClashDnsStatus.Uncertain, "FlClash contradictory nonzero saved result is uncertain");
        runner.Response = Response("Preview", "PreviewReady", 0) with { ExitCode = 1 };
        check((await service.PreviewAsync(request)).Status == FlClashDnsStatus.Uncertain, "FlClash exit code and report disagreement cannot pass");
        runner.Response = new(0, null, true);
        check((await service.ApplyAsync(request)).Status == FlClashDnsStatus.Uncertain, "FlClash missing report after dispatch preserves uncertainty");
        runner.Response = new(null, null, false);
        check((await service.ApplyAsync(request)).Status == FlClashDnsStatus.Failed, "FlClash declined or undispatched call is not saved");
        runner.Response = new(0, "{", true);
        check((await service.ApplyAsync(request)).Status == FlClashDnsStatus.Uncertain, "FlClash corrupt report does not infer success");
        runner.Response = Response("Apply", "GeneratedConfigVerified", 0, saved: true, reloaded: true, verified: true, network: true);
        check((await service.ApplyAsync(request)).Status == FlClashDnsStatus.Uncertain, "FlClash bridge rejects unsupported network verification claims");
        var source = Encoding.Unicode.GetString(Convert.FromBase64String(WindowsFlClashDnsRunner.BuildEncodedCommand(new(FlClashDnsAction.Apply, request, assets), @"D:\reports\report.json")));
        check(source.Contains("-ProxyGroup 'Proxy''s $variable;exit 99'", StringComparison.Ordinal) && source.Contains("-DataDir 'D:\\fixture''s\\data'", StringComparison.Ordinal), "FlClash PowerShell path and group remain safely quoted data");
        check(source.Contains("-Offline -NoRestart", StringComparison.Ordinal) && source.Contains("-Action 'Apply'", StringComparison.Ordinal), "FlClash fixtures cannot restart production software");
        var count = runner.Calls;
        check((await service.PreviewAsync(new("relative"))).Status == FlClashDnsStatus.Failed && runner.Calls == count, "FlClash invalid roots fail before runner dispatch");
    }
    private static FlClashDnsExecution Response(string action, string status, int code, bool saved = false, bool reloaded = false, bool verified = false, bool network = false) => new(code, JsonSerializer.Serialize(new
    {
        schemaVersion = 1, action, status, groups = new[] { "A", "B" }, appCandidates = new[] { @"D:\fixture\FlClash.exe" }, selectedGroup = "A",
        ports = new Dictionary<string, int?> { ["mixed-port"] = 7890 }, changedFields = new[] { "patchClashConfig.dns.enable" }, backupDirectory = @"D:\fixture\dns-backups\one",
        proxyPorts = new[] { new { kind = "mixed", port = 7890, bindAddress = (string?)null, requiresAuthentication = (bool?)null } },
        settingsSaved = saved, reloadConfirmed = reloaded, generatedConfigVerified = verified, networkVerified = network, exitCode = code, message = "fixture"
    }), true);
    private static FlClashDnsExecution WithDiscovery(FlClashDnsExecution execution, string configuration, string application)
    {
        var report = JsonNode.Parse(execution.ReportJson!)!.AsObject();
        report["configurationStatus"] = configuration; report["applicationDiscoveryStatus"] = application;
        report["applicationPath"] = application == "Found" ? @"D:\fixture\FlClash.exe" : null; report["profileId"] = 1;
        return execution with { ReportJson = report.ToJsonString() };
    }
    private sealed class FixtureRunner : IFlClashDnsRunner
    {
        public FlClashDnsExecution Response { get; set; } = new(null, null, false);
        public FlClashDnsInvocation? Last { get; private set; }
        public int Calls { get; private set; }
        public Task<FlClashDnsExecution> ExecuteAsync(FlClashDnsInvocation invocation, CancellationToken cancellationToken) { Calls++; Last = invocation; return Task.FromResult(Response); }
    }
}
