using System.IO;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.NodeTesting;

namespace ChunsonSpace.Desktop;

public sealed record AccountNetworkChange(bool ReopenBrowser, string Message);

// One transaction for the browser route, timezone policy and dedicated listener.
// Account environments retain references; no separate port-owner database.
public sealed class AccountNetworkModes(AccountRepository repository, ManagedNodeRoutes routes,
    IAccountBrowserNetwork browser, Func<CancellationToken, Task<LoginProxySelection>> currentNetwork,
    Func<TimeZoneInfo>? localTimezone = null)
{
    public async Task<AccountNetworkChange> SaveAsync(string name, bool follow,
        Func<CancellationToken, Task<(string Proxy, string Timezone)>> allocate, CancellationToken token = default, bool replaceIndependent = false)
    {
        var previous = repository.Profiles.ReadEnvironment(name);
        if (!follow && !replaceIndependent && !previous.FollowSystemProxy && previous.Proxy.Length > 0)
            return new(false, "独立 IP 与专用端口保持不变。");
        string created = "";
        bool committed = false;
        string[] Retained() => repository.Profiles.ListAccounts().Where(account => !account.Environment.FollowSystemProxy)
            .Select(account => account.Environment.Proxy).Where(proxy => proxy.Length > 0).ToArray();
        try
        {
            EnvironmentSettings next;
            if (follow)
            {
                _ = await currentNetwork(token); // Resolve the main FlClash listener, including TUN-only setups.
                TimeZoneInfo.ClearCachedData(); var local = localTimezone?.Invoke() ?? TimeZoneInfo.Local;
                var timezone = TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var zone) ? zone : local.Id;
                next = previous with { Proxy = "", FollowSystemProxy = true, Timezone = timezone, Locale = "en-US" };
            }
            else
            {
                var assigned = await allocate(token); created = assigned.Proxy;
                if (!ProfileRules.ValidTimezone(assigned.Timezone)) throw new IOException("独立 IP 的时区未确认，未切换模式。");
                next = previous with { Proxy = assigned.Proxy, FollowSystemProxy = false, Timezone = assigned.Timezone, Locale = "en-US" };
            }
            var reopen = await browser.CloseForNetworkChangeAsync(name, token);
            repository.Profiles.SaveEnvironment(name, next); committed = true;
            var released = previous.Proxy.Length > 0 && previous.Proxy != next.Proxy
                && await routes.ReleaseUnusedAsync(previous.Proxy, Retained(), token);
            return new(reopen, follow ? "已跟随 FlClash 当前网络和本机时区。" + (released ? "旧独立端口已释放。" : previous.Proxy.Length > 0 && Retained().Contains(previous.Proxy) ? "共用端口已解除本账号绑定，其他账号继续使用。" : "不再占用独立端口。")
                : "已自动分配独立端口，并采用该 IP 的时区。");
        }
        catch
        {
            if (committed) repository.Profiles.SaveEnvironment(name, previous);
            if (created.Length > 0) await routes.ReleaseUnusedAsync(created, Retained(), CancellationToken.None);
            throw;
        }
    }
}

public sealed partial class ShellViewModel
{
    private async Task<string> SaveAccountNetworkModeAsync(ProfileEditorViewModel input)
    {
        var services = workspace!.Accounts!;
        if (services.Browser is not IAccountBrowserNetwork browser) throw new InvalidOperationException("档案浏览器网络切换服务未连接。");
        var modes = new AccountNetworkModes(services.Repository, services.Routes!, browser, services.ResolveLoginProxyAsync);
        var name = input.Original!.Name;
        var selected = input.SelectedIp;
        if (!input.FollowSystemProxy && selected is not null)
        {
            if (!ProxyPanel.NodeTesting.LoginChoices.Contains(selected) || !selected.CanPrepareLogin)
                throw new InvalidOperationException("所选 IP 已失效，请重新选择已达标的 IP；原配置保留。");
            if (!input.Original.FollowSystemProxy && await services.Routes!.AssignedIpAsync(input.Original.Proxy) == selected.ExitIp)
                return "已在使用所选 IP，原端口和时区保持不变。";
        }
        var changed = await modes.SaveAsync(name, input.FollowSystemProxy, async token =>
        {
            var node = selected ?? await ProxyPanel.NodeTesting.ChooseIndependentAsync(random: true);
            var proxy = await services.Routes!.BindAsync(node.SubscriptionId, node.Node, node.Quality!, token, reuseExisting: false);
            try
            {
                var region = await services.LocalEgress.ProbeProxyAsync(proxy, token);
                if (!region.Ok || !EgressSources.SameIp(region.Ip, node.ExitIp))
                    throw new IOException("所选 IP 的当前出口与时区尚未确认，原配置保留。" + region.Message);
                return (proxy, region.Timezone);
            }
            catch { await services.ReleaseUnusedAuthorizationRouteAsync(proxy); throw; }
        }, replaceIndependent: selected is not null);
        if (!changed.ReopenBrowser) return changed.Message;
        try
        {
            await Task.Run(() => services.OpenProfileBrowserAsync(name));
            return changed.Message + "已重开此账号的浏览器，网页登录资料保留。";
        }
        catch (Exception error) when (error is AccountException or IOException or StorageException)
        {
            if (services.Browser is IBrowserSessionCleanup cleanup) await cleanup.CleanupAsync(null, CancellationToken.None);
            return changed.Message + "网页登录资料保留；浏览器暂未重开：" + ErrorRedaction.Scrub(error.Message);
        }
    }
}
