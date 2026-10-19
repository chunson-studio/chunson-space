namespace ChunsonSpace.Core;

public sealed record ExitAddress(string Ip = "", string Message = "未查询")
{
    public bool Available => Ip.Length > 0;
    public string Display => Available ? Ip : "不可用（" + Message + "）";
}

public sealed record ExitAddresses(ExitAddress Ipv4, ExitAddress Ipv6)
{
    public static ExitAddresses Unknown { get; } = new(new(), new());
    public string PrimaryIp => Ipv4.Available ? Ipv4.Ip : Ipv6.Ip;
    public string Summary => $"IPv4：{Ipv4.Display}\nIPv6：{Ipv6.Display}";
}

