using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChunsonSpace.Accounts;

// Per-test application evidence only. Never intercept authentication or other apps.
public sealed class ModelRequestTrace : IDisposable
{
    private readonly string directory;
    private readonly string[] secrets;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly StreamWriter stream;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private const long Limit = 64 * 1024 * 1024;
    private long bytes;
    private int lines;
    private double? firstEventSeconds;
    private int? httpStatus;
    private string state = "未完成", boundary = "未收到响应";
    private bool truncated, disposed;
    private JsonObject? usage;
    private string responseModel = "", error = "";
    private AccountTransportFailure? transportFailure;
    private readonly SafetyBufferingCollector safetyBuffering = new();
    private bool eof, failed;
    private readonly string accountIdentityHash;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    public SafetyBufferingObservation SafetyBuffering => safetyBuffering.Snapshot(eof, failed, truncated);
    public void ObserveEvent(JsonObject payload) => safetyBuffering.Observe(payload);
    public ModelRequestTrace(string directory, IEnumerable<string> secrets, string accountIdentityHash = "")
    {
        this.directory = directory; this.secrets = secrets.Where(s => s.Length > 0).Distinct().OrderByDescending(s => s.Length).ToArray();
        this.accountIdentityHash = accountIdentityHash;
        Directory.CreateDirectory(directory);
        stream = new(new FileStream(Path.Combine(directory, "response.sse"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { NewLine = "\n" };
    }
    public void Request(AccountHttpRequest request)
    {
        var body = request.Body is null ? null : JsonNode.Parse(request.Body);
        Write("wire-request.json", new { method = request.Method.Method, endpoint = request.Url.GetLeftPart(UriPartial.Path),
            proxy = ErrorRedaction.Proxy(request.Proxy), request.RequireProxy, request.UseSystemProxy, request.ContentType, headers = Headers(request.Headers), body = CleanNode(body),
            recordedAt = DateTimeOffset.UtcNow, captureScope = "本程序模型测试请求；凭据已脱敏；图像输入保留原始 PNG 数据" });
    }
    public void Response(AccountHttpResponse response)
    {
        httpStatus = response.Status; boundary = "已收到响应头";
        var opaqueHeaderMetadata = response.Headers.Where(p => p.Key.Equals("x-codex-turn-state", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => new { length = p.Value.Length, sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Value))) });
        Write("response-headers.json", new { status = response.Status, seconds = elapsed.Elapsed.TotalSeconds, headers = Headers(response.Headers), opaqueHeaderMetadata });
    }
    public void Line(string line)
    {
        if (line.StartsWith("data:", StringComparison.Ordinal)) firstEventSeconds ??= elapsed.Elapsed.TotalSeconds;
        var safe = CleanText(line);
        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            try { var node = JsonNode.Parse(line[5..].TrimStart()); if (node is not null) safe = "data: " + CleanNode(node)!.ToJsonString(); }
            catch (JsonException) { /* Keep malformed server events for diagnosis. */ }
        }
        var size = Encoding.UTF8.GetByteCount(safe) + 1;
        if (bytes + size > Limit) { truncated = true; return; }
        bytes += size; lines++; stream.WriteLine(safe);
        boundary = "已读取流式数据";
    }
    public void HttpError(string body) { Line(body); boundary = "HTTP 错误正文 EOF"; }
    public void EndOfStream() { boundary = "EOF"; eof = true; }
    public void Complete(string model, JsonObject? value)
    { state = "已完成"; responseModel = model; usage = value?.DeepClone() as JsonObject; }
    public void Fail(Exception exception, bool callerCancellationRequested = false)
    {
        failed = true;
        state = exception is OperationCanceledException ? "已取消" : "失败"; error = CleanText(exception.Message);
        transportFailure = exception is AccountException account ? account.TransportFailure
            : exception is OperationCanceledException ? AccountTransportFailure.Capture(exception, callerCancellationRequested) : null;
    }
    private static bool Sensitive(string name) => Regex.IsMatch(name, "(?i)(authorization|cookie|api[-_]?key|access[-_]?token|refresh[-_]?token|id[-_]?token|secret|password|account[-_]?id|session[-_]?id|turn[-_]?state|code_verifier)");
    private Dictionary<string, string> Headers(IReadOnlyDictionary<string, string> headers)
        => headers.ToDictionary(p => p.Key, p => Sensitive(p.Key) ? "<已隐去>" : CleanText(p.Value), StringComparer.OrdinalIgnoreCase);
    private JsonNode? CleanNode(JsonNode? node)
    {
        if (node is JsonObject obj) return new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Sensitive(p.Key) ? (JsonNode?)JsonValue.Create("<已隐去>") : CleanNode(p.Value))));
        if (node is JsonArray arr) return new JsonArray(arr.Select(CleanNode).ToArray());
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return JsonValue.Create(CleanText(text));
        return node?.DeepClone();
    }
    private string CleanText(string text)
    {
        foreach (var secret in secrets)
        {
            text = text.Replace(secret, "<已隐去>", StringComparison.Ordinal);
            text = text.Replace(JsonSerializer.Serialize(secret)[1..^1], "<已隐去>", StringComparison.Ordinal);
            text = text.Replace(Uri.EscapeDataString(secret), "<已隐去>", StringComparison.OrdinalIgnoreCase);
        }
        text = Regex.Replace(text, "(?i)(Bearer\\s+)[A-Za-z0-9._~+/-]+=*", "$1<已隐去>");
        return Regex.Replace(text, "(?i)(\"(?:access_token|refresh_token|id_token|api_key|password|cookie|authorization)\"\\s*:\\s*\")[^\"]*(\")", "$1<已隐去>$2");
    }
    private void Write(string name, object value) => File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, Json));
    public void Dispose()
    {
        if (disposed) return; disposed = true; stream.Dispose();
        Write("trace-summary.json", new { schema = 2, accountIdentityHash, started, state, responseModel, httpStatus, firstEventSeconds, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
            usage = CleanNode(usage), lines, bytes, truncated, readBoundary = boundary, error, transportFailure, safetyBuffering = SafetyBuffering,
            note = "记录实际发送的请求和响应文本事件直到 EOF；凭据已脱敏，换行与 JSON 排版可能规范化。失败/取消时可能只有部分响应，以 readBoundary 为准。无法查看服务端内部推理或隐藏调度。truncated=true 表示记录达到 64 MiB 上限。" });
    }
}
