using System.Diagnostics;
using System.IO;

namespace ChunsonSpace.Desktop;

public sealed partial class ShellViewModel
{
    private GitHubUpdateService? updates;
    private readonly CancellationTokenSource updateLifetime = new();
    private CancellationTokenSource? updateDownload;
    private bool automaticUpdateChecked, checkingUpdate, preparingUpdate;
    private AppRelease? availableUpdate;
    private string updateStatus = "";
    internal Func<Func<Task>, Task<bool>>? ExitManagerForUpdate { get; set; }
    internal Task UpdateOperation { get; private set; } = Task.CompletedTask;
    public DelegateCommand CheckUpdateCommand { get; private set; } = null!;
    public DelegateCommand OnlineUpdateCommand { get; private set; } = null!;
    public DelegateCommand ViewUpdateCommand { get; private set; } = null!;
    public DelegateCommand CancelUpdateCommand { get; private set; } = null!;
    public string AppVersionLabel => "椿生空间 " + UpdateRules.DisplayVersion(UpdateRules.CurrentVersion);
    public string UpdateStatus => updateStatus;
    public bool HasUpdate => availableUpdate is not null;
    public bool CanDownloadUpdate => availableUpdate?.Manifest is not null && availableUpdate.Archive is not null;
    public bool IsPreparingUpdate => preparingUpdate;
    public string UpdateTitle => availableUpdate is { } release ? "发现新版本 " + UpdateRules.DisplayVersion(release.Version) : "";
    private bool UpdateIdle => !globalBusy && !IsLoggingIn && ProxyPanel.NodeTesting.CanEdit && !preparingUpdate;
    private void InitializeUpdateCommands()
    {
        CheckUpdateCommand = new(_ => UpdateOperation = CheckForUpdatesAsync(true), _ => !checkingUpdate && !preparingUpdate && !exitDraining);
        OnlineUpdateCommand = new(_ => UpdateOperation = DownloadUpdateAsync(), _ => CanDownloadUpdate && UpdateIdle && ExitManagerForUpdate is not null);
        ViewUpdateCommand = new(_ =>
        {
            if (availableUpdate is { } release)
                try { Process.Start(new ProcessStartInfo(release.Page) { UseShellExecute = true }); }
                catch (Exception) { SetUpdateStatus("无法打开浏览器，请稍后重试。"); }
        }, _ => HasUpdate);
        CancelUpdateCommand = new(_ => updateDownload?.Cancel(), _ => preparingUpdate);
        ProxyPanel.NodeTesting.PropertyChanged += (_, e) => { if (e.PropertyName is "IsBusy" or "CanEdit") RefreshUpdateCommands(); };
    }
    private void RefreshUpdateCommands()
    {
        CheckUpdateCommand?.Refresh(); OnlineUpdateCommand?.Refresh(); ViewUpdateCommand?.Refresh(); CancelUpdateCommand?.Refresh();
        Raise(nameof(HasUpdate)); Raise(nameof(UpdateTitle)); Raise(nameof(CanDownloadUpdate)); Raise(nameof(IsPreparingUpdate));
    }
    private void SetUpdateStatus(string value) { updateStatus = value; Raise(nameof(UpdateStatus)); }
    internal void UseUpdateService(GitHubUpdateService service) { updates?.Dispose(); updates = service; }
    internal async Task CheckForUpdatesAsync(bool manual)
    {
        if (checkingUpdate || preparingUpdate || updateLifetime.IsCancellationRequested || !manual && automaticUpdateChecked) return;
        if (!manual) automaticUpdateChecked = true;
        checkingUpdate = true; RefreshUpdateCommands();
        if (manual) SetUpdateStatus("正在检查 GitHub 正式版…");
        try
        {
            var release = await (updates ??= new()).CheckAsync(updateLifetime.Token);
            if (release.Version > UpdateRules.CurrentVersion)
            {
                availableUpdate = release;
                SetUpdateStatus(CanDownloadUpdate ? "可在线更新：只下载变化文件，完成后重开椿生空间。" : "此版本没有增量更新清单，请查看发布页。");
            }
            else { availableUpdate = null; if (manual) SetUpdateStatus("已是最新正式版。"); }
        }
        catch (Exception) { if (manual && !updateLifetime.IsCancellationRequested) SetUpdateStatus("检查更新失败，请稍后重试。"); }
        finally { checkingUpdate = false; RefreshUpdateCommands(); }
    }
    internal async Task DownloadUpdateAsync()
    {
        if (!CanDownloadUpdate || !UpdateIdle || ExitManagerForUpdate is null) return;
        Navigate("Settings");
        preparingUpdate = true; SetBusy(true);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token); updateDownload = cancel;
        try
        {
            var root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var work = Path.Combine(Path.GetDirectoryName(root)!, ".chunson-updates", Guid.NewGuid().ToString("N"));
            SetUpdateStatus("正在准备增量更新…");
            var progress = new Progress<string>(SetUpdateStatus);
            var prepared = await Task.Run(() => (updates ??= new()).PrepareAsync(availableUpdate!, root, Path.Combine(work, "files"), progress, cancel.Token), cancel.Token);
            if (prepared.Files.Length == 0) { SetUpdateStatus("本机应用文件已与此版本一致。无需下载或重开。"); return; }
            var commit = await AppUpdateInstaller.StartAsync(prepared, root, cancel.Token);
            SetUpdateStatus("下载与校验完成，正在退出并更新；完成后自动重开…");
            // Release the shared operation boundary only after all bytes and helper identity are verified.
            preparingUpdate = false; SetBusy(false);
            if (!await ExitManagerForUpdate(async () => { cancel.Token.ThrowIfCancellationRequested(); await File.WriteAllTextAsync(commit, "install", cancel.Token); })) SetUpdateStatus("退出尚未完成，更新帮助程序会超时退出；当前文件未替换。请稍后重试。");
        }
        catch (OperationCanceledException) { SetUpdateStatus("已取消更新；当前应用未替换。"); }
        catch (Exception error) { SetUpdateStatus(error is InvalidDataException ? error.Message : "更新未完成；当前应用保留。请稍后重试或查看发布页。"); }
        finally { updateDownload = null; preparingUpdate = false; SetBusy(false); }
    }
    internal void UpdateCompleted() { Navigate("Settings"); SetUpdateStatus("更新完成，当前版本 " + UpdateRules.DisplayVersion(UpdateRules.CurrentVersion) + "。"); }
    internal void UpdateFailed() { Navigate("Settings"); SetUpdateStatus("更新未完成，已保留原版本。可稍后重试或查看发布页。"); }
    internal void DisposeUpdates() { updateLifetime.Cancel(); updates?.Dispose(); }
}
