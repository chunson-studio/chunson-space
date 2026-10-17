using System.Reflection;
using System.Text.RegularExpressions;
using FAswitch.Accounts;

namespace FAswitch.Desktop;

internal static class SupportReport
{
    public static string Create(string operation, string details, DateTimeOffset? occurred = null, params string?[] privateValues)
    {
        var version = typeof(SupportReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "未知";
        var text = $"椿生空间错误诊断\n版本：{version}\n系统：Windows {Environment.OSVersion.Version}\n记录时间：{(occurred ?? DateTimeOffset.Now):yyyy-MM-dd HH:mm:ss zzz}\n项目：{operation}\n{details}\n\n仅复制本次显示的诊断，不读取凭据、浏览器资料或完整日志；请检查后发给维护者。";
        return Sanitize(text, privateValues);
    }

    internal static string Sanitize(string text, params string?[] privateValues)
    {
        var value = ErrorRedaction.ResponseText(text);
        // Scrub structured credentials before redacting ordinary account/path values.
        value = Regex.Replace(value, "(?im)(authorization|proxy-authorization|cookie|set-cookie)\\s*[:=][^\\r\\n]*", "$1: <已隐去>");
        value = Regex.Replace(value, "(?i)([\"']?(?:access_token|refresh_token|id_token|session_token|token|client_secret|api[_-]?key|password|passwd|secret|code_verifier|account_id)[\"']?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;&}]+)", "$1<已隐去>");
        value = Regex.Replace(value, @"(?i)\bBearer\s+\S+", "Bearer <已隐去>");
        value = Regex.Replace(value, @"(?i)\bhttps?://[^\s<>\""']+", "<网址已隐去>");
        value = Regex.Replace(value, @"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n\""'<>；，]+", "<路径已隐去>");
        value = Regex.Replace(value, @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", "<邮箱已隐去>");
        value = Regex.Replace(value, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "<IP已隐去>");
        value = Regex.Replace(value, @"(?i)\b(?:[0-9a-f]{0,4}:){3,}[0-9a-f:]+\b", "<IP已隐去>");
        value = Regex.Replace(value, @"[A-Za-z0-9_\-.]{32,}", "<标识已隐去>");
        foreach (var secret in privateValues.Where(v => !string.IsNullOrWhiteSpace(v)).OrderByDescending(v => v!.Length))
        {
            // Boundary matching prevents a short profile name from erasing unrelated diagnostics.
            value = Regex.Replace(value, @"(?<![\p{L}\p{N}_])" + Regex.Escape(secret!) + @"(?![\p{L}\p{N}_])", "<账号已隐去>", RegexOptions.IgnoreCase);
        }
        return value.Length <= 16000 ? value : value[..16000] + "\n<内容过长，已截断>";
    }

    internal static bool TryCopy(string report, Action<string> write)
    {
        try { write(report); return true; }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or System.Threading.ThreadStateException) { return false; }
    }
}
