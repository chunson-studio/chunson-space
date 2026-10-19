using System.IO;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.NodeTesting;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

internal static class ReviewRecoveryVerification
{
    public static async Task RunAsync(string root, Action<string, bool> check)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var workspace = DesktopWorkspace.CreateManagedFixture(root);
        var services = workspace.Accounts!;
        var store = services.Repository.Profiles;
        store.CreateBoundAccount("Running", new("http://127.0.0.1:7890", "America/New_York", "en-US"));
        store.SaveAccount("Running", new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "review-fixture", ["access_token"] = "fixture-only" } }, overwrite: true);
        store.CreateBoundAccount("MissingLogin", new("http://127.0.0.1:7890", "America/New_York", "en-US"));
        var model = new ShellViewModel(workspace);
        var runtime = workspace.OfflineRuntime!;
        await workspace.ProxyServices.NodeTesting!.ProbeScoredExitAsync("http://127.0.0.1:7890", default);
        model.Selected = model.FilteredItems.Single(item => item.Name == "Running");
        runtime.LaunchStatus = LaunchStatus.Pending;
        await model.SwitchSelectedAsync(true);
        check("recovery fixture reaches real presentation pending branch", model.SelectedRuntimeState == "启动待确认");
        model.ApplyOwnedState(new(true, true, false, "fixture still pending", [], LaunchPending: true));
        check("alive process without completed handshake keeps pending status", model.SelectedRuntimeState == "启动待确认");
        runtime.HasPendingLaunch = false; runtime.DesktopAlive = true;
        model.ApplyOwnedState(new(true, true, false, "fixture late confirmation", []));
        check("late confirmed startup clears pending label and banner and enables owned close", model.SelectedRuntimeState == "运行中 · 账号待确认" && model.FeedbackTitle == "分身已启动" && model.CloseManagedCommand.CanExecute(null));
        model.ApplyOwnedState(new(false, false, false, "fixture ownership unavailable", []));
        check("uncertain ownership observation does not claim confirmed desktop stopped", model.SelectedRuntimeState == "运行中 · 账号待确认");

        runtime.LaunchStatus = LaunchStatus.Confirmed;
        await model.SwitchSelectedAsync(true);
        var stops = runtime.StopCalls;
        model.Selected = model.FilteredItems.Single(item => item.Name == "MissingLogin");
        await model.SwitchSelectedAsync(true);
        check("missing login preflight preserves actual old clone and close button", model.NeedsLogin && runtime.StopCalls == stops && runtime.DesktopAlive
            && model.IdentityState.Running?.Name == "Running" && model.CloseManagedCommand.CanExecute(null) && model.SelectedRuntimeState == "其他账号运行中");
        model.Selected = model.FilteredItems.Single(item => item.Name == "Running");
        services.OfflineTransport!.FailRequests = true;
        await model.LaunchCurrentAsync();
        check("failed egress preflight retains prior running identity", runtime.StopCalls == stops && runtime.DesktopAlive
            && model.IdentityState.Running?.Name == "Running" && model.CloseManagedCommand.CanExecute(null));
        services.OfflineTransport.FailRequests = false;
        model.CloseManagedCommand.Execute(null); await model.CurrentOperation;
        check("explicit close still clears actual runtime after preflight failure", !runtime.DesktopAlive && model.SelectedRuntimeState == "已退出");

        var source = workspace.ProxyServices.FlClashRoot;
        Directory.CreateDirectory(Path.Combine(source, "profiles"));
        var yaml = Path.Combine(source, "profiles", "Review.yaml");
        File.WriteAllText(yaml, "proxies:\n - {name: review, type: http, server: fixture.invalid, port: 443}\n");
        var node = new NodeCatalogImporter().Read(yaml, source).Nodes.Single();
        var quality = new IpQualityReport(true, "192.0.2.11", "US", "", "America/New_York", "", "", null, null, null, null, null,
            DateTimeOffset.UtcNow, "fixture", "fixture", new(5, "fixture", true));
        var routes = services.Routes!;
        async Task<string> Allocate()
        {
            var proxy = await routes.BindAsync("Review", node, quality, default, reuseExisting: false);
            await routes.EnsureProxyAsync(proxy, default);
            return proxy;
        }
        var unused = await Allocate();
        var browser = services.CreateAuthorizationBrowser(unused, true);
        await services.ReleaseAuthorizationBrowserAsync(browser);
        check("cancelled unbound authorization releases owned route binding", await routes.AssignedIpAsync(unused) == "");
        var shared = await Allocate();
        store.CreateBoundAccount("Shared", new(shared, "America/New_York", "en-US"));
        browser = services.CreateAuthorizationBrowser(shared, true);
        await services.ReleaseAuthorizationBrowserAsync(browser);
        check("authorization cleanup retains route referenced by saved account", await routes.AssignedIpAsync(shared) == quality.Ip && services.Repository.Proxy("Shared") == shared);
        var uncertain = await Allocate();
        browser = services.CreateAuthorizationBrowser(uncertain, true);
        services.OfflineNewAccountBrowser!.CleanupSucceeds = false;
        var cleanup = await services.ReleaseAuthorizationBrowserAsync(browser);
        check("unconfirmed browser exit keeps route alive for safe retry", !cleanup.Success && await routes.AssignedIpAsync(uncertain) == quality.Ip);
        services.OfflineNewAccountBrowser.CleanupSucceeds = true;
        await services.ReleaseAuthorizationBrowserAsync(browser);
        check("confirmed cleanup retry releases unreferenced route", await routes.AssignedIpAsync(uncertain) == "");
        var cancelled = await Allocate();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await model.LoginNewAccountAsync(new(cancelled, "offline allocated", true), cancellation.Token);
        check("cancellation before browser construction still releases allocated route", await routes.AssignedIpAsync(cancelled) == "");
        var boundaryRoute = "";
        var authorizationCalls = 0;
        AccountOnboardingViewModel? onboarding = null;
        using (onboarding = new(_ => throw new InvalidOperationException("dedicated fixture only"),
            async (selection, prepareAccount, token) => { authorizationCalls++; await model.LoginNewAccountAsync(selection, token, prepareAccount); },
            _ => { }, () => { }, createDedicated: async (_, _) =>
            {
                boundaryRoute = await Allocate();
                onboarding!.CloseCommand.Execute(null);
                return new(boundaryRoute, "cancelled allocation", true);
            }))
        {
            var choice = new NodeSelectionItem(node, () => { });
            choice.Apply(new(node.Id, node.Name, node.Type, true, quality, "fixture"));
            onboarding.UseDedicatedIp = true;
            onboarding.SelectedIp = choice;
            onboarding.AuthorizeCommand.Execute(null);
            await onboarding.CurrentOperation;
            check("onboarding cancellation at allocation boundary transfers route to cleanup", authorizationCalls == 1
                && boundaryRoute.Length > 0 && await routes.AssignedIpAsync(boundaryRoute) == "" && !onboarding.IsStarting);
        }
        var main = "http://127.0.0.1:7890";
        await services.ReleaseUnusedAuthorizationRouteAsync(main);
        check("authorization cleanup never removes saved account main proxy", services.Repository.Proxy("Running") == main);
        await services.DisposeAsync();
    }
}
