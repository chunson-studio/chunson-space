using System.IO;
using ChunsonSpace.Accounts;
using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

internal static class AutomaticLoginProxyVerification
{
    public static async Task RunAsync(string root, Action<string, bool> check)
    {
        Directory.CreateDirectory(root);
        var fl = Path.Combine(root, "fl.yaml"); var verge = Path.Combine(root, "verge.yaml");
        File.WriteAllText(fl, "mixed-port: 7897\nbind-address: '*'\nauthentication: []\ntun: {enable: true}\nlisteners: [{name: dedicated, type: http, port: 18081, listen: 127.0.0.1}]\n");
        var entries = new List<LocalProxyListener>(); string? system = null;
        var resolver = new AutomaticLoginProxy(() => system,
            [new("FlClash", fl, ["FlClashCore.exe"]), new("Clash Verge", verge, ["verge-mihomo.exe"])],
            _ => Task.FromResult<IReadOnlyList<LocalProxyListener>>(entries));
        async Task Refused(string name, string expected)
        {
            try { await resolver.ResolveAsync(default); check(name, false); }
            catch (AccountException error) { check(name, error.Message.Contains(expected)); }
        }
        await Refused("stale proxy configuration without running core never opens login", "没有找到");
        entries.Add(new("other.exe", "127.0.0.1", 7897));
        await Refused("port owned by another application is not a proxy", "没有找到");
        entries.Clear(); entries.Add(new("FlClashCore.exe", "127.0.0.1", 18081));
        await Refused("dedicated listener is never chosen instead of current main route", "没有找到");
        entries.Add(new("FlClashCore.exe", "0.0.0.0", 7897));
        var tun = await resolver.ResolveAsync(default);
        check("TUN with disabled Windows proxy resolves active FlClash main HTTP listener", tun == new LoginProxySelection("http://127.0.0.1:7897", "FlClash"));
        File.WriteAllText(verge, "mixed-port: 7898\nbind-address: 127.0.0.1\nauthentication: []\n");
        entries.Add(new("verge-mihomo.exe", "127.0.0.1", 7898));
        await Refused("two active clients require clarification rather than picking an arbitrary route", "多个");
        entries.RemoveAll(item => item.Process == "verge-mihomo.exe");
        File.WriteAllText(fl, "mixed-port: 7897\nbind-address: '*'\nauthentication: ['fixture-user:fixture-password']\n");
        await Refused("authenticated listeners fail clearly without exposing credentials", "需要密码");
        File.WriteAllText(fl, "mixed-port: 7897\nbind-address: '::1'\nauthentication: []\n");
        entries.Clear(); entries.Add(new("FlClashCore.exe", "::", 7897));
        check("IPv6-only local main listener is recognized", (await resolver.ResolveAsync(default)).Proxy == "http://[::1]:7897");
        system = "http://127.0.0.1:8899";
        check("explicit Windows proxy remains authoritative", (await resolver.ResolveAsync(default)).Proxy == system);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await resolver.ResolveAsync(cancellation.Token); check("cancelled discovery stops before authorization", false); }
        catch (OperationCanceledException) { check("cancelled discovery stops before authorization", true); }
        var authorizeCalls = 0; var busy = false;
        var slow = new AccountOnboardingViewModel(async token => { await Task.Delay(10000, token); return tun; },
            (_, _, _) => { authorizeCalls++; return Task.CompletedTask; }, value => busy = value, () => { });
        slow.AuthorizeCommand.Execute(null); slow.CloseCommand.Execute(null); await slow.CurrentOperation;
        check("cancel during discovery produces no browser or OAuth operation and enables retry", authorizeCalls == 0 && !busy && slow.CanRetry);
        var read = await LocalProxyListeners.ReadAsync(default, new ListenerRunner(new(0, "[{\"Process\":\"FlClashCore.exe\",\"Address\":\"127.0.0.1\",\"Port\":7897}]")));
        check("native listener reader parses a single-row array", read.Count == 1 && read[0].Port == 7897);
        foreach (var result in new[] { new CommandResult(1, "fixture-secret"), new CommandResult(0, "invalid fixture-secret") })
        {
            try { await LocalProxyListeners.ReadAsync(default, new ListenerRunner(result)); check("listener failure rejected", false); }
            catch (InvalidOperationException error) { check("listener failure is actionable and redacts raw output", !error.Message.Contains("fixture-secret")); }
        }
    }
    private sealed class ListenerRunner(CommandResult result) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
