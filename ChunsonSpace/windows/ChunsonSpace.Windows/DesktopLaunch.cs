using System.Text.RegularExpressions;
using ChunsonSpace.Core;

namespace ChunsonSpace.Windows;

public enum LaunchStatus { Confirmed, Failed, Pending }
public sealed record DesktopTarget(string Executable, string Aumid);
public sealed record DesktopLaunchRequest(DesktopTarget? Target, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Environment, int WindowTimeoutMilliseconds = 8000, string IdentityName = "", string ExpectedEgressIp = "")
{
    // Process-local evidence, never accepted from CLI/JSON or persisted between operations.
    internal object? Preparation { get; init; }
}
public sealed record DesktopLaunchPreparation(bool Success, DesktopLaunchRequest Request, string Message = "");
public sealed record LaunchReceipt(LaunchStatus Status, int? Pid, string Method, string Message, string? StateDirectory = null);
public sealed record DesktopLaunchResult(LaunchStatus Status, bool AlreadyRunning, int? Pid, string Method, string Message, string? StateDirectory = null, bool RuntimeIdentityConfirmed = true)
{
    public bool Success => Status == LaunchStatus.Confirmed;
    public bool PendingLaunch => Status == LaunchStatus.Pending;
}
public interface IDesktopLaunchSystem
{
    Task<LaunchReceipt?> CheckPendingAsync(CancellationToken cancellationToken);
    bool IsDesktopReady();
    Task<DesktopTarget?> FindTargetAsync(CancellationToken cancellationToken);
    Task<DesktopLaunchPreparation> PrepareAsync(DesktopTarget target, DesktopLaunchRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new DesktopLaunchPreparation(true, request with { Target = target }));
    Task<LaunchReceipt> StartAsync(DesktopTarget target, DesktopLaunchRequest request, CancellationToken cancellationToken);
    Task<LaunchReceipt> VerifyRuntimeAsync(LaunchReceipt receipt, DesktopLaunchRequest request, CancellationToken cancellationToken) => Task.FromResult(receipt);
    // Called ONLY after a visible/title-bearing desktop window has been stable.
    void Complete(LaunchReceipt receipt);
}

public sealed class DesktopLaunchService(IDesktopLaunchSystem system, IWaitClock clock)
{
    public async Task<DesktopLaunchPreparation> PrepareAsync(DesktopLaunchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = request.Target ?? await system.FindTargetAsync(cancellationToken);
            return target is null ? new(false, request, "未找到已安装的 Codex 桌面端；当前账号和分身保持不变。")
                : await system.PrepareAsync(target, request, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return new(false, request, "启动条件检查失败：" + exception.GetType().Name + "；当前账号和分身保持不变。"); }
    }
    public async Task<DesktopLaunchResult> LaunchAsync(DesktopLaunchRequest request, CancellationToken cancellationToken = default)
    {
        LaunchReceipt? receipt = null;
        bool startAttempted = false;
        var stage = "检查上次启动记录";
        try
        {
            var pending = await system.CheckPendingAsync(cancellationToken);
            if (pending is not null) return Result(pending);
            stage = "检查已有桌面窗口";
            if (system.IsDesktopReady())
            {
                var existing = await system.VerifyRuntimeAsync(new(LaunchStatus.Confirmed, null, "已有桌面窗口", "Codex 桌面端已在运行，档案启动环境未重新应用"), request, cancellationToken);
                return existing.Status == LaunchStatus.Confirmed ? new(LaunchStatus.Confirmed, true, existing.Pid, existing.Method, existing.Message) : Result(existing);
            }
            var target = request.Target ?? await system.FindTargetAsync(cancellationToken);
            if (target is null) return new(LaunchStatus.Failed, false, null, "检查桌面端", "未找到已安装的 Codex 桌面端");
            stage = "提交桌面启动请求";
            startAttempted = true;
            receipt = await system.StartAsync(target, request, cancellationToken);
            if (receipt.Status is LaunchStatus.Failed or LaunchStatus.Pending) return Result(receipt);
            stage = "确认桌面窗口";
            long? appeared = null;
            var deadline = clock.Milliseconds + request.WindowTimeoutMilliseconds;
            while (clock.Milliseconds < deadline)
            {
                if (system.IsDesktopReady())
                {
                    appeared ??= clock.Milliseconds;
                    if (clock.Milliseconds - appeared >= 500)
                    {
                        stage = "验证分身运行环境";
                        receipt = await system.VerifyRuntimeAsync(receipt, request, cancellationToken);
                        if (receipt.Status != LaunchStatus.Confirmed) return Result(receipt);
                        stage = "完成启动记录（桌面窗口和运行环境已确认）";
                        system.Complete(receipt);
                        return new(LaunchStatus.Confirmed, false, receipt.Pid, receipt.Method, receipt.Message.Length > 0 ? receipt.Message : "Codex 桌面窗口已启动并稳定确认", receipt.StateDirectory);
                    }
                }
                else appeared = null;
                await clock.DelayAsync(150, cancellationToken);
            }
            return Result(receipt with { Status = LaunchStatus.Pending, Message = "启动请求已发出，但桌面窗口尚未确认；保留切换与恢复材料" });
        }
        catch (OperationCanceledException) when (startAttempted)
        { return Result((receipt ?? new(LaunchStatus.Pending, null, "桌面启动", "")) with { Status = LaunchStatus.Pending, Message = "等待窗口已取消，启动结果仍待确认；保留恢复材料" }); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Any uncertainty after dispatch must block rollback and a second identity change.
            return !startAttempted ? new(LaunchStatus.Failed, false, null, "准备启动", "启动准备失败：" + ex.GetType().Name)
                : Result((receipt ?? new(LaunchStatus.Pending, null, "桌面启动", "")) with { Status = LaunchStatus.Pending, Message = stage + "时出错（" + ex.GetType().Name + "）；保留恢复材料，不自动重启分身" });
        }
    }
    private static DesktopLaunchResult Result(LaunchReceipt receipt) => new(receipt.Status, false, receipt.Pid, receipt.Method, receipt.Message, receipt.StateDirectory);
}

public sealed record LaunchSpecResult(bool Success, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Environment, string Message);
public static partial class DesktopLaunchSpec
{
    public static LaunchSpecResult Build(EnvironmentSettings settings, string codexHome, bool requireProxy,
        IEnumerable<KeyValuePair<string, string>> inherited, ISystemProxyResolver? systemProxy = null)
    {
        var proxy = settings.Proxy;
        if (settings.FollowSystemProxy)
        {
            try { proxy = (systemProxy ?? new SystemProxyResolver()).ResolveHttpProxy(); }
            catch (SystemProxyException ex) { return new(false, [], null, ex.Message); }
            catch (Exception) { return new(false, [], null, "无法确认系统 HTTP 代理，桌面未启动；不会直连。"); }
        }
        return Build(proxy, settings.Timezone, settings.Locale, codexHome, requireProxy, inherited);
    }
    [GeneratedRegex("token|secret|password|passwd|credential|api[_-]?key|access[_-]?key|private[_-]?key|[_\\-\\s]keys?$", RegexOptions.IgnoreCase)] private static partial Regex CredentialPattern();
    public static IReadOnlyDictionary<string, string> SanitizeEnvironment(IEnumerable<KeyValuePair<string, string>> environment)
        => environment.Where(p => !CredentialPattern().IsMatch(p.Key) && !p.Key.StartsWith("GM_", StringComparison.OrdinalIgnoreCase) && !p.Key.StartsWith("FUTUREAPI_", StringComparison.OrdinalIgnoreCase)
            && !new[] { "GPT Key", "Claude Key", "DeepSeek Key", "GLM Key", "GITHUB_PAT_TOKEN" }.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    public static LaunchSpecResult Build(string proxy, string timezone, string locale, string codexHome, bool requireProxy, IEnumerable<KeyValuePair<string, string>> inherited)
    {
        proxy = proxy.Trim(); timezone = timezone.Trim(); locale = locale.Trim();
        try { proxy = ProfileRules.NormalizeProxy(proxy); }
        catch (StorageException ex) { return new(false, [], null, ex.Message); }
        if (proxy.Length == 0 && requireProxy) return new(false, [], null, "档案里没填代理，桌面端拒绝启动");
        if (proxy.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)) return new(false, [], null, "桌面启动需要 http 代理端口，不支持 SOCKS");
        if (!ProfileRules.ValidTimezone(timezone)) timezone = "";
        if (!ProfileRules.ValidLocale(locale)) locale = "";
        var environment = new Dictionary<string, string>(SanitizeEnvironment(inherited), StringComparer.OrdinalIgnoreCase);
        var args = new List<string>();
        if (proxy.Length > 0)
        {
            foreach (var key in new[] { "ALL_PROXY", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY" }) environment.Remove(key);
            environment["HTTP_PROXY"] = environment["HTTPS_PROXY"] = proxy;
            environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
            args.Add("--proxy-server=" + proxy);
        }
        if (timezone.Length > 0) environment["TZ"] = timezone;
        environment["CODEX_HOME"] = codexHome;
        if (locale.Length > 0) args.Add("--accept-lang=" + locale);
        return new(true, args, environment, "");
    }
}
