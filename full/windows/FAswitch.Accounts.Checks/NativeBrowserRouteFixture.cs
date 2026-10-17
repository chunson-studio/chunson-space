using System.Net;
using System.Net.Sockets;
using System.Text;
using FAswitch.Accounts;

// Local-only acceptance fixture: it cannot forward external browser traffic.
internal sealed class NativeBrowserRouteFixture : IAsyncDisposable
{
    private readonly TcpListener proxy = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopped = new();
    private readonly LoopbackLoginCallback callback;
    private readonly Task worker;
    private int proxyHits;
    private readonly TaskCompletionSource<bool> stored = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> restored = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> StoredAsync(CancellationToken token) => stored.Task.WaitAsync(token);
    public Task<bool> RestoredAsync(CancellationToken token) => restored.Task.WaitAsync(token);
    public string Proxy => "http://127.0.0.1:" + ((IPEndPoint)proxy.LocalEndpoint).Port;
    public NativeBrowserRouteFixture()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        callback = new(listener, ((IPEndPoint)listener.LocalEndpoint).Port, "local-fixture-state");
        proxy.Start(); worker = RunAsync();
    }
    private async Task RunAsync()
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                using var client = await proxy.AcceptTcpClientAsync(stopped.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var line = await reader.ReadLineAsync(deadline.Token);
                    var headers = new List<string>(); string? header;
                    while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(deadline.Token))) headers.Add(header);
                    var matches = line == "GET http://chunson-route-check.invalid/probe HTTP/1.1";
                    if (matches) Interlocked.Increment(ref proxyHits);
                    var response = matches
                        ? $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{callback.Port}/auth/callback?state=local-fixture-state&code=local-fixture-code\r\n"
                        : "HTTP/1.1 502 Bad Gateway\r\n";
                    var body = "";
                    var uriText = line?.Split(' ').ElementAtOrDefault(1);
                    if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri) && uri.Host == "chunson-route-check.invalid" && !matches)
                    {
                        response = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n";
                        if (uri.AbsolutePath == "/remember")
                        {
                            response += "Set-Cookie: chunson_fixture=retained; Max-Age=86400; Path=/; SameSite=Lax\r\n";
                            body = "<title>Local persistence check</title><script>localStorage.setItem('fixture','retained');fetch('/stored');</script>Local test only.";
                        }
                        if (uri.AbsolutePath == "/verify") body = "<title>Local persistence restored</title><script>fetch('/checked?storage='+localStorage.getItem('fixture'));</script>Local test only.";
                        var cookie = headers.Any(value => value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) && value.Contains("chunson_fixture=retained"));
                        if (uri.AbsolutePath == "/stored") stored.TrySetResult(cookie);
                        if (uri.AbsolutePath == "/checked") restored.TrySetResult(cookie && uri.Query == "?storage=retained");
                    }
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n" + body), deadline.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException) { }
    }
    public async Task<bool> VerifyAsync(CancellationToken token)
    {
        var result = await callback.WaitAsync(TimeSpan.FromSeconds(15), token);
        return Volatile.Read(ref proxyHits) > 0 && result == new LoginCallback(CallbackKind.Code, "local-fixture-code");
    }
    public async ValueTask DisposeAsync()
    {
        stopped.Cancel(); proxy.Stop(); await worker; await callback.DisposeAsync(); stopped.Dispose();
    }
}

internal sealed class NativeRecordingChrome(string executable) : IChromeSystem
{
    private readonly WindowsChromeSystem actual = new(() => executable);
    public IReadOnlyList<string> Arguments { get; private set; } = [];
    public string? FindChrome() => actual.FindChrome();
    public Task StartAsync(string exe, IReadOnlyList<string> arguments, CancellationToken token) => actual.StartAsync(exe, arguments, token);
    public Task<BrowserIdentity?> StartTrackedAsync(string exe, IReadOnlyList<string> arguments, CancellationToken token)
    { Arguments = arguments.ToArray(); return actual.StartTrackedAsync(exe, arguments, token); }
    public Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(string exe, string profile, CancellationToken token) => actual.ObserveAsync(exe, profile, token);
    public Task<IReadOnlyList<BrowserIdentity>?> ObserveKnownAsync(string exe, string profile, IReadOnlyList<BrowserIdentity> known, CancellationToken token) => actual.ObserveKnownAsync(exe, profile, known, token);
    public Task<bool> CloseOwnedAsync(IReadOnlyList<BrowserIdentity> identities, CancellationToken token) => actual.CloseOwnedAsync(identities, token);
}
