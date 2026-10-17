using FAswitch.Accounts;

namespace FAswitch.Desktop;

public sealed partial class ShellViewModel
{
    private bool exitDraining;
    private async Task TrackNewAccountLoginAsync(LoginProxySelection input, CancellationToken token)
    {
        CurrentOperation = LoginNewAccountAsync(input, token);
        await CurrentOperation;
    }
    internal async Task<bool> DrainTasksForExitAsync()
    {
        var login = workspace?.Accounts?.Login.Status;
        var cancellableLogin = login?.Active == true && login.Stage is LoginStage.Checking or LoginStage.Browser;
        if (operationBusy && !cancellableLogin && onboarding?.IsStarting != true)
        {
            ReportLifecycleFailure("暂不能退出", "当前操作正在提交或写入资料。请等待完成后再退出；没有强制停止或删除数据。");
            return false;
        }
        exitDraining = true; accountTests.Accepting = false; RefreshBusyState();
        ShowFeedback("正在退出", "正在取消授权和测试，等待浏览器与独立端口释放…", "Info");
        var tasks = visualPanels.Where(panel => panel.IsBusy).Select(panel => panel.CurrentOperation)
            .Concat(cialloPanels.Where(panel => panel.IsBusy).Select(panel => panel.CurrentOperation)).ToList();
        if (operationBusy) tasks.Add(CurrentOperation);
        if (onboarding?.IsStarting == true) { tasks.Add(onboarding.CurrentOperation); onboarding.CloseCommand.Execute(null); }
        if (cancellableLogin) workspace!.Accounts!.Login.Cancel();
        foreach (var panel in visualPanels) panel.CancelCommand.Execute(null);
        foreach (var panel in cialloPanels) panel.CancelCommand.Execute(null);
        tasks.Add(ProxyPanel.NodeTesting.StopAsync());
        if (checkingLocalEnvironment) tasks.Add(LocalEnvironmentOperation);
        try { await Task.WhenAll(tasks); return true; }
        catch (OperationCanceledException) { return true; }
        catch (Exception error)
        {
            ReportLifecycleFailure("退出未完成", "后台任务释放未确认：" + ErrorRedaction.Scrub(error.Message));
            return false;
        }
    }
    internal void ResumeAfterExitFailure()
    {
        exitDraining = false; accountTests.Accepting = true; ProxyPanel.NodeTesting.ResumeAfterStop(); RefreshBusyState();
    }
}
