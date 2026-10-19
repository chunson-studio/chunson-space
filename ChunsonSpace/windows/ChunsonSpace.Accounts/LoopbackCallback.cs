using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ChunsonSpace.Accounts;

public sealed class LoopbackCallbackFactory : ILoginCallbackFactory
{
    public Task<ILoginCallback> StartAsync(string state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var port in new[] { 1455, 1457 })
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            try { listener.Start(8); return Task.FromResult<ILoginCallback>(new LoopbackLoginCallback(listener, port, state)); }
            catch (SocketException) { listener.Stop(); }
        }
        throw new AccountException(AccountError.Callback, "本机 1455/1457 登录端口都不可用，请关闭占用端口的登录服务后重试。");
    }
}

public sealed class LoopbackLoginCallback : IStatefulLoginCallback
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopped = new();
    private readonly TaskCompletionSource<LoginCallback> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task worker;
    public int Port { get; }
    public string State { get; }
    public LoopbackLoginCallback(TcpListener listener, int port, string state)
    { this.listener = listener; Port = port; State = state; worker = RunAsync(); }
    public bool Cancel() => completion.TrySetResult(new(CallbackKind.Cancelled));
    public async Task<LoginCallback> WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        try { return await completion.Task.WaitAsync(duration, cancellationToken); }
        catch (TimeoutException) { return new(CallbackKind.Waiting); }
    }
    public static LoginCallback Parse(string requestTarget, string expectedState)
    {
        if (!Uri.TryCreate("http://127.0.0.1" + requestTarget, UriKind.Absolute, out var uri)) return new(CallbackKind.Waiting);
        if (uri.AbsolutePath == "/cancel") return new(CallbackKind.Cancelled);
        if (uri.AbsolutePath != "/auth/callback") return new(CallbackKind.Waiting);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            if (query.ContainsKey(key)) return new(CallbackKind.StateMismatch);
            query[key] = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }
        var state = query.GetValueOrDefault("state", "");
        if (state.EndsWith(OAuthPkce.StateSuffix, StringComparison.Ordinal)) state = state[..^OAuthPkce.StateSuffix.Length];
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState))) return new(CallbackKind.StateMismatch);
        if (query.ContainsKey("error")) return new(CallbackKind.ProviderError);
        return query.TryGetValue("code", out var code) && code.Length > 0 ? new(CallbackKind.Code, code) : new(CallbackKind.MissingCode);
    }
    private async Task RunAsync()
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stopped.Token);
                try
                {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                var bytes = new List<byte>();
                var buffer = new byte[1];
                var headersComplete = false;
                while (bytes.Count < 8192 && await stream.ReadAsync(buffer, deadline.Token) > 0)
                {
                    bytes.Add(buffer[0]);
                    // Consume the complete bounded HTTP header before closing this
                    // connection. Unread request headers can cause Windows to send a
                    // reset and discard the callback result page in the browser.
                    if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
                    { headersComplete = true; break; }
                }
                var line = Encoding.ASCII.GetString(bytes.ToArray()).Split('\n')[0].TrimEnd('\r').Split(' ');
                var result = headersComplete && line.Length == 3 && line[0] == "GET" ? Parse(line[1], State) : new LoginCallback(CallbackKind.Waiting);
                var known = result.Kind != CallbackKind.Waiting;
                var message = result.Kind switch
                {
                    CallbackKind.Code => "已收到网页授权回调。工具正在获取并保存登录凭据，请返回工具查看最终结果；此页面不代表账号已保存。",
                    CallbackKind.Cancelled => "登录已取消，请返回工具查看结果。",
                    CallbackKind.Waiting => "此地址不是登录回调。",
                    _ => "未收到有效授权回调，账号尚未保存，请返回工具查看错误。"
                };
                var body = Encoding.UTF8.GetBytes(message);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.0 {(known ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                try
                {
                    await stream.WriteAsync(header, deadline.Token);
                    await stream.WriteAsync(body, deadline.Token);
                }
                finally
                {
                    // Give the result page a chance to leave before a fast token
                    // failure disposes this callback. A disconnected browser still
                    // delivers its already-validated callback exactly once.
                    if (known) completion.TrySetResult(result);
                }
                }
                catch (Exception error) when (error is OperationCanceledException or SocketException or IOException)
                { if (stopped.IsCancellationRequested) break; }
            }
        }
        // Stop can dispose the socket after the loop check but before the next
        // AcceptTcpClientAsync has registered cancellation. This is normal only
        // when this callback is already shutting down.
        catch (ObjectDisposedException) when (stopped.IsCancellationRequested) { }
        catch (Exception error) when (error is OperationCanceledException or SocketException or IOException)
        { if (!stopped.IsCancellationRequested) completion.TrySetResult(new(CallbackKind.ProviderError)); }
    }
    public async ValueTask DisposeAsync()
    {
        stopped.Cancel(); listener.Stop();
        try { await worker; } catch (OperationCanceledException) { }
        stopped.Dispose();
    }
}
