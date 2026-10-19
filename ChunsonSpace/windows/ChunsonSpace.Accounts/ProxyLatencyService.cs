using System.Diagnostics;

namespace ChunsonSpace.Accounts;

public enum ProxyLatencyState { Success, Timeout, Failed, Cancelled }
public sealed record ProxyLatencyReport(ProxyLatencyState State, long? Milliseconds, string Message);

public sealed class ProxyLatencyService(IAccountTransport transport)
{
    public static readonly Uri Endpoint = new("https://www.gstatic.com/generate_204");
    private readonly AccountNetwork network = new(transport);
    public async Task<ProxyLatencyReport> ProbeAsync(string proxy, CancellationToken token = default)
    {
        proxy = HttpAccountTransport.RequireHttpProxy(proxy);
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await network.PayloadAsync(new(HttpMethod.Get, Endpoint, proxy, true, new Dictionary<string, string>()), TimeSpan.FromSeconds(8), 4096, token);
            return response.Status == 204
                ? new(ProxyLatencyState.Success, watch.ElapsedMilliseconds, "HTTPS 延迟；包含首次连接与 TLS，非 ICMP ping。")
                : new(ProxyLatencyState.Failed, null, "延迟目标未返回预期的 HTTP 204。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (AccountException error) when (error.Code is not (AccountError.ExitUnavailable or AccountError.Cancelled))
        { return new(error.Code == AccountError.Timeout ? ProxyLatencyState.Timeout : ProxyLatencyState.Failed, null, error.Code == AccountError.Timeout ? "timeout · 8 秒内未完成 HTTPS 连接。" : "延迟测试失败；不代表所有网站均不可达。"); }
    }
}
