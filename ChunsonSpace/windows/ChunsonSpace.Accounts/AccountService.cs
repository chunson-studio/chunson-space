using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunsonSpace.Application;
using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

public sealed partial class AccountService(AccountRepository repository, IAccountTransport transport, IAccountClock? clock = null) : IResolvedProxyProfilePreparation
{
    private readonly IAccountClock clock = clock ?? new SystemAccountClock();
    private readonly AccountNetwork network = new(transport);
    public Task<RefreshResult> RefreshAsync(string profileName, CancellationToken cancellationToken = default)
        => RefreshCoreAsync(profileName, repository.Proxy(profileName), cancellationToken);
    private async Task<RefreshResult> RefreshCoreAsync(string profileName, string resolvedProxy, CancellationToken cancellationToken)
    {
        var proxy = HttpAccountTransport.RequireHttpProxy(resolvedProxy);
        var auth = repository.ReadAuth(profileName);
        var tokens = auth["tokens"] as JsonObject ?? throw new AccountException(AccountError.MissingCredentials, "账号凭据不完整，请先登录。");
        var refresh = AccountJson.Text(tokens, "refresh_token");
        if (refresh.Length == 0) throw new AccountException(AccountError.MissingCredentials, "没有 refresh_token，请重新登录。");
        var body = new JsonObject { ["grant_type"] = "refresh_token", ["client_id"] = AccountEndpoints.ClientId, ["refresh_token"] = refresh };
        var response = await network.JsonAsync(new(HttpMethod.Post, AccountEndpoints.Token, proxy, true,
            new Dictionary<string, string> { ["originator"] = "codex_cli_rs", ["user-agent"] = "" }, Encoding.UTF8.GetBytes(body.ToJsonString()), "application/json"),
            TimeSpan.FromSeconds(25), cancellationToken, refresh, AccountJson.Text(tokens, "access_token"), AccountJson.Text(tokens, "id_token"));
        var access = AccountJson.Text(response, "access_token");
        if (access.Length == 0) throw new AccountException(AccountError.CorruptResponse, "上游未返回 access_token，旧凭据未修改。");
        var id = AccountJson.Text(response, "id_token");
        if (id.Length > 0)
        {
            var refreshedId = AccountJson.Text(JwtClaims.Read(id)["https://api.openai.com/auth"] as JsonObject, "chatgpt_account_id");
            if (refreshedId.Length > 0 && refreshedId != AccountJson.Text(tokens, "account_id")) throw new AccountException(AccountError.IdentityMismatch, "刷新返回的账号身份不一致，旧凭据未修改。");
            tokens["id_token"] = id;
        }
        tokens["access_token"] = access;
        var newRefresh = AccountJson.Text(response, "refresh_token");
        if (newRefresh.Trim().Length > 0) tokens["refresh_token"] = newRefresh;
        auth["last_refresh"] = this.clock.UtcNow.ToString("O");
        var written = repository.SaveLogin(profileName, auth);
        return new(true, "token 已刷新。" + string.Join(" ", written.Warnings));
    }
    public async Task<IReadOnlyList<string>> PrepareAsync(IdentityRef target, CancellationToken cancellationToken)
    {
        if (target.Kind == IdentityKind.Provider) return [];
        try { return await PrepareWithProxyAsync(target, repository.Proxy(target.Name), cancellationToken); }
        catch (AccountException error) { return ["该账号令牌预热未完成：" + error.Message]; }
    }
    public async Task<IReadOnlyList<string>> PrepareWithProxyAsync(IdentityRef target, string resolvedProxy, CancellationToken cancellationToken)
    {
        if (target.Kind == IdentityKind.Provider) return [];
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = HttpAccountTransport.RequireHttpProxy(resolvedProxy);
            var credentials = Credentials(repository.ReadAuth(target.Name));
            var expiry = AccountJson.Integer(JwtClaims.Read(credentials.Access)["exp"]);
            // Expiry is only a refresh hint. The server still validates the credential.
            if (expiry > this.clock.UtcNow.AddMinutes(2).ToUnixTimeSeconds()) return [];
            await RefreshCoreAsync(target.Name, resolvedProxy, cancellationToken); return [];
        }
        catch (AccountException error) { return ["该账号令牌预热未完成：" + error.Message]; }
    }
    public async Task<QuotaResult> FetchQuotaAsync(string profileName, CancellationToken cancellationToken = default)
    {
        var session = new AuthSession(repository.ReadAuth(profileName), repository.Proxy(profileName));
        var payload = await AuthorizedJson(profileName, AccountEndpoints.Usage, session, cancellationToken);
        var rate = payload["rate_limit"] as JsonObject;
        var count = AccountJson.Integer((payload["rate_limit_reset_credits"] as JsonObject)?["available_count"]) ?? 0;
        var reset = new ResetCredits(count, [], null, null, null);
        if (count > 0)
            try { reset = ParseResetCredits(await AuthorizedJson(profileName, AccountEndpoints.ResetCredits, session, cancellationToken)); }
            catch (AccountException) { /* Details are optional; quota count remains visible. */ }
        var credits = payload["credits"] as JsonObject;
        var result = new QuotaResult(AccountJson.Text(payload, "email"), AccountJson.Text(payload, "plan_type"), AccountJson.Boolean(rate?["allowed"]), AccountJson.Boolean(rate?["limit_reached"]),
            Window(rate?["primary_window"] as JsonObject), Window(rate?["secondary_window"] as JsonObject), reset, credits?["balance"]?.DeepClone(), AccountJson.Boolean(credits?["has_credits"]), this.clock.UtcNow, session.Refreshed);
        var cache = payload.DeepClone().AsObject(); cache["ok"] = true; cache["fetched_at"] = result.FetchedAt.ToString("O");
        repository.WriteQuotaCache(profileName, cache);
        return result;
    }
    public async Task<ConnectivityResult> TestAccountAsync(string profileName, CancellationToken cancellationToken = default)
    {
        repository.RequireTestsAllowed(profileName);
        var session = new AuthSession(repository.ReadAuth(profileName), repository.Proxy(profileName));
        var started = this.clock.UtcNow;
        var identity = FileProfileStore.AccountIdentityHash(Credentials(session.Auth).Account);
        foreach (var model in ModelTestParameters.Models)
        {
            try
            {
                var result = await ReadResponses(profileName, model, session, cancellationToken);
                AccountEvidence.Apply(repository.Profiles, profileName, identity, started, AccountEvidence.Id("connectivity:" + Guid.NewGuid()),
                    [new(model, result.Model, "high", true, result.Observation)]);
                var reply = result.Text;
                var status = reply.Contains("(∠・ω< )", StringComparison.Ordinal) ? "good" : reply.Contains("ciallo", StringComparison.OrdinalIgnoreCase) ? "luna" : reply.Contains("ciao", StringComparison.OrdinalIgnoreCase) ? "old55" : "bad";
                var currentTokens = session.Auth["tokens"] as JsonObject;
                return new(status, result.Model.Length > 0 ? result.Model : model, ErrorRedaction.ModelReply(reply, AccountJson.Text(currentTokens, "access_token"), AccountJson.Text(currentTokens, "refresh_token"), AccountJson.Text(currentTokens, "id_token")), TokenUsageReader.Read(result.Usage), this.clock.UtcNow - started, reply.Contains("Ciallo～(∠・ω< )⌒☆", StringComparison.Ordinal));
            }
            catch (AccountException error) when (error.Status is 400 or 404) { }
            catch (Exception error) when (error is AccountException or OperationCanceledException)
            {
                AccountEvidence.Apply(repository.Profiles, profileName, identity, started, AccountEvidence.Id("connectivity:" + Guid.NewGuid()), [new(model, "", "high", false, null)], false);
                throw;
            }
        }
        AccountEvidence.Apply(repository.Profiles, profileName, identity, started, AccountEvidence.Id("connectivity:" + Guid.NewGuid()), [], false);
        throw new AccountException(AccountError.Http, "可用模型未完成连通性响应。");
    }
    private async Task<JsonObject> AuthorizedJson(string name, Uri uri, AuthSession session, CancellationToken token)
    {
        var credentials = Credentials(session.Auth);
        try { return await network.JsonAsync(new(HttpMethod.Get, uri, session.Proxy, true, Headers(credentials.Access, credentials.Account)), TimeSpan.FromSeconds(25), token, credentials.Access, credentials.Account); }
        catch (AccountException error) when (error.Status == 401 && !session.Refreshed)
        {
            await RefreshAsync(name, token); session.Refreshed = true; session.Auth = repository.ReadAuth(name);
            credentials = Credentials(session.Auth);
            return await network.JsonAsync(new(HttpMethod.Get, uri, session.Proxy, true, Headers(credentials.Access, credentials.Account)), TimeSpan.FromSeconds(25), token, credentials.Access, credentials.Account);
        }
    }
    private sealed class AuthSession(JsonObject auth, string proxy)
    { public JsonObject Auth { get; set; } = auth; public string Proxy { get; } = proxy; public bool Refreshed { get; set; } }
    private static (string Access, string Account) Credentials(JsonObject auth)
    {
        var tokens = auth["tokens"] as JsonObject;
        var access = AccountJson.Text(tokens, "access_token");
        var account = AccountJson.Text(tokens, "account_id");
        if (access.Length == 0 || account.Length == 0) throw new AccountException(AccountError.MissingCredentials, "账号凭据不完整，请重新登录。");
        return (access, account);
    }
    private static Dictionary<string, string> Headers(string access, string account) => new()
    { ["authorization"] = "Bearer " + access, ["chatgpt-account-id"] = account, ["user-agent"] = "", ["sec-fetch-site"] = "none", ["sec-fetch-mode"] = "no-cors", ["sec-fetch-dest"] = "empty", ["priority"] = "u=4, i" };
    private static QuotaWindow? Window(JsonObject? window)
    {
        if (window is null) return null;
        if (window["used_percent"] is not JsonValue value || !value.TryGetValue<double>(out var used) || !double.IsFinite(used)) return null;
        return new(Math.Round(used, 1), Math.Round(Math.Clamp(100 - used, 0, 100), 1), AccountJson.Integer(window["limit_window_seconds"]) ?? 0,
            AccountJson.Integer(window["reset_after_seconds"]) ?? 0, AccountJson.Integer(window["reset_at"]));
    }
    private ResetCredits ParseResetCredits(JsonObject payload)
    {
        var entries = new[] { "credits", "rate_limit_reset_credits", "items", "data" }.Select(key => payload[key] as JsonArray).FirstOrDefault(value => value is not null);
        var items = new List<ResetCredit>();
        foreach (var entry in entries?.OfType<JsonObject>() ?? [])
        {
            var expiry = AccountJson.Text(entry, "expires_at"); if (expiry.Length == 0) expiry = AccountJson.Text(entry, "expiresAt");
            var resetType = AccountJson.Text(entry, "reset_type"); if (resetType.Length == 0) resetType = AccountJson.Text(entry, "resetType");
            long? left = DateTimeOffset.TryParse(expiry, out var time) ? (long)(time - this.clock.UtcNow).TotalSeconds : null;
            items.Add(new(expiry, resetType, AccountJson.Text(entry, "status"), left));
        }
        var count = AccountJson.Integer(payload["available_count"] ?? payload["availableCount"]) ?? items.Count;
        return new(count, items, AccountJson.Integer(payload["total_earned_count"] ?? payload["totalEarnedCount"]), AccountJson.Boolean(payload["immediate_reset_purchase_eligible"] ?? payload["immediateResetPurchaseEligible"]), items.Where(item => item.SecondsLeft > 0).Select(item => item.SecondsLeft).DefaultIfEmpty(null).Min());
    }
    private async Task<(string Text, string Model, JsonObject? Usage, SafetyBufferingObservation Observation)> ReadResponses(string name, string model, AuthSession session, CancellationToken token,
        string prompt = "ciallo", string instructions = BuiltInTestInstructions.Helpful, string? effort = "high",
        IReadOnlyList<string>? images = null, TimeSpan? requestTimeout = null, bool refreshAuthorization = true, ModelRequestTrace? trace = null)
    {
        repository.RequireTestsAllowed(name);
        ModelTestParameters.RequireModel(model);
        ModelTestParameters.RequireEffort(effort ?? "");
        var credentials = Credentials(session.Auth);
        if (repository.Profiles.ReadAccountIdentityHash(name) != FileProfileStore.AccountIdentityHash(credentials.Account))
            throw new AccountException(AccountError.IdentityMismatch, "账号身份已变化，本次测试未继续请求。");
        var headers = Headers(credentials.Access, credentials.Account);
        headers["accept"] = "text/event-stream"; headers["originator"] = "codex_cli_rs"; headers["session-id"] = Guid.NewGuid().ToString();
        var content = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = prompt });
        foreach (var image in images ?? [])
        {
            var file = new FileInfo(image);
            if (!file.Exists || file.Length > 10 * 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("预览图不存在或无法读取，未发送看图请求。");
            var bytes = await File.ReadAllBytesAsync(image, token);
            if (!bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new IOException("预览图不是 PNG，未发送看图请求。");
            content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = "data:image/png;base64," + Convert.ToBase64String(bytes), ["detail"] = "high" });
        }
        var body = new JsonObject { ["model"] = model, ["instructions"] = instructions,
            ["input"] = new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = content }),
            ["tools"] = new JsonArray(), ["tool_choice"] = "none", ["parallel_tool_calls"] = false, ["store"] = false, ["stream"] = true };
        if (effort is not null) body["reasoning"] = new JsonObject { ["effort"] = effort };
        var timeout = requestTimeout ?? TimeSpan.FromSeconds(90);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        try
        {
            var request = new AccountHttpRequest(HttpMethod.Post, AccountEndpoints.Responses, session.Proxy, true, headers, Encoding.UTF8.GetBytes(body.ToJsonString()), "application/json");
            trace?.Request(request);
            await using var response = await network.SendAsync(request, timeout, deadline.Token);
            trace?.Response(response);
            if (response.Status == 401 && !session.Refreshed && refreshAuthorization)
            {
                await RefreshAsync(name, token); session.Refreshed = true; session.Auth = repository.ReadAuth(name);
                return await ReadResponses(name, model, session, token, prompt, instructions, effort, images, requestTimeout, refreshAuthorization);
            }
            if (response.Status is < 200 or >= 300)
            {
                string detail;
                if (trace is not null)
                {
                    using var errorReader = new StreamReader(response.Content, Encoding.UTF8, false, 8192, true);
                    detail = await errorReader.ReadToEndAsync(deadline.Token);
                    trace.HttpError(detail);
                }
                else detail = Encoding.UTF8.GetString(await AccountNetwork.ReadLimited(response.Content, 4096, deadline.Token));
                throw new AccountException(AccountError.Http, $"模型服务返回 HTTP {response.Status}：" + ErrorRedaction.Scrub(detail, credentials.Access, credentials.Account), response.Status);
            }
            using var reader = new StreamReader(response.Content, Encoding.UTF8, false, 8192, true);
            var parts = new StringBuilder();
            var observation = new SafetyBufferingCollector();
            var eventData = new StringBuilder();
            var completed = false;
            var resolved = "";
            AccountException? streamFailure = null;
            JsonObject? usage = null;
            void Consume()
            {
                if (eventData.Length == 0) return;
                var raw = eventData.ToString(); eventData.Clear();
                if (raw == "[DONE]") return;
                JsonObject payload;
                try { payload = JsonNode.Parse(raw) as JsonObject ?? throw new JsonException(); }
                catch (JsonException) { throw new AccountException(AccountError.CorruptResponse, "连通性事件流格式错误。"); }
                trace?.ObserveEvent(payload);
                observation.Observe(payload);
                switch (AccountJson.Text(payload, "type"))
                {
                    case "response.output_text.delta": parts.Append(AccountJson.Text(payload, "delta")); break;
                    case "response.failed": case "response.incomplete": case "error":
                        var failure = payload["response"]?["error"]?["message"]?.ToString() ?? payload["error"]?["message"]?.ToString()
                            ?? payload["message"]?.ToString() ?? "服务未完成模型响应。";
                        streamFailure = new AccountException(AccountError.Network, ErrorRedaction.Scrub(failure, credentials.Access, credentials.Account));
                        if (trace is null) throw streamFailure;
                        break;
                    case "response.completed":
                        var item = payload["response"] as JsonObject ?? throw new AccountException(AccountError.CorruptResponse, "完成事件缺少响应。");
                        if (AccountJson.Text(item, "status") is not ("" or "completed")) throw new AccountException(AccountError.Network, "服务未完成连通性响应。");
                        resolved = AccountJson.Text(item, "model"); usage = item["usage"] as JsonObject;
                        if (parts.Length == 0)
                            foreach (var output in (item["output"] as JsonArray)?.OfType<JsonObject>() ?? [])
                                foreach (var itemContent in (output["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
                                    if (AccountJson.Text(itemContent, "type") == "output_text") parts.Append(AccountJson.Text(itemContent, "text"));
                        completed = true; break;
                }
                if (parts.Length > 1048576) throw new AccountException(AccountError.CorruptResponse, "响应文本过长。");
            }
            string? line;
            while ((line = await reader.ReadLineAsync(deadline.Token)) is not null)
            {
                trace?.Line(line);
                if (line.Length == 0) { Consume(); continue; }
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                if (eventData.Length > 0) eventData.Append('\n');
                eventData.Append(line[5..].TrimStart(' '));
                if (eventData.Length > 1048576) throw new AccountException(AccountError.CorruptResponse, "事件内容过长。");
            }
            Consume();
            trace?.EndOfStream();
            if (streamFailure is not null) throw streamFailure;
            if (!completed) throw new AccountException(AccountError.Network, "事件流未收到 response.completed，未确认成功。");
            if (string.IsNullOrWhiteSpace(parts.ToString())) throw new AccountException(AccountError.CorruptResponse, "完成响应没有有效文本，未确认成功。");
            trace?.Complete(resolved, usage);
            return (parts.ToString(), resolved, usage, observation.Snapshot(true));
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested) { throw new AccountException(AccountError.Timeout, "模型请求或响应读取超时，未确认成功。", transportFailure: AccountTransportFailure.Capture(error, false, deadline.IsCancellationRequested)); }
        catch (IOException error) { throw new AccountException(AccountError.Network, "模型响应连接中断，未确认成功。", transportFailure: AccountTransportFailure.Capture(error)); }
        catch (HttpRequestException error) { throw new AccountException(AccountError.Network, "模型响应连接中断，未确认成功。", transportFailure: AccountTransportFailure.Capture(error)); }
    }
}

public sealed class ProviderService(AccountRepository repository, IAccountTransport transport, IAccountClock? clock = null)
{
    private readonly AccountNetwork network = new(transport);
    private readonly IAccountClock clock = clock ?? new SystemAccountClock();
    public ProviderUpdateResult Update(string name, string baseUrl, string apiKey = "", string model = "")
    {
        if (repository.IsPending) throw new AccountException(AccountError.Busy, "桌面启动结果尚未确认，请等待后再编辑供应商。");
        return repository.Profiles.UpdateProvider(name, baseUrl, apiKey, model, repository.ActiveHome);
    }
    public async Task<ProviderCheckResult> CheckAsync(string name, CancellationToken token = default)
    {
        var provider = repository.ReadProvider(name);
        var baseUrl = ProfileRules.BaseUrl(AccountJson.Text(provider, "base_url"));
        var key = AccountJson.Text(provider, "api_key");
        if (key.Length == 0) throw new AccountException(AccountError.MissingCredentials, "供应商缺少 API Key。");
        var started = this.clock.UtcNow;
        var response = await network.PayloadAsync(new(HttpMethod.Get, new Uri(baseUrl + "/models"), "", false,
            new Dictionary<string, string> { ["Authorization"] = "Bearer " + key, ["Accept"] = "application/json", ["User-Agent"] = "" }), TimeSpan.FromSeconds(15), 400000, token, key);
        var payload = AccountJson.Parse(response.Body);
        var models = (payload["data"] as JsonArray)?.OfType<JsonObject>().Select(item => AccountJson.Text(item, "id")).Where(id => id.Length > 0).ToArray() ?? [];
        return new(response.Status, models.Take(40).ToArray(), models.Length, this.clock.UtcNow - started);
    }
}

