using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

// IPinfo alone measures addresses. Location is a separate, explicit lookup of
// the measured primary IP; neither scoring nor country guesses supply a zone.
public sealed class IpInfoService(IAccountTransport transport)
{
    public static readonly Uri Ipv4Endpoint = new("https://v4.api.ipinfo.io/ip");
    public static readonly Uri Ipv6Endpoint = new("https://v6.api.ipinfo.io/ip");
    public const int AddressBodyLimit = 256;
    public const int LocationBodyLimit = 16384;
    private readonly AccountNetwork network = new(transport);
    public static Uri LocationEndpoint(string ip) => new("https://ipinfo.io/" + Uri.EscapeDataString(ip) + "/json");

    // Watchdog: one primary-address request, with IPv6 only when IPv4 is unavailable.
    // The unmeasured family is explicitly unknown, never a claimed fresh dual-stack result.
    public async Task<ExitAddresses> ProbePrimaryAsync(string resolvedProxy, CancellationToken token = default)
    {
        var proxy = HttpAccountTransport.RequireHttpProxy(resolvedProxy);
        var v4 = await ReadAsync(Ipv4Endpoint, AddressFamily.InterNetwork, proxy, token);
        var v6 = v4.Available ? new ExitAddress(Message: "后台本轮未检测 IPv6")
            : await ReadAsync(Ipv6Endpoint, AddressFamily.InterNetworkV6, proxy, token);
        token.ThrowIfCancellationRequested();
        return new(v4, v6);
    }

    public async Task<ExitAddresses> ProbeAsync(string resolvedProxy, CancellationToken token = default)
    {
        var proxy = HttpAccountTransport.RequireHttpProxy(resolvedProxy);
        token.ThrowIfCancellationRequested();
        var v4 = ReadAsync(Ipv4Endpoint, AddressFamily.InterNetwork, proxy, token);
        var v6 = ReadAsync(Ipv6Endpoint, AddressFamily.InterNetworkV6, proxy, token);
        await Task.WhenAll(v4, v6);
        token.ThrowIfCancellationRequested();
        return new(await v4, await v6);
    }

    private async Task<ExitAddress> ReadAsync(Uri endpoint, AddressFamily family, string proxy, CancellationToken token)
    {
        try
        {
            var response = await network.PayloadAsync(new(HttpMethod.Get, endpoint, proxy, true, new Dictionary<string, string>()),
                TimeSpan.FromSeconds(8), AddressBodyLimit, token);
            return ParseAddress(Encoding.UTF8.GetString(response.Body), family);
        }
        catch (AccountException error) when (error.Code != AccountError.ExitUnavailable)
        { token.ThrowIfCancellationRequested(); return new(Message: Failure(error)); }
    }

    public static ExitAddress ParseAddress(string text, AddressFamily family)
    {
        var value = text.Trim();
        return TryPublicAddress(value, out var ip) && ip!.AddressFamily == family
            ? new(ip.ToString(), "IPinfo") : new(Message: "IPinfo 未返回此地址族的有效公网地址");
    }

    public static bool TryPublicAddress(string value, out IPAddress? ip)
    {
        ip = null;
        if (value.Length is 0 or > 45 || value.Any(c => !(char.IsAsciiHexDigit(c) || c is '.' or ':'))
            || !IPAddress.TryParse(value, out var parsed) || IPAddress.IsLoopback(parsed)) return false;
        var b = parsed.GetAddressBytes();
        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            var parts = value.Split('.');
            if (parts.Length != 4 || parts.Any(p => p.Length == 0 || p.Length > 1 && p[0] == '0' || p.Any(c => c is < '0' or > '9'))) return false;
            if (b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 100 && b[1] is >= 64 and <= 127
                || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2)
                || b[0] == 192 && b[1] == 88 && b[2] == 99
                || b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)
                || b[0] == 203 && b[1] == 0 && b[2] == 113) return false;
        }
        else if (parsed.AddressFamily != AddressFamily.InterNetworkV6 || parsed.IsIPv4MappedToIPv6
            || (b[0] & 0xe0) != 0x20 || b[0] == 0x20 && b[1] == 1 && b[2] < 2 || b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8
            || b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0) return false;
        ip = parsed;
        return true;
    }

    public async Task<EgressResult> LocateAsync(ExitAddresses addresses, string resolvedProxy, CancellationToken token = default)
    {
        var proxy = HttpAccountTransport.RequireHttpProxy(resolvedProxy);
        token.ThrowIfCancellationRequested();
        if (addresses.PrimaryIp.Length == 0) return new(false, Message: "IPinfo 本次未取得出口地址。") { Addresses = addresses };
        try
        {
            var response = await network.PayloadAsync(new(HttpMethod.Get, LocationEndpoint(addresses.PrimaryIp), proxy, true, new Dictionary<string, string>()),
                TimeSpan.FromSeconds(8), LocationBodyLimit, token);
            token.ThrowIfCancellationRequested();
            return ParseLocation(AccountJson.Parse(response.Body), addresses);
        }
        catch (AccountException error) when (error.Code != AccountError.ExitUnavailable)
        { token.ThrowIfCancellationRequested(); return new(false, addresses.PrimaryIp, Message: "IPinfo 地区查询：" + Failure(error)) { Addresses = addresses }; }
    }

    public static EgressResult ParseLocation(JsonObject data, ExitAddresses addresses)
    {
        var partial = new EgressResult(false, addresses.PrimaryIp) { Addresses = addresses };
        var ip = AccountJson.Text(data, "ip").Trim();
        if (data["error"] is not null || AccountJson.Boolean(data["bogon"]) == true
            || !TryPublicAddress(ip, out _) || !EgressSources.SameIp(ip, addresses.PrimaryIp))
            return partial with { Message = "IPinfo 地区报告未对应本次主出口 IP，未采用地区与时区。" };
        var country = AccountJson.Text(data, "country").Trim().ToUpperInvariant();
        if (country.Length != 2 || country.Any(c => c is < 'A' or > 'Z')) country = "";
        var zone = AccountJson.Text(data, "timezone").Trim();
        var validZone = ProfileRules.WindowsTimezoneId(zone) is not null;
        return partial with { Ok = validZone, Country = country, CountryCode = country, City = AccountJson.Text(data, "city"),
            Timezone = validZone ? zone : "", Message = validZone ? "IPinfo 已返回本次主出口的具体时区。" : "IPinfo 已返回出口地区，但具体时区不可用。" };
    }

    internal static string Failure(AccountException error) => error.Code switch
    {
        AccountError.Http when error.Status == 429 => "查询额度受限（HTTP 429），未重试",
        AccountError.Http => $"查询返回 HTTP {error.Status}",
        AccountError.Cancelled => "本次查询取消",
        AccountError.Timeout => "本次查询超时",
        AccountError.Network => "本次连接或响应中断",
        AccountError.CorruptResponse => "响应格式或大小无效",
        _ => "本次查询未完成"
    };
}
