using ChunsonSpace.Core;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Application;

public sealed class SwitchService(ISwitchStorage storage, IRuntimeOperations runtime, IProfilePreparation? preparation = null, IIdentityEnvironmentSource? environments = null, ISystemProxyResolver? systemProxy = null, ILaunchEgressProbe? egressProbe = null)
{
    public LaunchEgressEvidence? LastEgressEvidence { get; private set; }
    public async Task<OperationResult> SwitchAsync(SwitchRequest request, IProgress<OperationPhase>? progress = null, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var recovery = new List<string>();
        IdentityRef? current = null;
        SwitchSnapshot? snapshot = null;
        bool archived = false, launchAttempted = false, operationConfirmed = false, mutableApply = false, snapshotAttempted = false;
        bool runningConfirmed = false;
        string launchDetails = "";
        IDisposable? launchScope = null;
        LastEgressEvidence = null;
        try
        {
            progress?.Report(OperationPhase.Preflight);
            cancellationToken.ThrowIfCancellationRequested();
            if (storage is ManagedCloneStorage managed) managed.ValidateOwnership();
            var pending = await GuardPendingAsync(cancellationToken);
            if (pending is not null) return Pending(current, pending, recovery, warnings);
            current = storage.ReadActiveIdentity();
            storage.Preflight(request.Target, request.Isolated);
            var environment = request.Launch ? environments?.Read(request.Target) ?? request.Environment : request.Environment;
            var launchSpec = request.Launch ? Spec(request.Target, environment, request.CodexHome) : null;
            if (launchSpec is { Success: false }) return Result(OperationOutcome.Failed, current, null, launchSpec.Message, recovery, warnings);
            if (request.Launch && storage is ManagedCloneStorage)
            {
                progress?.Report(OperationPhase.ProbeEgress);
                environment = await VerifiedLaunchEnvironmentAsync(request.Target, environment, launchSpec!, cancellationToken);
                launchSpec = Spec(request.Target, environment, request.CodexHome);
            }
            if (storage is not ManagedCloneStorage && current == request.Target && !request.Launch) return Result(OperationOutcome.Unchanged, current, null, "当前活动身份已经是目标", recovery, warnings);
            if (storage is not ManagedCloneStorage && current == request.Target) return await LaunchCurrentAsync(new(request.AutoSystemTimezone, request.CodexHome), progress, cancellationToken);
            launchScope = request.Launch ? await runtime.BeginLaunchAsync(cancellationToken) : null;
            DesktopLaunchRequest? preparedRequest = null;
            if (request.Launch)
            {
                progress?.Report(OperationPhase.Preflight);
                var prepared = await runtime.PrepareLaunchAsync(new(null, launchSpec!.Arguments, launchSpec.Environment,
                    IdentityName: request.Target.Name, ExpectedEgressIp: LastEgressEvidence?.Ip ?? ""), cancellationToken);
                if (!prepared.Success) return Result(OperationOutcome.Failed, current, null, prepared.Message, recovery, warnings);
                preparedRequest = prepared.Request;
            }
            progress?.Report(OperationPhase.Stop);
            var stopped = await runtime.StopAsync(ProcessScope.DesktopAndCli, cancellationToken);
            if (!stopped.Success) return Result(OperationOutcome.Failed, current, null, stopped.Message, recovery, warnings);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(OperationPhase.Snapshot);
            snapshotAttempted = true;
            snapshot = storage.Snapshot("switch", request.Isolated);
            recovery.Add(snapshot.Path);
            progress?.Report(OperationPhase.Archive);
            archived = storage.ArchiveCurrent(snapshot, current, request.Isolated);
            if (preparation is not null)
            {
                progress?.Report(OperationPhase.PrepareProfile);
                try
                {
                    var prepared = storage is ManagedCloneStorage && request.Launch && preparation is IResolvedProxyProfilePreparation resolvedPreparation
                        ? await resolvedPreparation.PrepareWithProxyAsync(request.Target, launchSpec!.Environment!["HTTP_PROXY"], cancellationToken)
                        : await preparation.PrepareAsync(request.Target, cancellationToken);
                    warnings.AddRange(prepared);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { warnings.Add("档案预热失败：" + ex.GetType().Name); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var rechecked = await runtime.ConfirmStoppedAsync(ProcessScope.DesktopAndCli, cancellationToken);
            if (!rechecked.Success) return Result(OperationOutcome.Failed, SafeActive(), null, rechecked.Message, recovery, warnings);
            progress?.Report(OperationPhase.Apply);
            mutableApply = true;
            var applied = storage.ApplyTarget(request.Target, current, request.Isolated);
            warnings.AddRange(applied.Warnings.Select(w => w.Message));
            recovery.AddRange(applied.RecoveryPaths);
            var active = storage.ReadActiveIdentity();
            if (active != request.Target) throw new InvalidDataException("迁移后活动身份未确认到目标");
            if (request.Launch)
            {
                progress?.Report(OperationPhase.Timezone);
                var timezone = await runtime.EnsureTimezoneForLaunchAsync(request.Target, environment, request.AutoSystemTimezone && (environments?.AllowSystemTimezoneChange ?? true), cancellationToken);
                if (!timezone.Success)
                {
                    warnings.Add(timezone.Message);
                    return await RollbackAsync(current, snapshot, request.Isolated, "启动前时区没有确认", warnings, recovery, progress);
                }
                progress?.Report(OperationPhase.Launch);
                launchAttempted = true;
                var launched = await runtime.LaunchAsync(preparedRequest!, cancellationToken);
                if (launched.PendingLaunch) return Pending(active, launched, recovery, warnings);
                if (!launched.Success) return await RollbackAsync(current, snapshot, request.Isolated, launched.Message, warnings, recovery, progress);
                launchAttempted = false; // Confirmed window: later cleanup errors cannot trigger a rollback.
                runningConfirmed = !launched.AlreadyRunning && launched.RuntimeIdentityConfirmed;
                launchDetails = launched.Message;
                if (launched.AlreadyRunning || !launched.RuntimeIdentityConfirmed) warnings.Add(launched.Message);
                try
                {
                    var reassert = await runtime.ReassertTimezoneAfterLaunchAsync(request.Target, environment, request.AutoSystemTimezone && (environments?.AllowSystemTimezoneChange ?? true), cancellationToken);
                    if (!reassert.Success) warnings.Add(reassert.Message);
                }
                catch (Exception ex) { warnings.Add("启动后时区检查未完成：" + ex.GetType().Name); }
            }
            operationConfirmed = true;
            progress?.Report(OperationPhase.Complete);
            try
            {
                var completed = storage.Complete(snapshot, archived, applied.RecoveryPaths);
                warnings.AddRange(completed.Warnings.Select(w => w.Message));
                if (!completed.SnapshotKept) recovery.Remove(snapshot.Path);
                if (completed.DeletedPaths is { } deleted) recovery.RemoveAll(path => deleted.Contains(path, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) { warnings.Add("切换已完成，恢复材料清理失败：" + ex.GetType().Name); }
            return Result(OperationOutcome.Success, request.Target, runningConfirmed ? request.Target : null, request.Launch ? "身份切换完成，桌面窗口存在。" + launchDetails : "活动身份切换已确认", recovery, warnings);
        }
        catch (Exception ex)
        {
            if (operationConfirmed)
            {
                warnings.Add("操作已确认，后续通知或清理未完成：" + ex.GetType().Name);
                return Result(OperationOutcome.Success, request.Target, runningConfirmed ? request.Target : null, "活动身份已确认，恢复材料仍保留", recovery, warnings);
            }
            if (launchAttempted) return Result(OperationOutcome.PendingLaunch, SafeActive(), null, "启动请求结果无法确认，保留当前身份与恢复材料", recovery, warnings);
            if (snapshot is not null && mutableApply) return await RollbackAsync(current, snapshot, request.Isolated, FailureMessage(ex), warnings, recovery, progress);
            if (snapshotAttempted && snapshot is null && ex is StorageException snapshotError) recovery.Add(snapshotError.FilePath);
            if (ex is StorageException { Code: StorageError.LoginRequired }) return new(OperationOutcome.NeedLogin, current, null, "目标账号尚未登录", recovery, warnings, request.Target);
            return Result(OperationOutcome.Failed, current, null, FailureMessage(ex), recovery, warnings);
        }
        finally { launchScope?.Dispose(); }
    }

    public async Task<OperationResult> LaunchCurrentAsync(LaunchCurrentRequest request, IProgress<OperationPhase>? progress = null, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        IdentityRef? current = null;
        bool attempted = false, timezoneAttempted = false;
        IDisposable? launchScope = null;
        LastEgressEvidence = null;
        try
        {
            progress?.Report(OperationPhase.Preflight);
            if (storage is ManagedCloneStorage managed) managed.ValidateOwnership();
            var pending = await GuardPendingAsync(cancellationToken);
            if (pending is not null) return Pending(current, pending, [], warnings);
            current = storage.ReadActiveIdentity();
            if (current is null) return Result(OperationOutcome.Failed, null, null, "没有可确认的当前活动身份", [], warnings);
            // Every actual managed start, including the same account after closing, uses the guarded clean rebuild path.
            if (storage is ManagedCloneStorage)
                return await SwitchAsync(new(current, true, false, request.AutoSystemTimezone, new(), request.CodexHome), progress, cancellationToken);
            if (environments is null) return Result(OperationOutcome.Failed, current, null, "当前身份环境解析器未组装，桌面未启动", [], warnings);
            var environment = environments.Read(current);
            var spec = Spec(current, environment, request.CodexHome);
            if (!spec.Success) return Result(OperationOutcome.Failed, current, null, spec.Message, [], warnings);
            if (storage is ManagedCloneStorage)
            {
                progress?.Report(OperationPhase.ProbeEgress);
                environment = await VerifiedLaunchEnvironmentAsync(current, environment, spec, cancellationToken);
                spec = Spec(current, environment, request.CodexHome);
            }
            launchScope = await runtime.BeginLaunchAsync(cancellationToken);
            progress?.Report(OperationPhase.Preflight);
            var prepared = await runtime.PrepareLaunchAsync(new(null, spec.Arguments, spec.Environment,
                IdentityName: current.Name, ExpectedEgressIp: LastEgressEvidence?.Ip ?? ""), cancellationToken);
            if (!prepared.Success) return Result(OperationOutcome.Failed, current, null, prepared.Message, [], warnings);
            progress?.Report(OperationPhase.Stop);
            var stopped = await runtime.StopAsync(ProcessScope.DesktopOnly, cancellationToken);
            if (!stopped.Success) return Result(OperationOutcome.Failed, current, null, stopped.Message, [], warnings);
            var rechecked = await runtime.ConfirmStoppedAsync(ProcessScope.DesktopOnly, cancellationToken);
            if (!rechecked.Success) return Result(OperationOutcome.Failed, current, null, rechecked.Message, [], warnings);
            progress?.Report(OperationPhase.Timezone);
            timezoneAttempted = true;
            var tz = await runtime.EnsureTimezoneForLaunchAsync(current, environment, request.AutoSystemTimezone && (environments?.AllowSystemTimezoneChange ?? true), cancellationToken);
            if (!tz.Success) { warnings.Add(tz.Message); await RestoreTimezoneWarningAsync(warnings); return Result(OperationOutcome.Failed, current, null, "启动前时区没有确认", [], warnings); }
            progress?.Report(OperationPhase.Launch);
            attempted = true;
            var launched = await runtime.LaunchAsync(prepared.Request, cancellationToken);
            if (launched.PendingLaunch) return Pending(current, launched, [], warnings);
            if (!launched.Success) { await RestoreTimezoneWarningAsync(warnings); return Result(OperationOutcome.Failed, current, null, launched.Message, [], warnings); }
            attempted = false;
            if (launched.AlreadyRunning || !launched.RuntimeIdentityConfirmed) warnings.Add(launched.Message);
            try
            {
                var reassert = await runtime.ReassertTimezoneAfterLaunchAsync(current, environment, request.AutoSystemTimezone && (environments?.AllowSystemTimezoneChange ?? true), cancellationToken);
                if (!reassert.Success) warnings.Add(reassert.Message);
            }
            catch (Exception ex) { warnings.Add("启动后时区检查未完成：" + ex.GetType().Name); }
            return Result(OperationOutcome.Success, current, launched.AlreadyRunning || !launched.RuntimeIdentityConfirmed ? null : current, launched.Message, [], warnings);
        }
        catch (Exception ex)
        {
            if (attempted) return Result(OperationOutcome.PendingLaunch, current, null, "启动结果未确认，保留时区恢复材料", [], warnings);
            if (timezoneAttempted) await RestoreTimezoneWarningAsync(warnings);
            return Result(OperationOutcome.Failed, current, null, FailureMessage(ex), [], warnings);
        }
        finally { launchScope?.Dispose(); }
    }

    private async Task<OperationResult> RollbackAsync(IdentityRef? original, SwitchSnapshot snapshot, bool isolated, string cause, List<string> warnings, List<string> recovery, IProgress<OperationPhase>? progress)
    {
        progress?.Report(OperationPhase.Rollback);
        try
        {
            var stopped = await runtime.StopAsync(ProcessScope.DesktopAndCli, CancellationToken.None);
            if (!stopped.Success) { warnings.Add(stopped.Message); return Result(OperationOutcome.RecoveryFailed, SafeActive(), null, "恢复前无法确认全部进程退出，恢复材料已保留", recovery, warnings); }
            storage.Restore(snapshot, isolated);
            var restored = storage.ReadActiveIdentity();
            if (restored != original) throw new InvalidDataException("恢复后活动身份未确认到原值");
            await RestoreTimezoneWarningAsync(warnings);
            // Retain the verified snapshot even after rollback for failure review and retry.
            return Result(OperationOutcome.RolledBack, restored, null, "操作失败，原活动身份已恢复：" + cause, recovery, warnings);
        }
        catch (Exception ex)
        {
            warnings.Add(FailureMessage(ex));
            await RestoreTimezoneWarningAsync(warnings);
            return Result(OperationOutcome.RecoveryFailed, SafeActive(), null, "操作失败且原身份恢复未完成，恢复材料已保留", recovery, warnings);
        }
    }
    private async Task RestoreTimezoneWarningAsync(List<string> warnings)
    {
        try { var result = await runtime.RestoreTimezoneAsync(CancellationToken.None); if (!result.Success) warnings.Add(result.Message); }
        catch (Exception ex) { warnings.Add("系统时区恢复未完成：" + ex.GetType().Name); }
    }
    private async Task<DesktopLaunchResult?> GuardPendingAsync(CancellationToken cancellationToken)
    {
        try { return await runtime.CheckPendingAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(LaunchStatus.Pending, false, null, "检查启动", "无法确认上次启动是否结束"); }
    }
    private IdentityRef? SafeActive() { try { return storage.ReadActiveIdentity(); } catch (Exception) { return null; } }
    private async Task<EnvironmentSettings> VerifiedLaunchEnvironmentAsync(IdentityRef identity, EnvironmentSettings environment, LaunchSpecResult spec, CancellationToken token)
    {
        if (egressProbe is null) throw new StorageException(StorageError.InvalidValue, "", "启动前出口 IP 与时区探测未连接，未启动分身。");
        var proxy = spec.Environment?.GetValueOrDefault("HTTP_PROXY") ?? "";
        LastEgressEvidence = await egressProbe.ProbeAsync(identity, proxy, token);
        if (!LaunchEgressRules.Verified(LastEgressEvidence)) throw new StorageException(StorageError.InvalidValue, "", LastEgressEvidence.Message + " 未启动分身。");
        return environment with { Proxy = proxy, FollowSystemProxy = false, Timezone = LastEgressEvidence.Timezone, Locale = "en-US", LocaleAuto = false };
    }
    private LaunchSpecResult Spec(IdentityRef target, EnvironmentSettings environment, string codexHome)
    {
        if (storage is ManagedCloneStorage managed)
        {
            managed.ValidateOwnership();
            if (!Path.GetFullPath(codexHome).TrimEnd(Path.DirectorySeparatorChar).Equals(managed.Paths.ActiveHome.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return new(false, [], new Dictionary<string, string>(), "受管分身启动目录与运行存储不一致，未启动。");
            codexHome = managed.Paths.ActiveHome;
        }
        // Providers have no per-profile proxy field: their public traffic still requires a definite system proxy.
        if (target.Kind == IdentityKind.Provider && string.IsNullOrWhiteSpace(environment.Proxy)) environment = environment with { FollowSystemProxy = true };
        return DesktopLaunchSpec.Build(environment, codexHome, true, [], systemProxy);
    }
    private static string FailureMessage(Exception ex) => ex is OperationCanceledException ? "操作已取消" : ex is StorageException storage ? storage.Message : "操作失败：" + ex.GetType().Name;
    private static OperationResult Result(OperationOutcome outcome, IdentityRef? active, IdentityRef? running, string message, IReadOnlyList<string> recovery, IReadOnlyList<string> warnings) => new(outcome, active, running, message, recovery.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), warnings.ToArray());
    private static OperationResult Pending(IdentityRef? active, DesktopLaunchResult pending, IReadOnlyList<string> recovery, IReadOnlyList<string> warnings) => Result(OperationOutcome.PendingLaunch, active, null, pending.Message, pending.StateDirectory is { } path ? recovery.Append(path).ToArray() : recovery, warnings);
}
