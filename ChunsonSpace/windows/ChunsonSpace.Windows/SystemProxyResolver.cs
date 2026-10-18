using ChunsonSpace.Core;
using Microsoft.Win32;

namespace ChunsonSpace.Windows;

public interface ISystemProxyResolver { string ResolveHttpProxy(); }
public sealed record SystemProxySettings(bool Enabled, string Server, string AutoConfigUrl = "", bool AutoDetect = false);
public sealed class SystemProxyException(string message) : Exception(message);

/// <summary>Reads a definite manual Windows proxy. PAC/WPAD never implies a DIRECT fallback.</summary>
public sealed class SystemProxyResolver(Func<SystemProxySettings>? readSettings = null) : ISystemProxyResolver
{
    private readonly Func<SystemProxySettings> readSettings = readSettings ?? ReadWindowsSettings;

    public string ResolveHttpProxy()
    {
        SystemProxySettings settings;
        try { settings = readSettings(); }
        catch (Exception) { throw new SystemProxyException("无法读取系统代理，请明确填写 HTTP 代理后重试；不会直连。"); }
        if (settings.AutoConfigUrl.Length > 0 || settings.AutoDetect)
            throw new SystemProxyException("系统使用 PAC 或自动发现，无法确认当前 HTTP 代理；请明确填写代理，软件不会直连。");
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Server))
            throw new SystemProxyException("系统没有启用明确的 HTTP 代理，请启用手动代理或填写专属代理；不会直连。");
        var server = settings.Server.Trim();
        if (server.Contains('='))
        {
            // OAuth/Codex destinations use HTTPS. An HTTP-only mapping would route them DIRECT.
            var mappings = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.Split('=', 2, StringSplitOptions.TrimEntries)).ToArray();
            if (mappings.Any(item => item.Length != 2)) throw Invalid();
            var https = mappings.Where(item => item[0].Equals("https", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (https.Length != 1) throw Invalid();
            server = https[0][1];
        }
        try
        {
            var proxy = ProfileRules.NormalizeProxy(server);
            if (!Uri.TryCreate(proxy, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/"
                || uri.Query.Length != 0 || uri.Fragment.Length != 0 || server.Any(char.IsWhiteSpace)
                || server.Contains(';') || server.Contains(',')) throw Invalid();
            return proxy;
        }
        catch (StorageException) { throw Invalid(); }
    }

    private static SystemProxyException Invalid() => new("系统代理不是明确的无认证 HTTP/HTTPS 主机和端口，请填写可用的专属 HTTP 代理；不会直连。");
    private static SystemProxySettings ReadWindowsSettings()
    {
        using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: false);
        var enabled = settings?.GetValue("ProxyEnable") is int value && value != 0;
        var autoDetect = settings?.GetValue("AutoDetect") is int automatic && automatic != 0;
        // WinINET also stores the automatic discovery flag in this connection blob.
        using var connections = settings?.OpenSubKey("Connections", writable: false);
        if (connections?.GetValue("DefaultConnectionSettings") is byte[] bytes && bytes.Length > 8)
            autoDetect |= (bytes[8] & 8) != 0;
        return new(enabled, settings?.GetValue("ProxyServer") as string ?? "", settings?.GetValue("AutoConfigURL") as string ?? "", autoDetect);
    }
}
