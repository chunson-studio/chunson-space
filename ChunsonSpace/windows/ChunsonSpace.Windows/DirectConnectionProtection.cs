using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using ChunsonSpace.Core;

namespace ChunsonSpace.Windows;

public enum DirectConnectionProtectionAction { Preview, Apply, Restore, Inspect }
public enum DirectConnectionProtectionStatus { PreviewReady, Configured, Restored, Failed, Uncertain, ProtectionStale, Disabled }
public sealed record DirectConnectionProtectionRequest(IReadOnlyList<string> ProgramPaths, string LocalProxy);
public sealed record DirectConnectionProtectionResult(DirectConnectionProtectionStatus Status, string Message, IReadOnlyList<string> ProgramPaths,
    bool RulesVerified = false, bool NetworkVerified = false, string? ReportPath = null)
{
    public bool Success => Status is DirectConnectionProtectionStatus.PreviewReady or DirectConnectionProtectionStatus.Configured or DirectConnectionProtectionStatus.Restored or DirectConnectionProtectionStatus.Disabled;
    public bool CanConfigure => Status is DirectConnectionProtectionStatus.PreviewReady or DirectConnectionProtectionStatus.ProtectionStale
        || Status == DirectConnectionProtectionStatus.Configured && RulesVerified;
}
public sealed record DirectConnectionProtectionExecution(int? ExitCode, string? ReportJson, string ReportPath);
public interface IDirectConnectionProtectionRunner
{
    Task<DirectConnectionProtectionExecution> ExecuteAsync(DirectConnectionProtectionAction action, DirectConnectionProtectionRequest request, CancellationToken cancellationToken);
}

public sealed class DirectConnectionProtectionService(IDirectConnectionProtectionRunner? runner = null)
{
    private readonly IDirectConnectionProtectionRunner runner = runner ?? new WindowsDirectConnectionProtectionRunner();
    public Task<DirectConnectionProtectionResult> PreviewAsync(DirectConnectionProtectionRequest request, CancellationToken token = default) => ExecuteAsync(DirectConnectionProtectionAction.Preview, request, token);
    public Task<DirectConnectionProtectionResult> ApplyAsync(DirectConnectionProtectionRequest request, CancellationToken token = default)
        => Task.FromResult(new DirectConnectionProtectionResult(DirectConnectionProtectionStatus.Failed, "系统防护功能已移除，不再创建禁止直连规则。", request.ProgramPaths));
    public Task<DirectConnectionProtectionResult> RestoreAsync(DirectConnectionProtectionRequest request, CancellationToken token = default) => ExecuteAsync(DirectConnectionProtectionAction.Restore, request, token);
    public Task<DirectConnectionProtectionResult> InspectAsync(DirectConnectionProtectionRequest request, CancellationToken token = default) => ExecuteAsync(DirectConnectionProtectionAction.Inspect, request, token);
    private async Task<DirectConnectionProtectionResult> ExecuteAsync(DirectConnectionProtectionAction action, DirectConnectionProtectionRequest request, CancellationToken token)
    {
        if (action is DirectConnectionProtectionAction.Preview or DirectConnectionProtectionAction.Apply)
        {
            if (!Uri.TryCreate(request.LocalProxy, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")
                || endpoint.UserInfo.Length > 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0
                || !(endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)))
                return new(DirectConnectionProtectionStatus.Failed, "禁止直连需要明确的本机 HTTP 代理端口，请先选择本地代理。", []);
            if (request.ProgramPaths.Count == 0 || request.ProgramPaths.Any(path => !Path.IsPathFullyQualified(path) || !File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                return new(DirectConnectionProtectionStatus.Failed, "无法确认 Codex 与 Chunson Space 的程序路径，未修改防火墙。", request.ProgramPaths);
        }
        try
        {
            var execution = await runner.ExecuteAsync(action, request, token);
            if (execution.ExitCode is null || execution.ReportJson is null) return new(DirectConnectionProtectionStatus.Uncertain, "防火墙操作结果尚未确认，请重新预览。", request.ProgramPaths, ReportPath: execution.ReportPath);
            using var json = JsonDocument.Parse(execution.ReportJson);
            var root = json.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("action").GetString() != action.ToString() || root.GetProperty("networkVerified").GetBoolean())
                throw new InvalidDataException("保护报告不一致");
            var status = Enum.Parse<DirectConnectionProtectionStatus>(root.GetProperty("status").GetString() ?? "", ignoreCase: false);
            var verified = root.GetProperty("rulesVerified").GetBoolean();
            bool coherent = action switch
            {
                DirectConnectionProtectionAction.Preview => status is DirectConnectionProtectionStatus.PreviewReady or DirectConnectionProtectionStatus.Configured or DirectConnectionProtectionStatus.ProtectionStale or DirectConnectionProtectionStatus.Failed or DirectConnectionProtectionStatus.Uncertain,
                DirectConnectionProtectionAction.Apply => status is DirectConnectionProtectionStatus.Configured or DirectConnectionProtectionStatus.Failed or DirectConnectionProtectionStatus.Uncertain,
                DirectConnectionProtectionAction.Inspect => status is DirectConnectionProtectionStatus.Disabled or DirectConnectionProtectionStatus.Configured or DirectConnectionProtectionStatus.ProtectionStale or DirectConnectionProtectionStatus.Failed or DirectConnectionProtectionStatus.Uncertain,
                _ => status is DirectConnectionProtectionStatus.Restored or DirectConnectionProtectionStatus.Failed or DirectConnectionProtectionStatus.Uncertain
            };
            if (!coherent || !Enum.IsDefined(status) || (status == DirectConnectionProtectionStatus.Configured && (!verified || action is not (DirectConnectionProtectionAction.Apply or DirectConnectionProtectionAction.Preview or DirectConnectionProtectionAction.Inspect)))
                || (status == DirectConnectionProtectionStatus.Disabled && verified)
                || (execution.ExitCode != 0 && status is not (DirectConnectionProtectionStatus.Failed or DirectConnectionProtectionStatus.Uncertain))) throw new InvalidDataException("保护阶段不一致");
            var paths = root.GetProperty("programPaths").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
            if (action != DirectConnectionProtectionAction.Restore && !paths.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(request.ProgramPaths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("保护目标不一致");
            return new(status, root.GetProperty("message").GetString() ?? "", paths, verified, false, execution.ReportPath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(DirectConnectionProtectionStatus.Uncertain, "无法确认禁止直连配置；不会把配置当作实际网络验证。", request.ProgramPaths); }
    }
}

public static class DirectConnectionProtectionTargets
{
    public static IReadOnlyList<string> Discover(DesktopTarget? desktop, string managerExecutable, string? localAppData = null)
    {
        if (desktop is null || !File.Exists(desktop.Executable) || !File.Exists(managerExecutable)) return [];
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(desktop.Executable), Path.GetFullPath(managerExecutable) };
        var roots = new[] { Path.GetDirectoryName(desktop.Executable)!, Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex") };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            PhysicalFileStorage.RejectLinks(root);
            foreach (var path in Directory.EnumerateFiles(root, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            {
                PhysicalFileStorage.RejectLinks(path);
                result.Add(Path.GetFullPath(path));
                if (result.Count > 512) throw new IOException("Codex 程序目标过多，无法确认保护范围");
            }
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public sealed class WindowsDirectConnectionProtectionRunner(string? reportRoot = null) : IDirectConnectionProtectionRunner
{
    public static string Script
    {
        get
        {
            using var stream = typeof(WindowsDirectConnectionProtectionRunner).Assembly.GetManifestResourceStream("ChunsonSpace.Windows.DirectConnectionProtection.ps1") ?? throw new IOException("缺少应用防直连脚本");
            using var reader = new StreamReader(stream, Encoding.UTF8); return reader.ReadToEnd();
        }
    }
    public static string BuildEncodedCommand(DirectConnectionProtectionAction action, DirectConnectionProtectionRequest request, string report)
    {
        var directory = Path.GetDirectoryName(report)!;
        var scriptPath = Path.Combine(directory, "protection.ps1");
        var requestPath = Path.Combine(directory, "request.json");
        var scriptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Script)));
        var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var source = "$ErrorActionPreference='Stop'; if((Get-FileHash -LiteralPath " + Literal(scriptPath) + " -Algorithm SHA256).Hash -ne '" + scriptHash
            + "' -or (Get-FileHash -LiteralPath " + Literal(requestPath) + " -Algorithm SHA256).Hash -ne '" + requestHash + "'){throw 'payload-changed'}; & ([ScriptBlock]::Create([IO.File]::ReadAllText("
            + Literal(scriptPath) + "))) -Action '" + action + "' -RequestJson ([IO.File]::ReadAllText(" + Literal(requestPath) + ")) -ReportPath " + Literal(report);
        return WindowsPowerShellCommand.Encode(source);
    }
    public async Task<DirectConnectionProtectionExecution> ExecuteAsync(DirectConnectionProtectionAction action, DirectConnectionProtectionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(reportRoot ?? Path.Combine(Path.GetTempPath(), "ChunsonSwitch-Protection"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var report = Path.Combine(directory, "report.json");
        await File.WriteAllTextAsync(Path.Combine(directory, "protection.ps1"), Script, new UTF8Encoding(false), cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(directory, "request.json"), JsonSerializer.SerializeToUtf8Bytes(request), cancellationToken);
        bool elevated = action is DirectConnectionProtectionAction.Apply or DirectConnectionProtectionAction.Restore;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + BuildEncodedCommand(action, request, report),
            UseShellExecute = elevated, Verb = elevated ? "runas" : "", CreateNoWindow = !elevated, WindowStyle = ProcessWindowStyle.Hidden
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null) return new(null, null, report);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(120000);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { return new(null, null, report); }
            return new(process.ExitCode, new FileInfo(report).Length <= 1024 * 1024 ? await File.ReadAllTextAsync(report, CancellationToken.None) : null, report);
        }
        catch (Exception) { return new(null, null, report); }
    }
}
