namespace ChunsonLogin;

public sealed record EnvironmentSnapshot(NetworkRoute Route, EgressInfo Egress, string SystemTimezone, string SystemCountry)
{
    public bool TimezoneMatches => string.Equals(SystemTimezone, Egress.WindowsTimezone, StringComparison.OrdinalIgnoreCase);
    public bool RegionMatches => SystemRegion.ValidCountry(Egress.CountryCode) && SystemCountry == Egress.CountryCode;
    public bool Matches => TimezoneMatches && RegionMatches;
}

public sealed class EnvironmentCoordinator(INetworkRouteResolver routes, IEgressProbe probes, ISystemTimezone timezone, ISystemRegion region)
{
    public async Task<EnvironmentSnapshot> DetectAsync(CancellationToken token)
    {
        var route = await routes.ResolveAsync(token);
        var egress = await probes.ProbeAsync(route, token);
        await EnsureCurrentRouteAsync(route, token);
        return new(route, egress, await timezone.ReadAsync(token), region.Read());
    }

    public async Task<EnvironmentSnapshot> SyncAsync(CancellationToken token)
    {
        // Every explicit sync takes a fresh sample. A displayed earlier success is never a write authority.
        var snapshot = await DetectAsync(token);
        if (snapshot.Matches) return snapshot;
        if (!SystemRegion.ValidCountry(snapshot.Egress.CountryCode))
            throw new InvalidOperationException("出口国家代码未确认，未修改国家/地区或时区。");
        await EnsureCurrentRouteAsync(snapshot.Route, token);
        token.ThrowIfCancellationRequested();
        if (!snapshot.TimezoneMatches) await timezone.SetAsync(snapshot.Egress.WindowsTimezone, token);
        var actual = await timezone.ReadAsync(CancellationToken.None);
        if (!string.Equals(actual, snapshot.Egress.WindowsTimezone, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("时区写入后未能确认一致，请检查 Windows 时区设置。工具不会恢复旧时区。");
        // Finish the pair and readback after a write begins; never roll back successful settings.
        await EnsureCurrentRouteAsync(snapshot.Route, CancellationToken.None);
        if (snapshot.TimezoneMatches) token.ThrowIfCancellationRequested();
        if (!snapshot.RegionMatches) region.Set(snapshot.Egress.CountryCode);
        var actualCountry = region.Read();
        actual = await timezone.ReadAsync(CancellationToken.None);
        if (actualCountry != snapshot.Egress.CountryCode || actual != snapshot.Egress.WindowsTimezone)
            throw new InvalidOperationException("国家/地区与时区没有全部回读一致，请检查 Windows 设置；已应用的设置不会还原。");
        return snapshot with { SystemTimezone = actual, SystemCountry = actualCountry };
    }

    private async Task EnsureCurrentRouteAsync(NetworkRoute expected, CancellationToken token)
    {
        var current = await routes.ResolveAsync(token);
        if (!string.Equals(current.Proxy, expected.Proxy, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("检测期间 FlClash 入口发生变化，请重新检测。");
    }
}
