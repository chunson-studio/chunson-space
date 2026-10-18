using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;

internal static class TransportFailureChecks
{
    private const string PrivateDetail = "DO_NOT_EXPORT account-ba4ddfcfa4@example.invalid ip=203.0.113.17 https://private.invalid/path?access_token=raw-token C:\\private\\auth.json Bearer raw-secret";
    public static async Task<int> Run(string root)
    {
        var count = 0; var run = 0; var results = new List<object>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        var repository = new AccountRepository(Path.Combine(root, "fixture-account"));
        repository.Profiles.CreateBoundAccount("A", new("http://127.0.0.1:7890"));
        repository.Profiles.SaveAccount("A", new JsonObject { ["tokens"] = new JsonObject { ["access_token"] = "fixture-transport-access", ["account_id"] = "fixture-transport-id" } }, overwrite: true);
        var authPath = Path.Combine(repository.AccountPath("A"), "auth.json");
        var auth = File.ReadAllBytes(authPath);
        var handler = new FixtureHandler();
        var service = new AccountService(repository, new HttpAccountTransport(_ => handler));
        var runtime = new ResponsesVisualTestRuntime(service, () => throw new InvalidOperationException("No browser in transport fixtures"));
        Task<VisualReply> Reply(CancellationToken token = default) => runtime.ReplyAsync("A", "gpt-6-astra", "hello", [], Path.Combine(root, "request-" + ++run), token, "high");
        JsonObject Summary() => JsonNode.Parse(File.ReadAllText(Path.Combine(root, "request-" + run, "trace-summary.json")))!.AsObject();
        async Task<AccountException> Failure(Func<Task> action, AccountError code, string message)
        {
            try { await action(); throw new InvalidOperationException("Expected fixture failure"); }
            catch (AccountException error)
            {
                Check(error.Code == code && error.Message == message && error.Status is null, "existing typed failure/message/status remain unchanged");
                Check(error.InnerException is null && !error.ToString().Contains("DO_NOT_EXPORT"), "raw transport exception is not retained on exported exception");
                return error;
            }
        }
        var cases = new (string Name, HttpRequestError Error, Exception? Inner, HttpStatusCode? Status, string? Socket, string[] Types)[]
        {
            ("proxy-tunnel", HttpRequestError.ProxyTunnelError, new PrivateNamedException(PrivateDetail), HttpStatusCode.ProxyAuthenticationRequired, null, ["Other"]),
            ("tls", HttpRequestError.SecureConnectionError, new AuthenticationException(PrivateDetail, new IOException(PrivateDetail, new SocketException((int)SocketError.ConnectionReset))), null, "ConnectionReset", ["AuthenticationException", "IOException", "SocketException"]),
            ("dns", HttpRequestError.NameResolutionError, new SocketException((int)SocketError.HostNotFound), null, "HostNotFound", ["SocketException"]),
            ("connect", HttpRequestError.ConnectionError, new SocketException((int)SocketError.ConnectionRefused), null, "ConnectionRefused", ["SocketException"]),
            ("response", HttpRequestError.InvalidResponse, null, null, null, []),
            ("undefined-enum", (HttpRequestError)123456, null, (HttpStatusCode)123456, null, [])
        };
        foreach (var item in cases)
        {
            var original = new HttpRequestException(item.Error, PrivateDetail, item.Inner, item.Status);
            original.Data["auth"] = PrivateDetail; handler.Failure = original;
            var before = handler.Calls;
            var error = await Failure(async () => await Reply(), AccountError.Network, "无法通过此档案的出口连接服务。");
            var summary = Summary(); var detail = summary["transportFailure"]!.AsObject();
            Check(detail["exceptionType"]!.ToString() == "HttpRequestException" && detail["httpRequestError"]?.ToString() == (Enum.IsDefined(item.Error) ? item.Error.ToString() : null), "transport class survives production network and trace boundaries");
            Check(detail["httpStatus"]?.GetValue<int>() == (item.Status == HttpStatusCode.ProxyAuthenticationRequired ? 407 : null) && summary["httpStatus"] is null, "proxy error status stays distinct from actual response HTTP status");
            Check(detail["innerExceptionTypes"]!.AsArray().Select(node => node!.ToString()).SequenceEqual(item.Types) && detail["socketErrorCode"]?.ToString() == item.Socket, "allowlisted inner types and socket enum retained");
            Check(detail["cancellationSource"] is null && summary["state"]!.ToString() == "失败" && summary["readBoundary"]!.ToString() == "未收到响应", "connection failure is not user cancellation or received response");
            Check(handler.Calls == before + 1 && !File.Exists(Path.Combine(root, "request-" + run, "response-headers.json")), "failed transport has one attempt and no fabricated response headers");
            Check(!JsonSerializer.Serialize(error.TransportFailure).Contains("PrivateNamedException"), "unknown inner exception class name is not exported");
            results.Add(new { name = item.Name, request = run, detail = detail.DeepClone() });
        }
        handler.Failure = null;
        handler.Content = () => new BrokenStream(new HttpIOException(HttpRequestError.ResponseEnded, PrivateDetail, new SocketException((int)SocketError.ConnectionReset)));
        await Failure(async () => await Reply(), AccountError.Network, "模型响应连接中断，未确认成功。");
        var disconnected = Summary();
        Check(disconnected["httpStatus"]!.GetValue<int>() == 200 && disconnected["transportFailure"]!["httpRequestError"]!.ToString() == "ResponseEnded"
            && disconnected["transportFailure"]!["socketErrorCode"]!.ToString() == "ConnectionReset", "body disconnect retains response headers and precise typed error");
        Check(disconnected["readBoundary"]!.ToString() == "已收到响应头" && disconnected["bytes"]!.GetValue<int>() == 0, "failed body capture does not fabricate EOF or response events");
        results.Add(new { name = "body-disconnect", request = run, detail = disconnected["transportFailure"]!.DeepClone() });

        var request = new AccountHttpRequest(HttpMethod.Get, AccountEndpoints.Usage, "http://127.0.0.1:7890", true, new Dictionary<string, string>());
        var blocking = new BlockingTransport(); var network = new AccountNetwork(blocking);
        var timedOut = await Failure(async () => await network.SendAsync(request, TimeSpan.FromMilliseconds(20), default), AccountError.Timeout, "请求超时，请检查此档案的代理连接。");
        Check(timedOut.TransportFailure?.CancellationSource == "DeadlineElapsed" && timedOut.TransportFailure.HttpRequestError is null, "deadline observed explicitly and never guessed as connection error");
        using (var trace = new ModelRequestTrace(Path.Combine(root, "request-" + ++run), [])) { trace.Request(request); trace.Fail(timedOut); }
        results.Add(new { name = "deadline", request = run, detail = Summary()["transportFailure"]!.DeepClone() });
        var unknownCancel = new AccountNetwork(new ThrowingTransport(new OperationCanceledException(PrivateDetail)));
        var unknown = await Failure(async () => await unknownCancel.SendAsync(request, Timeout.InfiniteTimeSpan, default), AccountError.Timeout, "请求超时，请检查此档案的代理连接。");
        Check(unknown.TransportFailure?.CancellationSource == "Unknown", "uncancelled source OCE retains old timeout behavior without claiming deadline fired");

        handler.Content = null; handler.Hold = true;
        using (var cancel = new CancellationTokenSource())
        {
            var pending = Reply(cancel.Token); await handler.Entered.Task;
            Check(!pending.IsCompleted, "infinite model request remains pending until explicit cancellation"); cancel.Cancel();
            try { await pending; Check(false, "user cancel must throw OCE"); } catch (OperationCanceledException) { Check(true, "user cancellation behavior preserved"); }
        }
        var cancelled = Summary();
        Check(cancelled["state"]!.ToString() == "已取消" && cancelled["transportFailure"]!["cancellationSource"]!.ToString() == "CallerRequested", "caller cancellation distinguished from deadline and transport failure");
        results.Add(new { name = "caller-cancel", request = run, detail = cancelled["transportFailure"]!.DeepClone() });

        var unavailable = await Failure(async () => await network.SendAsync(request with { Proxy = "" }, Timeout.InfiniteTimeSpan, default), AccountError.ExitUnavailable,
            "此请求需要档案绑定的 HTTP/HTTPS 代理；不会改用系统代理或直连。");
        Check(unavailable.TransportFailure is null && blocking.Calls == 1, "unavailable route remains preflight error, not timeout or extra network attempt");
        using (var trace = new ModelRequestTrace(Path.Combine(root, "request-" + ++run), [])) { trace.Request(request with { Proxy = "" }); trace.Fail(unavailable); }
        Check(Summary()["transportFailure"] is null, "preflight unavailable trace does not fabricate transport diagnosis");

        // Non-model payload consumers (including OAuth) use this same safe metadata.
        var payloadNetwork = new AccountNetwork(new PayloadTransport(new BrokenStream(new HttpIOException(HttpRequestError.ResponseEnded, PrivateDetail))));
        var payloadFailure = await Failure(async () => await payloadNetwork.JsonAsync(request, Timeout.InfiniteTimeSpan, default), AccountError.Network, "响应连接中断。");
        Check(payloadFailure.TransportFailure?.HttpRequestError == "ResponseEnded", "shared JSON/OAuth payload read keeps safe error categories");
        handler.Hold = false; handler.Status = HttpStatusCode.Unauthorized; handler.Content = () => new MemoryStream(Encoding.UTF8.GetBytes("unauthorized"));
        var beforeHttp = handler.Calls;
        try { await Reply(); Check(false, "401 must remain HTTP failure"); }
        catch (AccountException error) { Check(error.Status == 401 && error.Code == AccountError.Http && error.TransportFailure is null, "actual HTTP response semantics unchanged"); }
        Check(handler.Calls == beforeHttp + 1 && Summary()["transportFailure"] is null, "actual HTTP failure does not trigger refresh retry or false transport classification");
        handler.Status = HttpStatusCode.OK; handler.Content = () => new MemoryStream(Encoding.UTF8.GetBytes("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"model\":\"gpt-6-astra\",\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"hello\"}]}]}}\n\n"));
        Check((await Reply()).Text == "hello" && Summary()["transportFailure"] is null, "success remains successful without stale failure metadata");
        Check(File.ReadAllBytes(authPath).SequenceEqual(auth), "all diagnostic fixtures preserve source auth bytes");
        var files = Directory.EnumerateDirectories(root, "request-*").SelectMany(Directory.GetFiles).ToArray();
        var persisted = string.Join("\n", files.Select(File.ReadAllText));
        Check(new[] { "DO_NOT_EXPORT", "raw-secret", "raw-token", "account-a09ad6a9ce@example.invalid", "203.0.113.17", "private.invalid", "C:\\private", "PrivateNamedException", "fixture-transport-access", "fixture-transport-id" }.All(secret => !persisted.Contains(secret, StringComparison.Ordinal)), "all persisted/exportable trace files exclude private exception details and credentials");
        Check(Directory.EnumerateDirectories(root, "request-*").Where(path => File.Exists(Path.Combine(path, "request.json"))).All(path =>
        {
            var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(path, "request.json")))!;
            return metadata["requestTimeoutSeconds"] is null && metadata["automaticRetries"]!.GetValue<int>() == 0 && metadata["effort"]!.ToString() == "high";
        }), "new diagnostics do not change model request deadline, retries or selected effort");
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { passed = true, checks = count, actualNetworkRequests = false, attempts = run, results }, new JsonSerializerOptions { WriteIndented = true }));
        return count;
    }
    private sealed class PrivateNamedException(string message) : Exception(message);
    private sealed class FixtureHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public Func<Stream>? Content { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool Hold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            if (Hold) { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return new HttpResponseMessage(Status) { Content = new StreamContent(Content?.Invoke() ?? new MemoryStream()) };
        }
    }
    private sealed class BlockingTransport : IAccountTransport
    {
        public int Calls { get; private set; }
        public async Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken token)
        { Calls++; await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException("unreachable"); }
    }
    private sealed class ThrowingTransport(Exception error) : IAccountTransport
    { public Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken token) => Task.FromException<AccountHttpResponse>(error); }
    private sealed class PayloadTransport(Stream stream) : IAccountTransport
    { public Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken token) => Task.FromResult(new AccountHttpResponse(200, stream)); }
    private sealed class BrokenStream(Exception failure) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(failure);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
