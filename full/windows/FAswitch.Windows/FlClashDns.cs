using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FAswitch.Windows;

public enum FlClashDnsAction { Preview, Apply, Restore }
public enum FlClashDnsStatus { PreviewReady, SelectionRequired, SettingsSaved, ReloadUnconfirmed, GeneratedConfigVerified, Restored, Failed, Uncertain }
public enum FlClashConfigurationStatus { Unknown, NotRead, Missing, Invalid, Ready }
public enum FlClashApplicationDiscoveryStatus { Unknown, Found, NotFound, Ambiguous, InvalidPath }
public sealed record FlClashDnsAssets(string ScriptPath, string PolicyPath, string LibraryRoot, string HelperPath)
{
    public static FlClashDnsAssets FromApplicationRoot(string root) => new(
        Path.Combine(root, "scripts", "proxy", "FlClash-DNS.ps1"),
        Path.Combine(root, "Resources", "dns-policy.json"), root,
        Path.Combine(root, "scripts", "proxy", "lib", "FlDns.cs"));
}
public sealed record FlClashDnsRequest(string DataDirectory, string? ApplicationPath = null, string? ProxyGroup = null,
    string? BackupDirectory = null, bool OfflineFixture = false, bool AllSubscriptions = false);
public sealed record FlClashProxyPort(string Kind, int Port, string? BindAddress, bool? RequiresAuthentication);
public sealed record FlClashDnsResult(FlClashDnsStatus Status, string Message, IReadOnlyList<string> Groups,
    IReadOnlyList<string> ApplicationCandidates, string? SelectedGroup, IReadOnlyDictionary<string, int?> Ports,
    IReadOnlyList<string> ChangedFields, string? BackupDirectory, bool SettingsSaved, bool ReloadConfirmed,
    bool GeneratedConfigVerified, string? ReportPath = null, IReadOnlyList<FlClashProxyPort>? ProxyPorts = null,
    FlClashConfigurationStatus ConfigurationStatus = FlClashConfigurationStatus.Unknown,
    FlClashApplicationDiscoveryStatus ApplicationDiscoveryStatus = FlClashApplicationDiscoveryStatus.Unknown,
    string? ApplicationPath = null, long? ProfileId = null, IReadOnlyList<string>? NodeTargets = null, string? SelectionNote = null,
    string? Scope = null, int? SubscriptionCount = null)
{
    public bool NetworkVerified => false;
    public bool Completed => Status is not (FlClashDnsStatus.Failed or FlClashDnsStatus.Uncertain or FlClashDnsStatus.SelectionRequired);
}
public sealed record FlClashDnsInvocation(FlClashDnsAction Action, FlClashDnsRequest Request, FlClashDnsAssets Assets);
public sealed record FlClashDnsExecution(int? ExitCode, string? ReportJson, bool Dispatched, string? ReportPath = null);
public interface IFlClashDnsRunner
{
    Task<FlClashDnsExecution> ExecuteAsync(FlClashDnsInvocation invocation, CancellationToken cancellationToken);
}

public sealed class FlClashDnsService(IFlClashDnsRunner runner, FlClashDnsAssets assets)
{
    public Task<FlClashDnsResult> PreviewAsync(FlClashDnsRequest request, CancellationToken cancellationToken = default) => RunAsync(FlClashDnsAction.Preview, request, cancellationToken);
    public Task<FlClashDnsResult> ApplyAsync(FlClashDnsRequest request, CancellationToken cancellationToken = default) => RunAsync(FlClashDnsAction.Apply, request, cancellationToken);
    public Task<FlClashDnsResult> RestoreAsync(FlClashDnsRequest request, CancellationToken cancellationToken = default) => RunAsync(FlClashDnsAction.Restore, request, cancellationToken);
    private async Task<FlClashDnsResult> RunAsync(FlClashDnsAction action, FlClashDnsRequest request, CancellationToken cancellationToken)
    {
        FlClashDnsExecution execution;
        try
        {
            if (string.IsNullOrWhiteSpace(request.DataDirectory) || !Path.IsPathFullyQualified(request.DataDirectory))
                return Empty(FlClashDnsStatus.Failed, "请提供完整数据目录；FlClash 本机模式仅支持标准数据目录。");
            execution = await runner.ExecuteAsync(new(action, request, assets), cancellationToken);
        }
        catch (Exception) { return Empty(FlClashDnsStatus.Uncertain, "调用未得到可验证的报告；请保留备份，重新预览当前配置。"); }
        if (!execution.Dispatched) return Empty(FlClashDnsStatus.Failed, "调用未派发，配置未由此入口修改。", execution.ReportPath);
        if (execution.ExitCode is null || string.IsNullOrWhiteSpace(execution.ReportJson))
            return Empty(FlClashDnsStatus.Uncertain, "调用结果或报告缺失，配置状态待确认。", execution.ReportPath);
        try
        {
            using var document = JsonDocument.Parse(execution.ReportJson);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("action").GetString() != action.ToString()
                || root.GetProperty("exitCode").GetInt32() != execution.ExitCode || root.GetProperty("networkVerified").GetBoolean())
                return Empty(FlClashDnsStatus.Uncertain, "报告与执行结果不一致，状态待确认。", execution.ReportPath);
            if (!Enum.TryParse<FlClashDnsStatus>(root.GetProperty("status").GetString(), out var status) || !Enum.IsDefined(status))
                return Empty(FlClashDnsStatus.Uncertain, "报告阶段无法识别。", execution.ReportPath);
            var saved = root.GetProperty("settingsSaved").GetBoolean();
            var reloaded = root.GetProperty("reloadConfirmed").GetBoolean();
            var verified = root.GetProperty("generatedConfigVerified").GetBoolean();
            bool coherent = status switch
            {
                FlClashDnsStatus.SelectionRequired => execution.ExitCode == 2 && !saved && !reloaded && !verified,
                FlClashDnsStatus.Failed => execution.ExitCode == 1 && !saved,
                FlClashDnsStatus.PreviewReady => execution.ExitCode == 0 && action == FlClashDnsAction.Preview && !saved && !reloaded && !verified,
                FlClashDnsStatus.SettingsSaved or FlClashDnsStatus.ReloadUnconfirmed => execution.ExitCode == 0 && action == FlClashDnsAction.Apply && saved && !reloaded,
                FlClashDnsStatus.GeneratedConfigVerified => execution.ExitCode == 0 && action == FlClashDnsAction.Apply && saved && reloaded && verified,
                FlClashDnsStatus.Restored => execution.ExitCode == 0 && action == FlClashDnsAction.Restore && saved,
                _ => false
            };
            if (!coherent) return Empty(FlClashDnsStatus.Uncertain, "设置保存或重载阶段尚不能确认，请重新预览。", execution.ReportPath);
            var ports = new Dictionary<string, int?>();
            foreach (var port in root.GetProperty("ports").EnumerateObject())
                ports[port.Name] = port.Value.ValueKind == JsonValueKind.Null ? null : port.Value.GetInt32();
            var proxyPorts = new List<FlClashProxyPort>();
            if (root.TryGetProperty("proxyPorts", out var candidates))
                foreach (var candidate in candidates.EnumerateArray())
                {
                    var auth = candidate.GetProperty("requiresAuthentication");
                    proxyPorts.Add(new(candidate.GetProperty("kind").GetString() ?? "", candidate.GetProperty("port").GetInt32(),
                        Optional(candidate, "bindAddress"), auth.ValueKind == JsonValueKind.Null ? null : auth.GetBoolean()));
                }
            long? profileId = root.TryGetProperty("profileId", out var profile) && profile.ValueKind == JsonValueKind.Number && profile.TryGetInt64(out var id) ? id : null;
            return new(status, root.GetProperty("message").GetString() ?? "", Strings(root, "groups"), Strings(root, "appCandidates"),
                Optional(root, "selectedGroup"), ports, Strings(root, "changedFields"), Optional(root, "backupDirectory"), saved, reloaded, verified, execution.ReportPath, proxyPorts,
                OptionalEnum<FlClashConfigurationStatus>(root, "configurationStatus"), OptionalEnum<FlClashApplicationDiscoveryStatus>(root, "applicationDiscoveryStatus"),
                root.TryGetProperty("applicationPath", out var app) && app.ValueKind == JsonValueKind.String ? app.GetString() : null, profileId,
                root.TryGetProperty("nodeTargets", out _) ? Strings(root, "nodeTargets") : [],
                root.TryGetProperty("selectionNote", out var note) && note.ValueKind == JsonValueKind.String ? note.GetString() : null,
                root.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String ? scope.GetString() : null,
                root.TryGetProperty("subscriptionCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var number) ? number : null);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return Empty(FlClashDnsStatus.Uncertain, "结构化报告损坏或不完整，状态待确认。", execution.ReportPath); }
    }
    private static string[] Strings(JsonElement root, string property) => root.GetProperty(property).EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
    private static string? Optional(JsonElement root, string property) => root.GetProperty(property).ValueKind == JsonValueKind.Null ? null : root.GetProperty(property).GetString();
    private static T OptionalEnum<T>(JsonElement root, string property) where T : struct, Enum => root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String && Enum.TryParse<T>(value.GetString(), out var parsed) && Enum.IsDefined(parsed) ? parsed : default;
    private static FlClashDnsResult Empty(FlClashDnsStatus status, string message, string? report = null) => new(status, message, [], [], null, new Dictionary<string, int?>(), [], null, false, false, false, report);
}

public sealed class WindowsFlClashDnsRunner(string? reportRoot = null, int timeoutMilliseconds = 120000) : IFlClashDnsRunner
{
    public static string BuildEncodedCommand(FlClashDnsInvocation invocation, string reportPath)
    {
        var source = "& ([ScriptBlock]::Create([IO.File]::ReadAllText(" + Literal(invocation.Assets.ScriptPath) + "))) -AssetRoot "
            + Literal(Path.GetDirectoryName(invocation.Assets.ScriptPath)!) + " -Action " + Literal(invocation.Action.ToString())
            + " -NonInteractive -ReportPath " + Literal(reportPath) + " -DataDir " + Literal(invocation.Request.DataDirectory)
            + " -PolicyPath " + Literal(invocation.Assets.PolicyPath) + " -LibraryRoot " + Literal(invocation.Assets.LibraryRoot)
            + " -CsHelperPath " + Literal(invocation.Assets.HelperPath);
        if (invocation.Request.ApplicationPath is { Length: > 0 } app) source += " -AppPath " + Literal(app);
        if (invocation.Request.ProxyGroup is { Length: > 0 } group) source += " -ProxyGroup " + Literal(group);
        if (invocation.Request.BackupDirectory is { Length: > 0 } backup) source += " -BackupDir " + Literal(backup);
        if (invocation.Request.OfflineFixture) source += " -Offline -NoRestart";
        if (invocation.Request.AllSubscriptions) source += " -AllSubscriptions";
        // All user data is single-quoted before UTF16 encoding; no path or group becomes PowerShell code.
        return WindowsPowerShellCommand.Encode(source);
    }
    public async Task<FlClashDnsExecution> ExecuteAsync(FlClashDnsInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = Path.Combine(reportRoot ?? Path.Combine(Path.GetTempPath(), "ChunsonSwitch-FlClashDns"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var report = Path.Combine(folder, "report.json");
        var elevated = (invocation.Action is FlClashDnsAction.Apply or FlClashDnsAction.Restore) && !invocation.Request.OfflineFixture;
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoLogo -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand " + BuildEncodedCommand(invocation, report),
            UseShellExecute = elevated, Verb = elevated ? "runas" : "", CreateNoWindow = !elevated, WindowStyle = ProcessWindowStyle.Hidden
        };
        Process? process;
        try { process = Process.Start(start); }
        catch (Exception) { return new(null, null, false, report); }
        if (process is null) return new(null, null, false, report);
        using (process)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMilliseconds);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (Exception) { return new(null, null, true, report); } // A dispatched transaction is never killed on a UI timeout.
            string? json = null;
            try { if (new FileInfo(report).Length <= 1024 * 1024) json = await File.ReadAllTextAsync(report, cancellationToken); }
            catch (Exception) { /* Missing or inaccessible report is explicitly uncertain. */ }
            return new(process.ExitCode, json, true, report);
        }
    }
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
