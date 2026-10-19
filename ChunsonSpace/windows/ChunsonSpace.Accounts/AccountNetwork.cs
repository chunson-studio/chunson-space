using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

public sealed record AccountHttpRequest(HttpMethod Method, Uri Url, string Proxy, bool RequireProxy, IReadOnlyDictionary<string, string> Headers, byte[]? Body = null, string? ContentType = null, bool UseSystemProxy = false);
public sealed class AccountHttpResponse(int status, Stream content, IDisposable? owner = null, IReadOnlyDictionary<string, string>? headers = null) : IAsyncDisposable
{
    public int Status { get; } = status;
    public Stream Content { get; } = content;
    public IReadOnlyDictionary<string, string> Headers { get; } = headers ?? new Dictionary<string, string>();
    public async ValueTask DisposeAsync() { await Content.DisposeAsync(); owner?.Dispose(); }
}
public interface IAccountTransport { Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken cancellationToken); }

public sealed class HttpAccountTransport(Func<HttpClientHandler, HttpMessageHandler>? handlerFactory = null, Func<string?>? systemProxyProvider = null) : IAccountTransport
{
    public static string RequireHttpProxy(string? value)
    {
        string proxy;
        try { proxy = ProfileRules.NormalizeProxy(value); }
        catch (StorageException) { throw new AccountException(AccountError.ExitUnavailable, "此档案的代理写法不合法，请绑定 HTTP/HTTPS 代理。"); }
        if (proxy.Length == 0 || proxy.StartsWith("socks5://", StringComparison.Ordinal)) throw new AccountException(AccountError.ExitUnavailable, "此请求需要档案绑定的 HTTP/HTTPS 代理；不会改用系统代理或直连。");
        return proxy;
    }
    public async Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken cancellationToken)
    {
        if (request.UseSystemProxy && (request.RequireProxy || request.Proxy.Length > 0))
            throw new AccountException(AccountError.ExitUnavailable, "系统网络与档案专属代理不能混用。");
        var proxy = request.UseSystemProxy ? ResolveSystemHttpProxy(systemProxyProvider) : request.RequireProxy ? RequireHttpProxy(request.Proxy) : request.Proxy.Length > 0 ? RequireHttpProxy(request.Proxy) : "";
        if (request.Url.Scheme is not ("http" or "https")) throw new AccountException(AccountError.Network, "请求地址必须使用 HTTP/HTTPS。");
        var policy = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, UseProxy = proxy.Length > 0 };
        if (proxy.Length > 0) policy.Proxy = new WebProxy(new Uri(proxy)) { BypassProxyOnLocal = false, BypassList = [] };
        var handler = handlerFactory?.Invoke(policy) ?? policy;
        if (!ReferenceEquals(handler, policy)) policy.Dispose();
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            using var message = new HttpRequestMessage(request.Method, request.Url);
            foreach (var header in request.Headers) message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Body is not null)
            {
                message.Content = new ByteArrayContent(request.Body);
                if (request.ContentType is not null) message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.ContentType);
            }
            var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var headers = response.Headers.Concat(response.Content.Headers).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => string.Join(", ", group.SelectMany(p => p.Value)), StringComparer.OrdinalIgnoreCase);
            return new((int)response.StatusCode, stream, new ResponseOwner(client, response), headers);
        }
        catch { client.Dispose(); throw; }
    }
    private sealed class ResponseOwner(HttpClient client, HttpResponseMessage response) : IDisposable
    { public void Dispose() { response.Dispose(); client.Dispose(); } }
    public static string ResolveSystemHttpProxy(Func<string?>? resolver = null)
    {
        try
        {
            var value = resolver is null ? new ChunsonSpace.Windows.SystemProxyResolver().ResolveHttpProxy() : resolver();
            if (string.IsNullOrWhiteSpace(value)) throw new AccountException(AccountError.ExitUnavailable, "未配置可明确解析的系统 HTTP/HTTPS 代理；禁止改为直连。");
            return RequireHttpProxy(value);
        }
        catch (Exception error) when (error is ChunsonSpace.Windows.SystemProxyException or IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException)
        { throw new AccountException(AccountError.ExitUnavailable, "系统代理无法明确解析；禁止改为直连，请选择专属 HTTP/HTTPS 出口。"); }
    }
}

public static class ErrorRedaction
{
    public static string ModelReply(string text, params string?[] secrets)
    {
        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s!.Length))
            text = text.Replace(secret!, "<已隐去>", StringComparison.Ordinal);
        return text;
    }
    public static string Proxy(string value)
    {
        var at = value.LastIndexOf('@');
        if (at < 0) return Scrub(value);
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        return Scrub((scheme >= 0 ? value[..(scheme + 3)] : "") + "<已隐去>@" + value[(at + 1)..]);
    }
    public static string Scrub(string? text, params string?[] secrets)
    {
        var value = ResponseText(text, secrets);
        value = Regex.Replace(value, "[A-Za-z0-9_\\-.]{40,}", "<已隐去>");
        value = Regex.Replace(value, "[A-Za-z0-9._%+\\-]+@[A-Za-z0-9.\\-]+\\.[A-Za-z]{2,}", "<邮箱已隐去>");
        value = Regex.Replace(value, "\\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\b", "<id 已隐去>");
        return value.Length > 220 ? value[..220] : value;
    }
    public static string ResponseText(string? text, params string?[] secrets)
    {
        var value = text ?? "";
        foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)).OrderByDescending(secret => secret!.Length))
        {
            value = value.Replace(secret!, "<已隐去>", StringComparison.Ordinal);
            value = value.Replace(Uri.EscapeDataString(secret!), "<已隐去>", StringComparison.OrdinalIgnoreCase);
        }
        value = Regex.Replace(value, "(?i)(code|code_verifier|state|access_token|refresh_token|id_token|api_key)=([^&\\s]+)", "$1=<已隐去>");
        return value;
    }
}

internal static class AccountJson
{
    public static string Text(JsonObject? value, string key) => value?[key] is JsonValue node && node.TryGetValue<string>(out var text) ? text : "";
    public static long? Integer(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<long>(out var number) ? number : value.TryGetValue<int>(out var small) ? small : null : null;
    public static bool? Boolean(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
    public static double Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
    public static JsonObject Parse(byte[] bytes)
    {
        try { return JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new AccountException(AccountError.CorruptResponse, "服务返回的数据格式不完整。"); }
    }
}

internal sealed class AccountNetwork(IAccountTransport transport)
{
    public async Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, TimeSpan timeout, CancellationToken token)
    {
        if (request.RequireProxy) _ = HttpAccountTransport.RequireHttpProxy(request.Proxy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try { return await transport.SendAsync(request, deadline.Token); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested) { throw new AccountException(AccountError.Timeout, "请求超时，请检查此档案的代理连接。", transportFailure: AccountTransportFailure.Capture(error, false, deadline.IsCancellationRequested)); }
        catch (HttpRequestException error) { throw new AccountException(AccountError.Network, "无法通过此档案的出口连接服务。", transportFailure: AccountTransportFailure.Capture(error)); }
        catch (IOException error) { throw new AccountException(AccountError.Network, "网络连接中断。", transportFailure: AccountTransportFailure.Capture(error)); }
    }
    public async Task<JsonObject> JsonAsync(AccountHttpRequest request, TimeSpan timeout, CancellationToken token, params string?[] secrets)
    {
        var payload = await PayloadAsync(request, timeout, 1048576, token, secrets);
        return AccountJson.Parse(payload.Body);
    }
    public async Task<(int Status, byte[] Body)> PayloadAsync(AccountHttpRequest request, TimeSpan timeout, int limit, CancellationToken token, params string?[] secrets)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            await using var response = await SendAsync(request, timeout, deadline.Token);
            var body = await ReadLimited(response.Content, limit, deadline.Token);
            if (response.Status is < 200 or >= 300) throw new AccountException(AccountError.Http, $"服务返回 HTTP {response.Status}：" + ErrorRedaction.Scrub(Encoding.UTF8.GetString(body), secrets), response.Status);
            return (response.Status, body);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested) { throw new AccountException(AccountError.Timeout, "请求或响应读取超时，请检查此档案的代理连接。", transportFailure: AccountTransportFailure.Capture(error, false, deadline.IsCancellationRequested)); }
        catch (IOException error) { throw new AccountException(AccountError.Network, "响应连接中断。", transportFailure: AccountTransportFailure.Capture(error)); }
        catch (HttpRequestException error) { throw new AccountException(AccountError.Network, "响应连接中断。", transportFailure: AccountTransportFailure.Capture(error)); }
    }
    public static async Task<byte[]> ReadLimited(Stream stream, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) return output.ToArray();
            if (output.Length + read > limit) throw new AccountException(AccountError.CorruptResponse, "服务返回的数据超过允许大小。");
            output.Write(buffer, 0, read);
        }
    }
}
