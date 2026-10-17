using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using FAswitch.Accounts;

namespace FAswitch.Desktop;

// Explicit fixture implementations. No HTTP client, callback socket, browser, or system process is created.
public sealed class OfflineAccountTransport(AccountRepository repository, OfflineLoginBrowser browser, OfflineDefaultLoginBrowser? newAccountBrowser = null) : IAccountTransport
{
    public bool FailRequests { get; set; }
    public bool FailTokenRequests { get; set; }
    public int RequestCount { get; private set; }
    public List<AccountHttpRequest> Requests { get; } = [];
    public Action<AccountHttpRequest>? RequestObserved { get; set; }
    public Func<AccountHttpRequest, CancellationToken, Task<string>>? ResponseFactory { get; set; }
    public Queue<string> Responses { get; } = new();
    public string QualityPayload { get; set; } = "{\"ip\":\"192.0.2.10\",\"asn\":{\"asn\":64500},\"company\":{\"name\":\"Fixture network\"},\"location\":{\"country_code\":\"US\",\"country\":\"United States\",\"city\":\"Fixture city\",\"timezone\":\"America/Chicago\"},\"is_datacenter\":null,\"is_proxy\":null,\"is_vpn\":false,\"is_tor\":false,\"is_abuser\":null}";
    public TaskCompletionSource? HoldResponse { get; set; }
    public string ScorePayload { get; set; } = "{\"ip\":\"192.0.2.10\",\"fraudScore\":5,\"isResidential\":true}";
    public string EgressTimezone { get; set; } = "America/Chicago";
    public bool DelayTimeout { get; set; }
    public TaskCompletionSource? HoldQualityResponse { get; set; }
    public TaskCompletionSource? HoldEgressResponse { get; set; }
    public async Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); RequestCount++; Requests.Add(request);
        RequestObserved?.Invoke(request);
        if ((request.Url == AccountEndpoints.Egress || request.Url == NetCoffeeService.TraceEndpoint) && HoldEgressResponse is not null) await HoldEgressResponse.Task.WaitAsync(cancellationToken);
        if (FailRequests) throw new AccountException(AccountError.Network, "隔离网络替身返回连接失败。");
        if (request.Url == ProxyLatencyService.Endpoint)
        {
            if (DelayTimeout) throw new AccountException(AccountError.Timeout, "fixture timeout");
            return new(204, new MemoryStream());
        }
        string payload;
        if (request.Url == AccountEndpoints.Token)
        {
            if (FailTokenRequests) throw new AccountException(AccountError.Network, "隔离令牌请求失败。");
            var name = browser.LastProfile;
            var profile = repository.Profiles.ListAccounts().FirstOrDefault(a => a.Name == name);
            var id = profile?.HasCredentials == true ? repository.ReadAuth(name)["tokens"]?["account_id"]?.GetValue<string>() ?? "fixture-account" : "fixture-" + name;
            var newAccount = newAccountBrowser?.LastUrl is not null && request.ContentType == "application/x-www-form-urlencoded" && request.Body is { } form && Encoding.UTF8.GetString(form).Contains("fixture-code-" + LoginService.NewAccountRequestProfile, StringComparison.Ordinal);
            if (newAccount) id = newAccountBrowser!.AccountId;
            if (request.ContentType == "application/json" && request.Body is { } body && JsonNode.Parse(body)?["refresh_token"]?.GetValue<string>() is { } refresh && refresh.StartsWith("fixture-refresh-", StringComparison.Ordinal)) id = refresh["fixture-refresh-".Length..];
            var claims = new JsonObject { ["email"] = newAccount ? "account-7eb1b79067@example.invalid" : "account-b8c6e0ac5f@example.invalid", ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = id } };
            var jwt = "eyJhbGciOiJub25lIn0." + OAuthPkce.Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString())) + ".fixture";
            payload = new JsonObject { ["access_token"] = "fixture-access-" + id, ["refresh_token"] = "fixture-refresh-" + id, ["id_token"] = jwt }.ToJsonString();
        }
        else if (request.Url == AccountEndpoints.Usage)
            payload = "{\"email\":\"account-b8c6e0ac5f@example.invalid\",\"plan_type\":\"plus\",\"rate_limit\":{\"allowed\":true,\"limit_reached\":false,\"primary_window\":{\"used_percent\":21,\"limit_window_seconds\":18000,\"reset_after_seconds\":7200},\"secondary_window\":{\"used_percent\":43,\"limit_window_seconds\":604800,\"reset_after_seconds\":86400}},\"rate_limit_reset_credits\":{\"available_count\":2},\"credits\":{\"balance\":12,\"has_credits\":true}}";
        else if (request.Url == AccountEndpoints.ResetCredits) payload = "{\"available_count\":2,\"credits\":[{\"expires_at\":\"2030-01-01T00:00:00Z\",\"status\":\"available\"}]}";
        else if (request.Url == AccountEndpoints.Responses)
        {
            if (HoldResponse is not null) await HoldResponse.Task.WaitAsync(cancellationToken);
            var reply = ResponseFactory is not null ? await ResponseFactory(request, cancellationToken)
                : Responses.Count > 0 ? Responses.Dequeue() : "Ciallo～(∠・ω< )⌒☆";
            payload = "data: " + JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = reply })
                + "\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"model\":\"fixture-model\",\"usage\":{\"cached_input_tokens\":128}}}\n\n";
        }
        else if (request.Url == AccountEndpoints.Egress) payload = JsonSerializer.Serialize(new { success = true, ip = "192.0.2.10", country = "United States", country_code = "US", city = "Fixture city", timezone = new { id = EgressTimezone } });
        else if (request.Url == AccountEndpoints.Trace || request.Url == NetCoffeeService.TraceEndpoint) payload = "ip=192.0.2.10\nloc=US\n";
        else if (NetCoffeeService.IsLookup(request.Url))
        {
            if (HoldQualityResponse is not null) await HoldQualityResponse.Task.WaitAsync(cancellationToken);
            var score = JsonNode.Parse(ScorePayload)!.AsObject();
            var quality = JsonNode.Parse(QualityPayload)!.AsObject();
            var risk = score["fraudScore"] is JsonValue value && value.TryGetValue<int>(out var number) ? (int?)number : null;
            payload = JsonSerializer.Serialize(new {
                ip = quality["ip"]?.GetValue<string>() != "192.0.2.10" ? quality["ip"]?.GetValue<string>() : score["ip"]?.GetValue<string>(),
                trust_score = risk is null ? null : 100 - risk,
                isResidential = score["isResidential"]?.GetValue<bool>(), is_datacenter = quality["is_datacenter"]?.GetValue<bool>(),
                is_proxy = quality["is_proxy"]?.GetValue<bool>(), is_vpn = quality["is_vpn"]?.GetValue<bool>(),
                countryCode = "US", country = "United States", city = "Fixture city", timezone = EgressTimezone,
                asn = 64500, asOrganization = "Fixture network"
            });
        }
        else if (request.Url == IpQualityService.ScoreEndpoint) payload = ScorePayload;
        else if (request.Url.Host == "api.ipapi.is")
        {
            if (HoldQualityResponse is not null) await HoldQualityResponse.Task.WaitAsync(cancellationToken);
            payload = QualityPayload;
        }
        else if (request.Url.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)) payload = "{\"data\":[{\"id\":\"fixture-model\"},{\"id\":\"fixture-model-2\"}]}";
        else throw new AccountException(AccountError.Network, "隔离网络替身未定义此请求。");
        return new AccountHttpResponse(200, new MemoryStream(Encoding.UTF8.GetBytes(payload)));
    }
}

public sealed class OfflineLoginBrowser(AccountRepository repository) : ILoginBrowser, IAccountBrowserNetwork, IAccountPreparationBrowser
{
    public Task<BrowserSessionCleanupResult> CleanupAsync(BrowserLaunch? launch, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(new BrowserSessionCleanupResult(true, "fixture cleanup")); }
    public bool NetworkBrowserOpen { get; set; }
    public bool FailNetworkClose { get; set; }
    public Task<bool> CloseForNetworkChangeAsync(string profileName, CancellationToken token)
    { if (FailNetworkClose) throw new IOException("fixture close failed"); var wasOpen = NetworkBrowserOpen; NetworkBrowserOpen = false; return Task.FromResult(wasOpen); }
    public string LastProfile { get; private set; } = "";
    public Uri? LastUrl { get; private set; }
    public int OpenCount { get; private set; }
    public bool PreparationClosed { get; set; }
    public Task<BrowserLaunch> OpenAccountPreparationAsync(string name, string proxy, EgressResult? egress, CancellationToken token)
    { PreparationClosed = false; return OpenAsync(name, new Uri("https://chatgpt.com/auth/login"), token); }
    public Task<bool> IsPreparationClosedAsync(BrowserLaunch launch, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(PreparationClosed); }
    public Task<BrowserLaunch> OpenAsync(string profileName, Uri authorizeUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); _ = repository.Proxy(profileName);
        LastProfile = profileName; LastUrl = authorizeUrl; OpenCount++;
        return Task.FromResult(new BrowserLaunch("fixture-chrome.exe", Path.Combine(repository.Root, "browser-envs", profileName), ["隔离浏览器替身：未启动真实浏览器。"]));
    }
    public Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(BrowserLaunch launch, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BrowserIdentity>?>([new(999, 1, launch.Executable, launch.ProfileDirectory)]);
}

public sealed class OfflineDefaultLoginBrowser : IProxyBoundLoginBrowser, IVerifiedProxyLoginBrowser, IResumableLoginBrowser, IAccountPreparationBrowser, IBrowserSessionCleanup, IAsyncDisposable
{
    public int ResumeCount { get; private set; }
    public bool PreparationClosed { get; set; }
    public Task<BrowserLaunch> OpenAccountPreparationAsync(string name, string proxy, EgressResult? egress, CancellationToken token)
    { PreparationClosed = false; return OpenAsync(name, new Uri("https://chatgpt.com/auth/login"), token); }
    public Task<bool> IsPreparationClosedAsync(BrowserLaunch launch, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(PreparationClosed); }
    public Task ResumeAsync(BrowserLaunch launch, CancellationToken token)
    { token.ThrowIfCancellationRequested(); ResumeCount++; return Task.CompletedTask; }
    public Func<string, CancellationToken, Task>? Prepare { get; init; }
    public Task PrepareAsync(string proxy, CancellationToken token) => Prepare?.Invoke(proxy, token) ?? Task.CompletedTask;
    public Task<BrowserLaunch> OpenVerifiedAsync(string profileName, Uri authorizeUrl, string proxy, EgressResult egress, CancellationToken token)
        => OpenAsync(profileName, authorizeUrl, token);
    public string Proxy { get; set; } = "";
    public string? SystemProxy { get; set; } = "http://127.0.0.1:7890";
    public string AccountId { get; set; } = "fixture-new-account";
    public string LastProfile { get; private set; } = "";
    public Uri? LastUrl { get; private set; }
    public int OpenCount { get; private set; }
    public string? FailureMessage { get; set; }
    public bool CleanupSucceeds { get; set; } = true;
    public IReadOnlyList<string> CleanupWarnings { get; set; } = [];
    public int CleanupCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public void ResetCallback() => LastUrl = null;
    public Task<BrowserLaunch> OpenAsync(string profileName, Uri authorizeUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailureMessage is { Length: > 0 } failure) throw new AccountException(AccountError.ExitUnavailable, failure);
        LastProfile = profileName; LastUrl = authorizeUrl; OpenCount++;
        return Task.FromResult(new BrowserLaunch("fixture-default-browser", "", ["离线默认浏览器替身：未打开真实网站。"]));
    }
    public Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(BrowserLaunch launch, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BrowserIdentity>?>(null);
    public Task<BrowserSessionCleanupResult> CleanupAsync(BrowserLaunch? launch, CancellationToken token)
    { token.ThrowIfCancellationRequested(); CleanupCalls++; return Task.FromResult(new BrowserSessionCleanupResult(CleanupSucceeds, CleanupSucceeds ? "离线授权会话已结束。" : "离线授权会话退出未确认；缓存保持保留。", CleanupWarnings)); }
    public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
}

public sealed class OfflineLoginCallbackFactory(OfflineLoginBrowser browser, OfflineDefaultLoginBrowser? newAccountBrowser = null) : ILoginCallbackFactory
{
    public int Starts { get; private set; }
    public bool HoldCallback { get; set; }
    public Task<ILoginCallback> StartAsync(string state, CancellationToken cancellationToken) { Starts++; newAccountBrowser?.ResetCallback(); return Task.FromResult<ILoginCallback>(new Callback(this, browser, newAccountBrowser)); }
    private sealed class Callback(OfflineLoginCallbackFactory owner, OfflineLoginBrowser browser, OfflineDefaultLoginBrowser? newAccountBrowser) : ILoginCallback
    {
        private bool cancelled;
        public int Port => 1455;
        public async Task<LoginCallback> WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            if (cancelled) return new(CallbackKind.Cancelled);
            if (owner.HoldCallback) { await Task.Delay(40, cancellationToken); return new(CallbackKind.Waiting); }
            return new(CallbackKind.Code, "fixture-code-" + (newAccountBrowser?.LastUrl is not null ? newAccountBrowser.LastProfile : browser.LastProfile));
        }
        public bool Cancel() { cancelled = true; return true; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
