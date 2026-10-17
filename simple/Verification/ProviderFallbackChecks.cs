using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace ChunsonLogin;

internal static class ProviderFallbackChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const string who = """{"success":true,"ip":"198.51.100.24","country":"United States","country_code":"US","city":"New York","timezone":{"id":"America/New_York"}}""";
        const string api = """{"ip":"198.51.100.24","country":"United States","city":"New York","timezone":"America/New_York"}""";
        const string backup = """{"ip":"198.51.100.24","country_code":"US","country_name":"United States","city":"New York","timezone":"America/New_York"}""";
        string backupBody = backup;
        string primaryBody = api;
        var failures = new HashSet<string>();
        var status = new Dictionary<string, HttpStatusCode>();
        var calls = new List<string>();
        var policies = new List<bool>();
        string? hanging = null;
        CancellationTokenSource? cancelAtFallback = null;
        var probe = new EgressProbe(policy =>
        {
            policies.Add(policy.UseProxy && policy.Proxy is WebProxy proxy && proxy.Address?.AbsoluteUri == "http://127.0.0.1:7890/"
                && !proxy.BypassProxyOnLocal && !policy.UseCookies && !policy.AllowAutoRedirect);
            return new Handler(async (request, token) =>
            {
                var host = request.RequestUri!.Host;
                calls.Add(host);
                if (host == "ipapi.co") cancelAtFallback?.Cancel();
                token.ThrowIfCancellationRequested();
                if (host == hanging) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                if (failures.Contains(host)) throw new HttpRequestException("fixture unavailable");
                return new(status.GetValueOrDefault(host, HttpStatusCode.OK))
                { Content = new StringContent(host switch { "ipwho.is" => who, "api.ipapi.is" => primaryBody, "ipapi.co" => backupBody, _ => throw new Exception("unexpected endpoint") }, Encoding.UTF8, "application/json") };
            });
        }, timeout: TimeSpan.FromMilliseconds(100));
        var route = new NetworkRoute("http://127.0.0.1:7890", "fixture");
        var result = await probe.ProbeAsync(route, default);
        check(result.QueryNote.Length == 0 && calls.Count == 2 && !calls.Contains("ipapi.co"), "fallback: healthy primary pair avoids extra request");
        check(result.Sources == "ipwho.is + ipapi.is", "fallback: successful sources are recorded");

        failures.Add("api.ipapi.is"); calls.Clear();
        result = await probe.ProbeAsync(route, default);
        check(result.Ip == "198.51.100.24" && result.WindowsTimezone == "Eastern Standard Time" && result.Sources == "ipwho.is + ipapi.co",
            "fallback: reported ipapi.is connection failure succeeds with an agreeing alternate");
        check(result.QueryNote.Contains("备用") && calls.Count == 3, "fallback: alternate is bounded and disclosed");
        check(policies.All(value => value), "fallback: every attempt retains exact proxy, HTTPS policy and no cookies");

        failures.Clear(); failures.Add("ipwho.is");
        result = await probe.ProbeAsync(route, default);
        check(result.Sources == "ipapi.is + ipapi.co" && result.Country == "United States", "fallback: anonymous ipapi country name agrees with backup ISO country when primary fails");

        failures.Clear(); status["api.ipapi.is"] = HttpStatusCode.TooManyRequests;
        check((await probe.ProbeAsync(route, default)).QueryNote.Contains("备用"), "fallback: rate-limited primary uses alternate");
        status.Clear(); hanging = "api.ipapi.is";
        check((await probe.ProbeAsync(route, default)).Sources.Contains("ipapi.co"), "fallback: request timeout uses alternate after bounded wait");
        hanging = null;
        primaryBody = "{broken";
        check((await probe.ProbeAsync(route, default)).Sources.Contains("ipapi.co"), "fallback: incomplete provider response uses alternate without fabricating data");
        primaryBody = api.Replace("198.51.100.24", "198.51.100.25"); calls.Clear();
        await Refused("不同出口 IP");
        check(calls.Count == 2, "fallback: real primary disagreement is not hidden by a third-provider vote");
        primaryBody = api; failures.Add("api.ipapi.is");
        backupBody = backup.Replace("America/New_York", "America/Los_Angeles");
        await Refused("时区不一致");
        backupBody = backup.Replace("198.51.100.24", "198.51.100.25");
        await Refused("不同出口 IP");
        backupBody = backup.Replace("\"US\"", "\"CA\"");
        await Refused("国家定位不一致");
        backupBody = backup.Replace("America/New_York", "");
        await Refused("ipapi.co 未返回可识别");
        backupBody = "{\"error\":true,\"reason\":\"RateLimited\"}";
        await Refused("ipapi.co 没有返回成功");
        backupBody = backup; failures.Add("ipapi.co");
        var message = await Refused("1/2 个有效来源");
        check(message.Contains("ipapi.is 定位服务连接") && message.Contains("ipapi.co 定位服务连接") && !message.Contains("请检查 FlClash 当前网络"),
            "fallback: partial outage names providers instead of asserting proxy failure");
        failures.Add("ipwho.is"); calls.Clear();
        await Refused("0/2 个有效来源");
        check(calls.Count == 3, "fallback: complete outage has three bounded attempts and no endless retries");
        failures.Clear(); failures.Add("api.ipapi.is");
        using (cancelAtFallback = new CancellationTokenSource())
        {
            try { await probe.ProbeAsync(route, cancelAtFallback.Token); check(false, "fallback: cancellation expected"); }
            catch (OperationCanceledException) { check(true, "fallback: user cancellation during alternate propagates"); }
        }
        cancelAtFallback = null;
        return;

        async Task<string> Refused(string text)
        {
            try { await probe.ProbeAsync(route, default); throw new Exception("expected refusal: " + text); }
            catch (InvalidOperationException error)
            { check(error.Message.Contains(text, StringComparison.Ordinal), "fallback: refusal retains " + text); return error.Message; }
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}
