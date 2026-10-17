using FAswitch.Windows;

internal static class RegionChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var fake = new RegionFake(); var service = new SystemRegionService(fake);
        check(fake.Writes == 0, "constructing region service never writes Windows settings");
        foreach (var pair in new[] { ("ZZ", "America/Chicago"), ("US", ""), ("US", "UTC"), ("US", "Unknown/Zone") })
            check(!(await service.ConfigureExplicitlyAsync(pair.Item1, pair.Item2)).Success && fake.Writes == 0, "unknown country or unspecified/unmapped concrete timezone blocks all writes");
        fake.State = fake.State with { Success = false };
        check(!(await service.ConfigureExplicitlyAsync("US", "America/Chicago")).Success && fake.Writes == 0, "unreadable initial system values block writes");
        fake.State = new(true, 45, "China Standard Time"); fake.TimezoneAllowed = false;
        check(!(await service.ConfigureExplicitlyAsync("US", "America/Chicago")).Success && fake.GeoWrites == 0 && fake.State.GeoId == 45, "declined timezone privilege does not change home location");
        fake.TimezoneAllowed = true; fake.GeoAllowed = false;
        var rejected = await service.ConfigureExplicitlyAsync("US", "America/Chicago");
        check(!rejected.Success && rejected.Changed && fake.State == new RegionState(true, 45, "Central Standard Time"), "home location failure retains applied timezone and never restores Beijing");
        fake.GeoAllowed = true;
        var result = await service.ConfigureExplicitlyAsync("US", "America/Chicago");
        check(result.Success && result.Changed && fake.State == new RegionState(true, 244, "Central Standard Time"), "multi-zone country uses the supplied exact timezone and rereads both fields");
        var count = fake.Writes;
        check((await service.ConfigureExplicitlyAsync("US", "America/Chicago")).Success && fake.Writes == count, "already-matching system region and timezone is idempotent");
        fake = new() { GeoAllowed = false, RefuseRollback = true }; service = new(fake);
        check(!(await service.ConfigureExplicitlyAsync("US", "America/Chicago")).Success && fake.State.WindowsTimezone == "Central Standard Time", "unconfirmed rollback never reports success");
        fake = new() { GeoAllowed = false, ConcurrentTimezone = "User choice" }; service = new(fake);
        check(!(await service.ConfigureExplicitlyAsync("US", "America/Chicago")).Success && fake.State.WindowsTimezone == "User choice", "recovery preserves concurrent manual timezone choice");
        fake = new(); service = new(fake); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.ConfigureExplicitlyAsync("US", "America/Chicago", cancelled.Token); throw new InvalidOperationException("cancel accepted"); }
        catch (OperationCanceledException) { check(fake.Writes == 0, "cancellation before dispatch leaves system values intact"); }
        var region = new RegionFake();
        var baselineStore = new RegionBaselineStore { Value = new("China Standard Time", "Central Standard Time", "old", "America/Chicago", "old") };
        var timezoneAdapter = new RegionTimezoneAdapter(region);
        var explicitTimezone = new SystemTimezoneService(timezoneAdapter, baselineStore);
        service = new(region);
        Task<RegionConfigurationResult> Configure() => service.ConfigureExplicitlyAsync("US", "America/Chicago",
            baselinePreflight: explicitTimezone.ValidateExplicitBaselineAsync, commitBaseline: explicitTimezone.CommitExplicitBaselineAsync);
        baselineStore.ThrowRead = true;
        check(!(await Configure()).Success && region.Writes == 0, "corrupt legacy timezone restore record blocks explicit region system writes");
        baselineStore.ThrowRead = false; baselineStore.Value = new("unknown baseline", "Central Standard Time", "old", "", "old");
        check(!(await Configure()).Success && region.Writes == 0 && baselineStore.Value.Saved == "unknown baseline", "unknown legacy restore timezone is preserved and blocks system changes");
        baselineStore.Value = new("China Standard Time", "Central Standard Time", "old", "America/Chicago", "old");
        baselineStore.FailWrite = true;
        var failedCommit = await Configure();
        check(!failedCommit.Success && failedCommit.Changed && region.State == new RegionState(true, 244, "Central Standard Time"), "baseline bookkeeping failure never rolls the system back to Beijing");
        baselineStore.FailWrite = false; baselineStore.IgnoreWrite = true;
        check(!(await Configure()).Success && region.State == new RegionState(true, 244, "Central Standard Time"), "unconfirmed bookkeeping is reported without restoring system values");
        baselineStore.IgnoreWrite = false; baselineStore.FailReadAfterWrite = true;
        check(!(await Configure()).Success && region.State == new RegionState(true, 244, "Central Standard Time"), "unreadable bookkeeping cannot falsely complete or reset system configuration");
        baselineStore.FailReadAfterWrite = false; baselineStore.ThrowRead = false;
        baselineStore.Value = new("China Standard Time", "Central Standard Time", "old", "America/Chicago", "old");
        var endedDesktop = new ProcessIdentity(99, 1, "Codex.exe", @"C:\Program Files\WindowsApps\OpenAI.Codex_1_x64__pub\app\Codex.exe", 123);
        var ownedProcesses = new FakeProcessSystem([endedDesktop]);
        var lifecycle = new OwnedProcessLifecycleService(ownedProcesses, new FakeClock(), new FakeOwnedStore(), explicitTimezone);
        check(lifecycle.Track(endedDesktop, OwnedProcessKind.Desktop, "fixture"), "baseline lifecycle fixture tracks a real fake desktop identity before simulating its exit");
        ownedProcesses.Items.Clear();
        var lease = await lifecycle.BeginLaunchAsync();
        var pendingPoll = lifecycle.PollAsync();
        check(!pendingPoll.IsCompleted, "existing ownership lease blocks automatic timezone restoration during explicit region transaction");
        check((await Configure()).Success && baselineStore.Value is { Saved: "Central Standard Time", Target: "", Account: "", Iana: "" }, "successful explicit region choice replaces old automatic restore target with verified current baseline");
        var appliedWrites = region.Writes;
        lease.Dispose();
        check((await pendingPoll).Success && region.State == new RegionState(true, 244, "Central Standard Time") && region.Writes == appliedWrites && timezoneAdapter.RestoreWrites == 0, "post-lease owned-process exit cannot restore China after explicit US/Central success");
        baselineStore.Value = new("China Standard Time", "Central Standard Time", "old", "America/Chicago", "old");
        check((await Configure()).Success && region.Writes == appliedWrites && baselineStore.Value is { Saved: "Central Standard Time", Target: "" }, "already matching no-op still clears stale legacy restore target without system writes");
        baselineStore.FailWrite = true;
        check(!(await Configure()).Success && region.Writes == appliedWrites, "no-op baseline write failure is reported as failure without rewriting system settings");
        baselineStore.FailWrite = false;
        region.State = new(true, 244, "User choice");
        var manual = await explicitTimezone.CommitExplicitBaselineAsync("Central Standard Time");
        check(!manual.Success && region.State.WindowsTimezone == "User choice", "baseline commit preserves an unexpected manual timezone instead of reapplying expected target");
        var native = new WindowsRegionSystem(new RegionNoCommands());
        var timezoneOnly = new RegionFake(); var timezoneOnlyService = new SystemRegionService(timezoneOnly);
        var synced = await timezoneOnlyService.ConfigureExplicitlyAsync("", "America/Chicago", timezoneOnly: true);
        check(synced.Success && timezoneOnly.State == new RegionState(true, 45, "Central Standard Time") && timezoneOnly.GeoWrites == 0, "timezone-only sync accepts exact timezone without changing country or needing country mapping");
        check((await timezoneOnlyService.ConfigureExplicitlyAsync("", "America/Chicago", timezoneOnly: true)).Success && timezoneOnly.Writes == 1, "timezone-only repeated sync does not repeat native writes");
        check(!(await timezoneOnlyService.ConfigureExplicitlyAsync("US", "Unknown/Zone", timezoneOnly: true)).Success && timezoneOnly.Writes == 1, "timezone-only mode refuses unknown timezone without a country guess");
        timezoneOnly.State = new(true, 45, "China Standard Time"); timezoneOnly.TimezoneAllowed = false;
        check(!(await timezoneOnlyService.ConfigureExplicitlyAsync("US", "America/Chicago", timezoneOnly: true)).Success && timezoneOnly.GeoWrites == 0, "timezone-only privilege denial preserves region and reports failure");
        check(native.ResolveGeoId("US") == 244 && native.ResolveGeoId("CN") == 45 && native.ResolveGeoId("us") is null && native.ResolveGeoId("bad") is null, "ISO country maps to native Windows GeoID without setting culture");
        check(FAswitch.Core.ProfileRules.WindowsTimezoneId("Asia/Calcutta") == FAswitch.Core.ProfileRules.WindowsTimezoneId("Asia/Kolkata"), "known IANA aliases share Windows timezone mapping");
    }
    private sealed class RegionTimezoneAdapter(RegionFake region) : ITimezoneSystem
    {
        public int RestoreWrites;
        public string? ResolveWindowsId(string id) => id is "China Standard Time" or "Central Standard Time" ? id : null;
        public Task<TimezoneReadResult> ReadAsync(CancellationToken token) => Task.FromResult(new TimezoneReadResult(region.State.Success, region.State.WindowsTimezone));
        public Task<bool> SetAsync(string id, CancellationToken token) { RestoreWrites++; region.State = region.State with { WindowsTimezone = id }; return Task.FromResult(true); }
    }
    private sealed class RegionBaselineStore : ITimezoneRestoreStore
    {
        public TimezoneRestoreRecord? Value;
        public bool ThrowRead, FailWrite, IgnoreWrite, FailReadAfterWrite;
        public TimezoneRestoreRecord? Read() => ThrowRead ? throw new System.Text.Json.JsonException("fixture corrupt baseline") : Value;
        public bool TryWrite(TimezoneRestoreRecord record)
        { if (FailWrite) return false; if (!IgnoreWrite) Value = record; if (FailReadAfterWrite) ThrowRead = true; return true; }
    }
    private sealed class RegionNoCommands : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string path, IReadOnlyList<string> arguments, CancellationToken token) => throw new InvalidOperationException("native command forbidden in offline region check");
    }
    private sealed class RegionFake : IRegionSystem
    {
        public RegionState State = new(true, 45, "China Standard Time");
        public bool TimezoneAllowed = true, GeoAllowed = true, RefuseRollback;
        public string? ConcurrentTimezone;
        public int Writes, GeoWrites;
        public int? ResolveGeoId(string country) => country == "US" ? 244 : null;
        public string? ResolveWindowsTimezone(string zone) => zone == "America/Chicago" ? "Central Standard Time" : null;
        public Task<RegionState> ReadAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(State); }
        public Task<bool> SetTimezoneExplicitlyAsync(string zone, CancellationToken token)
        { Writes++; bool allowed = TimezoneAllowed && !(RefuseRollback && zone == "China Standard Time"); if (allowed) State = State with { WindowsTimezone = zone }; return Task.FromResult(allowed); }
        public bool SetHomeLocation(string country, int geo)
        { Writes++; GeoWrites++; if (GeoAllowed) State = State with { GeoId = geo }; if (ConcurrentTimezone is { } value) State = State with { WindowsTimezone = value }; return GeoAllowed; }
    }
}
