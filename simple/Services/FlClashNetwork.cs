using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace ChunsonLogin;

public sealed record NetworkRoute(string Proxy, string Source);
public interface INetworkRouteResolver
{
    Task<NetworkRoute> ResolveAsync(CancellationToken token);
}
public sealed record ProxyListener(string Process, string Address, int Port);

// Reads the existing main listener. It never changes FlClash or allocates a port.
public sealed class FlClashNetwork : INetworkRouteResolver
{
    private const int MaxConfigBytes = 5 * 1024 * 1024;
    private readonly string configPath;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ProxyListener>>> listeners;

    public FlClashNetwork(string? root = null,
        Func<CancellationToken, Task<IReadOnlyList<ProxyListener>>>? listenerReader = null)
    {
        configPath = Path.Combine(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.follow", "clash"), "config.yaml");
        listeners = listenerReader ?? (token => Task.FromResult(ReadListeners(token)));
    }

    public async Task<NetworkRoute> ResolveAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var active = (await listeners(token)).Where(item => item.Process.Equals("FlClashCore.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (active.Length == 0) throw new InvalidOperationException("未找到运行中的 FlClash 本地入口，请先打开 FlClash。");
        if (!File.Exists(configPath)) throw new InvalidOperationException("未找到 FlClash 当前配置，请先在 FlClash 载入配置。");
        RejectLinks(configPath);
        if (new FileInfo(configPath).Length > MaxConfigBytes) throw InvalidConfig();
        var config = ParseConfig(await File.ReadAllTextAsync(configPath, token));
        if (config["authentication"] is { } authentication && authentication is not JsonArray { Count: 0 })
            throw new InvalidOperationException("FlClash 本地入口需要密码，此工具暂不支持；请使用无需密码的本机入口。");
        var bind = Text(config["bind-address"]);
        var address = bind is "" or "*" or "0.0.0.0" ? IPAddress.Loopback : bind == "::" ? IPAddress.IPv6Loopback
            : IPAddress.TryParse(bind, out var parsed) && IPAddress.IsLoopback(parsed) ? parsed : null;
        if (address is null) throw new InvalidOperationException("FlClash 主入口没有绑定本机地址，无法确认当前网络。");
        foreach (var key in new[] { "mixed-port", "port" })
        {
            if (config[key] is not JsonValue value || !value.TryGetValue<int>(out var port) || port is < 1 or > 65535) continue;
            if (!active.Any(item => item.Port == port && IPAddress.TryParse(item.Address, out var bound)
                && (bound.Equals(address) || bound.Equals(IPAddress.Any) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    || bound.Equals(IPAddress.IPv6Any) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6))) continue;
            var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
            return new($"http://{host}:{port}", "FlClash 当前入口");
        }
        throw new InvalidOperationException("FlClash 主 HTTP 入口尚未监听。TUN 可以保持开启，请同时保留本机 mixed-port 或 HTTP 端口。");
    }

    private static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
    private static InvalidOperationException InvalidConfig() => new("FlClash 配置格式不兼容或损坏，未修改配置。");
    private static JsonObject ParseConfig(string yaml)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > MaxConfigBytes) throw InvalidConfig();
        try
        {
            var parser = new Parser(new StringReader(yaml));
            var documents = 0;
            while (parser.MoveNext())
            {
                if (parser.Current is DocumentStart && ++documents > 1) throw InvalidConfig();
                if (parser.Current is AnchorAlias || parser.Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                    throw InvalidConfig();
            }
            var value = new DeserializerBuilder().WithDuplicateKeyChecking().WithAttemptingUnquotedStringTypeDeserialization().Build().Deserialize<object>(yaml);
            var json = new SerializerBuilder().JsonCompatible().Build().Serialize(value);
            return JsonNode.Parse(json) as JsonObject ?? throw InvalidConfig();
        }
        catch (Exception error) when (error is YamlException or System.Text.Json.JsonException or ArgumentException)
        { throw InvalidConfig(); }
    }

    private static void RejectLinks(string path)
    {
        for (var current = new FileInfo(Path.GetFullPath(path)) as FileSystemInfo; current is not null;
            current = current is FileInfo file ? file.Directory : (current as DirectoryInfo)?.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("FlClash 配置路径包含链接，请使用实际配置目录。");
    }

    private static IReadOnlyList<ProxyListener> ReadListeners(CancellationToken token)
    {
        var rows = new List<ProxyListener>();
        var processes = new Dictionary<int, string>();
        foreach (var family in new[] { 2, 23 })
        {
            uint size = 0;
            var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (result is not (0 or 122)) throw new IOException("无法读取本机代理监听，请重试。");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (size is < 4 or > 16 * 1024 * 1024) throw new IOException("本机监听表格式无效。");
                var capacity = size;
                var buffer = Marshal.AllocHGlobal((int)capacity);
                try
                {
                    result = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                    if (result == 122 && attempt < 2) continue;
                    if (result != 0) throw new IOException("本机代理监听读取未完成，请重试。");
                    var count = Marshal.ReadInt32(buffer);
                    var stride = family == 2 ? 24 : 56;
                    if (count < 0 || 4L + (long)count * stride > capacity) throw new IOException("本机监听表长度无效。");
                    for (var i = 0; i < count; i++)
                    {
                        var row = IntPtr.Add(buffer, 4 + i * stride);
                        var pid = Marshal.ReadInt32(row, family == 2 ? 20 : 52);
                        if (!processes.TryGetValue(pid, out var name))
                        {
                            name = "";
                            try { using var process = Process.GetProcessById(pid); name = process.ProcessName + ".exe"; }
                            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                            processes[pid] = name;
                        }
                        if (!name.Equals("FlClashCore.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        var bytes = new byte[family == 2 ? 4 : 16];
                        Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), bytes, 0, bytes.Length);
                        var offset = family == 2 ? 8 : 20;
                        rows.Add(new(name, new IPAddress(bytes).ToString(), (Marshal.ReadByte(row, offset) << 8) | Marshal.ReadByte(row, offset + 1)));
                    }
                    break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        return rows;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
