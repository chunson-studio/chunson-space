namespace ChunsonLogin;

internal static class RegionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var current = "JP"; var writes = new List<string>(); bool allowed = true, apply = true;
        var region = new SystemRegion(() => current, code => { writes.Add(code); if (allowed && apply) current = code; return allowed; });
        void Refused(string code, string message)
        {
            try { region.Set(code); check(false, message); }
            catch (InvalidOperationException) { check(true, message); }
        }
        check(region.Read() == "JP" && writes.Count == 0, "region: read is independent from Windows language and timezone");
        region.Set("US");
        check(current == "US" && writes.SequenceEqual(["US"]), "region: explicit ISO country is written and read back");
        region.Set("US"); check(writes.Count == 1, "region: matching setting needs no write");
        foreach (var invalid in new[] { "", "USA", "ZZ", "us", " US", "001" }) Refused(invalid, "region: invalid country refused: " + invalid);
        check(writes.Count == 1, "region: invalid country never reaches Windows setter");
        allowed = false; Refused("JP", "region: denied write cannot claim success");
        check(current == "US", "region: denied write does not revert country");
        allowed = true; apply = false; Refused("JP", "region: unchanged readback cannot claim success");
        check(writes.SequenceEqual(["US", "JP", "JP"]), "region: no automatic recovery writes");
        check(SystemRegion.Label("US").Contains("US"), "region: display includes ISO country");
    }
}
