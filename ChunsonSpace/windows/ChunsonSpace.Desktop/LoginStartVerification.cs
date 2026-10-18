using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using ChunsonSpace.Accounts;

namespace ChunsonSpace.Desktop;

internal static class LoginStartVerification
{
    public static async Task RunAsync(MainWindow window, string directory, Action<string, bool> check, Action<string> capture)
    {
        var previous = window.DataContext;
        var workspace = BackgroundAccountTestsVerification.Fixture(directory);
        var model = new ShellViewModel(workspace);
        var services = workspace.Accounts!;
        var transport = services.OfflineTransport!;
        var browser = services.OfflineNewAccountBrowser!;
        var callbacks = services.OfflineCallbacks!;
        window.DataContext = model;
        void Flush() => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            model.NewAccountLoginCommand.Execute(null); var input = model.Onboarding!; Flush();
            check("new account offers preparation by default without starting any request", input.PrepareAccountFirst && !input.DirectAuthorization
                && transport.RequestCount == 0 && browser.OpenCount == 0 && callbacks.Starts == 0);
            var direct = (RadioButton)window.FindName("DirectAuthorizationMode");
            var prepare = (RadioButton)window.FindName("PrepareAccountMode");
            var start = (Button)window.FindName("OnboardingAuthorizeButton");
            direct.IsChecked = true; Flush();
            check("direct authorization choice updates the visible start action without network", input.DirectAuthorization && !input.PrepareAccountFirst
                && start.Content?.ToString() == "直接登录授权" && transport.RequestCount == 0);
            foreach (var dark in new[] { false, true })
            {
                model.DarkTheme = dark; capture("login-start-choice-" + (dark ? "dark" : "light"));
                check("login start choices and action fit the minimum " + (dark ? "dark" : "light") + " dialog",
                    UiVerification.FullyVisible(prepare, window.RenderRoot) && UiVerification.FullyVisible(direct, window.RenderRoot)
                    && UiVerification.FullyVisible(start, window.RenderRoot));
            }
            model.DarkTheme = false;
            callbacks.HoldCallback = true;
            input.AuthorizeCommand.Execute(null);
            await BackgroundAccountTestsVerification.Until(() => services.Login.Status.Stage == LoginStage.Browser
                && browser.LastUrl is not null && browser.OpenCount == 1 && callbacks.Starts == 1);
            input.PrepareAccountFirst = true;
            check("direct flow opens only OAuth and keeps its selected mode fixed while running", input.DirectAuthorization && browser.OpenCount == 1
                && browser.LastUrl?.Host == AccountEndpoints.Authorize.Host && callbacks.Starts == 1
                && browser.CleanupCalls == 0 && !services.Login.CanBeginAuthorization
                && !transport.Requests.Any(request => request.Url == AccountEndpoints.Token));
            model.CancelLoginCommand.Execute(null); await input.CurrentOperation;
            check("cancel direct authorization leaves existing accounts unchanged and cleans its browser", !model.IsBusy
                && workspace.Profiles.ListAccounts().Count == 2 && browser.CleanupCalls > 0);
            model.NewAccountLoginCommand.Execute(null); input = model.Onboarding!;
            check("a later addition offers an explicit fresh choice", input.PrepareAccountFirst && !input.DirectAuthorization);
            input.DirectAuthorization = true; transport.FailTokenRequests = true; callbacks.HoldCallback = false;
            browser.CleanupSucceeds = false;
            var count = transport.Requests.Count(request => request.Url == AccountEndpoints.Token);
            input.AuthorizeCommand.Execute(null); await input.CurrentOperation; Flush();
            capture("login-token-failure-stage");
            check("exchange failure distinguishes received callback from saved account and explains direct retry", model.IsError
                && model.FeedbackTitle == "网页授权已收到，凭据未保存" && model.Feedback.Contains("未保存新授权")
                && model.FeedbackNextAction.Contains("直接登录授权") && model.FeedbackErrorReport.Contains("登录阶段：Token")
                && model.FeedbackErrorReport.Contains("回调状态：Code") && workspace.Profiles.ListAccounts().Count == 2
                && model.FeedbackErrorReport.Contains("会话清理：未确认")
                && transport.Requests.Count(request => request.Url == AccountEndpoints.Token) == count + 1);
            transport.FailTokenRequests = false; browser.CleanupSucceeds = true;
            var opens = browser.OpenCount;
            model.NewAccountLoginCommand.Execute(null); input = model.Onboarding!; input.DirectAuthorization = true;
            input.AuthorizeCommand.Execute(null); await input.CurrentOperation;
            check("direct retry saves credentials once without the preparation browser", !model.IsError && model.FeedbackTitle == "账号已登录并保存"
                && workspace.Profiles.ListAccounts().Count == 3 && browser.OpenCount == opens + 1
                && services.Login.Status is { Stage: LoginStage.Done, Result.Ok: true });
            model.NewAccountLoginCommand.Execute(null); input = model.Onboarding!; input.DirectAuthorization = true;
            transport.RequestObserved = request => { if (request.Url == AccountEndpoints.Token) browser.CleanupSucceeds = false; };
            input.AuthorizeCommand.Execute(null); await input.CurrentOperation;
            check("saved credentials remain explicit when later browser cleanup fails", model.IsError
                && model.FeedbackTitle == "账号已保存，浏览器清理待完成" && model.Feedback.Contains("授权已保存")
                && model.FeedbackErrorReport.Contains("登录阶段：Done") && model.FeedbackErrorReport.Contains("会话清理：未确认")
                && services.Login.Status.Result?.Ok == true && workspace.Profiles.ListAccounts().Count == 3);
            transport.RequestObserved = null; browser.CleanupSucceeds = true;
            model.ShowError();
            check("unrelated feedback clears the old login diagnosis", !model.FeedbackErrorReport.Contains("登录阶段："));
            check("login choices never send model requests or write system region", !transport.Requests.Any(request => request.Url == AccountEndpoints.Responses)
                && services.OfflineRegion!.TimezoneWrites == 0 && services.OfflineRegion.RegionWrites == 0);
        }
        finally { browser.CleanupSucceeds = true; await services.DisposeAsync(); window.DataContext = previous; }
    }
}
