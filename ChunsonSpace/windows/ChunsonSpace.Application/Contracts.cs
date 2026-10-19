using ChunsonSpace.Core;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Application;

public enum OperationOutcome { Success, Unchanged, Failed, RolledBack, RecoveryFailed, PendingLaunch, NeedLogin }
public enum OperationPhase { Preflight, ProbeEgress, PrepareProfile, Stop, Snapshot, Archive, Apply, Timezone, Launch, Rollback, Complete }
public sealed record SwitchRequest(IdentityRef Target, bool Launch, bool Isolated, bool AutoSystemTimezone, EnvironmentSettings Environment, string CodexHome);
public sealed record LaunchCurrentRequest(bool AutoSystemTimezone, string CodexHome);
public sealed record OperationResult(OperationOutcome Outcome, IdentityRef? ActiveIdentity, IdentityRef? RunningIdentity, string Message, IReadOnlyList<string> RecoveryPaths, IReadOnlyList<string> Warnings, IdentityRef? LoginTarget = null)
{
    public bool Success => Outcome is OperationOutcome.Success or OperationOutcome.Unchanged;
    public bool PendingLaunch => Outcome == OperationOutcome.PendingLaunch;
    public bool NeedsLogin => Outcome == OperationOutcome.NeedLogin;
}

public interface IIdentityEnvironmentSource
{
    EnvironmentSettings Read(IdentityRef identity);
    bool AllowSystemTimezoneChange => true;
}
public sealed class FileIdentityEnvironmentSource(FileProfileStore profiles) : IIdentityEnvironmentSource
{
    public EnvironmentSettings Read(IdentityRef identity) => profiles.ReadEnvironment(identity.Name);
}

public interface IProfilePreparation
{
    // Accounts may refresh stored tokens here. Failure warns and does not block switching.
    Task<IReadOnlyList<string>> PrepareAsync(IdentityRef target, CancellationToken cancellationToken);
}
public interface IResolvedProxyProfilePreparation : IProfilePreparation
{
    Task<IReadOnlyList<string>> PrepareWithProxyAsync(IdentityRef target, string resolvedProxy, CancellationToken cancellationToken);
}

public sealed record LaunchEgressEvidence(bool Success, string Ip, string Timezone, string Message)
{
    public ExitAddresses Addresses { get; init; } = ExitAddresses.Unknown;
}
public interface ILaunchEgressProbe
{
    Task<LaunchEgressEvidence> ProbeAsync(IdentityRef identity, string resolvedProxy, CancellationToken cancellationToken);
}
public static class LaunchEgressRules
{
    public static bool Verified(LaunchEgressEvidence evidence)
    {
        if (!evidence.Success || !System.Net.IPAddress.TryParse(evidence.Ip, out var ip) || System.Net.IPAddress.IsLoopback(ip)
            || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any) || !ProfileRules.ValidTimezone(evidence.Timezone)) return false;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(evidence.Timezone); return true; }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
    }
}

public interface IRuntimeOperations
{
    Task<DesktopLaunchPreparation> PrepareLaunchAsync(DesktopLaunchRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new DesktopLaunchPreparation(true, request));
    Task<IDisposable?> BeginLaunchAsync(CancellationToken cancellationToken) => Task.FromResult<IDisposable?>(null);
    Task<StopResult> PrepareMaintenanceAsync(CancellationToken cancellationToken) => Task.FromResult(new StopResult(true, false, "", []));
    Task<StopResult> PrepareMaintenanceAsync(CancellationToken cancellationToken, bool discard) => PrepareMaintenanceAsync(cancellationToken);
    Task<DesktopLaunchResult?> CheckPendingAsync(CancellationToken cancellationToken);
    Task<StopResult> StopAsync(ProcessScope scope, CancellationToken cancellationToken);
    Task<StopResult> StopAsync(ProcessScope scope, CancellationToken cancellationToken, bool force) => StopAsync(scope, cancellationToken);
    Task<StopResult> ConfirmStoppedAsync(ProcessScope scope, CancellationToken cancellationToken);
    Task<StopResult> ConfirmStoppedAsync(ProcessScope scope, CancellationToken cancellationToken, bool force) => ConfirmStoppedAsync(scope, cancellationToken);
    Task<TimezoneResult> SwitchTimezoneAsync(IdentityRef identity, EnvironmentSettings environment, bool enabled, CancellationToken cancellationToken);
    Task<TimezoneResult> EnsureTimezoneForLaunchAsync(IdentityRef identity, EnvironmentSettings environment, bool enabled, CancellationToken cancellationToken);
    Task<TimezoneResult> RestoreTimezoneAsync(CancellationToken cancellationToken);
    Task<TimezoneResult> ReassertTimezoneAfterLaunchAsync(IdentityRef identity, EnvironmentSettings environment, bool enabled, CancellationToken cancellationToken);
    Task<DesktopLaunchResult> LaunchAsync(DesktopLaunchRequest request, CancellationToken cancellationToken);
}

public sealed class WindowsRuntimeOperations(CodexProcessService processes, DesktopLaunchService launch, IDesktopLaunchSystem launchSystem, SystemTimezoneService timezone, OwnedProcessLifecycleService? ownership = null) : IRuntimeOperations
{
    public Task<DesktopLaunchPreparation> PrepareLaunchAsync(DesktopLaunchRequest request, CancellationToken cancellationToken) => launch.PrepareAsync(request, cancellationToken);
    public async Task<IDisposable?> BeginLaunchAsync(CancellationToken cancellationToken) => ownership is null ? null : await ownership.BeginLaunchAsync(cancellationToken);
    public async Task<DesktopLaunchResult?> CheckPendingAsync(CancellationToken cancellationToken)
    {
        var value = await launchSystem.CheckPendingAsync(cancellationToken);
        return value is null ? null : new(value.Status, false, value.Pid, value.Method, value.Message, value.StateDirectory);
    }
    public Task<StopResult> PrepareMaintenanceAsync(CancellationToken cancellationToken) => Task.FromResult(launchSystem is WindowsDesktopLaunchSystem native ? native.CancelPendingForMaintenance(cancellationToken) : new StopResult(true, false, "", []));
    public Task<StopResult> PrepareMaintenanceAsync(CancellationToken cancellationToken, bool discard) => Task.FromResult(launchSystem is WindowsDesktopLaunchSystem native ? native.CancelPendingForMaintenance(cancellationToken, discard) : new StopResult(true, false, "", []));
    public Task<StopResult> StopAsync(ProcessScope scope, CancellationToken cancellationToken) => processes.StopAsync(scope, cancellationToken);
    public Task<StopResult> StopAsync(ProcessScope scope, CancellationToken cancellationToken, bool force) => processes.StopAsync(scope, cancellationToken, force);
    public Task<StopResult> ConfirmStoppedAsync(ProcessScope scope, CancellationToken cancellationToken, bool force)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(processes.ConfirmStopped(scope, force)); }
    public Task<StopResult> ConfirmStoppedAsync(ProcessScope scope, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(processes.ConfirmStopped(scope)); }
    public Task<TimezoneResult> SwitchTimezoneAsync(IdentityRef identity, EnvironmentSettings environment, bool enabled, CancellationToken cancellationToken) => timezone.SwitchAsync(identity.Name, environment.Timezone, enabled, cancellationToken);
    public Task<TimezoneResult> EnsureTimezoneForLaunchAsync(IdentityRef identity, EnvironmentSettings environment, bool enabled, CancellationToken cancellationToken) => timezone.EnsureForLaunchAsync(identity.Name, environment.Timezone, enabled, cancellationToken);
    public Task<TimezoneResult> RestoreTimezoneAsync(CancellationToken cancellationToken) => timezone.RestoreAsync(cancellationToken);
    public Task<TimezoneResult> ReassertTimezoneAfterLaunchAsync(IdentityRef identity, EnvironmentSettings environment, bool enabled, CancellationToken cancellationToken) => timezone.ReassertAfterLaunchAsync(identity.Name, environment.Timezone, enabled, cancellationToken);
    public async Task<DesktopLaunchResult> LaunchAsync(DesktopLaunchRequest request, CancellationToken cancellationToken)
    {
        var before = ownership is null ? [] : processes.Discover(ProcessScope.DesktopOnly);
        var createdAfter = DateTime.UtcNow.ToFileTimeUtc();
        var result = await launch.LaunchAsync(request, cancellationToken);
        if (ownership is not null && result.Success && !result.AlreadyRunning)
        {
            try
            {
                if (!ownership.TrackConfirmedDesktop(result, before, createdAfter, request.IdentityName))
                    return result with { RuntimeIdentityConfirmed = false, Message = result.Message + "；启动进程所有权记录未确认，运行身份保持未知" };
            }
            catch (Exception) { return result with { RuntimeIdentityConfirmed = false, Message = result.Message + "；启动进程身份无法记录，运行身份保持未知" }; }
        }
        return result;
    }
}
