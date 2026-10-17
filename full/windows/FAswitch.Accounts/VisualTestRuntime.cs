using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FAswitch.Accounts;

public sealed class CodexVisualTestRuntime(AccountRepository repository, Func<string> findCli,
    Func<string> findBrowser, Func<string, CancellationToken, Task>? prepareRoute = null,
    Func<ProcessStartInfo, string?, CancellationToken, Task<(int Code, string Output, string Error)>>? execute = null) : IVisualTestRuntime
{
    public async Task<VisualReply> ReplyAsync(string profile, string model, string prompt, IReadOnlyList<string> images, string directory, CancellationToken token)
    {
        var cli = findCli();
        var authPath = Path.Combine(repository.AccountPath(profile), "auth.json");
        var raw = await File.ReadAllBytesAsync(authPath, token);
        var auth = JsonNode.Parse(raw) as JsonObject ?? throw new IOException("授权文件无效。");
        var credentials = auth["tokens"] as JsonObject ?? throw new IOException("缺少账号授权。");
        if (string.IsNullOrWhiteSpace(credentials["account_id"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(credentials["access_token"]?.GetValue<string>()) || auth["OPENAI_API_KEY"]?.GetValue<string>() is { Length: > 0 })
            throw new IOException("请选择已保存的 ChatGPT 账号授权。");
        var secrets = credentials.Select(p => p.Value?.ToString() ?? "").Where(s => s.Length > 0).ToList();
        var sourceHash = SHA256.HashData(raw);
        var proxy = repository.Proxy(profile);
        if (prepareRoute is not null) await prepareRoute(proxy, token);
        var privateHome = Path.Combine(Path.GetTempPath(), "faswitch-visual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateHome); Directory.CreateDirectory(directory);
        var copiedAuth = Path.Combine(privateHome, "auth.json");
        var watch = Stopwatch.StartNew();
        string stdout = "", stderr = "", version = "unknown";
        var exit = -1; var changed = false; var sourceChanged = false; var cleaned = false;
        try
        {
            await File.WriteAllBytesAsync(copiedAuth, raw, token);
            var versionInfo = NewProcess(cli, privateHome); versionInfo.ArgumentList.Add("--version");
            var versionResult = await (execute ?? ExecuteAsync)(versionInfo, null, token);
            version = versionResult.Output.Trim();
            if (versionResult.Code != 0 || !System.Text.RegularExpressions.Regex.IsMatch(version, @"^codex(?:-cli)? \d+\.\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new IOException("未找到可用的原生 Codex CLI。");
            var info = BuildStartInfo(cli, privateHome, proxy, model, images);
            var result = await (execute ?? ExecuteAsync)(info, prompt, token);
            stdout = result.Output; stderr = result.Error; exit = result.Code;
        }
        finally
        {
            sourceChanged = !File.Exists(authPath) || !SHA256.HashData(File.ReadAllBytes(authPath)).SequenceEqual(sourceHash);
            changed = !File.Exists(copiedAuth) || !SHA256.HashData(File.ReadAllBytes(copiedAuth)).SequenceEqual(sourceHash);
            if (changed && File.Exists(copiedAuth))
            {
                try { foreach (var item in (JsonNode.Parse(File.ReadAllText(copiedAuth))?["tokens"] as JsonObject ?? [])) if (item.Value?.ToString() is { Length: > 0 } value) secrets.Add(value); }
                catch (System.Text.Json.JsonException) { }
            }
            if (!changed)
            {
                cleaned = LocalVisualRenderer.TryDeletePrivateDirectory(privateHome, Path.GetTempPath(), "faswitch-visual-");
            }
            // Preserve HTML/JS assignments such as state=...; query-string redaction would corrupt generated code.
            foreach (var secret in secrets.OrderByDescending(s => s.Length))
            {
                stdout = stdout.Replace(secret, "<已隐去>", StringComparison.Ordinal);
                stdout = stdout.Replace(JsonSerializerText(secret), "<已隐去>", StringComparison.Ordinal);
            }
            stderr = ErrorRedaction.Scrub(stderr, secrets.ToArray());
            File.WriteAllText(Path.Combine(directory, "events.jsonl"), stdout);
            File.WriteAllText(Path.Combine(directory, "stderr.txt"), stderr);
            File.WriteAllText(Path.Combine(directory, "isolation.json"), System.Text.Json.JsonSerializer.Serialize(new { sourceUnchanged = !sourceChanged, copiedAuthChanged = changed, credentialsCleaned = cleaned, retainedHome = cleaned ? null : privateHome, exitCode = exit, cliVersion = version, requestTimeoutSeconds = (int?)null }));
        }
        if (changed || sourceChanged || !cleaned) throw new IOException("授权保全检查未通过，测试已停止；诊断目录保留恢复位置。");
        var events = new List<JsonObject>();
        foreach (var line in stdout.Split('\n')) try { if (JsonNode.Parse(line) is JsonObject item) events.Add(item); } catch (System.Text.Json.JsonException) { }
        var completed = events.LastOrDefault(e => e["type"]?.ToString() == "turn.completed");
        var tools = events.Any(e => e["type"]?.ToString() is "item.started" or "item.updated" or "item.completed" && e["item"]?["type"]?.ToString() is not (null or "agent_message" or "reasoning" or "plan"));
        if (tools) throw new IOException("本轮出现非预期工具调用，已停止。");
        if (exit != 0 || completed is null) throw new IOException(DescribeFailure(events));
        var reply = string.Join("\n", events.Where(e => e["type"]?.ToString() == "item.completed" && e["item"]?["type"]?.ToString() == "agent_message").Select(e => e["item"]?["text"]?.ToString()));
        var usage = completed["usage"] as JsonObject;
        long? cached = FAswitch.Core.TokenUsageReader.Read(usage).CachedInputTokens;
        return new(reply, cached, watch.Elapsed.TotalSeconds, version);
    }
    private static string DescribeFailure(IReadOnlyList<JsonObject> events)
    {
        var failure = events.LastOrDefault(e => e["type"]?.ToString() == "turn.failed")?["error"]?["message"]?.ToString()
            ?? events.LastOrDefault(e => e["type"]?.ToString() == "error")?["message"]?.ToString();
        if (string.IsNullOrWhiteSpace(failure)) return "Codex CLI 未完整返回；未取得具体错误，诊断已保存，不自动重试。";
        var reason = failure.Contains("at capacity", StringComparison.OrdinalIgnoreCase) || failure.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
            ? "所选模型当前容量不足，请稍后手动重试；CLI 已收到服务响应。"
            : failure.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || failure.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
                ? "服务返回用量或请求限制，请查看额度后再试。"
                : failure.Contains("Connection failed", StringComparison.OrdinalIgnoreCase) || failure.Contains("error sending request", StringComparison.OrdinalIgnoreCase)
                    ? "请求连接失败，请检查当前账号的代理与网络。"
                    : "CLI 或服务返回错误。";
        return reason + "不自动重试。\n原始错误：" + ErrorRedaction.Scrub(failure);
    }
    public static ProcessStartInfo BuildStartInfo(string cli, string home, string proxy, string model, IReadOnlyList<string> images)
    {
        HttpAccountTransport.RequireHttpProxy(proxy);
        var info = NewProcess(cli, home); info.RedirectStandardInput = true;
        foreach (var key in info.Environment.Keys.ToArray())
            if (key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CHATGPT_", StringComparison.OrdinalIgnoreCase) || key.Contains("FEDERAT", StringComparison.OrdinalIgnoreCase)
                || key is "AWS_WEB_IDENTITY_TOKEN_FILE" or "GOOGLE_APPLICATION_CREDENTIALS" or "AZURE_OPENAI_API_KEY" or "AZURE_OPENAI_AD_TOKEN") info.Environment.Remove(key);
        info.Environment["CODEX_HOME"] = home;
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }) info.Environment[key] = proxy;
        info.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
        foreach (var arg in new[] { "exec", "--ephemeral", "--ignore-user-config", "--skip-git-repo-check", "--sandbox", "read-only", "--json", "--model", model }) info.ArgumentList.Add(arg);
        foreach (var config in new[] { "model_reasoning_effort=\"high\"", "service_tier=\"default\"", "mcp_servers={}", "cli_auth_credentials_store=\"file\"", "project_doc_max_bytes=0", "instructions=\"\"", "developer_instructions=\"\"", "web_search=\"disabled\"",
            "model_provider=\"visual_test\"", "model_providers.visual_test.name=\"Visual test\"", "model_providers.visual_test.base_url=\"https://chatgpt.com/backend-api/codex\"", "model_providers.visual_test.wire_api=\"responses\"", "model_providers.visual_test.requires_openai_auth=true", "model_providers.visual_test.supports_websockets=false", "model_providers.visual_test.request_max_retries=0", "model_providers.visual_test.stream_max_retries=0" }) { info.ArgumentList.Add("-c"); info.ArgumentList.Add(config); }
        foreach (var flag in new[] { "hooks", "apps", "plugins", "shell_tool", "unbounded_connection_retries" }) { info.ArgumentList.Add("--disable"); info.ArgumentList.Add(flag); }
        foreach (var image in images) { info.ArgumentList.Add("--image"); info.ArgumentList.Add(image); }
        info.ArgumentList.Add("-"); return info;
    }
    public Task RenderAsync(string page, string screenshot, CancellationToken token) => new LocalVisualRenderer(findBrowser).RenderAsync(page, screenshot, token);
    private static ProcessStartInfo NewProcess(string executable, string home) => new(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = home, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
    private static string JsonSerializerText(string text) => System.Text.Json.JsonSerializer.Serialize(text)[1..^1];
    private static async Task<(int Code, string Output, string Error)> ExecuteAsync(ProcessStartInfo info, string? input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = info };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null) { await process.StandardInput.WriteAsync(input.AsMemory(), token); process.StandardInput.Close(); }
            await process.WaitForExitAsync(token);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
