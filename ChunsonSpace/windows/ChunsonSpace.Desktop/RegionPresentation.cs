using System.IO;
using System.ComponentModel;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

public sealed partial class ShellViewModel
{
    private string regionStatus = "按本机默认出口设置 Windows 地区和时区；账号浏览器独立配置，退出后不恢复旧时区。";
    public string RegionStatus => regionStatus;
    public DelegateCommand ConfigureRegionCommand { get; private set; } = null!;
    public DelegateCommand SyncLocalTimezoneCommand { get; private set; } = null!;
    public async Task ConfigureRegionAsync()
    {
        if (busy || checkingLocalEnvironment || workspace?.Accounts is null || workspace.LocalEnvironment is null && Selected?.Kind != "Account") return;
        var action = "地区与时区同步";
        var target = Selected; var services = workspace.Accounts;
        SetBusy(true); SetRegionStatus("同步中：正在核对本机默认出口的国家和具体时区，尚未修改系统。");
        try
        {
            var local = workspace.LocalEnvironment is not null ? await workspace.LocalEnvironment.CheckAsync() : null;
            var proxy = local?.Proxy ?? services.Repository.Proxy(target!.Name);
            var result = local is not null ? new EgressResult(local.Verified, local.Ip, CountryCode: local.CountryCode, Timezone: local.ExitTimezone, Message: local.Message)
                : await Task.Run(() => services.Egress.ProbeRegionProxyAsync(proxy));
            if (local is not null) ApplyLocalEnvironmentReport(local);
            if (!result.Ok) { SetRegionStatus("同步未完成：" + result.Message); ShowFeedback(action + "未完成", RegionStatus, "Error"); return; }
            var currentProxy = local is not null ? (await services.ResolveLoginProxyAsync(default)).Proxy : services.Repository.Proxy(target!.Name);
            if (currentProxy != proxy) { SetRegionStatus("同步未完成：检测期间代理已改变，未修改系统。请重新点击。"); ShowFeedback(action + "未完成", RegionStatus, "Error"); return; }
            if (!workspace.IsFixture && (workspace.Ownership is null || workspace.NativeTimezone is null))
                throw new InvalidOperationException("系统环境配置与时区恢复服务尚未连接，未修改系统。");
            using var lease = workspace.Ownership is { } ownership ? await ownership.BeginLaunchAsync() : null;
            var nativeTimezone = workspace.NativeTimezone;
            SetRegionStatus("同步中：正在应用并回读 Windows 地区与时区，请稍候…");
            var configured = await Task.Run(() => services.Region.ConfigureExplicitlyAsync(result.CountryCode, result.Timezone,
                baselinePreflight: nativeTimezone is null ? null : nativeTimezone.ValidateExplicitBaselineAsync,
                commitBaseline: nativeTimezone is null ? null : nativeTimezone.CommitExplicitBaselineAsync));
            SetRegionStatus((workspace.IsFixture ? "隔离验证：" : "") + (configured.Success ? "同步完成：" : "同步未完成：") + $"{(local is not null ? "本机默认出口" : target!.Name)} · {result.CountryCode} · {result.Timezone}。" + configured.Message);
            if (local is not null) ApplyLocalEnvironmentReport(workspace.LocalEnvironment!.RereadSystemEnvironment(local));
            ShowFeedback(action + (configured.Success ? "完成" : "未完成"), RegionStatus, configured.Success ? "Success" : "Error");
        }
        catch (Exception error) when (error is AccountException or StorageException or IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        { SetRegionStatus(action + "未完成：" + ErrorRedaction.Scrub(error.Message)); ShowFeedback(action + "未完成", RegionStatus, "Error"); }
        finally { SetBusy(false); }
    }
    private void SetRegionStatus(string value) { regionStatus = value; Raise(nameof(RegionStatus)); }
}
