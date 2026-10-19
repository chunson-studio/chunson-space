using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace ChunsonSpace.Accounts;

public sealed class CdpConnectionFactory(int maxMessageBytes = 2097152) : ICdpConnectionFactory
{
    public async Task<ICdpConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != "ws" || uri.Host != "127.0.0.1" || !uri.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal))
            throw new AccountException(AccountError.Browser, "CDP 仅允许访问此档案的本机环回调试端口。");
        var socket = new ClientWebSocket(); socket.Options.Proxy = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try { await socket.ConnectAsync(uri, deadline.Token); return new CdpConnection(socket, Math.Clamp(maxMessageBytes, 2097152, 33554432)); }
        catch { socket.Dispose(); throw new AccountException(AccountError.Browser, "无法连接此档案的本机调试端口。"); }
    }
}

internal sealed class CdpConnection : ICdpConnection
{
    private readonly ClientWebSocket socket;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> pending = new();
    private readonly Channel<JsonObject> events = Channel.CreateUnbounded<JsonObject>();
    private readonly CancellationTokenSource stopped = new();
    private readonly SemaphoreSlim sends = new(1, 1);
    private readonly Task worker;
    private int sequence;
    private readonly int maxMessageBytes;
    public CdpConnection(ClientWebSocket socket, int maxMessageBytes = 2097152) { this.socket = socket; this.maxMessageBytes = maxMessageBytes; worker = ReadAsync(); }
    public async Task<JsonObject> CallAsync(string method, JsonObject parameters, string? sessionId, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        var packet = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters };
        if (!string.IsNullOrEmpty(sessionId)) packet["sessionId"] = sessionId;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopped.Token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await sends.WaitAsync(deadline.Token);
            try { await socket.SendAsync(Encoding.UTF8.GetBytes(packet.ToJsonString()), WebSocketMessageType.Text, true, deadline.Token); }
            finally { sends.Release(); }
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException) { throw new AccountException(AccountError.Browser, "本机浏览器控制未完成：" + method + "。"); }
        finally { pending.TryRemove(id, out _); }
    }
    public IAsyncEnumerable<JsonObject> Events(CancellationToken cancellationToken) => events.Reader.ReadAllAsync(cancellationToken);
    private async Task ReadAsync()
    {
        try
        {
            var buffer = new byte[8192];
            while (!stopped.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stopped.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > maxMessageBytes) throw new AccountException(AccountError.Browser, "CDP 返回过长。");
                } while (!result.EndOfMessage);
                var packet = JsonNode.Parse(message.ToArray()) as JsonObject ?? throw new AccountException(AccountError.Browser, "CDP 返回格式不完整。");
                var id = AccountJson.Integer(packet["id"]);
                if (id is not null && pending.TryGetValue((int)id, out var completion))
                {
                    if (packet["error"] is not null) completion.TrySetException(new AccountException(AccountError.Browser, "CDP 未接受操作。"));
                    else completion.TrySetResult(packet["result"] as JsonObject ?? new());
                }
                else if (packet["method"] is not null) await events.Writer.WriteAsync(packet, stopped.Token);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or System.Text.Json.JsonException or AccountException) { }
        finally
        {
            events.Writer.TryComplete();
            foreach (var completion in pending.Values) completion.TrySetException(new AccountException(AccountError.Browser, "本机 CDP 连接已结束。"));
        }
    }
    public async ValueTask DisposeAsync()
    {
        stopped.Cancel(); socket.Abort();
        try { await worker; } catch (OperationCanceledException) { }
        socket.Dispose(); stopped.Dispose(); sends.Dispose();
    }
}
