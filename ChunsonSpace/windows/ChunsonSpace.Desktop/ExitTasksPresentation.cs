using ChunsonSpace.Accounts;

namespace ChunsonSpace.Desktop;

public sealed partial class ShellViewModel
{
    private bool exitDraining;
    private async Task TrackNewAccountLoginAsync(LoginProxySelection input, bool prepareAccount, CancellationToken token)
    {
        CurrentOperation = LoginNewAccountAsync(input, token, prepareAccount);
        await CurrentOperation;
    }
    internal async Task<bool> DrainTasksForExitAsync(bool explicitExit = false)
    {
        var login = workspace?.Accounts?.Login.Status;
        var cancellableLogin = login?.Active == true && login.Stage is LoginStage.Checking or LoginStage.AccountPreparation or LoginStage.ReadyToAuthorize or LoginStage.Browser;
        if (!explicitExit && operationBusy && !cancellableLogin && onboarding?.IsStarting != true && !preparingUpdate)
        {
            ReportLifecycleFailure("暂不能退出", "当前操作正在提交或写入资料。请等待完成后再退出；没有强制停止或删除数据。");
            return false;
        }
        exitDraining = true; accountTests.Accepting = false; RefreshBusyState();
        updateDownload?.Cancel();
        ShowFeedback("正在退出", "正在取消授权和测试，等待浏览器与独立端口释放…", "Info");
        var tasks = visualPanels.Where(panel => panel.IsBusy).Select(panel => panel.CurrentOperation)
            .Concat(cialloPanels.Where(panel => panel.IsBusy).Select(panel => panel.CurrentOperation)).ToList();
        if (preparingUpdate) tasks.Add(UpdateOperation);
        else if (operationBusy) tasks.Add(CurrentOperation);
        if (retainedHealth?.IsBusy == true) { tasks.Add(retainedHealth.CurrentOperation); retainedHealth.CancelCommand.Execute(null); }
        if (onboarding?.IsStarting == true) { tasks.Add(onboarding.CurrentOperation); onboarding.CloseCommand.Execute(null); }
        if (cancellableLogin) workspace!.Accounts!.Login.Cancel();
        foreach (var panel in visualPanels) panel.CancelCommand.Execute(null);
        foreach (var panel in cialloPanels) panel.CancelCommand.Execute(null);
        tasks.Add(ProxyPanel.NodeTesting.StopAsync());
        if (checkingLocalEnvironment) tasks.Add(LocalEnvironmentOperation);
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(explicitExit ? 3 : 30)); return true; }
        catch (OperationCanceledException) { return true; }
        catch (Exception error)
        {
            ReportLifecycleFailure("退出未完成", "后台任务释放未确认：" + ErrorRedaction.Scrub(error.Message));
            return explicitExit;
        }
    }
    internal void ResumeAfterExitFailure()
    {
        exitDraining = false; accountTests.Accepting = true; ProxyPanel.NodeTesting.ResumeAfterStop(); RefreshBusyState();
    }
}
