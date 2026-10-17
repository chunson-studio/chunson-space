using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace FAswitch.Windows;

public sealed class ManagedRuntimeCdpException(string method, int code, string detail) : IOException($"CDP {method} ({code})")
{
    public string Method { get; } = method;
    public int Code { get; } = code;
    public bool ContextChanged => detail.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) || detail.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase);
    // Chromium shares these overrides between pages in a renderer. The reply alone
    // does not prove the value: callers must still read the actual page environment.
    public bool SharedOverride => Code == -32000 && (Method == "Emulation.setTimezoneOverride" && detail == "Timezone override is already in effect"
        || Method == "Emulation.setLocaleOverride" && detail == "Another locale override is already in effect");
}

public sealed class ManagedRuntimeCdpTimeoutException(string method) : IOException("CDP command timed out: " + method)
{
    public string Method { get; } = method;
}

public sealed class ManagedRuntimeCdpFactory : IManagedRuntimeCdpFactory
{
    public async Task<IManagedRuntimeCdp> ConnectAsync(Uri endpoint, CancellationToken token)
    {
        if (endpoint.Scheme != "ws" || endpoint.Host != "127.0.0.1" || !Regex.IsMatch(endpoint.AbsolutePath, "\\A/devtools/browser/[A-Za-z0-9-]+\\z")) throw new IOException();
        var socket = new ClientWebSocket(); socket.Options.Proxy = null;
        try { await socket.ConnectAsync(endpoint, token); return new ManagedRuntimeCdp(socket); } catch { socket.Dispose(); throw; }
    }
}
internal sealed class ManagedRuntimeCdp : IManagedRuntimeCdp
{
    private readonly ClientWebSocket socket;
    private readonly ConcurrentDictionary<int, (string Method, TaskCompletionSource<JsonObject> Completion)> pending = new();
    private readonly Channel<JsonObject> events = Channel.CreateUnbounded<JsonObject>();
    private readonly SemaphoreSlim sends = new(1, 1);
    private readonly CancellationTokenSource stopped = new();
    private readonly Task worker;
    private int sequence;
    private int disposed;
    public ManagedRuntimeCdp(ClientWebSocket socket) { this.socket = socket; worker = ReadAsync(); }
    public async Task<JsonObject> CallAsync(string method, JsonObject parameters, string? session, CancellationToken token)
    {
        var id = Interlocked.Increment(ref sequence); var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously); pending[id] = (method, completion);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stopped.Token); deadline.CancelAfter(8000);
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }; if (session is not null) message["sessionId"] = session;
        try
        {
            await sends.WaitAsync(deadline.Token);
            try { await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, deadline.Token); } finally { sends.Release(); }
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !stopped.IsCancellationRequested)
        { throw new ManagedRuntimeCdpTimeoutException(method); }
        finally { pending.TryRemove(id, out _); }
    }
    public IAsyncEnumerable<JsonObject> Events(CancellationToken token) => events.Reader.ReadAllAsync(token);
    private async Task ReadAsync()
    {
        try
        {
            var bytes = new byte[8192];
            while (!stopped.IsCancellationRequested)
            {
                using var buffer = new MemoryStream(); WebSocketReceiveResult received;
                do { received = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), stopped.Token); if (received.MessageType == WebSocketMessageType.Close) return; buffer.Write(bytes, 0, received.Count); if (buffer.Length > 1024 * 1024) throw new IOException(); } while (!received.EndOfMessage);
                var packet = JsonNode.Parse(buffer.ToArray()) as JsonObject ?? throw new IOException();
                if (packet["id"] is JsonValue number && number.TryGetValue<int>(out var id) && pending.TryGetValue(id, out var call))
                {
                    if (packet["error"] is JsonObject error) call.Completion.TrySetException(new ManagedRuntimeCdpException(call.Method, error["code"]?.GetValue<int>() ?? 0, error["message"]?.GetValue<string>() ?? ""));
                    else call.Completion.TrySetResult(packet["result"] as JsonObject ?? new());
                }
                else if (packet["method"] is not null) await events.Writer.WriteAsync(packet, stopped.Token);
            }
        }
        catch (Exception) { }
        finally { events.Writer.TryComplete(); foreach (var call in pending.Values) call.Completion.TrySetException(new IOException("CDP connection closed")); }
    }
    public async ValueTask DisposeAsync()
    { if (Interlocked.Exchange(ref disposed, 1) != 0) return; stopped.Cancel(); socket.Abort(); await worker; socket.Dispose(); sends.Dispose(); stopped.Dispose(); }
}
