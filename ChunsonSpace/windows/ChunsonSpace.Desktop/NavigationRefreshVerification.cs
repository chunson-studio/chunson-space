using System.IO;
using System.Reflection;
using System.Text.Json;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

internal static class NavigationRefreshVerification
{
    private static SavedResultRow Row(string id, string profile) => new()
    {
        Id = id, Profile = profile, Model = "synthetic", Started = DateTimeOffset.UtcNow,
        TaskType = "Ciallo", Stage = "fixture", Version = "fixture", State = "已完成", Prompt = "fixture", Text = "fixture"
    };
    private static async Task Settle(ResultsGalleryViewModel gallery)
    {
        for (int i = 0; i < 10; i++)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var task = gallery.CurrentLoad; await task.WaitAsync(TimeSpan.FromSeconds(15));
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (task == gallery.CurrentLoad && !gallery.IsLoading) return;
        }
        throw new InvalidOperationException("History did not settle");
    }
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        void Check(string name, bool ok) { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); }
        // Deliberately delayed synthetic reader: checks correctness, not timing claims.
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var next = new SavedResultsSnapshot([Row("old-A", "A"), Row("old-B", "B")], ["A", "B"]);
        var gallery = new ResultsGalleryViewModel(() =>
        {
            var captured = next;
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Fixture release timed out");
            }
            return captured;
        }, _ => { });
        gallery.Enter(); await started.Task;
        for (int i = 0; i < 10; i++) gallery.Enter();
        Check("rapid Enter coalesces pending history", calls == 1);
        gallery.SetProfileScope("A"); gallery.SetProfileScope("B");
        gallery.DiscardCachedResults(); gallery.Leave();
        next = new([Row("new-A", "A"), Row("new-B", "B")], ["A", "B"]);
        release.Set(); await Settle(gallery);
        Check("invalidated snapshot never returns after clear; hidden page does not reload", calls == 1 && !gallery.Items.Cast<object>().Any());
        gallery.Enter(); await Settle(gallery);
        Check("latest selection wins after loading", calls == 2 && gallery.Items.Cast<SavedResultRow>().Select(r => r.Id).SequenceEqual(["new-B"]));
        gallery.Leave(); gallery.Enter(); await Settle(gallery);
        Check("unchanged revisit reuses existing rows", calls == 2);
        next = new([Row("fresh-B", "B")], ["B"]);
        gallery.RefreshCommand.Execute(null); await Settle(gallery);
        Check("manual refresh reads new local data", calls == 3 && gallery.Items.Cast<SavedResultRow>().Single().Id == "fresh-B");

        var failures = 0;
        var retry = new ResultsGalleryViewModel(() => { if (++failures == 1) throw new IOException("synthetic"); return new([], []); }, _ => { });
        retry.Enter(); await Settle(retry);
        Check("failed load does not loop", failures == 1 && !retry.IsLoading);
        retry.Leave(); retry.Enter(); await Settle(retry);
        Check("failed load can retry on next entry", failures == 2);

        var fixture = Path.Combine(directory, "synthetic");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var workspace = DesktopWorkspace.CreateManagedFixture(fixture);
        foreach (var name in new[] { "A", "B", "C" })
        {
            workspace.Profiles.CreateBoundAccount(name, new("", "Asia/Shanghai", "en-US", FollowSystemProxy: true));
            if (name != "C") File.WriteAllText(Path.Combine(workspace.Profiles.ProfileDirectory(new(IdentityKind.Account, name)), "auth.json"), "{}");
        }
        var model = new ShellViewModel(workspace);
        await Settle(model.ResultsGallery);
        Check("eligible account snapshot retains credentials rule", model.ModelTestProfiles.SequenceEqual(["A", "B"]));
        model.Selected = model.FilteredItems.Single(i => i.Name == "B"); model.InterfaceMode = "单账号";
        Check("single account snapshot follows displayed selection", model.ModelTestProfiles.SequenceEqual(["B"]));
        Check("health command enabled for eligible cached account", model.TestAllAccountsCommand.CanExecute(null));
        model.ToggleTestFreezeCommand.Execute(model.Selected);
        Check("freeze refresh disables health command without a request", !model.TestAllAccountsCommand.CanExecute(null));
        model.ToggleTestFreezeCommand.Execute(model.Selected);
        Check("unfreeze refresh enables health command without a request", model.TestAllAccountsCommand.CanExecute(null));
        model.Selected = model.FilteredItems.Single(i => i.Name == "A");
        Check("rapid selection uses latest account without loading credentials", model.ModelTestProfiles.SequenceEqual(["A"]));
        model.InterfaceMode = "多账号";
        File.WriteAllText(Path.Combine(workspace.Profiles.ProfileDirectory(new(IdentityKind.Account, "C")), "auth.json"), "{}");
        model.RefreshCommand.Execute(null);
        Check("existing profile refresh updates eligibility snapshot", model.ModelTestProfiles.SequenceEqual(["A", "B", "C"]));
        var queue = (AccountTestQueue)typeof(ShellViewModel).GetField("accountTests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        void Seed(string id)
        {
            var root = Path.Combine(workspace.Accounts!.Repository.AccountPath("A"), "ciallo-tests"); Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, id + ".json"), JsonSerializer.Serialize(new CialloHistoryEntry(DateTimeOffset.UtcNow,
                new([new("gpt-6.1-sol", "fixture", "high", "completed", "synthetic", TimeSpan.Zero)], false), false)));
        }
        model.Navigate("Settings");
        await queue.RunAsync("A", () => { Seed("one"); return Task.FromResult(true); }, default);
        Check("completed result on hidden page only invalidates snapshot", !model.ResultsGallery.IsLoading && !model.ResultsGallery.Items.Cast<object>().Any());
        model.Navigate("Results"); await Settle(model.ResultsGallery);
        Check("next entry shows newly completed result", model.ResultsGallery.Items.Cast<object>().Count() == 1);
        await queue.RunAsync("A", () => { Seed("two"); return Task.FromResult(true); }, default);
        await Settle(model.ResultsGallery);
        Check("visible page refreshes after new result", model.ResultsGallery.Items.Cast<object>().Count() == 2);
        model.InterfaceMode = "单账号";
        model.Selected = model.FilteredItems.Single(i => i.Name == "B");
        Check("single mode selection updates existing result scope immediately", !model.ResultsGallery.Items.Cast<object>().Any());
        model.Selected = model.FilteredItems.Single(i => i.Name == "A");
        Check("returning selection reuses correctly scoped results", model.ResultsGallery.Items.Cast<object>().Count() == 2);
        model.InterfaceMode = "多账号";
        model.ConfirmClearTestHistory = () => true;
        model.ClearAllTestHistoryCommand.Execute(null); await model.CurrentOperation; await Settle(model.ResultsGallery);
        Check("real clear command on synthetic files clears all bound rows", !model.ResultsGallery.Items.Cast<object>().Any()
            && model.FeedbackTitle == "全部测试历史已清除"
            && File.Exists(Path.Combine(workspace.Profiles.ProfileDirectory(new(IdentityKind.Account, "A")), "auth.json")));
        Check("fixture never launches or requests account/network services", workspace.Accounts!.OfflineTransport!.RequestCount == 0
            && workspace.OfflineRuntime!.StopCalls == 0 && workspace.OfflineRuntime.LastLaunchRequest is null);
        model.DisposeUpdates();
        File.WriteAllText(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(new { passed = true, checks,
            delayedReaderIsSynthetic = true, actualAccountRequests = 0, actualUserProgramOperations = 0 }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

