using FAswitch.Core;
using FAswitch.Windows;

namespace FAswitch.Application;

public sealed record MaintenanceResult(bool Success, string Message, RestoreResult? Restore, IReadOnlyList<string> RecoveryPaths, ProfileSaveResult? Save = null, ProfileLogoutResult? Logout = null, RebuildResult? Rebuild = null, AccountDeleteResult? Delete = null);
public sealed class MaintenanceService(IRuntimeOperations runtime)
{
    public async Task<MaintenanceResult> ResetManagedRuntimeAsync(ManagedCloneStorage storage, CancellationToken cancellationToken = default)
    {
        var block = await EnsureSafeAsync(cancellationToken);
        if (block is not null) return block;
        try
        {
            storage.ResetRuntime();
            return new(true, "受管分身已清理，账号库授权与配置已保留。", null, []);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException)
        {
            var detail = error.Message;
            if (error is StorageException failure && failure.FilePath.Length > 0 && !detail.Contains(failure.FilePath, StringComparison.OrdinalIgnoreCase))
                detail += " 位置：" + failure.FilePath;
            return new(false, "受管分身清理未完成，账号库保持保留：" + detail, null, [storage.ClonePaths.RecoveryRoot]);
        }
    }

    public async Task<MaintenanceResult> RestoreBackupAsync(BackupService backups, string name, CancellationToken cancellationToken = default)
    {
        var block = await EnsureSafeAsync(cancellationToken);
        if (block is not null) return block;
        var result = backups.Restore(name);
        var recovery = new List<string>();
        if (result.PreBackup.Length > 0) recovery.Add(result.PreBackup);
        if (result.StagingPath is { Length: > 0 } staging) recovery.Add(staging);
        return new(result.Ok, result.Ok ? "备份已恢复" : "备份恢复未完成，恢复前备份与暂存材料已保留", result, recovery);
    }

    public async Task<MaintenanceResult> SaveCurrentAccountAsync(ProfileMaintenance profiles, string name, bool overwrite, bool isolated, bool developer = false, CancellationToken cancellationToken = default)
    {
        var block = await EnsureSafeAsync(cancellationToken);
        if (block is not null) return block;
        var result = profiles.SaveCurrentAccount(name, overwrite, isolated, developer);
        return new(result.Success, result.Message, null, result.RecoveryPaths, Save: result);
    }

    public async Task<MaintenanceResult> DeleteAccountAsync(ProfileMaintenance profiles, string name, CancellationToken cancellationToken = default)
    {
        var block = await EnsureSafeAsync(cancellationToken);
        if (block is not null) return block;
        var result = profiles.DeleteAccount(name);
        return new(result.Success, result.Message, null, result.Archives, Delete: result);
    }

    public async Task<MaintenanceResult> LogoutAsync(ProfileMaintenance profiles, bool clearDesktopState = false, CancellationToken cancellationToken = default)
    {
        var block = await EnsureSafeAsync(cancellationToken);
        if (block is not null) return block;
        var result = profiles.Logout(clearDesktopState);
        var message = result.Message;
        try { var timezone = await runtime.RestoreTimezoneAsync(CancellationToken.None); if (!timezone.Success) message += "；" + timezone.Message; }
        catch (Exception error) { message += "；时区恢复未完成：" + error.GetType().Name; }
        return new(result.Success, message, null, [result.Snapshot], Logout: result);
    }

    public RebuildPlan PreviewRebuild(EnvironmentRebuild rebuild) => rebuild.Preview();
    public async Task<MaintenanceResult> RebuildAsync(EnvironmentRebuild rebuild, CancellationToken cancellationToken = default)
    {
        var block = await EnsureSafeAsync(cancellationToken);
        if (block is not null) return block;
        var result = rebuild.Execute();
        var message = result.Message;
        try { var timezone = await runtime.RestoreTimezoneAsync(CancellationToken.None); if (!timezone.Success) message += "；" + timezone.Message; }
        catch (Exception error) { message += "；时区恢复未完成：" + error.GetType().Name; }
        return new(result.Success, message, null, result.Destination.Length > 0 ? [result.Destination] : [], Rebuild: result);
    }

    private async Task<MaintenanceResult?> EnsureSafeAsync(CancellationToken cancellationToken)
    {
        DesktopLaunchResult? pending;
        try { pending = await runtime.CheckPendingAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(false, "无法确认上次桌面启动，本次操作未执行", null, []); }
        if (pending is not null) return new(false, "桌面启动仍待确认，本次操作未执行", null, pending.StateDirectory is { } directory ? [directory] : []);
        var stopped = await runtime.StopAsync(ProcessScope.DesktopAndCli, cancellationToken);
        if (!stopped.Success) return new(false, stopped.Message, null, []);
        cancellationToken.ThrowIfCancellationRequested();
        var rechecked = await runtime.ConfirmStoppedAsync(ProcessScope.DesktopAndCli, cancellationToken);
        if (!rechecked.Success) return new(false, rechecked.Message, null, []);
        return null;
    }
}
