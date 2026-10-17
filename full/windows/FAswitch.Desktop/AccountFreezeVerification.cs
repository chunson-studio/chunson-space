using System.IO;
using System.Windows.Controls;
using FAswitch.Accounts;
using FAswitch.Core;

namespace FAswitch.Desktop;

internal static class AccountFreezeVerification
{
    public static async Task RunAsync(MainWindow window, string directory, Action<string, bool> check, Action<string> capture)
    {
        var workspace = BackgroundAccountTestsVerification.Fixture(directory);
        var model = new ShellViewModel(workspace); window.DataContext = model;
        var services = workspace.Accounts!; var runtime = services.VisualRuntimeFixture!;
        var auth = File.ReadAllBytes(Path.Combine(services.Repository.AccountPath("A"), "auth.json"));
        var environment = workspace.Profiles.ReadEnvironment("A");
        model.Selected = model.FilteredItems.Single(item => item.Name == "A");
        model.OpenVisualTestsCommand.Execute(null); var oldPanel = model.VisualTests!; oldPanel.CloseCommand.Execute(null);
        model.OpenCialloCommand.Execute(null); var oldCiallo = model.Ciallo!; oldCiallo.CloseCommand.Execute(null);
        check("existing accounts are testable until explicitly frozen", !workspace.Profiles.AreTestsFrozen("A") && oldPanel.StartCommand.CanExecute(null));
        model.ToggleTestFreezeCommand.Execute(null);
        check("freeze persists across repository reload and shows in account list", new FileProfileStore(workspace.Root).AreTestsFrozen("A") && model.Selected!.TestsFrozen && model.Selected.SavedStateLabel == "已冻结" && model.TestFreezeLabel == "解冻账号");
        check("freeze leaves credentials and environment unchanged", File.ReadAllBytes(Path.Combine(services.Repository.AccountPath("A"), "auth.json")).SequenceEqual(auth) && workspace.Profiles.ReadEnvironment("A") == environment);
        oldPanel.StartCommand.Execute(null); oldCiallo.StartCommand.Execute(null);
        check("retained visual and Ciallo panels cannot start frozen accounts", !oldPanel.StartCommand.CanExecute(null) && !oldCiallo.StartCommand.CanExecute(null) && runtime.Requests.Count == 0 && services.OfflineTransport!.Requests.Count == 0);
        model.OpenDetailsCommand.Execute(null); window.UpdateLayout();
        check("freeze control is visible in account management", UiVerification.FullyVisible((Button)window.FindName("AccountTestFreezeButton"), window.RenderRoot));
        capture("account-frozen-minimum"); model.CloseDetailsCommand.Execute(null);
        async Task Rejected(string label, Func<Task> action)
        {
            var refused = false;
            try { await action(); }
            catch (AccountException error) when (error.Code == AccountError.Busy) { refused = true; }
            check(label, refused && services.OfflineTransport!.Requests.Count == 0);
        }
        await Rejected("service rejects frozen visual test before runtime", () => services.VisualTests.RunAsync("A", "gpt-6-astra", VisualTestKind.Static, VisualTestCatalog.Variants[0].Id));
        await Rejected("service rejects frozen Ciallo before transport", () => services.Accounts.TestCialloAsync("A"));
        await Rejected("service rejects frozen connectivity test before transport", () => services.Accounts.TestAccountAsync("A"));
        await Rejected("service rejects frozen legacy fingerprint before transport", () => services.Accounts.TestFingerprintAsync("A", "gpt-6-astra", []));
        oldPanel.StartAllCommand.Execute(null); await oldPanel.CurrentOperation;
        check("batch skips frozen account even when panel predates freeze", runtime.Requests.Count == 1 && runtime.Requests.Single().Profile == "B" && oldPanel.BatchRows.Count == 1 && oldPanel.BatchRows[0].Profile == "B");
        model.Selected = model.FilteredItems.Single(item => item.Name == "B"); model.ToggleTestFreezeCommand.Execute(null);
        check("all frozen disables batch start without requests", !oldPanel.StartAllCommand.CanExecute(null) && runtime.Requests.Count == 1);
        model.Selected = model.FilteredItems.Single(item => item.Name == "A"); model.ToggleTestFreezeCommand.Execute(null);
        check("unfreeze enables a new manual test without automatically requesting", !workspace.Profiles.AreTestsFrozen("A") && runtime.Requests.Count == 1 && oldCiallo.StartCommand.CanExecute(null));
        runtime.ReplyFactory = (profile, _, _) => { workspace.Profiles.SetTestsFrozen(profile, true); return Task.FromResult(OfflineVisualTestRuntime.Svg); };
        var partial = await services.VisualTests.RunAsync("A", "gpt-6-astra", VisualTestKind.Revision, VisualTestCatalog.Variants[0].Id);
        check("freeze between stages prevents every later model request", !partial.Completed && partial.Steps.Count == 1 && partial.Message.Contains("冻结") && runtime.Requests.Count == 2);
        workspace.Profiles.SetTestsFrozen("A", false); runtime.ReplyFactory = null;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new AccountTestQueue(); var predecessor = queue.RunAsync("A", () => gate.Task.ContinueWith(_ => true), CancellationToken.None);
        var queued = new VisualTestPanelViewModel(services, "A", VisualTestKind.Static, _ => {}, () => {}, ["A"], queue);
        queued.StartCommand.Execute(null); workspace.Profiles.SetTestsFrozen("A", true); gate.SetResult();
        await Task.WhenAll(predecessor, queued.CurrentOperation);
        check("queued test rechecks freeze before its first request", runtime.Requests.Count == 2 && queued.Result?.Message.Contains("冻结") == true);
        var policy = Path.Combine(services.Repository.AccountPath("A"), "test-settings.json"); File.WriteAllText(policy, "{\"frozen\":\"invalid\"}");
        var invalidRejected = false;
        try { await services.Accounts.TestCialloAsync("A"); } catch (StorageException) { invalidRejected = true; }
        check("unreadable freeze flag never becomes permission to test", invalidRejected && services.OfflineTransport!.Requests.Count == 0);
        workspace.Profiles.SetTestsFrozen("A", true);
        check("test checks preserve account credentials", File.ReadAllBytes(Path.Combine(services.Repository.AccountPath("A"), "auth.json")).SequenceEqual(auth));
        await services.DisposeAsync();
    }
}
