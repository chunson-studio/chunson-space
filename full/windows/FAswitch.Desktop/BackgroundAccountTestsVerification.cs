using System.IO;
using System.Text.Json.Nodes;
using FAswitch.Accounts;
using FAswitch.Core;

namespace FAswitch.Desktop;

internal static class BackgroundAccountTestsVerification
{
    internal static DesktopWorkspace Fixture(string directory)
    {
        var root = Path.Combine(directory, "accounts"); var home = Path.Combine(directory, "home");
        Directory.CreateDirectory(root); Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(root, "faswitch-fixture.json"), "{\"fixture\":true}");
        var store = new FileProfileStore(root);
        foreach (var (name, port) in new[] { ("A", 7901), ("B", 7902) })
        {
            store.CreateBoundAccount(name, new($"http://127.0.0.1:{port}", "America/New_York", "en-US"));
            store.SaveAccount(name, new JsonObject { ["tokens"] = new JsonObject { ["access_token"] = "fixture-access-" + name, ["account_id"] = "fixture-id-" + name } }, overwrite: true);
        }
        return new(root, home, Path.Combine(directory, "desktop"), fixture: true);
    }
    internal static async Task Until(Func<bool> predicate)
    {
        for (var count = 0; count < 1000 && !predicate(); count++) await Task.Delay(2);
        if (!predicate()) throw new InvalidOperationException("Background account fixture did not reach the expected state.");
    }
    public static async Task RunAsync(string directory, Action<string, bool> check)
    {
        var workspace = Fixture(directory); var model = new ShellViewModel(workspace);
        var runtime = workspace.Accounts!.VisualRuntimeFixture!;
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = new Dictionary<string, int>(); int maximum = 0;
        runtime.ReplyFactory = async (profile, prompt, token) =>
        {
            active[profile] = active.GetValueOrDefault(profile) + 1; maximum = Math.Max(maximum, active[profile]);
            try { await (profile == "A" ? gateA : gateB).Task.WaitAsync(token); return prompt.StartsWith("Inspect") ? "fixture critique" : OfflineVisualTestRuntime.Svg; }
            finally { active[profile]--; }
        };
        workspace.Accounts.OfflineTransport!.ResponseFactory = async (request, token) =>
        {
            active["A"] = active.GetValueOrDefault("A") + 1; maximum = Math.Max(maximum, active["A"]);
            try { await Task.Yield(); token.ThrowIfCancellationRequested(); return "fixture ciallo"; }
            finally { active["A"]--; }
        };
        VisualTestPanelViewModel Open(string name, VisualTestKind kind)
        {
            model.Selected = model.FilteredItems.Single(item => item.Name == name);
            model.OpenModelTestCommand.Execute(null); model.ChooseVisualTestCommand.Execute(kind.ToString()); return model.VisualTests!;
        }
        var first = Open("A", VisualTestKind.Static); first.StartCommand.Execute(null);
        await Until(() => runtime.Requests.Count == 1); first.CloseCommand.Execute(null);
        model.Selected = model.FilteredItems.Single(item => item.Name == "B");
        check("account A background test leaves B browser quota and settings available", model.CanSelectIdentity && model.OpenBrowserCommand.CanExecute(null) && model.QuotaCommand.CanExecute(null) && model.EditSelectedCommand.CanExecute(null));
        check("global restore and rebuild stay blocked across account selection", !model.CreateBackupCommand.CanExecute(null) && !model.RestoreBackupCommand.CanExecute(null) && !model.RebuildCommand.CanExecute(null));
        model.EditSelectedCommand.Execute(null); var editor = model.Editor;
        check("B editor opens while A test is active", editor?.Original?.Name == "B" && first.IsBusy);
        model.CloseEditor();
        var second = Open("B", VisualTestKind.Static); second.StartCommand.Execute(null);
        await Until(() => runtime.Requests.Count == 2); second.CloseCommand.Execute(null);
        check("independently started account panels execute concurrently", active.GetValueOrDefault("A") == 1 && active.GetValueOrDefault("B") == 1 && first.IsBusy && second.IsBusy);
        var queued = Open("A", VisualTestKind.Revision); queued.StartCommand.Execute(null); await Task.Delay(10);
        check("another test for A queues without issuing a parallel request", queued.IsBusy && runtime.Requests.Count == 2 && maximum == 1);
        queued.CancelCommand.Execute(null); await queued.CurrentOperation;
        check("queued cancellation does not send a model request or cancel predecessor", queued.Result?.Cancelled == true && first.IsBusy && runtime.Requests.Count == 2);
        var cancelledTime = queued.ProgressSummary; await Task.Delay(1100);
        check("cancelled queue time is frozen while predecessor remains active", queued.ProgressSummary == cancelledTime && first.IsBusy && queued.CompletedReplies == 0);
        queued.StartCommand.Execute(null); queued.CloseCommand.Execute(null);
        model.OpenCialloCommand.Execute(null); var ciallo = model.Ciallo!; ciallo.SelectedProfile = "A";
        ciallo.StartCommand.Execute(null); ciallo.CloseCommand.Execute(null);
        check("Ciallo shares the same account queue as visual tests", ciallo.IsBusy && workspace.Accounts.OfflineTransport.Requests.Count == 0);
        var cialloTime = ciallo.ProgressSummary; await Task.Delay(1250);
        check("Ciallo queued background timer advances without issuing requests", ciallo.ProgressSummary != cialloTime && ciallo.ProgressSummary.Contains("已结束 0/") && workspace.Accounts.OfflineTransport.Requests.Count == 0);
        gateA.SetResult(); await Task.WhenAll(first.CurrentOperation, queued.CurrentOperation, ciallo.CurrentOperation);
        check("A sequences complete serially while B remains independently running", maximum == 1 && first.Result?.Completed == true && queued.Result?.Completed == true && !ciallo.IsBusy && second.IsBusy);
        cialloTime = ciallo.ProgressSummary; await Task.Delay(1100);
        check("Ciallo completion freezes elapsed time and records ended models", ciallo.ProgressSummary == cialloTime && ciallo.Rows.All(row => row.Finished));
        model.Selected = model.FilteredItems.Single(item => item.Name == "A"); model.EditSelectedCommand.Execute(null); var finalEditor = model.Editor;
        check("account editor becomes available when its own tests finish", model.Editor?.Original?.Name == "A");
        gateB.SetResult(); await second.CurrentOperation;
        check("other account completion preserves the open editor", ReferenceEquals(model.Editor, finalEditor) && model.Editor?.Original?.Name == "A"); model.CloseEditor();
        var recovered = new HashSet<object>();
        for (var i = 0; i < 5; i++) { model.OpenBackgroundModelCommand.Execute(null); var panel = (object?)model.Ciallo ?? model.VisualTests; if (panel is not null) recovered.Add(panel); }
        check("every independent background panel can be recovered without new requests", recovered.Contains(first) && recovered.Contains(second) && recovered.Contains(queued) && recovered.Contains(ciallo) && runtime.Requests.Count == 5);
        check("account testing state releases after all retained panels finish", !model.IsModelTestRunning && !model.IsBusy && model.RebuildCommand.CanExecute(null));
        model.Ciallo?.CloseCommand.Execute(null); model.VisualTests?.CloseCommand.Execute(null);
        model.Selected = model.FilteredItems.Single(item => item.Name == "B"); model.EditSelectedCommand.Execute(null); var unchangedEditor = model.Editor;
        gateA = new(TaskCreationOptions.RunContinuationsAsynchronously); first.StartCommand.Execute(null);
        await Until(() => runtime.Requests.Count == 6);
        check("starting A in the background leaves B's open configuration available", ReferenceEquals(model.Editor, unchangedEditor) && model.Editor?.Original?.Name == "B" && !model.IsBusy && model.EditSelectedCommand.CanExecute(null));
        gateA.SetResult(); await first.CurrentOperation;
        check("finishing A in the background preserves B's open configuration", ReferenceEquals(model.Editor, unchangedEditor) && model.Editor?.Original?.Name == "B" && !model.IsBusy);
        model.CloseEditor();
        model.Selected = model.FilteredItems.Single(item => item.Name == "A"); model.EditSelectedCommand.Execute(null); var protectedEditor = model.Editor!;
        var originalTimezone = workspace.Profiles.ListAccounts().Single(item => item.Name == "A").Environment.Timezone;
        protectedEditor.Timezone = "America/Los_Angeles"; gateA = new(TaskCreationOptions.RunContinuationsAsynchronously); first.StartCommand.Execute(null);
        await Until(() => runtime.Requests.Count == 7);
        model.Selected = model.FilteredItems.Single(item => item.Name == "B"); protectedEditor.SaveCommand.Execute(null); await protectedEditor.CurrentOperation;
        check("saving an already open A editor is blocked while A tests even after selecting B", protectedEditor.Error.Contains("测试") && ReferenceEquals(model.Editor, protectedEditor) && workspace.Profiles.ListAccounts().Single(item => item.Name == "A").Environment.Timezone == originalTimezone);
        model.CloseEditor(); model.EditSelectedCommand.Execute(null); var allowedEditor = model.Editor!; allowedEditor.SaveCommand.Execute(null); await allowedEditor.CurrentOperation;
        check("saving B remains available while A tests", !allowedEditor.HasError && !model.IsEditorOpen && first.IsBusy);
        gateA.SetResult(); await first.CurrentOperation;
        await workspace.Accounts.DisposeAsync();
    }
}
