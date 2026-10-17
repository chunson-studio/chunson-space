using System.IO;
using System.Text.Json.Nodes;
using FAswitch.Core;

namespace FAswitch.Desktop;

internal static class LocalAccountImportVerification
{
    public static async Task RunAsync(string root, Action<string, bool> check)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "faswitch-fixture.json"), "{}");
        var original = Path.Combine(root, "original"); Directory.CreateDirectory(original);
        var originalAuth = Path.Combine(original, "auth.json");
        var auth = new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "local-fixture-id", ["access_token"] = "fixture-local-token" } };
        File.WriteAllText(originalAuth, auth.ToJsonString());
        var bytes = File.ReadAllBytes(originalAuth);
        var workspace = DesktopWorkspace.CreateManagedFixture(root, new(root, original, null, null));
        var model = new ShellViewModel(workspace);
        model.Search = "unmatched search"; model.AccountFilter = "待登录";
        var requests = workspace.Accounts!.OfflineTransport!.RequestCount;
        check("managed local import uses original client home while legacy save-current stays disabled", model.ImportLocalAccountCommand.CanExecute(null) && !model.SaveCurrentAccountCommand.CanExecute(null) && workspace.OriginalHome == original && workspace.ActiveHome != original);
        model.ImportLocalAccountCommand.Execute(null); await model.CurrentOperation;
        check("homepage local import selects saved account and keeps main credentials", model.Selected is not null && workspace.Profiles.ListAccounts().Single().HasCredentials && model.ItemCount == 1 && model.Search == "" && model.AccountFilter == "全部账号" && bytes.SequenceEqual(File.ReadAllBytes(originalAuth)) && model.FeedbackTitle == "本机账号已导入");
        model.ImportLocalAccountCommand.Execute(null); await model.CurrentOperation;
        check("repeating homepage import updates without extra profile or browser and model request", model.ItemCount == 1 && model.FeedbackTitle == "本机账号授权已更新" && workspace.Accounts.OfflineTransport.RequestCount == requests && !model.IsLoggingIn && !model.IsOnboardingOpen && workspace.OfflineRuntime!.StopCalls == 0);
        File.Delete(originalAuth);
        model.ImportLocalAccountCommand.Execute(null); await model.CurrentOperation;
        check("missing original login gives visible failure and retains saved account", model.FeedbackTitle == "本机账号未导入" && model.ItemCount == 1 && model.ImportLocalAccountCommand.CanExecute(null));
    }
}
