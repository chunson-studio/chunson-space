using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using FAswitch.Accounts;
using FAswitch.Core;
using FAswitch.NodeTesting;

namespace FAswitch.Desktop;

internal static class AccountIpChangeVerification
{
    public static async Task RunAsync(MainWindow window, string root, Action<string, bool> check, Action<string> capture)
    {
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "faswitch-fixture.json"), "{\"fixture\":true}");
        var workspace = DesktopWorkspace.CreateManagedFixture(root);
        Directory.CreateDirectory(workspace.ProxyServices.FlClashRoot);
        File.WriteAllText(Path.Combine(workspace.ProxyServices.FlClashRoot, "config.yaml"), "proxies:\n  - {name: fixture-one, type: http, server: one.fixture.invalid, port: 443}\n  - {name: fixture-two, type: http, server: two.fixture.invalid, port: 443}\n  - {name: fixture-low, type: http, server: low.fixture.invalid, port: 443}\n");
        var model = new ShellViewModel(workspace); window.DataContext = model;
        var pool = model.ProxyPanel.NodeTesting; pool.LoadCommand.Execute(null); await pool.CurrentOperation;
        var high = new IpQualityReport(true, "192.0.2.11", "US", "", "America/New_York", "", "", null, null, null, null, null,
            DateTimeOffset.UtcNow, "fixture", "fixture", new(5, "fixture", true));
        var other = high with { Ip = "192.0.2.22", Timezone = "Europe/London", CountryCode = "GB" };
        for (var i = 0; i < pool.Items.Count; i++)
        {
            var item = pool.Items[i]; var q = i == 0 ? high : i == 1 ? other : high with { Ip = "192.0.2.33", Score = new(80, "fixture", true) };
            item.Apply(new(item.Node.Id, item.Name, item.Type, true, q, "fixture"));
        }
        var first = pool.LoginChoices.Single(item => item.ExitIp == high.Ip); var second = pool.LoginChoices.Single(item => item.ExitIp == other.Ip);
        var routes = workspace.Accounts!.Routes!;
        var initial = await routes.BindAsync(first.SubscriptionId, first.Node, high, default, reuseExisting: false);
        workspace.Profiles.CreateBoundAccount("可换IP账号", new(initial, high.Timezone, "en-US"));
        workspace.Profiles.SaveAccount("可换IP账号", new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "fixture-account", ["access_token"] = "fixture-only" } }, overwrite: true);
        var authBefore = workspace.Accounts.Repository.ReadAuth("可换IP账号").ToJsonString();
        model.RefreshCommand.Execute(null); model.Selected = model.FilteredItems.Single(); model.EditSelectedCommand.Execute(null);
        var editor = model.Editor!;
        capture("account-change-ip-ready-minimum");
        check("existing independent account exposes IP picker and random replacement at minimum size", editor.ShowIpPicker
            && UiVerification.FullyVisible((ComboBox)window.FindName("AccountChangeIpPicker"), window.RenderRoot)
            && UiVerification.FullyVisible((Button)window.FindName("AccountRandomIpButton"), window.RenderRoot));
        editor.SelectedIp = second;
        check("selecting another IP previews matching timezone without mutating route", editor.NetworkModeDescription.Contains(other.Ip) && editor.NetworkModeDescription.Contains(other.Timezone)
            && workspace.Profiles.ReadEnvironment("可换IP账号").Proxy == initial);
        editor.CancelCommand.Execute(null);
        check("canceling IP selection leaves existing route", model.Editor is null && workspace.Profiles.ReadEnvironment("可换IP账号").Proxy == initial);
        model.EditSelectedCommand.Execute(null); editor = model.Editor!;
        editor.RandomIpCommand.Execute(null); await editor.CurrentOperation;
        check("random replacement excludes current IP and low scores without network calls", editor.SelectedIp == second && pool.LoginChoices.Count == 2
            && workspace.Accounts.OfflineTransport!.RequestCount == 0 && workspace.Profiles.ReadEnvironment("可换IP账号").Proxy == initial);
        capture("account-change-ip-selected-minimum");
        model.DarkTheme = true; capture("account-change-ip-selected-dark-minimum"); model.DarkTheme = false;
        editor.SaveCommand.Execute(null); await editor.CurrentOperation;
        var next = workspace.Profiles.ReadEnvironment("可换IP账号");
        check("editor save replaces existing independent IP instead of short-circuiting", model.Editor is null && !model.IsError && next.Proxy != initial
            && next.Timezone == other.Timezone && await routes.AssignedIpAsync(next.Proxy) == other.Ip && await routes.AssignedIpAsync(initial) == "");
        model.EditSelectedCommand.Execute(null); editor = model.Editor!; editor.SelectedIp = second; editor.SaveCommand.Execute(null); await editor.CurrentOperation;
        check("selecting current IP keeps its existing port", model.Editor is null && workspace.Profiles.ReadEnvironment("可换IP账号") == next);
        model.EditSelectedCommand.Execute(null); editor = model.Editor!; editor.SelectedIp = first;
        first.Apply(new(first.Node.Id, first.Name, first.Type, false, null, "fixture invalidated"));
        editor.SaveCommand.Execute(null); await editor.CurrentOperation;
        check("invalidated cached choice cannot replace a saved route", model.Editor == editor && editor.HasError && workspace.Profiles.ReadEnvironment("可换IP账号") == next);
        capture("account-change-ip-invalid-minimum");
        editor.RandomIpCommand.Execute(null); await editor.CurrentOperation;
        check("random swap with no alternative reports an error and retains route", editor.HasError && editor.Error.Contains("没有其他") && workspace.Profiles.ReadEnvironment("可换IP账号") == next);
        editor.FollowSystemProxy = true;
        check("follow mode clears pending independent selection and hides replacement controls", editor.SelectedIp is null && !editor.ShowIpPicker);
        editor.CancelCommand.Execute(null);
        check("IP editing keeps account authorization and sends no score or account requests", workspace.Accounts.Repository.ReadAuth("可换IP账号").ToJsonString() == authBefore
            && workspace.Accounts.OfflineTransport!.RequestCount == 0 && workspace.Accounts.OfflineRegion!.TimezoneWrites == 0);
        await workspace.Accounts.DisposeAsync();
    }
}
