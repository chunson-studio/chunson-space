using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FAswitch.Accounts;
using FAswitch.NodeTesting;
using FAswitch.Proxy;
using FAswitch.Windows;

namespace FAswitch.Desktop;

internal sealed record AgentNodeSelection(string SubscriptionId, string NodeId);
internal sealed record AgentRequest(string Command, string? SubscriptionId = null, string? NodeId = null, string? Group = null, bool DelayOnly = false, AgentNodeSelection[]? Selections = null, string? ExpectedIp = null, string? Profile = null, string? Model = null, string? TestKind = null, string? Variant = null);

internal static class AgentCli
{
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() } };
    public static async Task<int> RunAsync(DesktopWorkspace workspace, string requestPath, string reportPath, CancellationToken token = default)
    {
        var output = Path.GetFullPath(reportPath);
        if (File.Exists(output)) throw new IOException("报告已存在，请使用新路径。");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string command = "";
        async Task Write(string state, string message, object? data = null)
        {
            var json = JsonSerializer.Serialize(new { schema = 1, command, state, message, data }, Json);
            await File.WriteAllTextAsync(output, json, CancellationToken.None);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { command, state, message, report = output }));
        }
        try
        {
            var input = Path.GetFullPath(requestPath);
            if (new FileInfo(input).Length > 16384) throw new InvalidOperationException("请求文件过大。");
            var request = JsonSerializer.Deserialize<AgentRequest>(await File.ReadAllTextAsync(input, token), Json) ?? throw new InvalidOperationException("缺少请求。");
            command = request.Command;
            var services = workspace.Accounts ?? throw new InvalidOperationException("账号服务未连接。");
            var proxyServices = workspace.ProxyServices;
            var nodes = proxyServices.NodeTesting!;
            switch (command)
            {
                case "capabilities":
                    await Write("completed", "UI 与 CLI 共用账号、节点及代理服务。", new {
                        readOnly = new[] { "status", "browser-status", "subscriptions", "capabilities", "dns-preview", "launch-check", "protection-status", "visual-results" },
                        publicNetwork = new[] { "exit", "screen", "screen-pending", "test-node", "test-nodes", "test-subscription" },
                        userActions = new[] { "login", "dns-apply", "ciallo", "visual-test", "protection-disable" },
                        visualTests = new { kinds = Enum.GetNames<VisualTestKind>(), variants = VisualTestCatalog.Variants, effort = "high", transport = "HTTP Responses", requestTimeoutSeconds = (int?)null, requests = new { Static = 1, Animation = 1, Variation = 1, Revision = 3 } },
                        ciallo = new { prompt = CialloTest.Prompt, effort = CialloTest.Effort, models = CialloTest.Models, usesAccountQuota = true },
                        scorePolicy = "成功分数按实际 IP 长期复用；登录不测分；通常质量至少 85；IPEqual 住宅线路免评分门槛，仍需确认出口并显示实际评分。",
                        login = "沿用当前 FlClash 出口；或提供 subscriptionId、nodeId、expectedIp 自动分配独立端口。仅复用已有评分；用户本人完成授权。",
                        resultSchema = 1, exitCodes = new { completed = 0, failed = 1, partial = 3, cancelled = 130 } });
                    return 0;
                case "browser-status":
                    var installed = InstalledBrowsers.Find();
                    await Write("completed", installed.Count > 0 ? "已找到本机浏览器；未启动窗口。" : "请安装浏览器后重试。", new {
                        installed, selected = installed.FirstOrDefault(), ready = installed.Count > 0,
                        downloads = InstalledBrowsers.Downloads, language = "en-US", profile = "账号独立配置；授权后保留网页登录", debugControl = false });
                    return 0;
                case "status":
                    LoginProxySelection? route = null; string routeError = "";
                    try { route = await services.ResolveLoginProxyAsync(token); }
                    catch (Exception error) when (error is not OperationCanceledException) { routeError = ErrorRedaction.Scrub(error.Message); }
                    await Write("completed", "只读状态；未请求网络或改变配置。", new { client = "FlClash", route, routeError,
                        policy = new { minimumQuality = IpAdmissionPolicy.MinimumScore, unknownScoreAllowed = false, exception = "IPEqual 住宅线路需确认实际出口", groups = new[] { "家庭宽带组", "机房组" } },
                        accounts = workspace.Profiles.ListAccounts().Select(account => new { account.Name, account.HasCredentials }),
                        browser = new { installed = InstalledBrowsers.Find(), downloads = InstalledBrowsers.Downloads, debugControl = false }, networkVerified = false });
                    return 0;
                case "protection-status":
                    var inspectedProtection = await proxyServices.InspectProtectionAsync(token);
                    var knownProtection = inspectedProtection.Status is DirectConnectionProtectionStatus.Disabled or DirectConnectionProtectionStatus.Configured or DirectConnectionProtectionStatus.ProtectionStale;
                    await Write(knownProtection ? "completed" : "failed", inspectedProtection.Message,
                        new { protection = inspectedProtection, settingsChangeAttempted = false, networkVerified = false });
                    return knownProtection ? 0 : 1;
                case "protection-disable":
                    var disabledProtection = await proxyServices.Protection.RestoreAsync(new([], ""), token);
                    var disabled = disabledProtection.Status == DirectConnectionProtectionStatus.Restored;
                    await Write(disabled ? "completed" : "failed", disabledProtection.Message,
                        new { protection = disabledProtection, settingsChangeAttempted = true, networkVerified = false });
                    return disabled ? 0 : 1;
                case "protection-preview":
                case "protection-apply":
                    await Write("failed", "系统防护功能已移除；不再创建禁止直连规则。可用 protection-disable 清除旧规则。");
                    return 1;
                case "launch-check":
                    var checkAccounts = workspace.Profiles.ListAccounts().Where(account => account.HasCredentials).ToArray();
                    var checkProfile = string.IsNullOrWhiteSpace(request.Profile) ? checkAccounts.Length == 1 ? checkAccounts[0].Name : null : request.Profile;
                    if (checkProfile is null || !checkAccounts.Any(account => account.Name == checkProfile))
                        throw new InvalidOperationException("请用 profile 指定一个已保存的登录账号。");
                    var settings = workspace.Profiles.ReadEnvironment(checkProfile);
                    var spec = DesktopLaunchSpec.Build(settings, workspace.ActiveHome, true, []);
                    var ready = spec.Success ? await workspace.Runtime!.PrepareLaunchAsync(new(null, spec.Arguments, spec.Environment, IdentityName: checkProfile), token)
                        : new DesktopLaunchPreparation(false, new(null, [], null), spec.Message);
                    await Write(ready.Success ? "completed" : "failed", ready.Success
                        ? "本地启动条件检查通过；尚未检查实际出口、IP 分数或执行启动。" : ready.Message,
                        new { profile = checkProfile, localConditionsReady = ready.Success, networkVerified = false, accountRequests = 0,
                            launched = false, settingsChanged = false, accountChanged = false, timezoneChanged = false });
                    return ready.Success ? 0 : 1;
                case "subscriptions":
                    var catalog = nodes.Subscriptions(ProxyClientKind.FlClash, proxyServices.FlClashRoot);
                    await Write("completed", "已读取全部本地订阅；未下载订阅或测试节点。", new
                    {
                        subscriptions = catalog.Subscriptions.Select(subscription => new { subscription.Id, subscription.Name, subscription.IsCurrent,
                            catalog = nodes.LoadSubscription(subscription, proxyServices.FlClashRoot) }), catalog.Issues
                    });
                    return 0;
                case "test-node":
                case "test-subscription":
                case "test-nodes":
                case "screen":
                case "screen-pending":
                    var selectedNodes = SelectNodes(nodes, proxyServices.FlClashRoot, request);
                    if (request.Command == "screen-pending")
                    {
                        var total = selectedNodes.Length;
                        selectedNodes = nodes.History.Pending(selectedNodes).ToArray();
                        if (selectedNodes.Length == 0) { await Write("completed", $"无需补测，已跳过 {total} 个已完成节点。", new { skipped = total, tested = 0 }); return 0; }
                    }
                    if (selectedNodes.Length == 0) throw new InvalidOperationException("该订阅没有可测试节点。");
                    await Write("running", "正在通过独立核心检测所选节点；不会切换 FlClash 当前节点。");
                    var measured = await nodes.CreateBatch().ProbeAsync(selectedNodes, token: token, delayOnly: request.DelayOnly);
                    var partial = measured.Results.Any(result => result.Success || result.Quality?.HasVerifiedExit == true);
                    var complete = measured.Results.Count == selectedNodes.Length && measured.Results.All(item => item.Success) && !measured.StopUnconfirmed;
                    await Write(complete ? "completed" : partial ? "partial" : "failed", measured.Message, measured);
                    return complete ? 0 : partial ? 3 : 1;
                case "exit":
                    var current = await services.ResolveLoginProxyAsync(token);
                    await Write("running", "正在核对 FlClash 当前出口；不更改节点。", current);
                    var quality = await nodes.ProbeScoredExitAsync(current.Proxy, token);
                    await Write(quality.Success ? "completed" : quality.HasVerifiedExit ? "partial" : "failed", quality.Message, new { route = current, quality });
                    return quality.Success ? 0 : quality.HasVerifiedExit ? 3 : 1;
                case "login":
                    LoginProxySelection selected;
                    if (!string.IsNullOrWhiteSpace(request.SubscriptionId) || !string.IsNullOrWhiteSpace(request.NodeId) || !string.IsNullOrWhiteSpace(request.ExpectedIp))
                    {
                        if (string.IsNullOrWhiteSpace(request.SubscriptionId) || string.IsNullOrWhiteSpace(request.NodeId) || string.IsNullOrWhiteSpace(request.ExpectedIp))
                            throw new InvalidOperationException("独立 IP 登录需要检测结果中的 subscriptionId、nodeId 和 expectedIp；不会回退当前网络。");
                        var candidate = SelectNodes(nodes, proxyServices.FlClashRoot, request).Single();
                        selected = await nodes.PrepareDedicatedLoginAsync(request.SubscriptionId, candidate, nodes.CachedLoginExit(request.ExpectedIp, request.SubscriptionId, candidate), token);
                    }
                    else selected = await services.ResolveLoginProxyAsync(token);
                    await Write("preparing", "使用已选出口，打开本机浏览器的英文独立配置；登录复用评分。", selected);
                    var browser = services.CreateAuthorizationBrowser(selected.Proxy, selected.IndependentBrowserTimezone);
                    try
                    {
                        var task = services.Login.LoginNewAsync(explicitProxy: selected.Proxy, authorizationBrowser: browser, cancellationToken: token);
                        var stage = "";
                        while (!task.IsCompleted)
                        {
                            var next = services.Login.Status.BrowserOpened && services.Login.Status.Stage == LoginStage.Browser ? "BrowserReady" : services.Login.Status.Stage.ToString();
                            if (next != stage) { stage = next; await Write(next == "BrowserReady" ? "waiting_for_login" : "running", next == "BrowserReady" ? "请在已打开的浏览器完成登录。" : "登录阶段：" + next, selected); }
                            await Task.WhenAny(task, Task.Delay(400, token)); token.ThrowIfCancellationRequested();
                        }
                        var result = await task;
                        await Write(result.Ok ? "completed" : "failed", result.Message, new { result.Ok, result.Profile, result.Warnings, route = selected });
                        return result.Ok ? 0 : 1;
                    }
                    finally
                    {
                        var cleanup = await services.ReleaseAuthorizationBrowserAsync(browser);
                        if (!cleanup.Success) throw new IOException(cleanup.Message);
                    }
                case "visual-results":
                case "visual-test":
                    if (string.IsNullOrWhiteSpace(request.Profile) || !workspace.Profiles.ListAccounts().Any(p => p.Name == request.Profile && p.HasCredentials))
                        throw new InvalidOperationException("请通过 profile 指定一个已授权账号。");
                    if (!Enum.TryParse<VisualTestKind>(request.TestKind, true, out var kind) || !Enum.IsDefined(kind)) throw new InvalidOperationException("testKind 需为 Static、Animation、Variation 或 Revision。");
                    var visualModel = request.Model ?? "gpt-6-astra"; var variant = request.Variant ?? VisualTestCatalog.Variants[0].Id;
                    if (!VisualTestCatalog.ValidModel(visualModel) || !VisualTestCatalog.Variants.Any(v => v.Id == variant)) throw new InvalidOperationException("模型或题目选择无效。");
                    var visual = command == "visual-results" ? services.VisualTests.Load(request.Profile, visualModel, kind, variant)
                        : await services.VisualTests.RunAsync(request.Profile, visualModel, kind, variant, token: token);
                    await Write(command == "visual-results" ? "completed" : visual!.Completed ? "completed" : visual.Cancelled ? "cancelled" : "partial",
                        command == "visual-results" ? "只读保存的结果，未发送请求。" : visual!.Message, visual);
                    return command == "visual-results" || visual!.Completed ? 0 : visual.Cancelled ? 130 : 3;
                case "ciallo":
                    if (string.IsNullOrWhiteSpace(request.Profile)) throw new InvalidOperationException("ciallo 需要 status 中的账号名称 profile；会使用该账号额度。");
                    await Write("running", "正在比较各模型的 ciallo 回复；思考程度统一 high。");
                    var comparison = await services.Accounts.TestCialloAsync(request.Profile, token: token);
                    var completeComparison = comparison.Completed == CialloTest.Models.Count;
                    await Write(completeComparison ? "completed" : comparison.Cancelled ? "cancelled" : "partial", "Ciallo 原始回复对比；不是型号或能力认证。", comparison);
                    return completeComparison ? 0 : comparison.Cancelled ? 130 : 3;
                case "dns-preview":
                case "dns-apply":
                    var dnsRequest = new FlClashDnsRequest(proxyServices.FlClashRoot, BackupDirectory: proxyServices.BackupRoot, AllSubscriptions: true);
                    var plan = await proxyServices.FlClash.PreviewAsync(dnsRequest, token);
                    if (command == "dns-apply" && plan.Status == FlClashDnsStatus.PreviewReady)
                        plan = await proxyServices.FlClash.ApplyAsync(dnsRequest with { ApplicationPath = plan.ApplicationPath }, token);
                    var configured = command == "dns-preview" ? plan.Status is FlClashDnsStatus.PreviewReady or FlClashDnsStatus.SelectionRequired : plan.GeneratedConfigVerified;
                    await Write(configured ? "completed" : plan.SettingsSaved ? "partial" : "failed", plan.Message, plan);
                    return configured ? 0 : plan.SettingsSaved ? 3 : 1;
                default: throw new InvalidOperationException("请使用 capabilities 获取支持的命令。");
            }
        }
        catch (OperationCanceledException) { await Write("cancelled", "已取消。"); return 130; }
        catch (Exception error)
        {
            await Write("failed", error is AccountException or FAswitch.Core.StorageException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException
                ? ErrorRedaction.Scrub(error.Message) : "操作未完成：" + error.GetType().Name);
            return 1;
        }
    }
    private static NodeCandidate[] SelectNodes(DesktopNodeServices service, string root, AgentRequest request)
    {
        var subscriptions = service.Subscriptions(ProxyClientKind.FlClash, root).Subscriptions;
        IReadOnlyList<NodeCandidate> Read(string id) => service.LoadSubscription(subscriptions.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("订阅 ID 不存在，请先读取 subscriptions。"), root).Nodes;
        if (request.Command is "screen" or "screen-pending") return subscriptions.SelectMany(subscription => Read(subscription.Id))
            .Where(NodeScreeningRules.IsAccountCandidate).DistinctBy(node => node.Id)
            .OrderByDescending(node => NodeScreeningRules.IsResidentialName(node.Name)).ToArray();
        if (request.Command == "test-subscription") return Read(request.SubscriptionId ?? "").Where(item => item.Supported).ToArray();
        var selections = request.Command == "test-nodes" ? request.Selections : [new AgentNodeSelection(request.SubscriptionId ?? "", request.NodeId ?? "")];
        if (selections is null || selections.Length is < 1 or > 100) throw new InvalidOperationException("test-nodes 需要 1–100 个明确的订阅与节点 ID。");
        return selections.GroupBy(item => item.SubscriptionId).SelectMany(group =>
        {
            var candidates = Read(group.Key);
            return group.Select(item => candidates.FirstOrDefault(node => node.Id == item.NodeId)
                ?? throw new InvalidOperationException("节点已改变或不存在，请重新读取 subscriptions；未测试其他节点。"));
        }).DistinctBy(node => node.Id).ToArray();
    }
}
