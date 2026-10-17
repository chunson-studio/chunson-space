using System.IO;
using System.Text.Json.Nodes;
using FAswitch.Accounts;
using FAswitch.Application;
using FAswitch.Core;
using FAswitch.Windows;

namespace FAswitch.Desktop;

internal static class LocalEnvironmentVerification
{
    public static async Task RunAsync(string directory, Action<string, bool> check)
    {
        var route = "http://127.0.0.1:7890";
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
        var exit = new EgressResult(true, "192.0.2.24", "Japan", "JP", "Tokyo", "Asia/Tokyo", "fixture");
        int probes = 0; string? probedRoute = null;
        var admission = new FixtureAdmission();
        bool offline = false, changeRouteOnProbe = false;
        TaskCompletionSource<EgressResult>? pendingProbe = null;
        Func<TimeZoneInfo> zoneReader = () => zone;
        Func<string> countryReader = () => "JP";
        var local = new LocalEnvironmentService(_ => Task.FromResult(new LoginProxySelection(route, "fixture")),
            (proxy, _) => { probes++; probedRoute = proxy; if (offline) throw new AccountException(AccountError.ExitUnavailable, "fixture"); if (changeRouteOnProbe) route = "http://127.0.0.1:7893"; return pendingProbe?.Task ?? Task.FromResult(exit); }, admission, () => zoneReader(), () => countryReader());
        var first = local.Read(new(IdentityKind.Account, "East"));
        var second = local.Read(new(IdentityKind.Account, "West"));
        check("both Codex identities resolve the same local default proxy and Windows timezone without probing", first == second && first.Proxy == route && first.Timezone == "Asia/Tokyo" && !local.AllowSystemTimezoneChange && probes == 0);
        var report = await local.CheckAsync();
        check("startup checks actual default proxy exit once without admission or score requests", report.Verified && report.Matches && report.Ip == exit.Ip && probedRoute == route && probes == 1 && admission.Calls == 0);
        countryReader = () => "CN";
        report = await local.CheckAsync();
        check("matching timezone cannot conceal different Windows country", report.Verified && !report.Matches && report.SystemCountryCode == "CN" && report.CountryCode == "JP");
        countryReader = () => "JP";
        exit = exit with { Timezone = "America/New_York" };
        report = await local.CheckAsync();
        check("startup reports timezone mismatch instead of claiming configured equals verified", report.Verified && !report.Matches && report.SystemTimezone == "Asia/Tokyo" && report.ExitTimezone == "America/New_York");
        check("timezone comparison recognizes Windows mapping and rejects merely equal seasonal offsets", LocalEnvironmentService.Matches(zone, "Asia/Tokyo") && !LocalEnvironmentService.Matches(TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"), "Africa/Abidjan"));
        exit = exit with { Timezone = "" };
        check("missing exit timezone cannot confirm local environment", !(await local.CheckAsync()).Verified);
        offline = true;
        check("unavailable proxy produces an unconfirmed status without system changes", !(await local.CheckAsync()).Verified);
        offline = false; exit = exit with { Timezone = "Asia/Tokyo" };
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "faswitch-fixture.json"), "{\"fixture\":true}");
        var workspace = DesktopWorkspace.CreateManagedFixture(directory);
        workspace.LocalEnvironment = local;
        var model = new ShellViewModel(workspace);
        await model.RefreshLocalEnvironmentAsync();
        check("startup result is visible without selecting an account or blocking login", model.LocalEnvironmentStatus.Contains("192.0.2.24") && model.LocalEnvironmentStatus.Contains("已核对") && model.NewAccountLoginCommand.CanExecute(null));
        check("local evidence includes check time, selected proxy, both zones and explicit account boundary", model.LocalEnvironmentDetail.Contains("核对时间：") && model.LocalEnvironmentDetail.Contains(route)
            && model.LocalEnvironmentDetail.Contains("出口时区：Asia/Tokyo") && model.LocalEnvironmentDetail.Contains("Windows 时区：Asia/Tokyo") && model.LocalEnvironmentDetail.Contains("不代表账号授权")
            && model.LocalEnvironmentDetail.Contains("出口国家/地区：JP") && model.LocalEnvironmentDetail.Contains("Windows 国家/地区：JP"));
        offline = true;
        model.RecheckLocalEnvironmentCommand.Execute(null); await model.LocalEnvironmentOperation;
        check("failed recheck cannot display past success as current evidence", model.LocalEnvironmentStatus.Contains("本次未确认") && !model.LocalEnvironmentStatus.Contains("已核对")
            && model.LocalEnvironmentDetail.Contains("仅历史，不代表当前出口") && model.LocalEnvironmentDetail.Contains("192.0.2.24") && model.LocalEnvironmentDetail.Contains("查询本机出口、地区与时区未完成"));
        offline = false;
        pendingProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeBackground = probes;
        model.RecheckLocalEnvironmentCommand.Execute(null);
        check("rechecking disables duplicate query and timezone writing but leaves navigation and login usable", model.LocalEnvironmentCheckLabel == "核对中…" && !model.RecheckLocalEnvironmentCommand.CanExecute(null)
            && !model.SyncLocalTimezoneCommand.CanExecute(null) && model.NavigateCommand.CanExecute("Settings") && model.NewAccountLoginCommand.CanExecute(null));
        model.RecheckLocalEnvironmentCommand.Execute(null); await model.RefreshLocalEnvironmentAsync();
        check("repeated clicks do not duplicate the running query or replace the operation task", probes == beforeBackground + 1 && !model.LocalEnvironmentOperation.IsCompleted);
        model.NavigateCommand.Execute("Settings");
        pendingProbe.SetResult(exit); await model.LocalEnvironmentOperation; pendingProbe = null;
        check("background completion restores controls and updates evidence without moving the page", model.LocalEnvironmentCheckLabel == "重新核对" && model.RecheckLocalEnvironmentCommand.CanExecute(null)
            && model.IsSettingsPage && model.LocalEnvironmentStatus.Contains("已核对"));
        model.NavigateCommand.Execute("Accounts");
        exit = exit with { Timezone = "America/New_York" };
        await model.RefreshLocalEnvironmentAsync();
        check("homepage distinguishes mismatched Windows and exit timezone", model.LocalEnvironmentStatus.Contains("时区不一致") && model.LocalEnvironmentDetail.Contains("America/New_York"));
        var region = workspace.Accounts!.OfflineRegion!;
        workspace.Accounts.OfflineNewAccountBrowser!.SystemProxy = route;
        region.State = new(true, 45, zone.Id);
        zoneReader = () => TimeZoneInfo.FindSystemTimeZoneById(region.State.WindowsTimezone);
        countryReader = () => region.State.GeoId switch { 244 => "US", 122 => "JP", 45 => "CN", _ => "" };
        var requests = workspace.Accounts.OfflineTransport!.RequestCount;
        check("manual homepage timezone command needs no selected account and startup has made no writes", model.Selected is null && model.SyncLocalTimezoneCommand.CanExecute(null) && region.TimezoneWrites == 0 && region.RegionWrites == 0);
        exit = exit with { Ip = "192.0.2.25", CountryCode = "US", Timezone = "America/Chicago" };
        var beforeSync = probes;
        model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
        check("manual homepage sync rereads switched exit and sets both country and timezone", !model.IsError && region.State == new RegionState(true, 244, "Central Standard Time")
            && region.TimezoneWrites == 1 && region.RegionWrites == 1 && probedRoute == route && probes == beforeSync + 1 && model.LocalEnvironmentStatus.Contains("192.0.2.25") && model.LocalEnvironmentStatus.Contains("已核对") && model.FeedbackTitle == "地区与时区同步完成");
        model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
        check("matching manual sync is idempotent and sends no account request", !model.IsError && region.TimezoneWrites == 1 && region.RegionWrites == 1 && workspace.Accounts.OfflineTransport.RequestCount == requests);
        offline = true; model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
        check("failed fresh exit cannot reuse earlier zone or write Windows", model.IsError && region.TimezoneWrites == 1 && model.LocalEnvironmentStatus.Contains("未确认"));
        check("local timezone failure guidance never directs users to edit account egress", model.FeedbackNextAction.Contains("FlClash 当前网络") && !model.FeedbackNextAction.Contains("高级管理") && !model.FeedbackNextAction.Contains("账号出口可"));
        offline = false; exit = exit with { Timezone = "" }; model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
        check("unknown timezone blocks manual sync without country fallback", model.IsError && region.TimezoneWrites == 1 && region.RegionWrites == 1);
        exit = exit with { Timezone = "America/New_York", CountryCode = "" };
        model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
        check("unknown country blocks both settings before any new write", model.IsError && region.TimezoneWrites == 1 && region.RegionWrites == 1);
        exit = exit with { CountryCode = "US" };
        exit = exit with { Timezone = "America/New_York" }; changeRouteOnProbe = true;
        model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
        check("proxy changes during manual check reject system writes", model.IsError && region.TimezoneWrites == 1);
        changeRouteOnProbe = false; route = "http://127.0.0.1:7890"; region.State = new(true, 45, zone.Id);
        var target = new IdentityRef(IdentityKind.Account, "BrowserEast");
        var browserEnvironment = new EnvironmentSettings("http://127.0.0.1:7999", "America/New_York", "en-US");
        workspace.Profiles.CreateBoundAccount(target.Name, browserEnvironment);
        workspace.Profiles.SaveAccount(target.Name, new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "fixture", ["access_token"] = "fixture-only" } }, overwrite: true);
        model.RefreshCommand.Execute(null); model.Selected = model.FilteredItems.FirstOrDefault(item => item.Name == target.Name);
        exit = exit with { Timezone = "America/Chicago" };
        model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
        check("selected independent browser keeps its own proxy and timezone after local sync", !model.IsError && probedRoute == route && workspace.Profiles.ReadEnvironment(target.Name) == browserEnvironment);
        var zoneWrites = region.TimezoneWrites; var countryWrites = region.RegionWrites;
        region.State = region.State with { GeoId = 45 };
        model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
        check("country-only mismatch does not rewrite matching timezone", !model.IsError && region.State.GeoId == 244 && region.RegionWrites == countryWrites + 1 && region.TimezoneWrites == zoneWrites);
        region.State = new(true, 45, "Tokyo Standard Time"); region.AllowRegion = false;
        model.SyncLocalTimezoneCommand.Execute(null); await model.CurrentOperation;
        check("country failure preserves applied timezone and remains visibly incomplete", model.IsError && region.State == new RegionState(true, 45, "Central Standard Time")
            && model.FeedbackTitle == "地区与时区同步未完成" && !model.LocalEnvironmentStatus.Contains("已核对") && region.TimezoneWrites == zoneWrites + 1);
        region.AllowRegion = true;
        region.State = new(true, 45, zone.Id);
        var switching = new SwitchService(workspace.SwitchStorage, workspace.Runtime!, environments: local, egressProbe: local);
        admission.Evidence = new(true, "192.0.2.24", "America/New_York", "fixture");
        var request = new SwitchRequest(target, true, true, true, browserEnvironment, workspace.ActiveHome);
        var result = await switching.SwitchAsync(request);
        check("mismatched default exit stops clone before runtime stop or credential copy", !result.Success && workspace.OfflineRuntime!.StopCalls == 0 && !File.Exists(Path.Combine(workspace.ActiveHome, "auth.json")));
        admission.Evidence = admission.Evidence with { Timezone = "Asia/Tokyo" };
        result = await switching.SwitchAsync(request);
        var launched = workspace.OfflineRuntime!.LastLaunchRequest;
        check("clone receives local proxy and timezone while account browser settings remain intact", result.Success && launched?.Environment?["HTTP_PROXY"] == route && launched.Environment["TZ"] == "Asia/Tokyo" && workspace.Profiles.ReadEnvironment(target.Name) == browserEnvironment);
        check("successful local clone launch never enables a Windows timezone change", !workspace.OfflineRuntime.LastTimezoneEnabled);
        route = "http://127.0.0.1:7892";
        var beforeAdmission = admission.Calls;
        check("changed default proxy rejects stale launch input without another probe", !(await local.ProbeAsync(target, first.Proxy, default)).Success && admission.Calls == beforeAdmission);
        await workspace.Accounts!.DisposeAsync();
    }
    private sealed class FixtureAdmission : ILaunchEgressProbe
    {
        public int Calls { get; private set; }
        public LaunchEgressEvidence Evidence { get; set; } = new(true, "192.0.2.24", "Asia/Tokyo", "fixture");
        public Task<LaunchEgressEvidence> ProbeAsync(IdentityRef identity, string proxy, CancellationToken token) { Calls++; return Task.FromResult(Evidence); }
    }
}
