using System.Globalization;
using System.Text.RegularExpressions;

namespace ChunsonSpace.Core;

public static class ProfileRules
{
    public static string Name(string? name)
    {
        var value = (name ?? "").Trim();
        var stem = value.Split('.')[0].ToUpperInvariant();
        if (value.Length == 0 || value is "." or ".." || value.Equals(AccountBrowserProfiles.PoolName, StringComparison.OrdinalIgnoreCase) || value.EndsWith('.') ||
            value.Any(c => c < 32 || "\\/:*?\"<>|".Contains(c)) ||
            stem is "AUX" or "CON" or "NUL" or "PRN" || Regex.IsMatch(stem, "^(COM|LPT)[1-9]$"))
            throw new StorageException(StorageError.InvalidName, value, "名称不能为空，不能包含 Windows 非法字符、保留名或结尾点。");
        return value;
    }
    public static string NormalizeProxy(string? proxy)
    {
        var text = (proxy ?? "").Trim();
        if (text.Length == 0) return "";
        if (text.Contains('@')) throw Invalid("认证型代理不能直接填，请先转换成本机代理端口。");
        var parts = text.Split("://", 2, StringSplitOptions.None);
        var scheme = parts.Length == 2 ? parts[0].ToLowerInvariant() : "http";
        var hostPort = parts[^1].Trim().Trim('/');
        if (scheme is not ("http" or "https" or "socks5")) throw Invalid("代理仅支持 http、https、socks5。");
        var colon = hostPort.LastIndexOf(':');
        if (colon < 1 || hostPort.Contains('/') || hostPort.Contains(' ') ||
            !hostPort[(colon + 1)..].All(char.IsAsciiDigit) ||
            !int.TryParse(hostPort[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is <= 0 or > 65535 ||
            hostPort[..colon].Any(c => c > 127 || char.IsControl(c)))
            throw Invalid("代理必须写成 ASCII 主机名:端口，端口范围为 1–65535。");
        return $"{scheme}://{hostPort}";
    }
    public static bool ValidTimezone(string value) => value.Length <= 64 && Regex.IsMatch(value, "\\A[A-Za-z][A-Za-z0-9_+\\-]*(?:/[A-Za-z0-9_+\\-]+)*\\z");
    public static string? WindowsTimezoneId(string iana)
        => ValidTimezone(iana) && iana.Contains('/') && TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var id) ? id : null;
    public static bool ValidLocale(string value) => Regex.IsMatch(value, "\\A[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*\\z");
    public static EnvironmentSettings Environment(EnvironmentSettings environment)
    {
        var tz = environment.Timezone.Trim();
        var locale = environment.Locale.Trim();
        if (tz.Length > 0 && !ValidTimezone(tz)) throw Invalid("时区写法不合法。");
        if (locale.Length > 0 && !ValidLocale(locale)) throw Invalid("语言标签写法不合法。");
        return environment with { Proxy = environment.FollowSystemProxy ? "" : NormalizeProxy(environment.Proxy), Timezone = tz, Locale = locale };
    }
    public static string BaseUrl(string value)
    {
        value = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || value.Any(char.IsWhiteSpace))
            throw Invalid("接口地址要以 http:// 或 https:// 开头。");
        return value;
    }
    private static StorageException Invalid(string message) => new(StorageError.InvalidValue, "", message);
}
