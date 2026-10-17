using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChunsonLogin;

public sealed record EgressInfo(string Ip, string Country, string City, string IanaTimezone, string WindowsTimezone, DateTimeOffset CheckedAt,
    string Sources = "", string QueryNote = "", string CountryCode = "");
public interface IEgressProbe
{
    Task<EgressInfo> ProbeAsync(NetworkRoute route, CancellationToken token);
}

public sealed class EgressProbe(
    Func<HttpClientHandler, HttpMessageHandler>? handlerFactory = null,
    Func<DateTimeOffset>? clock = null, TimeSpan? timeout = null) : IEgressProbe
{
    private readonly TimeSpan requestTimeout = timeout ?? TimeSpan.FromSeconds(12);
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private sealed record Location(IPAddress Ip, string CountryCode, string Country, string City, string Iana, string Windows);
    private sealed record ProviderResult(string Provider, Location? Location, string? Error);

    public async Task<EgressInfo> ProbeAsync(NetworkRoute route, CancellationToken token)
    {
        var proxy = RequireProxy(route.Proxy);
        token.ThrowIfCancellationRequested();
        var results = (await Task.WhenAll(
            QueryAsync("ipwho.is", new Uri("https://ipwho.is/"), proxy, token),
            QueryAsync("ipapi.is", new Uri("https://api.ipapi.is/"), proxy, token))).ToList();
        // A service outage is not evidence that FlClash or the account is broken.
        // Keep the same proxy and query a bounded alternate; never vote away an observed disagreement.
        if (results.Any(result => result.Location is null))
        {
            token.ThrowIfCancellationRequested();
            results.Add(await QueryAsync("ipapi.co", new Uri("https://ipapi.co/json/"), proxy, token));
        }
        token.ThrowIfCancellationRequested();
        var valid = results.Where(result => result.Location is not null).ToArray();
        if (valid.Length < 2)
        {
            var details = string.Join(" ", results.Where(result => result.Error is not null).Select(result => result.Error));
            throw new InvalidOperationException($"已识别 FlClash 入口，但定位查询暂未完成（{valid.Length}/2 个有效来源）。请稍后重试；未修改时区。 {details}");
        }
        // Prefer a source with an explicit ISO code so anonymous country names can be compared correctly.
        var ordered = valid.OrderByDescending(result => result.Location!.CountryCode.Length > 0).ToArray();
        var a = ordered[0].Location!;
        var b = ordered[1].Location!;
        if (!a.Ip.Equals(b.Ip)) throw new InvalidOperationException("两处查询返回不同出口 IP，请检查 FlClash 当前分流后重试；未修改时区。");
        if (!CountryMatches(a, b)) throw new InvalidOperationException("两处查询的国家定位不一致，本次不能确认时区。");
        if (a.Windows != b.Windows) throw new InvalidOperationException($"两处查询的时区不一致（{a.Iana} / {b.Iana}），未修改系统。");
        return new(a.Ip.ToString(), a.Country, a.City, a.Iana, a.Windows, now(),
            string.Join(" + ", valid.Select(result => result.Provider)), results.Count > 2 ? "已使用备用定位服务" : "", a.CountryCode);
    }

    private async Task<ProviderResult> QueryAsync(string provider, Uri endpoint, Uri proxy, CancellationToken token)
    {
        try { return new(provider, await ReadProviderAsync(provider, endpoint, proxy, token), null); }
        catch (InvalidOperationException error) { return new(provider, null, error.Message); }
    }

    private static bool CountryMatches(Location expected, Location actual)
    {
        if (actual.CountryCode.Length > 0) return expected.CountryCode == actual.CountryCode;
        // Anonymous ipapi.is reports an English country name, without country_code.
        // Compare that evidence; do not guess a country or timezone from a city.
        if (actual.Country.Length == 0) return false;
        if (actual.Country.Equals(expected.Country, StringComparison.OrdinalIgnoreCase)) return true;
        try { return actual.Country.Equals(new RegionInfo(expected.CountryCode).EnglishName, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    internal static Uri RequireProxy(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) || !IPAddress.IsLoopback(ip)
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("未确认 FlClash 本机 HTTP 入口，不会改为直连。");
        return uri;
    }

    internal static string MapTimezone(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '+' or '-'))
            || !TimeZoneInfo.TryConvertIanaIdToWindowsId(value, out var mapped))
            throw new InvalidOperationException("未返回可识别的具体时区。");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(mapped); return mapped; }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new InvalidOperationException("Windows 无法识别此时区。"); }
    }

    private async Task<Location> ReadProviderAsync(string provider, Uri endpoint, Uri proxy, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(requestTimeout);
        var policy = new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, UseProxy = true,
            Proxy = new WebProxy(proxy) { BypassProxyOnLocal = false, BypassList = [] }
        };
        HttpMessageHandler handler;
        try { handler = handlerFactory?.Invoke(policy) ?? policy; }
        catch { policy.Dispose(); throw; }
        if (!ReferenceEquals(handler, policy)) policy.Dispose();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("ChunsonLogin/1.1");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(provider + (response.StatusCode == HttpStatusCode.TooManyRequests ? " 查询次数受限，请稍后重试。"
                    : response.StatusCode == HttpStatusCode.Forbidden ? " 拒绝本次查询；这不代表账号有问题。" : $" 查询失败（HTTP {(int)response.StatusCode}）。"));
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, deadline.Token);
                if (count == 0) break;
                if (body.Length + count > 256 * 1024) throw new InvalidOperationException(provider + " 返回的数据过大，未采用。");
                body.Write(buffer, 0, count);
            }
            var data = JsonNode.Parse(body.ToArray()) as JsonObject ?? throw new JsonException();
            return Parse(provider, data);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException(provider + " 查询超时，未确认出口时区。"); }
        catch (HttpRequestException) { throw new InvalidOperationException(provider + " 定位服务连接未完成。"); }
        catch (IOException) { throw new InvalidOperationException(provider + " 响应中断，未确认出口时区。"); }
        catch (JsonException) { throw new InvalidOperationException(provider + " 返回的数据格式不完整。"); }
    }

    private static Location Parse(string provider, JsonObject data)
    {
        if (provider == "ipwho.is" && data["success"]?.GetValueKind() != JsonValueKind.True
            || provider == "ipapi.is" && (data["error"] is not null || data["error_code"] is not null)
            || provider == "ipapi.co" && data["error"] is { } failure && failure.GetValueKind() != JsonValueKind.False)
            throw new InvalidOperationException(provider + " 没有返回成功的定位结果，请稍后重试。");
        if (!IPAddress.TryParse(Text(data, "ip"), out var ip) || !IsPublicAddress(ip))
            throw new InvalidOperationException(provider + " 没有返回有效的公开出口 IP。");
        var location = data["location"] as JsonObject ?? data;
        var country = Text(location, "country_code").ToUpperInvariant();
        // ipapi.co documents country_code (and country as an ISO-code alias), country_name, timezone.
        if (provider == "ipapi.co" && location["country_code"] is null) country = Text(location, "country").ToUpperInvariant();
        var countryName = Text(location, provider == "ipapi.co" ? "country_name" : "country");
        var hasCode = location["country_code"] is not null;
        if (country.Length == 0 && (provider != "ipapi.is" || hasCode || countryName.Length == 0)
            || country.Length > 0 && (country.Length != 2 || country.Any(c => c is < 'A' or > 'Z')))
            throw new InvalidOperationException(provider + " 未返回有效的国家代码。");
        var timezone = provider == "ipwho.is" ? Text(data["timezone"] as JsonObject, "id") : Text(location, "timezone");
        string mapped;
        try { mapped = MapTimezone(timezone); }
        catch (InvalidOperationException error) { throw new InvalidOperationException(provider + " " + error.Message); }
        return new(ip, country, countryName.Length > 0 ? countryName : country, Text(location, "city"), timezone, mapped);
    }

    private static string Text(JsonObject? data, string key) => data?[key] is JsonValue scalar && scalar.TryGetValue<string>(out var value) ? value.Trim() : "";
    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16) return (bytes[0] & 0xfe) != 0xfc;
        return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
            && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }
}
