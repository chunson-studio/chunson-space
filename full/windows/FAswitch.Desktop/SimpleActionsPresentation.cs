using FAswitch.Core;
using FAswitch.Accounts;
using System.IO;

namespace FAswitch.Desktop;

public sealed partial class ShellViewModel
{
    public DelegateCommand HomeLaunchCommand { get; private set; } = null!;
    public DelegateCommand OneClickRebuildCommand { get; private set; } = null!;
    public DelegateCommand CloseManagedCommand { get; private set; } = null!;
    private bool ResumeSelectedLogin => NeedsLogin && LoginTarget == Selected?.Identity;
    private bool SelectedIsRunning => Selected is not null && runningIdentity == Selected.Identity;
    public string HomeLaunchLabel => ResumeSelectedLogin ? "继续登录并启动" : workspace is null ? "模拟切换并启动"
        : SelectedIsRunning ? "运行中" : SelectedIsActive ? "启动" : "切换并启动";
    public string HomeLaunchTarget => Selected is null ? "先在首页选择账号" : "目标：" + Selected.Name;
    public bool CanSelectIdentity => !operationBusy && !exitDraining;
    public string FeedbackNextAction => !IsError && !FeedbackTitle.Contains("启动待确认", StringComparison.Ordinal) ? ""
        : FeedbackTitle == "地区与时区同步未完成" ? "下一步：根据上方原因核对 FlClash 当前网络，再点击「同步地区与时区」；此操作与账号独立 IP 配置无关。"
        : Feedback.Contains("上次专用登录浏览器", StringComparison.Ordinal) ? "下一步：关闭本次授权浏览器后重试；软件会重新核对退出状态，已有账号资料保留。"
        : Feedback.Contains("目录链接", StringComparison.Ordinal) || Feedback.Contains("缓存链接", StringComparison.Ordinal) || Feedback.Contains("符号链接", StringComparison.Ordinal) || Feedback.Contains("目录联接", StringComparison.Ordinal) ? "下一步：复制错误信息反馈；保留提示中的目录与恢复材料。"
        : ResumeSelectedLogin && FeedbackTitle.Contains("目标档案需要登录", StringComparison.Ordinal) ? "下一步：点击「继续登录并启动」，完成该账号授权后自动继续。"
        : Feedback.Contains("代理", StringComparison.Ordinal) || Feedback.Contains("出口", StringComparison.Ordinal) ? "下一步：到「网络与 IP」检查客户端与出口，再重试；账号出口可在高级管理中修改。"
        : Feedback.Contains("时区", StringComparison.Ordinal) || Feedback.Contains("地区", StringComparison.Ordinal) ? "下一步：检查代理出口，或到「环境设置」重新配置地区与时区。"
        : Feedback.Contains("权限", StringComparison.Ordinal) || Feedback.Contains("授权", StringComparison.Ordinal) ? "下一步：查看上方具体原因，完成所需授权后重试。"
        : Feedback.Contains("清理", StringComparison.Ordinal) || Feedback.Contains("进程", StringComparison.Ordinal) || Feedback.Contains("占用", StringComparison.Ordinal) ? "下一步：关闭受管分身窗口后重试；保留提示中的恢复材料。"
        : FeedbackTitle.Contains("启动待确认", StringComparison.Ordinal) ? "下一步：检查受管分身窗口是否已打开，再刷新列表确认运行状态。"
        : "下一步：按上方原因处理后重试；高级管理可检查账号配置，恢复材料请保留。";
    public bool HasFeedbackNextAction => FeedbackNextAction.Length > 0;

    private void InitializeSimpleActions()
    {
        HomeLaunchCommand = new(_ =>
        {
            if (ResumeSelectedLogin)
            {
                if (loginSwitchRequest is { } request && Selected is { } target)
                    loginSwitchRequest = request with { Launch = true, Isolated = IsolateSessions, AutoSystemTimezone = UsesManagedClone ? false : AutoSystemTimezone, Environment = target.Environment };
                LoginCommand.Execute(null);
            }
            else if (workspace is not null && SelectedIsActive) LaunchCurrentCommand.Execute(null);
            else LaunchCommand.Execute(null);
        },
            _ => ResumeSelectedLogin ? LoginCommand.CanExecute(null) : !SelectedIsRunning && (workspace is not null && SelectedIsActive ? LaunchCurrentCommand.CanExecute(null) : LaunchCommand.CanExecute(null)));
        OneClickRebuildCommand = new(_ => CurrentOperation = RebuildManagedEnvironmentAsync(),
            _ => !globalBusy && workspace?.ManagedPaths is not null && workspace.SwitchStorage is ManagedCloneStorage && workspace.Maintenance is not null);
        CloseManagedCommand = new(_ => CurrentOperation = CloseManagedAsync(),
            _ => !busy && UsesManagedClone && workspace?.Runtime is not null && (runtimeProcessAlive || runningIdentity is not null));
    }
    private void RefreshSimpleActions()
    {
        HomeLaunchCommand?.Refresh(); OneClickRebuildCommand?.Refresh(); CloseManagedCommand?.Refresh();
        NavigateCommand.Refresh(); Raise(nameof(CanSelectIdentity));
        Raise(nameof(HomeLaunchLabel)); Raise(nameof(HomeLaunchTarget));
        Raise(nameof(FeedbackNextAction)); Raise(nameof(HasFeedbackNextAction));
    }
    private async Task CloseManagedAsync()
    {
        if (!CloseManagedCommand.CanExecute(null)) return;
        SetBusy(true);
        try
        {
            using var lease = workspace!.Ownership is { } ownership ? await ownership.BeginLaunchAsync() : null;
            if (workspace.Ownership is null && await workspace.Runtime!.CheckPendingAsync(CancellationToken.None) is not null)
            { ShowFeedback("分身关闭待确认", "桌面启动尚未确认，本次未关闭进程。", "Info"); return; }
            var stopped = workspace.Ownership is { } tracked
                ? await tracked.CloseTrackedAsync()
                : await workspace.Runtime!.StopAsync(FAswitch.Windows.ProcessScope.DesktopAndCli, CancellationToken.None);
            if (!stopped.Success) { ShowFeedback("分身未关闭", stopped.Message, "Error"); return; }
            var confirmed = await workspace.Runtime!.ConfirmStoppedAsync(FAswitch.Windows.ProcessScope.DesktopAndCli, CancellationToken.None);
            if (!confirmed.Success) { ShowFeedback("分身关闭待确认", confirmed.Message, "Error"); return; }
            runningIdentity = null; runtimeProcessAlive = false; runtimeExited = true; lastOutcome = null;
            Raise(nameof(RunningLabel)); Raise(nameof(ManagedCloneStatus)); NotifyHomeDetails();
            ShowFeedback("分身已关闭", "账号授权、运行资料和当前系统时区保留；Codex 本体保持运行。", "Success");
        }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { ShowFeedback("分身未关闭", ErrorRedaction.Scrub(error.Message), "Error"); }
        finally { SetBusy(false); }
    }
    public async Task RebuildManagedEnvironmentAsync()
    {
        if (!OneClickRebuildCommand.CanExecute(null)) return;
        try
        {
            ShowFeedback("正在重建受管环境", "关闭受管分身、保存授权资料并重建运行目录。账号库、项目文件和 Codex 本体保留。", "Info");
            await SaveEditorAsync(new(ProfileEditMode.Rebuild, IdentityKind.Account, null, SaveEditorAsync, CloseEditor) { Name = Selected?.Name ?? "" });
        }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { ShowFeedback("环境重建未完成", ErrorRedaction.Scrub(error.Message), "Error"); }
    }
}
