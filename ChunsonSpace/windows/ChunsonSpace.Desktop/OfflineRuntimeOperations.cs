using ChunsonSpace.Application;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

// Only used with the explicitly marked fixture workspace. Never discovers or controls real processes.
public sealed class OfflineRuntimeOperations : IRuntimeOperations
{
    public bool StopSucceeds { get; set; } = true;
    public bool ConfirmStopSucceeds { get; set; } = true;
    public LaunchStatus LaunchStatus { get; set; } = LaunchStatus.Confirmed;
    public bool HasPendingLaunch { get; set; }
    public bool DesktopAlive { get; set; }
    public int StopCalls { get; private set; }
    public DesktopLaunchRequest? LastLaunchRequest { get; private set; }
    public bool LastTimezoneEnabled { get; private set; }
    public Task<DesktopLaunchResult?> CheckPendingAsync(CancellationToken cancellationToken) => Task.FromResult<DesktopLaunchResult?>(HasPendingLaunch ? new(LaunchStatus.Pending, false, null, "隔离替身", "隔离启动仍待确认") : null);
    public Task<StopResult> StopAsync(ProcessScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); StopCalls++;
        if (StopSucceeds) DesktopAlive = false;
        return Task.FromResult(new StopResult(StopSucceeds, true, StopSucceeds ? "隔离进程替身已停止" : "隔离停止失败，恢复未开始", StopSucceeds ? [] : [999]));
    }
    private static Task<TimezoneResult> Timezone(EnvironmentSettings? environment = null) => Task.FromResult(new TimezoneResult(true, false, true, environment?.Timezone ?? "", "隔离时区替身：未修改系统"));
    public Task<TimezoneResult> SwitchTimezoneAsync(IdentityRef identity, EnvironmentSettings environment, bool enabled, CancellationToken cancellationToken) => Timezone(environment);
    public Task<TimezoneResult> EnsureTimezoneForLaunchAsync(IdentityRef identity, EnvironmentSettings environment, bool enabled, CancellationToken cancellationToken) { LastTimezoneEnabled = enabled; return Timezone(environment); }
    public Task<TimezoneResult> RestoreTimezoneAsync(CancellationToken cancellationToken) => Timezone();
    public Task<TimezoneResult> ReassertTimezoneAfterLaunchAsync(IdentityRef identity, EnvironmentSettings environment, bool enabled, CancellationToken cancellationToken) => Timezone(environment);
    public Task<DesktopLaunchResult> LaunchAsync(DesktopLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastLaunchRequest = request;
        HasPendingLaunch = LaunchStatus == LaunchStatus.Pending;
        DesktopAlive = LaunchStatus == LaunchStatus.Confirmed;
        return Task.FromResult(new DesktopLaunchResult(LaunchStatus, false, LaunchStatus == LaunchStatus.Confirmed ? 999 : null, "隔离启动替身", LaunchStatus switch { LaunchStatus.Confirmed => "隔离替身已确认启动，未启动真实客户端", LaunchStatus.Pending => "隔离启动待确认", _ => "隔离启动失败" }));
    }
    public Task<StopResult> ConfirmStoppedAsync(ProcessScope scope, CancellationToken cancellationToken)
    {
        var stopped = ConfirmStopSucceeds && !DesktopAlive;
        return Task.FromResult(new StopResult(stopped, false, stopped ? "隔离确认：没有运行进程" : "隔离确认：进程仍在运行", stopped ? [] : [999]));
    }
}
