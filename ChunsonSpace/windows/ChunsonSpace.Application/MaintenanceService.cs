using ChunsonSpace.Core;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Application;

public sealed record MaintenanceResult(bool Success, string Message, IReadOnlyList<string> RecoveryPaths, ProfileSaveResult? Save = null, ProfileLogoutResult? Logout = null, RebuildResult? Rebuild = null, AccountDeleteResult? Delete = null);
public sealed class MaintenanceService(IRuntimeOperations runtime)
{
    public async Task<MaintenanceResult> ResetManagedRuntimeAsync(ManagedCloneStorage storage, CancellationToken cancellationToken = default, bool discard = false)
    {
        using var lease = await runtime.BeginLaunchAsync(cancellationToken);
        try
        {
            storage.ValidateOwnership();
            var block = await EnsureSafeAsync(cancellationToken, discard);
            if (block is not null) return block;
            if (discard) storage.DiscardRuntime(); else storage.ResetRuntime();
            return new(true, "分身已重置，保持关闭。账号库、项目和 Codex 本体已保留。", []);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException)
        {
            return new(false, "分身重置未完成：运行资料被占用或路径归属未确认（" + error.GetType().Name + "）。账号库已保留。", []);
        }
    }

    public async Task<MaintenanceResult> SaveCurrentAccountAsync(ProfileMaintenance profiles, string name, bool overwrite, bool isolated, bool developer = false, CancellationToken cancellationToken = default)
    {
        var block = await EnsureSafeAsync(cancellationToken);
        if (block is not null) return block;
        var result = profiles.SaveCurrentAccount(name, overwrite, isolated, developer);
        return new(result.Success, result.Message, result.RecoveryPaths, Save: result);
    }

    public async Task<MaintenanceResult> DeleteAccountAsync(ProfileMaintenance profiles, string name, CancellationToken cancellationToken = default, ManagedCloneStorage? managed = null)
    {
        name = ProfileRules.Name(name);
        // No auth reads and no runtime action for an unrelated account.
        IdentityRef? reference = null;
        bool unknown = false;
        if (managed is not null)
        {
            try { reference = managed.ReadRuntimeReference(); }
            catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException) { unknown = true; }
        }
        using var lease = reference == new IdentityRef(IdentityKind.Account, name) ? await runtime.BeginLaunchAsync(cancellationToken) : null;
        // Switching uses the same lease. Recheck after acquiring it, before stopping anything.
        if (reference == new IdentityRef(IdentityKind.Account, name)) reference = managed!.ReadRuntimeReference();
        if (reference == new IdentityRef(IdentityKind.Account, name))
        {
            var block = await EnsureSafeAsync(cancellationToken);
            if (block is not null) return block;
            managed!.DiscardRuntime();
        }
        var result = profiles.DeleteAccount(name);
        return new(result.Success, result.Message + (unknown ? "；分身归属未知，运行资料保留。" : ""), result.Archives, Delete: result);
    }

    public async Task<MaintenanceResult> LogoutAsync(ProfileMaintenance profiles, bool clearDesktopState = false, CancellationToken cancellationToken = default)
    {
        var block = await EnsureSafeAsync(cancellationToken);
        if (block is not null) return block;
        var result = profiles.Logout(clearDesktopState);
        var message = result.Message;
        try { var timezone = await runtime.RestoreTimezoneAsync(CancellationToken.None); if (!timezone.Success) message += "；" + timezone.Message; }
        catch (Exception error) { message += "；时区恢复未完成：" + error.GetType().Name; }
        return new(result.Success, message, [result.Snapshot], Logout: result);
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
        return new(result.Success, message, result.Destination.Length > 0 ? [result.Destination] : [], Rebuild: result);
    }

    private async Task<MaintenanceResult?> EnsureSafeAsync(CancellationToken cancellationToken, bool force = false)
    {
        var prepared = await runtime.PrepareMaintenanceAsync(cancellationToken, force);
        if (!prepared.Success && !force) return new(false, prepared.Message, []);
        var stopped = await runtime.StopAsync(ProcessScope.DesktopAndCli, cancellationToken, force);
        if (!stopped.Success) return new(false, stopped.Message, []);
        if (!prepared.Success) return new(false, prepared.Message + "运行资料未清理。", []);
        cancellationToken.ThrowIfCancellationRequested();
        var rechecked = await runtime.ConfirmStoppedAsync(ProcessScope.DesktopAndCli, cancellationToken, force);
        if (!rechecked.Success) return new(false, rechecked.Message, []);
        return null;
    }
}
