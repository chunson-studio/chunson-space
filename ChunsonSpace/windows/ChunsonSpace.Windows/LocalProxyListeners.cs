using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace ChunsonSpace.Windows;

public sealed record LocalProxyListener(string Process, string Address, int Port);

/// <summary>Read-only listener inventory for supported local proxy cores. No connection or port scan.</summary>
public static class LocalProxyListeners
{
    public static async Task<IReadOnlyList<LocalProxyListener>> ReadAsync(CancellationToken token, ICommandRunner? runner = null)
    {
        if (runner is null) return ReadNative(token);
        const string script = """
            [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
            $ErrorActionPreference='Stop'
            $cores=@(Get-CimInstance Win32_Process -Filter "Name='FlClashCore.exe' OR Name='verge-mihomo.exe' OR Name='mihomo.exe' OR Name='clash-meta.exe' OR Name='clash.exe'")
            $rows=@()
            if ($cores.Count -gt 0) {
              $listeners=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue)
              $rows=@(foreach ($core in $cores) {
                foreach ($listener in $listeners | Where-Object OwningProcess -eq $core.ProcessId) {
                  [pscustomobject]@{Process=$core.Name;Address=$listener.LocalAddress;Port=[int]$listener.LocalPort}
                }
              })
            }
            ConvertTo-Json -InputObject $rows -Compress
            """;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var result = await (runner ?? new WindowsCommandRunner()).RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], deadline.Token);
        if (result.ExitCode != 0) throw new InvalidOperationException("暂时无法读取代理软件运行状态，请重新打开代理软件后重试。");
        try { return JsonSerializer.Deserialize<LocalProxyListener[]>(result.Output) ?? []; }
        catch (JsonException) { throw new InvalidOperationException("代理软件运行状态无法识别，请重试。"); }
    }

    // GetExtendedTcpTable uses OWNER_PID_LISTENER (3) for both address families.
    // Reading the native table avoids launching PowerShell/CIM on every login.
    private static IReadOnlyList<LocalProxyListener> ReadNative(CancellationToken token)
    {
        var rows = new List<LocalProxyListener>();
        var processes = new Dictionary<int, string>();
        foreach (var family in new[] { 2, 23 })
        {
            uint size = 0;
            var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (result is not (0 or 122)) throw new InvalidOperationException("无法读取本地代理入口，请重试。");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (size < 4 || size > 16 * 1024 * 1024) throw new InvalidOperationException("本地监听表格式无效。");
                var capacity = size; var buffer = Marshal.AllocHGlobal((int)capacity);
                try
                {
                    result = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                    if (result == 122 && attempt < 2) continue;
                    if (result != 0) throw new InvalidOperationException("本地代理入口读取未完成，请重试。");
                    var count = Marshal.ReadInt32(buffer); var stride = family == 2 ? 24 : 56;
                    if (count < 0 || 4L + (long)count * stride > capacity) throw new InvalidOperationException("本地监听表长度无效。");
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
                        if (!new[] { "FlClashCore.exe", "verge-mihomo.exe", "mihomo.exe", "clash-meta.exe", "clash.exe" }.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                        var bytes = new byte[family == 2 ? 4 : 16]; Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), bytes, 0, bytes.Length);
                        var portOffset = family == 2 ? 8 : 20;
                        var port = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                        rows.Add(new(name, new IPAddress(bytes).ToString(), port));
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
