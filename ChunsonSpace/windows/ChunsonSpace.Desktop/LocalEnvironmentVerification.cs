using System.IO;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Application;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

internal static partial class LocalEnvironmentVerification
{
    public static async Task RunTimezoneMatchAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("Timezone assertion failed: " + name);
        }
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        Check("Windows and IANA Pacific aliases match without comparing offsets",
            LocalEnvironmentService.Matches(pacific, "America/Los_Angeles") && LocalEnvironmentService.Matches(pacific, "US/Pacific")
            && LocalEnvironmentService.Matches(TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"), "US/Pacific"));
        var phoenix = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        Check("Phoenix and Los Angeles remain different despite equal summer offsets",
            pacific.GetUtcOffset(new DateTime(2026, 7, 1)) == phoenix.GetUtcOffset(new DateTime(2026, 7, 1))
            && pacific.GetUtcOffset(new DateTime(2026, 1, 1)) != phoenix.GetUtcOffset(new DateTime(2026, 1, 1))
            && !LocalEnvironmentService.Matches(pacific, "America/Phoenix") && !LocalEnvironmentService.Matches(pacific, "America/New_York"));
        var old = new IpQualityReport(true, "192.0.2.24", "United States", "New York", "America/New_York", "", "",
            null, null, null, null, null, DateTimeOffset.Parse("2026-10-15T18:48:40-07:00"), "fixture", "Net.Coffee") { CountryCode = "US" };
        var score = new IpPureScore(null, "fixture") { Provider = "Net.Coffee", DirectQuality = 98 };
        var cached = new ChunsonSpace.NodeTesting.CachedIpScore(old.Ip, score, old.FetchedAt, old);
        var measured = old with { Timezone = "", FetchedAt = DateTimeOffset.UtcNow };
        var admitted = cached.Apply(measured);
        Check("historical score no longer supplies a timezone for a fresh measurement",
            admitted.Admission.Allowed && admitted.Timezone == "" && !LocalEnvironmentService.Matches(pacific, admitted.Timezone));
        var route = "http://127.0.0.1:7890";
        var zone = pacific;
        var region = new EgressResult(true, old.Ip, "United States", "US", "Los Angeles", "America/Los_Angeles", "fixture fresh region");
        var admission = new FixtureAdmission { Evidence = new(admitted.Admission.Allowed, admitted.Ip, admitted.Timezone, admitted.Admission.Message) };
        var target = new IdentityRef(IdentityKind.Account, "TimezoneFixture");
        int probes = 0; bool changeRoute = false, changeZone = false;
        var local = new LocalEnvironmentService(_ => Task.FromResult(new LoginProxySelection(route, "fixture")),
            (_, _) => { probes++; if (changeRoute) route = "http://127.0.0.1:7891"; if (changeZone) zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); return Task.FromResult(region); },
            admission, () => zone, () => "US");
        var home = await local.CheckAsync();
        var launch = await local.ProbeAsync(target, route, default);
        Check("homepage and launch use fresh region while keeping score admission and the same IP",
            home.Matches && launch.Success && launch.Timezone == home.ExitTimezone && launch.Ip == admitted.Ip && probes == 2 && admission.Calls == 1 && !local.AllowSystemTimezoneChange);
        var count = probes;
        admission.Evidence = admission.Evidence with { Success = false, Message = "fixture score denied" };
        Check("denied score does not run or bypass region verification", !(await local.ProbeAsync(target, route, default)).Success && probes == count);
        admission.Evidence = admission.Evidence with { Success = true };
        region = region with { Ok = false, Message = "fixture provider disagreement" };
        Check("unconfirmed or conflicting region cannot fall back to cached timezone", !(await local.ProbeAsync(target, route, default)).Success);
        region = region with { Ok = true, Ip = "192.0.2.25" };
        Check("different admission and region IPs fail closed", !(await local.ProbeAsync(target, route, default)).Success);
        region = region with { Ip = old.Ip };
        changeRoute = true;
        Check("proxy change during region verification fails closed", !(await local.ProbeAsync(target, route, default)).Success);
        changeRoute = false;
        count = admission.Calls;
        Check("stale resolved proxy is rejected before admission", !(await local.ProbeAsync(target, "http://127.0.0.1:7890", default)).Success && admission.Calls == count);
        changeZone = true;
        Check("system timezone is reread after the region request", !(await local.ProbeAsync(target, route, default)).Success);
        changeZone = false; zone = pacific;

        var fixture = Path.Combine(directory, "fixture");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var workspace = DesktopWorkspace.CreateManagedFixture(fixture);
        try
        {
            var browser = new EnvironmentSettings("http://127.0.0.1:7999", "Asia/Tokyo", "en-US");
            workspace.Profiles.CreateBoundAccount(target.Name, browser);
            workspace.Profiles.SaveAccount(target.Name, new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "fixture", ["access_token"] = "fixture-only" } }, overwrite: true);
            var switching = new SwitchService(workspace.SwitchStorage, workspace.Runtime!, environments: local, egressProbe: local);
            var request = new SwitchRequest(target, true, true, true, browser, workspace.ActiveHome);
            region = region with { Timezone = "America/New_York" };
            var denied = await switching.SwitchAsync(request);
            Check("real mismatch reports both zones and exactly one refusal before stop or credential copy",
                !denied.Success && denied.Message.Contains("系统：America/Los_Angeles；出口：America/New_York")
                && denied.Message.Split("未启动分身", StringSplitOptions.None).Length == 2
                && workspace.OfflineRuntime!.StopCalls == 0 && !File.Exists(Path.Combine(workspace.ActiveHome, "auth.json")));
            region = region with { Timezone = "America/Los_Angeles" };
            var accepted = await switching.SwitchAsync(request);
            var launched = workspace.OfflineRuntime!.LastLaunchRequest;
            Check("synthetic launch uses fresh zone and local route without rewriting browser or Windows settings",
                accepted.Success && launched?.Environment?["TZ"] == region.Timezone && launched.Environment["HTTP_PROXY"] == route
                && !workspace.OfflineRuntime.LastTimezoneEnabled && workspace.Profiles.ReadEnvironment(target.Name) == browser);
        }
        finally { await workspace.Accounts!.DisposeAsync(); }
        File.WriteAllText(Path.Combine(directory, "timezone-verification.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = true, checks }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

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
        File.WriteAllText(Path.Combine(directory, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
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
        check("rechecking disables duplicate query and timezone writing but leaves navigation and login usable", model.LocalEnvironmentCheckLabel == "刷新中…" && !model.RecheckLocalEnvironmentCommand.CanExecute(null)
            && !model.SyncLocalTimezoneCommand.CanExecute(null) && model.NavigateCommand.CanExecute("Settings") && model.NewAccountLoginCommand.CanExecute(null));
        model.RecheckLocalEnvironmentCommand.Execute(null); await model.RefreshLocalEnvironmentAsync();
        check("repeated clicks do not duplicate the running query or replace the operation task", probes == beforeBackground + 1 && !model.LocalEnvironmentOperation.IsCompleted);
        model.NavigateCommand.Execute("Settings");
        pendingProbe.SetResult(exit); await model.LocalEnvironmentOperation; pendingProbe = null;
        check("background completion restores controls and updates evidence without moving the page", model.LocalEnvironmentCheckLabel == "刷新首页状态" && model.RecheckLocalEnvironmentCommand.CanExecute(null)
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
        exit = exit with { Ip = admission.Evidence.Ip, Timezone = admission.Evidence.Timezone };
        var request = new SwitchRequest(target, true, true, true, browserEnvironment, workspace.ActiveHome);
        var result = await switching.SwitchAsync(request);
        check("mismatched default exit stops clone before runtime stop or credential copy", !result.Success && workspace.OfflineRuntime!.StopCalls == 0 && !File.Exists(Path.Combine(workspace.ActiveHome, "auth.json")));
        admission.Evidence = admission.Evidence with { Timezone = "Asia/Tokyo" };
        exit = exit with { Timezone = admission.Evidence.Timezone };
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
