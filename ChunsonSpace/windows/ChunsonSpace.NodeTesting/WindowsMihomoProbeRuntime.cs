using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using ChunsonSpace.Core;
using ChunsonSpace.Accounts;

namespace ChunsonSpace.NodeTesting;

public sealed class MihomoProbeRuntimeException(bool stopUnconfirmed, string message) : Exception(message)
{ public bool StopUnconfirmed { get; } = stopUnconfirmed; }

public sealed class WindowsMihomoProbeRuntime : IMihomoProbeRuntime
{
    public const string PinnedMihomoSha256 = "04F8D7FC2B314771E1ECBF15951D59F0E1CB7914503C88CFB6972FF6A824E314";
    public const string RootMarker = ".chunson-node-probe-root.json";
    public const string JobMarker = ".chunson-node-probe-job.json";
    private readonly string executable;
    private readonly string jobRoot;
    private readonly string expectedSha256;
    private readonly int preferredPort;
    private int active;
    private readonly object gate = new();
    private FileStream? verifiedBinary;
    private bool batchOpen, faulted;
    public int MaximumConcurrency => 6;
    internal int PeakConcurrentSessions { get; private set; }
    internal int BinaryVerificationCount { get; private set; }
    public WindowsMihomoProbeRuntime(string executable, string jobRoot, string expectedSha256 = PinnedMihomoSha256, int preferredPort = 0)
    { this.executable = Path.GetFullPath(executable); this.jobRoot = Path.GetFullPath(jobRoot).TrimEnd(Path.DirectorySeparatorChar); this.expectedSha256 = expectedSha256; this.preferredPort = preferredPort; }

    public IDisposable BeginBatch()
    {
        lock (gate)
        {
            if (batchOpen || active != 0 || faulted) throw new IOException("已有节点测试或清理未确认。");
            try { verifiedBinary = VerifyBinary(); EnsureRoot(); batchOpen = true; return new BatchLease(this); }
            catch { verifiedBinary?.Dispose(); verifiedBinary = null; throw; }
        }
    }
    private FileStream VerifyBinary()
    {
        BinaryVerificationCount++;
        if (!Path.GetFileName(executable).Equals("mihomo.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("需要标准 mihomo.exe。");
        PhysicalFileStorage.RejectLinks(executable);
        // Keep the verified file open without write/delete sharing for this batch.
        var input = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (expectedSha256.Length != 64 || !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("测试核心哈希未确认。");
            return input;
        }
        catch { input.Dispose(); throw; }
    }
    private sealed class BatchLease(WindowsMihomoProbeRuntime owner) : IDisposable
    {
        public void Dispose()
        {
            lock (owner.gate)
            { owner.batchOpen = false; owner.verifiedBinary?.Dispose(); owner.verifiedBinary = null; if (owner.active != 0) owner.faulted = true; }
        }
    }
    private void Release() { lock (gate) active--; }
    private MihomoProbeRuntimeException Unconfirmed(string message) { lock (gate) faulted = true; return new(true, message); }

    public async Task<IMihomoProbeSession> StartAsync(NodeCandidate node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (faulted || active >= (batchOpen ? MaximumConcurrency : 1)) throw new InvalidOperationException("测试核心数量达到上限或此前清理未确认。");
            active++;
            PeakConcurrentSessions = Math.Max(PeakConcurrentSessions, active);
        }
        string directory = ""; Process? child = null; Session? session = null; bool dispatchAttempted = false;
        try
        {
            if (!node.Supported || !Path.GetFileName(executable).Equals("mihomo.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
                throw new IOException("需要已验证的标准 mihomo.exe，不能使用 FlClashCore.exe。");
            lock (gate)
            {
                if (!batchOpen) { using var binary = VerifyBinary(); EnsureRoot(); }
            }
            directory = Path.Combine(jobRoot, "job-" + Guid.NewGuid().ToString("N"));
            PhysicalFileStorage.RejectLinks(directory); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, JobMarker), new JsonObject { ["kind"] = "chunson-node-probe", ["root"] = jobRoot, ["job"] = directory }.ToJsonString(), new UTF8Encoding(false));
            int port, controllerPort;
            using (var reserve = new TcpListener(IPAddress.Loopback, preferredPort))
            using (var controller = new TcpListener(IPAddress.Loopback, 0))
            { reserve.Start(); controller.Start(); port = ((IPEndPoint)reserve.LocalEndpoint).Port; controllerPort = ((IPEndPoint)controller.LocalEndpoint).Port; }
            var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var config = Path.Combine(directory, "config.yaml");
            File.WriteAllText(config, ProbeConfiguration.Build(node, port, controllerPort, secret), new UTF8Encoding(false));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-d", directory, "-f", config }) start.ArgumentList.Add(argument);
            foreach (var key in start.Environment.Keys.Where(key => key.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            token.ThrowIfCancellationRequested(); dispatchAttempted = true;
            child = Process.Start(start) ?? throw new IOException("测试核心没有返回进程句柄。");
            session = new Session(child, executable, jobRoot, directory, port, controllerPort, secret, Release);
            // Consume and discard output; config rejection logs can contain subscription secrets.
            session.DiscardOutput();
            var deadline = Environment.TickCount64 + 8000;
            while (Environment.TickCount64 < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (child.HasExited) throw new IOException("独立测试核心在监听前退出。");
                if (TcpOwner.IsOwnedLoopbackListener(port, child.Id) && TcpOwner.IsOwnedLoopbackListener(controllerPort, child.Id))
                {
                    await Task.Delay(150, token);
                    if (!child.HasExited && TcpOwner.IsOwnedLoopbackListener(port, child.Id) && TcpOwner.IsOwnedLoopbackListener(controllerPort, child.Id)) return session;
                }
                await Task.Delay(100, token);
            }
            throw new IOException("独立核心的本机 HTTP 监听未确认。");
        }
        catch (Exception error)
        {
            if (session is not null)
            {
                var stopped = await session.StopAsync(CancellationToken.None);
                if (!stopped.Success) throw Unconfirmed("测试核心启动未完成且退出或临时清理未确认。");
            }
            else if (dispatchAttempted && (child is not null || error is not Win32Exception))
                throw Unconfirmed("测试核心创建结果未确认，临时材料保留，停止后续测试。");
            else
            {
                try { if (directory.Length > 0) Cleanup(jobRoot, directory); }
                catch (Exception) { throw Unconfirmed("临时测试材料清理未确认。"); }
                Release();
            }
            if (error is OperationCanceledException) throw;
            throw new MihomoProbeRuntimeException(false, "无法启动独立测试核心；未使用主客户端核心或修改其节点。");
        }
    }
    private void EnsureRoot()
    {
        PhysicalFileStorage.RejectLinks(jobRoot);
        if (Path.GetPathRoot(jobRoot)?.TrimEnd(Path.DirectorySeparatorChar) == jobRoot) throw new IOException("测试材料目录不能为磁盘根。");
        var marker = Path.Combine(jobRoot, RootMarker);
        if (!File.Exists(marker))
        {
            if (Directory.Exists(jobRoot) && Directory.EnumerateFileSystemEntries(jobRoot).Any()) throw new IOException("拒绝接管已有非空测试目录。");
            Directory.CreateDirectory(jobRoot);
            File.WriteAllText(marker, new JsonObject { ["kind"] = "chunson-node-probe-root", ["root"] = jobRoot }.ToJsonString(), new UTF8Encoding(false));
        }
        PhysicalFileStorage.RejectLinks(marker);
        var data = JsonNode.Parse(File.ReadAllText(marker)) as JsonObject;
        if (data is null || NodeRules.Text(data, "kind") != "chunson-node-probe-root" || !NodeRules.Text(data, "root").Equals(jobRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("测试目录归属标记不一致。");
        if (Directory.EnumerateDirectories(jobRoot).Any(directory => Path.GetFileName(directory).StartsWith("job-", StringComparison.Ordinal))) throw new IOException("旧测试job尚未清理，未启动第二测试核心。");
    }
    internal static void Cleanup(string root, string directory)
    {
        if (!Path.GetDirectoryName(Path.GetFullPath(directory))!.Equals(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(directory).StartsWith("job-", StringComparison.Ordinal)) throw new IOException("测试清理路径越界。");
        var physical = new PhysicalFileStorage(); PhysicalFileStorage.RejectLinks(directory);
        var marker = Path.Combine(directory, JobMarker);
        var data = JsonNode.Parse(physical.Read(marker)) as JsonObject;
        if (data is null || NodeRules.Text(data, "kind") != "chunson-node-probe" || !NodeRules.Text(data, "root").Equals(root, StringComparison.OrdinalIgnoreCase) || !NodeRules.Text(data, "job").Equals(directory, StringComparison.OrdinalIgnoreCase)) throw new IOException("测试job标记不一致。");
        _ = physical.Files(directory, true);
        try { physical.DeleteDirectory(directory, true); }
        catch (IOException)
        {
            // A terminated process can briefly retain its working directory after the files disappear.
            // Retry only the already-validated empty directory, never another recursive deletion.
            for (var attempt = 0; ; attempt++)
            {
                PhysicalFileStorage.RejectLinks(directory);
                if (!Directory.Exists(directory)) return;
                if (Directory.EnumerateFileSystemEntries(directory).Any() || attempt == 5) throw;
                Thread.Sleep(100 * (attempt + 1));
                try { Directory.Delete(directory, false); return; }
                catch (IOException) { }
            }
        }
    }
    private sealed class Session : IMihomoProbeSession
    {
        private readonly Process process;
        private readonly string executable, root, directory;
        private readonly long created;
        private readonly Action released;
        private bool complete;
        private readonly int controllerPort;
        private readonly string secret;
        public string ProxyAddress { get; }
        public bool IsRunning => !complete && !process.HasExited && TcpOwner.IsOwnedLoopbackListener(new Uri(ProxyAddress).Port, process.Id);
        public Session(Process process, string executable, string root, string directory, int port, int controllerPort, string secret, Action released)
        { this.process = process; this.executable = executable; this.root = root; this.directory = directory; this.released = released; this.controllerPort = controllerPort; this.secret = secret; created = process.StartTime.ToUniversalTime().ToFileTimeUtc(); ProxyAddress = "http://127.0.0.1:" + port; }
        public async Task<ProxyLatencyReport?> MeasureDelayAsync(CancellationToken token)
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{controllerPort}/proxies/ChunsonProbeNode/delay?timeout=5000&expected=204&url=" + Uri.EscapeDataString(ProxyLatencyService.Endpoint.AbsoluteUri));
            request.Headers.Authorization = new("Bearer", secret);
            try
            {
                using var response = await client.SendAsync(request, token);
                if (!response.IsSuccessStatusCode) return new(response.StatusCode == HttpStatusCode.GatewayTimeout ? ProxyLatencyState.Timeout : ProxyLatencyState.Failed, null, "核心测速未通过；当前测试目标不可达，已跳过评分。");
                var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
                return data?["delay"] is JsonValue value && value.TryGetValue<long>(out var delay) && delay is >= 0 and <= 65535
                    ? new(ProxyLatencyState.Success, delay, "Mihomo 原生 URL 测速；不是 ICMP ping。")
                    : new(ProxyLatencyState.Failed, null, "核心没有返回有效延迟。");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(ProxyLatencyState.Timeout, null, "核心测速超时，已跳过评分。"); }
            catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException) { return new(ProxyLatencyState.Failed, null, "核心测速暂不可用，已跳过评分。"); }
        }
        public void DiscardOutput() { _ = Drain(process.StandardOutput); _ = Drain(process.StandardError); }
        private static async Task Drain(StreamReader reader) { try { var buffer = new char[4096]; while (await reader.ReadAsync(buffer) > 0) { } } catch (Exception) { } }
        public async Task<MihomoProbeStopResult> StopAsync(CancellationToken token)
        {
            if (complete) return new(true, "独立核心与临时材料已清理。");
            try
            {
                if (!process.HasExited)
                {
                    var handle = process.SafeHandle;
                    var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
                    if (!GetProcessTimes(handle, out var creation, out _, out _, out _) || creation.Value != created
                        || !QueryFullProcessImageNameW(handle, 0, path, ref length) || !path.ToString().Equals(executable, StringComparison.OrdinalIgnoreCase)
                        || !TerminateProcess(handle, 1)) return new(false, "未确认测试核心身份或退出，材料保留。", directory);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(10000);
                    await process.WaitForExitAsync(deadline.Token);
                }
                if (!process.HasExited) return new(false, "测试核心退出未确认，材料保留。", directory);
                Cleanup(root, directory);
                complete = true; process.Dispose(); released();
                return new(true, "仅本次独立核心已退出，临时配置已清理。");
            }
            catch (Exception error) { return new(false, $"测试核心退出或安全清理未确认（{error.GetType().Name}/0x{error.HResult:X8}），材料保留。", directory); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; public readonly long Value => ((long)High << 32) | Low; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);

    private static class TcpOwner
    {
        public static bool IsOwnedLoopbackListener(int port, int pid)
        {
            uint size = 0; _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, 3, 0);
            if (size is 0 or > 16 * 1024 * 1024) return false;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, true, 2, 3, 0) != 0) return false;
                var count = Marshal.ReadInt32(buffer);
                if (count < 0 || 4L + count * 24L > size) return false;
                for (var i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(buffer, 4 + i * 24);
                    var state = Marshal.ReadInt32(row); var address = Marshal.ReadInt32(row, 4); var rawPort = Marshal.ReadInt32(row, 8);
                    var localPort = (rawPort & 255) << 8 | (rawPort >> 8 & 255);
                    if (state == 2 && new IPAddress((uint)address).Equals(IPAddress.Loopback) && localPort == port && Marshal.ReadInt32(row, 20) == pid) return true;
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    }
}
