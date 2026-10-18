using System.Windows.Controls;
using System.Windows.Threading;
using ChunsonSpace.Accounts;

namespace ChunsonSpace.Desktop;

internal static class SupportReportVerification
{
    public static async Task RunAsync(MainWindow window, string directory, Action<string, bool> check, Action<string> capture)
    {
        var text = SupportReport.Create("动物与机械", "HTTP 400 model_not_supported\nAuthorization: Bearer private-auth\nCookie: private-cookie\n{\"access_token\":\"private-access\",\"password\":\"private-password\"}\n邮箱 account-d96f4cbfc0@example.invalid\n路径 C:\\Users\\private-user\\auth.json\nhttps://user:account-87887e050a@example.invalid/path?code=private-code\n198.51.100.12\n2001:db8::1234\n账号 SampleAccount\n" + new string('甲', 300) + " diagnostic-tail", new DateTimeOffset(2026, 10, 17, 14, 33, 17, TimeSpan.Zero), "SampleAccount");
        check("support report preserves time status code and full diagnostic tail", text.Contains("14:33:17") && text.Contains("HTTP 400 model_not_supported") && text.Contains("diagnostic-tail"));
        check("support report removes credentials contact paths IP and profile name", new[] { "private-auth", "private-cookie", "private-access", "private-password", "private-user", "private-code", "account-d96f4cbfc0@example.invalid", "198.51.100.12", "2001:db8::1234", "SampleAccount" }.All(secret => !text.Contains(secret)));
        string? copied = null;
        check("copy writes only prepared report to injected clipboard", SupportReport.TryCopy(text, value => copied = value) && copied == text);
        check("clipboard busy returns recoverable failure", !SupportReport.TryCopy(text, _ => throw new System.Runtime.InteropServices.ExternalException("busy")));
        check("oversized support report is bounded", SupportReport.Sanitize(new string('甲', 20000)).Length < 16100);

        var workspace = BackgroundAccountTestsVerification.Fixture(directory);
        var model = new ShellViewModel(workspace); window.DataContext = model;
        model.Selected = model.FilteredItems.Single(item => item.Name == "A");
        var runtime = workspace.Accounts!.VisualRuntimeFixture!;
        runtime.ReplyFactory = (_, _, _) => throw new AccountException(AccountError.Network, "HTTP 400 model_not_supported：当前模型不可用，account-d96f4cbfc0@example.invalid");
        model.OpenVisualTestsCommand.Execute(null); var panel = model.VisualTests!;
        panel.StartCommand.Execute(null); await panel.CurrentOperation;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); capture("copy-test-error-minimum");
        var button = (Button)window.FindName("VisualTestCopyErrorButton");
        check("failed visual result offers visible bound diagnostic copy", UiVerification.FullyVisible(button, window.RenderRoot) && button.IsEnabled && button.Tag is string report && report.Contains("HTTP 400") && !report.Contains("account-d96f4cbfc0@example.invalid"));
        var requests = runtime.Requests.Count;
        panel.SelectedHistory = panel.History.First();
        check("historical failure can be reported without another request", panel.ErrorReport.Contains("HTTP 400") && runtime.Requests.Count == requests);
        panel.CloseCommand.Execute(null); model.OpenCialloCommand.Execute(null); var ciallo = model.Ciallo!;
        ciallo.Rows[0].Update(new("gpt-6-astra", "", "high", "failed", "HTTP 403 access_token=private-value", TimeSpan.Zero));
        ciallo.RefreshTestEligibility();
        check("ciallo report includes failed models and removes token", ciallo.ErrorReport.Contains("HTTP 403") && !ciallo.ErrorReport.Contains("private-value"));
        ciallo.CloseCommand.Execute(null); model.ReportLifecycleFailure("启动失败", "HTTP 502 account-d96f4cbfc0@example.invalid");
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); capture("copy-main-error-minimum");
        check("main operation failure has visible sanitized report", UiVerification.FullyVisible((Button)window.FindName("FeedbackCopyErrorButton"), window.RenderRoot) && model.FeedbackErrorReport.Contains("HTTP 502") && !model.FeedbackErrorReport.Contains("account-d96f4cbfc0@example.invalid"));
        check("copy verification sends no additional model requests", runtime.Requests.Count == requests);
    }
}
