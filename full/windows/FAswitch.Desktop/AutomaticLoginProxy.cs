using System.IO;
using System.Net;
using FAswitch.Accounts;
using FAswitch.Core;
using FAswitch.Proxy;
using FAswitch.Windows;

namespace FAswitch.Desktop;

public sealed record LoginProxyConfiguration(string Client, string File, IReadOnlyList<string> CoreNames);
public sealed record LoginProxySelection(string Proxy, string Source, bool IndependentBrowserTimezone = false);

/// <summary>Use an explicit system proxy or an active core's configured main HTTP listener, including TUN-only setups.</summary>
public sealed class AutomaticLoginProxy(Func<string?> systemProxy, IReadOnlyList<LoginProxyConfiguration> configurations,
    Func<CancellationToken, Task<IReadOnlyList<LocalProxyListener>>> listeners)
{
    public static AutomaticLoginProxy ForFlClash(string root, Func<CancellationToken, Task<IReadOnlyList<LocalProxyListener>>> listeners)
        => new(() => null, [new("FlClash", Path.Combine(root, "config.yaml"), ["FlClashCore.exe"])], listeners);
    public static AutomaticLoginProxy ForClients(Func<string?> systemProxy, string flClashRoot, string clashVergeRoot,
        Func<CancellationToken, Task<IReadOnlyList<LocalProxyListener>>> listeners) => new(systemProxy,
            [new("FlClash", Path.Combine(flClashRoot, "config.yaml"), ["FlClashCore.exe"]),
             new("Clash Verge", Path.Combine(clashVergeRoot, "clash-verge.yaml"), ["verge-mihomo.exe", "mihomo.exe", "clash-meta.exe", "clash.exe"])], listeners);

    public async Task<LoginProxySelection> ResolveAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? configured = null;
        try { configured = systemProxy(); } catch (SystemProxyException) { }
        if (!string.IsNullOrWhiteSpace(configured)) return new(RequireProxy(configured), "系统当前代理");
        var active = await listeners(token);
        var candidates = new List<LoginProxySelection>();
        bool authentication = false;
        foreach (var source in configurations)
        {
            token.ThrowIfCancellationRequested();
            if (!active.Any(item => source.CoreNames.Contains(item.Process, StringComparer.OrdinalIgnoreCase)) || !File.Exists(source.File)) continue;
            try
            {
                PhysicalFileStorage.RejectLinks(source.File);
                if (new FileInfo(source.File).Length > 16 * 1024 * 1024) continue;
                var ports = MihomoInspector.InspectPorts(await File.ReadAllTextAsync(source.File, token));
                // Dedicated listeners can have different node bindings; never choose one by accident.
                foreach (var port in ports.Where(port => port.Source is "mixed-port" or "port").OrderBy(port => port.Source == "mixed-port" ? 0 : 1))
                {
                    var address = new Uri(RequireProxy(port.Address));
                    if (!IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip) || !IPAddress.IsLoopback(ip)) continue;
                    if (!active.Any(item => source.CoreNames.Contains(item.Process, StringComparer.OrdinalIgnoreCase) && item.Port == address.Port
                        && IPAddress.TryParse(item.Address, out var bound) && (bound.Equals(ip) || (bound.Equals(IPAddress.Any) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            || (bound.Equals(IPAddress.IPv6Any) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)))) continue;
                    if (port.RequiresAuthentication) { authentication = true; continue; }
                    candidates.Add(new(port.Address, source.Client)); break;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException or ProxyException or AccountException or UriFormatException) { }
        }
        var distinct = candidates.DistinctBy(item => item.Proxy).ToArray();
        if (distinct.Length == 1) return distinct[0];
        if (distinct.Length > 1) throw new AccountException(AccountError.ExitUnavailable, "检测到多个正在运行的代理软件。请只保留本次要用的一个，再点重试；不会自动切换出口。");
        throw new AccountException(AccountError.ExitUnavailable, authentication
            ? "当前代理端口需要密码，暂不能自动登录。请在代理软件提供无需密码的本地端口后重试；不会直连。"
            : "没有找到可用的本地代理入口。请打开 FlClash 或 Clash Verge 并连接节点，再点重试。TUN 可以保持开启，无需开启 Windows 系统代理；不会直连。");
    }

    private static string RequireProxy(string value)
    {
        var proxy = ProfileRules.NormalizeProxy(value);
        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0
            || uri.Host.Length == 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new AccountException(AccountError.ExitUnavailable, "当前代理入口无法使用，请重新连接代理软件后重试；不会直连。");
        return proxy;
    }
}
