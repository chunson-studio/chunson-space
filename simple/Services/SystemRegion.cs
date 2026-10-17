using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ChunsonLogin;

public interface ISystemRegion
{
    string Read();
    void Set(string countryCode);
}

// Windows home country/region only: never display language, formats or keyboard settings.
public sealed class SystemRegion(Func<string>? read = null, Func<string, bool>? write = null) : ISystemRegion
{
    public static bool ValidCountry(string code)
    {
        if (code.Length != 2 || code.Any(c => c is < 'A' or > 'Z')) return false;
        try { var region = new RegionInfo(code); return region.TwoLetterISORegionName == code && region.GeoId > 0; }
        catch (ArgumentException) { return false; }
    }
    public static string Label(string code)
    {
        if (code.Length == 0) return "尚未检测";
        try { return new RegionInfo(code).DisplayName + "（" + code + "）"; }
        catch (ArgumentException) { return code; }
    }
    public string Read()
    {
        var value = (read ?? ReadNative)().Trim().ToUpperInvariant();
        if (value.Length == 0) throw new InvalidOperationException("无法读取 Windows 当前国家/地区。");
        return value;
    }
    public void Set(string countryCode)
    {
        if (!ValidCountry(countryCode)) throw new InvalidOperationException("出口国家代码无可靠的 Windows 对应值，未修改国家/地区。");
        if (Read() == countryCode) return;
        if (!(write ?? SetUserGeoName)(countryCode)) throw new InvalidOperationException("Windows 国家/地区设置未完成，请检查系统设置；已应用的时区不会还原。");
        if (Read() != countryCode) throw new InvalidOperationException("国家/地区写入后回读不一致；不会恢复旧地区或时区。");
    }
    private static string ReadNative()
    {
        var buffer = new StringBuilder(16);
        return GetUserDefaultGeoName(buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetUserDefaultGeoName(StringBuilder name, int count);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetUserGeoName(string name);
}
