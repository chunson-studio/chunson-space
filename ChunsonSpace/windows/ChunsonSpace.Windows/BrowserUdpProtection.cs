using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChunsonSpace.Windows;

public sealed record BrowserUdpProtectionResult(bool RulesVerified, string Message, IReadOnlyList<string> ProgramPaths, string? ReportPath = null);
public interface IBrowserUdpProtection
{
    Task<BrowserUdpProtectionResult> ApplyAsync(CancellationToken token = default);
}

public sealed class WindowsBrowserUdpProtection : IBrowserUdpProtection
{
    public static string Script
    {
        get
        {
            using var stream = typeof(WindowsBrowserUdpProtection).Assembly.GetManifestResourceStream("ChunsonSpace.Windows.BrowserUdpProtection.ps1")
                ?? throw new IOException("缺少浏览器网络保护组件。");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }

    public static BrowserUdpProtectionResult Parse(int exitCode, string json, string? reportPath = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var report = document.RootElement;
            var paths = report.GetProperty("programPaths").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
            if (exitCode != 0 || report.GetProperty("action").GetString() != "Apply" || report.GetProperty("status").GetString() != "Configured"
                || !report.GetProperty("rulesVerified").GetBoolean() || report.GetProperty("networkVerified").GetBoolean()
                || paths.Length == 0 || paths.Any(path => !Path.IsPathFullyQualified(path)
                    || !(Path.GetFileName(path).Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(path).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase))))
                return new(false, FailureMessage(report, reportPath), paths, reportPath);
            return new(true, "Chrome / Edge 的系统 UDP 规则已生效，覆盖这些程序的已有和新建配置；网页检测结果需另行确认。", paths, reportPath);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return new(false, "无法读取浏览器防泄露结果，请重新执行网络修复。", [], reportPath); }
    }

    private static string FailureMessage(JsonElement report, string? reportPath)
    {
        var error = report.TryGetProperty("error", out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
        var detail = error switch
        {
            "Chrome or Edge was not found. Supply exact ProgramPaths." => "未找到 Chrome 或 Edge，请安装浏览器后重试；便携版需要指定实际程序路径。",
            "Windows Firewall is disabled or local rules are not accepted." => "Windows 防火墙已关闭或不接受本地规则。请检查防火墙及本地规则设置后重试。",
            "An existing rule conflicts with this repair; no rule was replaced." => "已有同名浏览器防火墙规则与修复冲突，未覆盖。请检查 Windows 防火墙中的 Chunson Switch 浏览器规则后重试。",
            "Saved and effective firewall rules did not match." => "保存的浏览器规则未在有效规则中确认。请检查 Windows 防火墙状态和规则策略后重试。",
            "Expected an existing absolute chrome.exe or msedge.exe path." => "浏览器程序位置无效，请检查 Chrome 或 Edge 的实际安装位置。",
            _ => ""
        };
        if (detail.Length == 0 && error.Length > 0)
        {
            var value = error.Length > 2000 ? error[..2000] : error;
            value = Regex.Replace(value, @"(?i)(://)[^/@\s]+@", "$1<已隐去>@");
            value = Regex.Replace(value, @"(?i)\b(Bearer|Basic)\s+\S+", "$1 <已隐去>");
            value = Regex.Replace(value, @"(?i)\b(authorization|access_token|refresh_token|id_token|api[_-]?key|token|password|secret)(?:[""']?\s*[:=]\s*[""']?|\s+)[^\s""',;]+", "$1=<已隐去>");
            value = Regex.Replace(value, @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", "<邮箱已隐去>");
            value = Regex.Replace(value, @"[A-Za-z0-9_\-.]{40,}", "<已隐去>");
            value = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
            detail = "具体原因：" + (value.Length > 220 ? value[..220] : value) + "。请按此原因处理后重新执行网络修复。";
        }
        if (detail.Length == 0) detail = "结果与完成条件不符，请重新检查浏览器和 Windows 防火墙配置。";
        return "浏览器防泄露规则未确认。" + detail + (string.IsNullOrWhiteSpace(reportPath) ? "" : "\n结果记录：" + reportPath);
    }

    public async Task<BrowserUdpProtectionResult> ApplyAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChunsonSwitch", "network-runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var scriptPath = Path.Combine(root, "browser-udp.ps1");
        var reportPath = Path.Combine(root, "browser-udp.json");
        var script = Script;
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false), token);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法确认当前 Windows 用户。");
        static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var command = "$ErrorActionPreference='Stop'; if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -cne " + Literal(sid)
            + "){throw 'user-changed'}; if((Get-FileHash -LiteralPath " + Literal(scriptPath) + " -Algorithm SHA256).Hash -cne " + Literal(hash)
            + "){throw 'payload-changed'}; & ([ScriptBlock]::Create([IO.File]::ReadAllText(" + Literal(scriptPath) + "))) -Action Apply -ReportPath " + Literal(reportPath);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + WindowsPowerShellCommand.Encode(command),
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
        };
        token.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(start);
            if (process is null) return new(false, "未启动系统防火墙配置。", [], reportPath);
            // Once elevated writes are dispatched, retain the busy operation until that process really exits.
            await process.WaitForExitAsync(CancellationToken.None);
            if (!File.Exists(reportPath) || new FileInfo(reportPath).Length > 1024 * 1024)
                return new(false, "浏览器保护结果未确认，请重新检查。", [], reportPath);
            return Parse(process.ExitCode, await File.ReadAllTextAsync(reportPath, CancellationToken.None), reportPath);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or OperationCanceledException)
        { return new(false, "浏览器保护未完成或系统授权被取消；请重新执行网络修复。", [], reportPath); }
    }
}
