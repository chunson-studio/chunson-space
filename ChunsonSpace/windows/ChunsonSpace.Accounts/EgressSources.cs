using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

// Public endpoints only. Each lookup remains tied to the measured exit, never a
// previously saved IP, and a missing timezone is not guessed from the country.
public enum EgressLocationSource { IpWho, IpApi, NetCoffee }

public static class EgressSources
{
    public static IReadOnlyList<EgressLocationSource> Locations { get; } = Array.AsReadOnly(new[] { EgressLocationSource.IpWho, EgressLocationSource.IpApi, EgressLocationSource.NetCoffee });
    public static string Name(EgressLocationSource source) => source switch { EgressLocationSource.IpWho => "ipwho.is", EgressLocationSource.IpApi => "ipapi.is", _ => "Net.Coffee" };
    public static Uri Endpoint(EgressLocationSource source) => source switch { EgressLocationSource.IpWho => AccountEndpoints.Egress, EgressLocationSource.IpApi => IpQualityService.Endpoint, _ => NetCoffeeService.TraceEndpoint };
    public static bool ValidIp(string value) => IPAddress.TryParse(value, out var ip) && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any);
    public static bool SameIp(string left, string right) => IPAddress.TryParse(left, out var a) && IPAddress.TryParse(right, out var b) && a.Equals(b);
    public static bool SameZone(string left, string right) => ProfileRules.WindowsTimezoneId(left) is { } a && a == ProfileRules.WindowsTimezoneId(right);
    public static bool HasCountry(EgressResult value) => value.CountryCode.Length == 2 && value.CountryCode.All(c => c is >= 'A' and <= 'Z');
    public static bool LookupConflicts(JsonObject data, EgressResult measured)
    {
        var ip = AccountJson.Text(data, "ip");
        var country = AccountJson.Text(data, "countryCode").Trim().ToUpperInvariant();
        return ValidIp(ip) && !SameIp(ip, measured.Ip) || HasCountry(measured) && country.Length == 2 && country != measured.CountryCode;
    }

    public static EgressResult Trace(string text, string source)
    {
        var values = text.Split('\n').Select(line => line.Trim().Split('=', 2)).Where(pair => pair.Length == 2).ToArray();
        var ips = values.Where(pair => pair[0] == "ip").Select(pair => pair[1]).ToArray();
        var countries = values.Where(pair => pair[0] == "loc").Select(pair => pair[1].ToUpperInvariant()).ToArray();
        if (ips.Length != 1 || !ValidIp(ips[0])) return new(false, Message: source + " 未返回唯一、有效的出口 IP，本次无法核对。");
        return new(true, IPAddress.Parse(ips[0]).ToString(), CountryCode: countries.Length == 1 ? countries[0] : "", Message: source + " 已返回实际出口。");
    }

    public static EgressResult Parse(EgressLocationSource source, JsonObject data, EgressResult? measured = null)
    {
        var name = Name(source);
        if (data["error"] is not null || data["error_code"] is not null || AccountJson.Boolean(data["is_bogon"]) == true
            || source == EgressLocationSource.IpWho && AccountJson.Boolean(data["success"]) != true)
            return new(false, Message: name + " 返回查询错误，未采用该数据。");
        var location = source == EgressLocationSource.IpApi ? data["location"] as JsonObject ?? data : data;
        var ip = AccountJson.Text(data, "ip");
        if (!ValidIp(ip)) return new(false, Message: name + " 未返回有效出口 IP。");
        var country = AccountJson.Text(location, "country").Trim();
        var code = AccountJson.Text(location, source == EgressLocationSource.NetCoffee ? "countryCode" : "country_code").Trim().ToUpperInvariant();
        if (code.Length == 0 && country.Length > 0) code = CountryCode(country);
        var city = AccountJson.Text(location, "city");
        var zone = source == EgressLocationSource.IpWho ? AccountJson.Text(data["timezone"] as JsonObject, "id") : AccountJson.Text(location, "timezone");
        if (source == EgressLocationSource.NetCoffee)
        {
            if (measured is null || !measured.Ok || !SameIp(ip, measured.Ip))
                return new(false, Message: "Net.Coffee 查询报告与实际出口 IP 不一致，未采用地区与时区。");
            if (HasCountry(measured) && code != measured.CountryCode)
                return new(false, Message: "Net.Coffee 查询报告与实际出口国家不一致，未采用地区与时区。");
            zone = NetCoffeeService.ResolveTimezone(data, code, ref city, allowCountryFallback: false);
        }
        var valid = ProfileRules.WindowsTimezoneId(zone) is not null;
        return new(valid, IPAddress.Parse(ip).ToString(), country, code, city, valid ? zone : "",
            valid ? name + " 已返回出口与具体时区。" : name + " 已返回出口 IP，但未返回可识别的具体时区。");
    }

    private static string CountryCode(string name)
    {
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try { var region = new RegionInfo(culture.Name); if (region.EnglishName.Equals(name, StringComparison.OrdinalIgnoreCase)) return region.TwoLetterISORegionName; }
            catch (ArgumentException) { }
        }
        return "";
    }
}
