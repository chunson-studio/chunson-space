using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.NodeTesting;

public sealed record ManagedNodeBinding(string Id, string SubscriptionId, string NodeId, string Ip, int Port)
{
    public string Proxy => "http://127.0.0.1:" + Port;
}

// Persistent references and local ports only. Node credentials stay in the existing subscription.
public sealed class ManagedNodeRoutes : IAsyncDisposable
{
    private readonly string path;
    private readonly Func<ManagedNodeBinding, NodeCandidate> resolve;
    private readonly Func<ManagedNodeBinding, IMihomoProbeRuntime> runtime;
    private readonly Func<string, CancellationToken, Task<IpQualityReport>> probe;
    private readonly Func<ManagedNodeBinding, string> subscriptionName;
    private readonly List<ManagedNodeBinding> bindings;
    private readonly Dictionary<string, IMihomoProbeSession> sessions = [];
    private readonly Dictionary<string, IpQualityReport> prepared = [];
    private readonly SemaphoreSlim gate = new(1);
    public ManagedNodeRoutes(string path, Func<ManagedNodeBinding, NodeCandidate> resolve,
        Func<ManagedNodeBinding, IMihomoProbeRuntime> runtime, Func<string, CancellationToken, Task<IpQualityReport>> probe,
        Func<ManagedNodeBinding, string>? subscriptionName = null)
    {
        this.path = path; this.resolve = resolve; this.runtime = runtime; this.probe = probe;
        this.subscriptionName = subscriptionName ?? (_ => "");
        var files = new PhysicalFileStorage();
        bindings = files.FileExists(path) ? JsonSerializer.Deserialize<List<ManagedNodeBinding>>(files.Read(path)) ?? [] : [];
        if (bindings.Any(item => !Guid.TryParseExact(item.Id, "N", out _) || !IPAddress.TryParse(item.Ip, out _) || item.Port is < 1024 or > 65535
            || item.NodeId.Length != 64 || !item.NodeId.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(item.SubscriptionId))
            || bindings.Select(item => item.Port).Distinct().Count() != bindings.Count)
            throw new IOException("独立出口记录不完整，请检查本地记录；未改用当前网络。");
    }
    public async Task<string> BindAsync(string subscriptionId, NodeCandidate node, IpQualityReport measured, CancellationToken token, bool reuseExisting = true)
    {
        measured = ApplyAdmission(new("", subscriptionId, node.Id, measured.Ip, 0), measured);
        if (!node.Supported || !measured.Admission.Allowed)
            throw new AccountException(AccountError.ExitUnavailable, "请选择已确认出口且符合准入规则的 IP。");
        await gate.WaitAsync(token);
        try
        {
            var binding = reuseExisting ? bindings.FirstOrDefault(item => item.SubscriptionId == subscriptionId && item.NodeId == node.Id && item.Ip == measured.Ip) : null;
            if (binding is null)
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                binding = new(Guid.NewGuid().ToString("N"), subscriptionId, node.Id, measured.Ip, ((IPEndPoint)listener.LocalEndpoint).Port);
                if (bindings.Any(item => item.Port == binding.Port)) throw new IOException("自动端口暂不可用，请重试。");
                bindings.Add(binding);
                try { new PhysicalFileStorage().WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(bindings)); }
                catch { bindings.Remove(binding); throw; }
            }
            return binding.Proxy;
        }
        finally { gate.Release(); }
    }
    public async Task EnsureProxyAsync(string proxy, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var binding = Find(proxy); if (binding is null) return;
            await EnsureCoreAsync(binding, token);
        }
        finally { gate.Release(); }
    }
    public async Task<string> AssignedIpAsync(string proxy, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { return Find(proxy)?.Ip ?? ""; }
        finally { gate.Release(); }
    }
    // Account environments are the source of truth for retained references.
    // Never stop a shared port or any listener not created by this service.
    public async Task<bool> ReleaseUnusedAsync(string proxy, IReadOnlyCollection<string> retained, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var binding = Find(proxy);
            if (binding is null || retained.Contains(binding.Proxy, StringComparer.OrdinalIgnoreCase)) return false;
            await StopOneAsync(binding.Id);
            bindings.Remove(binding); prepared.Remove(binding.Id);
            try { new PhysicalFileStorage().WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(bindings)); }
            catch { bindings.Add(binding); throw; }
            return true;
        }
        finally { gate.Release(); }
    }
    public async Task<(string Proxy, IpQualityReport Quality)> BindUnmeasuredAsync(string subscriptionId, NodeCandidate node, CancellationToken token)
    {
        if (!node.Supported || string.IsNullOrWhiteSpace(subscriptionId)) throw new IOException("请选择有效的订阅节点。");
        await gate.WaitAsync(token);
        ManagedNodeBinding? binding = null;
        try
        {
            int port;
            using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
            if (bindings.Any(item => item.Port == port)) throw new IOException("自动端口暂不可用，请重试。");
            binding = new(Guid.NewGuid().ToString("N"), subscriptionId, node.Id, "", port);
            var current = resolve(binding);
            if (!current.Supported || current.Id != node.Id) throw new IOException("所选订阅节点已更新，请重新选择。");
            var session = await runtime(binding).StartAsync(current, token);
            sessions[binding.Id] = session;
            if (session.ProxyAddress != binding.Proxy) throw new IOException("独立端口不一致，未连接账号。");
            var measured = ApplyAdmission(binding, await probe(binding.Proxy, token));
            token.ThrowIfCancellationRequested();
            if (!measured.Admission.Allowed || !IPAddress.TryParse(measured.Ip, out _))
                throw new AccountException(AccountError.ExitUnavailable, measured.Admission.Message);
            binding = binding with { Ip = measured.Ip };
            bindings.Add(binding);
            try { new PhysicalFileStorage().WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(bindings)); }
            catch { bindings.Remove(binding); throw; }
            prepared[binding.Id] = measured;
            return (binding.Proxy, measured);
        }
        catch { if (binding is not null) await StopOneAsync(binding.Id); throw; }
        finally { gate.Release(); }
    }
    private ManagedNodeBinding? Find(string proxy) => Uri.TryCreate(proxy, UriKind.Absolute, out var uri) && uri.Scheme == "http"
        && uri.Host == "127.0.0.1" && uri.UserInfo.Length == 0 ? bindings.FirstOrDefault(item => item.Port == uri.Port) : null;
    private async Task EnsureCoreAsync(ManagedNodeBinding binding, CancellationToken token)
    {
        if (sessions.TryGetValue(binding.Id, out var previous))
        {
            if (previous.IsRunning) return;
            await StopOneAsync(binding.Id); prepared.Remove(binding.Id);
        }
        var node = resolve(binding);
        if (!node.Supported || node.Id != binding.NodeId) throw new IOException("所选节点已更新或移除，请从检测结果重新选择 IP。");
        var session = await runtime(binding).StartAsync(node, token);
        sessions[binding.Id] = session;
        try
        {
            if (session.ProxyAddress != binding.Proxy) throw new IOException("独立端口不一致，未连接账号。");
            // Selection already admitted this node. Starting its local listener must
            // not rescreen it; callers obtain exit/timezone evidence for their task.
        }
        catch { await StopOneAsync(binding.Id); throw; }
    }
    private async Task<IpQualityReport> CheckAsync(ManagedNodeBinding binding, CancellationToken token)
    {
        var report = ApplyAdmission(binding, await probe(binding.Proxy, token));
        if (!IPAddress.TryParse(report.Ip, out var actual))
            throw new AccountException(AccountError.ExitUnavailable, "此线路暂未取得有效出口 IP，不能据此判断出口已改变。" + report.Message);
        if (!report.Admission.Allowed) throw new AccountException(AccountError.ExitUnavailable, report.Admission.Message);
        if (!actual.Equals(IPAddress.Parse(binding.Ip)))
            throw new AccountException(AccountError.ExitUnavailable, $"此线路出口已改变：筛选时 {binding.Ip}，本次 {actual}。请重新选择 IP；未使用其他出口登录。");
        return report;
    }
    private IpQualityReport ApplyAdmission(ManagedNodeBinding binding, IpQualityReport report)
    {
        var current = resolve(binding);
        if (!NodeScreeningRules.IsAccountCandidate(current) || current.Id != binding.NodeId)
            throw new IOException("所选节点已更新或移除，请重新选择 IP。");
        return NodeScreeningRules.ApplyAdmission(report, current, subscriptionName(binding));
    }
    public async Task<IpQualityReport?> ProbeAdmissionAsync(string proxy, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var binding = Find(proxy); if (binding is null) return null;
            await EnsureCoreAsync(binding, token);
            if (prepared.Remove(binding.Id, out var report) && DateTimeOffset.UtcNow - report.FetchedAt < TimeSpan.FromSeconds(30)) return ApplyAdmission(binding, report);
            return await CheckAsync(binding, token);
        }
        finally { gate.Release(); }
    }
    private async Task StopOneAsync(string id)
    {
        if (!sessions.TryGetValue(id, out var session)) return;
        var stopped = await session.StopAsync(CancellationToken.None);
        if (!stopped.Success) throw new IOException(stopped.Message);
        sessions.Remove(id);
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { foreach (var id in sessions.Keys.ToArray()) await StopOneAsync(id); prepared.Clear(); }
        finally { gate.Release(); }
    }
}
