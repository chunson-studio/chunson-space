using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

// Explicit fixture mode: no P/Invoke, shell, UAC, network or real Windows writes.
public sealed class OfflineRegionSystem : IRegionSystem
{
    public RegionState State { get; set; } = new(true, 45, "China Standard Time");
    public bool AllowTimezone { get; set; } = true;
    public bool AllowRegion { get; set; } = true;
    public int TimezoneWrites { get; private set; }
    public int RegionWrites { get; private set; }
    public int AutomaticTimezoneWrites { get; private set; }
    public Action? TimezoneWritten { get; set; }
    public int? ResolveGeoId(string countryCode) => countryCode switch { "US" => 244, "CN" => 45, "JP" => 122, _ => null };
    public string? ResolveWindowsTimezone(string iana) => ChunsonSpace.Core.ProfileRules.WindowsTimezoneId(iana);
    public Task<RegionState> ReadAsync(CancellationToken token) => Task.FromResult(State);
    public Task<bool> SetTimezoneExplicitlyAsync(string id, CancellationToken token)
    { TimezoneWrites++; if (AllowTimezone) State = State with { WindowsTimezone = id }; TimezoneWritten?.Invoke(); return Task.FromResult(AllowTimezone); }
    public Task<bool> SetTimezoneAutomaticallyAsync(string id, CancellationToken token)
    { AutomaticTimezoneWrites++; return SetTimezoneExplicitlyAsync(id, token); }
    public bool SetHomeLocation(string countryCode, int geoId)
    { RegionWrites++; if (AllowRegion) State = State with { GeoId = geoId }; return AllowRegion; }
}
