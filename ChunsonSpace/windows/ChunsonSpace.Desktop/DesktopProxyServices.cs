using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ChunsonSpace.Proxy;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

public interface IExternalUrlOpener { void Open(Uri uri); }
public sealed class WindowsExternalUrlOpener : IExternalUrlOpener
{
    public void Open(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
}
public sealed class OfflineUrlOpener : IExternalUrlOpener
{
    public List<Uri> Opened { get; } = [];
    public void Open(Uri uri) => Opened.Add(uri);
}

public sealed class DesktopProxyServices(ClashVergeDnsService clashVerge, FlClashDnsService flClash,
    IExternalUrlOpener urls, string flClashRoot, string clashVergeRoot, string backupRoot, bool fixture)
{
    public ClashVergeDnsService ClashVerge { get; } = clashVerge;
    public FlClashDnsService FlClash { get; } = flClash;
    public IExternalUrlOpener Urls { get; } = urls;
    public string FlClashRoot { get; } = flClashRoot;
    public string ClashVergeRoot { get; } = clashVergeRoot;
    public string BackupRoot { get; } = backupRoot;
    public bool IsFixture { get; } = fixture;
    public OfflineFlClashDnsRunner? FixtureFlClashRunner { get; private init; }
    public DirectConnectionProtectionService Protection { get; private init; } = null!;
    public OfflineProtectionRunner? FixtureProtectionRunner { get; private init; }
    public IBrowserUdpProtection BrowserUdp { get; private init; } = null!;
    public OfflineBrowserUdpProtection? FixtureBrowserUdp { get; private init; }
    public Func<string, Task<DirectConnectionProtectionRequest>> ProtectionRequest { get; private init; } = null!;
    public DesktopNodeServices? NodeTesting { get; private init; }
    public async Task<DirectConnectionProtectionResult> InspectProtectionAsync(CancellationToken token = default)
        => await Protection.InspectAsync(await ProtectionRequest(""), token);
    public bool HasConfiguration(ProxyClientKind kind) => kind == ProxyClientKind.FlClash
        ? File.Exists(Path.Combine(FlClashRoot, "database.sqlite"))
        : File.Exists(Path.Combine(ClashVergeRoot, "profiles.yaml"));
    public static DesktopProxyServices Create(DesktopWorkspace workspace)
    {
        var roaming = workspace.IsFixture ? Path.Combine(workspace.Root, "proxy-fixtures") : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        IFlClashDnsRunner runner = workspace.IsFixture ? new OfflineFlClashDnsRunner() : new WindowsFlClashDnsRunner();
        var protectionRunner = workspace.IsFixture ? new OfflineProtectionRunner() : null;
        var browserUdp = workspace.IsFixture ? new OfflineBrowserUdpProtection() : null;
        return new(new(client: workspace.IsFixture ? new OfflineClashVergeState() : null),
            new(runner, FlClashDnsAssets.FromApplicationRoot(AppContext.BaseDirectory)),
            workspace.IsFixture ? new OfflineUrlOpener() : new WindowsExternalUrlOpener(),
            Path.Combine(roaming, "com.follow", "clash"), Path.Combine(roaming, "io.github.clash-verge-rev.clash-verge-rev"),
            Path.Combine(workspace.Root, "proxy-backups"), workspace.IsFixture)
        {
            FixtureFlClashRunner = runner as OfflineFlClashDnsRunner,
            FixtureProtectionRunner = protectionRunner,
            Protection = new(protectionRunner),
            BrowserUdp = browserUdp is null ? new WindowsBrowserUdpProtection() : browserUdp,
            FixtureBrowserUdp = browserUdp,
            NodeTesting = new(workspace),
            ProtectionRequest = async proxy =>
            {
                if (workspace.IsFixture)
                {
                    var programs = Path.Combine(workspace.Root, "proxy-fixtures", "programs"); Directory.CreateDirectory(programs);
                    var paths = new[] { Path.Combine(programs, "ChunsonSwitch.exe"), Path.Combine(programs, "Codex.exe") };
                    foreach (var path in paths) if (!File.Exists(path)) await File.WriteAllTextAsync(path, "offline fixture; not executable");
                    return new(paths, proxy);
                }
                var target = workspace.NativeLaunch is null ? null : await workspace.NativeLaunch.FindTargetAsync(CancellationToken.None);
                return new(DirectConnectionProtectionTargets.Discover(target, DesktopWorkspace.ApplicationExecutable), proxy);
            }
        };
    }
}

public sealed class OfflineBrowserUdpProtection : IBrowserUdpProtection
{
    public int Calls { get; private set; }
    public bool FailApply { get; set; }
    public Task<BrowserUdpProtectionResult> ApplyAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); Calls++;
        return Task.FromResult(new BrowserUdpProtectionResult(!FailApply,
            FailApply ? "离线替身：浏览器保护失败。" : "离线替身：浏览器规则回读通过，未修改真实防火墙。", []));
    }
}

public sealed class OfflineProtectionRunner : IDirectConnectionProtectionRunner
{
    public List<DirectConnectionProtectionAction> Actions { get; } = [];
    public List<DirectConnectionProtectionRequest> Requests { get; } = [];
    public bool FailApply { get; set; }
    public DirectConnectionProtectionStatus PreviewStatus { get; set; } = DirectConnectionProtectionStatus.PreviewReady;
    public DirectConnectionProtectionStatus InspectionStatus { get; set; } = DirectConnectionProtectionStatus.Disabled;
    public Task<DirectConnectionProtectionExecution> ExecuteAsync(DirectConnectionProtectionAction action, DirectConnectionProtectionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Actions.Add(action); Requests.Add(request);
        var failed = FailApply && action == DirectConnectionProtectionAction.Apply;
        var status = failed ? DirectConnectionProtectionStatus.Failed : action == DirectConnectionProtectionAction.Apply ? DirectConnectionProtectionStatus.Configured : action == DirectConnectionProtectionAction.Restore ? DirectConnectionProtectionStatus.Restored : action == DirectConnectionProtectionAction.Inspect ? InspectionStatus : PreviewStatus;
        if (status == DirectConnectionProtectionStatus.Configured) InspectionStatus = status;
        if (status == DirectConnectionProtectionStatus.Restored) InspectionStatus = DirectConnectionProtectionStatus.Disabled;
        var report = JsonSerializer.Serialize(new { schema = 1, action = action.ToString(), status = status.ToString(), message = failed ? "离线防火墙替身：配置失败。" : "离线防火墙替身；未修改系统规则。", rulesVerified = status == DirectConnectionProtectionStatus.Configured, networkVerified = false, programPaths = request.ProgramPaths });
        return Task.FromResult(new DirectConnectionProtectionExecution(failed ? 1 : 0, report, ""));
    }
}

public sealed class OfflineClashVergeState : IClashVergeState
{
    public ProxyClientState State { get; set; } = ProxyClientState.Stopped;
    public ProxyClientState Read() => State;
}

// Only protocol and UI state are simulated here. Windows.Checks exercises the actual script against files.
public sealed class OfflineFlClashDnsRunner : IFlClashDnsRunner
{
    public List<FlClashDnsInvocation> Invocations { get; } = [];
    public FlClashDnsStatus ApplyStatus { get; set; } = FlClashDnsStatus.ReloadUnconfirmed;
    public FlClashDnsStatus? PreviewStatusOverride { get; set; }
    public string? Scope { get; set; }
    public int? SubscriptionCount { get; set; }
    public string[] ChangedFields { get; set; } = ["DNS 加密解析", "国内服务直连", "TUN 严格路由"];
    public IReadOnlyList<FlClashProxyPort> ProxyPorts { get; set; } = [new("mixed", 7890, "127.0.0.1", false)];
    public IReadOnlyList<string> Groups { get; set; } = ["代理选择"];
    public IReadOnlyList<string> NodeTargets { get; set; } = [];
    public string SelectionNote { get; set; } = "";
    public string? SelectedGroup { get; set; } = "代理选择";
    public IReadOnlyList<string> ApplicationCandidates { get; set; } = ["D:/fixture/FlClash.exe"];
    public TaskCompletionSource? Hold { get; set; }
    public async Task<FlClashDnsExecution> ExecuteAsync(FlClashDnsInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Invocations.Add(invocation);
        if (Hold is not null) await Hold.Task.WaitAsync(cancellationToken);
        var action = invocation.Action;
        var status = action == FlClashDnsAction.Preview ? Groups.Count == 0 ? FlClashDnsStatus.Failed : invocation.Request.ProxyGroup is null && SelectedGroup is null ? FlClashDnsStatus.SelectionRequired : FlClashDnsStatus.PreviewReady
            : action == FlClashDnsAction.Restore ? FlClashDnsStatus.Restored : ApplyStatus;
        if (action == FlClashDnsAction.Preview && PreviewStatusOverride is not null) status = PreviewStatusOverride.Value;
        var code = status == FlClashDnsStatus.Failed ? 1 : status == FlClashDnsStatus.SelectionRequired ? 2 : 0;
        var saved = action != FlClashDnsAction.Preview && status != FlClashDnsStatus.Failed;
        var reloaded = status == FlClashDnsStatus.GeneratedConfigVerified;
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, action = action.ToString(), exitCode = code,
            status = status.ToString(), message = "离线协议替身；未调用 FlClash、修改系统代理或验证网络。",
            configurationStatus = (Groups.Count == 0 ? FlClashConfigurationStatus.Missing : FlClashConfigurationStatus.Ready).ToString(),
            applicationDiscoveryStatus = (ApplicationCandidates.Count == 1 ? FlClashApplicationDiscoveryStatus.Found : ApplicationCandidates.Count == 0 ? FlClashApplicationDiscoveryStatus.NotFound : FlClashApplicationDiscoveryStatus.Ambiguous).ToString(),
            applicationPath = invocation.Request.ApplicationPath ?? (ApplicationCandidates.Count == 1 ? ApplicationCandidates[0] : null), profileId = 1,
            networkVerified = false, settingsSaved = saved, reloadConfirmed = reloaded, generatedConfigVerified = reloaded,
            groups = Groups, appCandidates = ApplicationCandidates, selectedGroup = invocation.Request.ProxyGroup ?? SelectedGroup,
            nodeTargets = NodeTargets, selectionNote = SelectionNote,
            ports = new Dictionary<string, int?> { ["mixed-port"] = 7890 },
            proxyPorts = ProxyPorts.Select(port => new { kind = port.Kind, port = port.Port, bindAddress = port.BindAddress, requiresAuthentication = port.RequiresAuthentication }),
            scope = Scope, subscriptionCount = SubscriptionCount,
            changedFields = ChangedFields, backupDirectory = invocation.Request.BackupDirectory
        });
        return new FlClashDnsExecution(code, json, true);
    }
}
