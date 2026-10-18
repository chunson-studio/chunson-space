using ChunsonSpace.Core;
using ChunsonSpace.Windows;

internal static class SystemProxyChecks
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var (server, expected) in new[] {
            ("127.0.0.1:7897", "http://127.0.0.1:7897"),
            ("http://localhost:8080", "http://localhost:8080"),
            ("http=first.invalid:1111;https=second.invalid:2222;socks=localhost:3333", "http://second.invalid:2222"),
            ("https=https://proxy.invalid:8443", "https://proxy.invalid:8443"),
            ("[::1]:7897", "http://[::1]:7897") })
            check(new SystemProxyResolver(() => new(true, server)).ResolveHttpProxy() == expected, "manual proxy uses explicit HTTPS route and port: " + expected);
        foreach (var settings in new[] {
            new SystemProxySettings(false, "localhost:8080"), new(true, ""), new(true, "localhost"),
            new(true, "http=localhost:8080"), new(true, "socks=localhost:8080"), new(true, "socks5://localhost:8080"),
            new(true, "DIRECT"), new(true, "localhost:0"), new(true, "localhost:65536"),
            new(true, "http://fixture-secret@localhost:8080"), new(true, "localhost:8080;other:9090"),
            new(true, "https=one:1;https=two:2"), new(true, "localhost:8080", "https://fixture.invalid/proxy.pac"),
            new(true, "localhost:8080", AutoDetect: true), new(true, "http://localhost:8080?fixture-secret") })
        {
            try { new SystemProxyResolver(() => settings).ResolveHttpProxy(); check(false, "unsafe system proxy was accepted"); }
            catch (SystemProxyException error) { check(!error.ToString().Contains("fixture-secret", StringComparison.Ordinal), "missing, PAC, ambiguous, authenticated or malformed system proxy never becomes DIRECT"); }
        }
        var broken = new SystemProxyResolver(() => throw new IOException("fixture-secret"));
        try { broken.ResolveHttpProxy(); check(false, "unreadable system settings accepted"); }
        catch (SystemProxyException error) { check(!error.ToString().Contains("fixture-secret", StringComparison.Ordinal), "system registry failure exposes no raw setting or exception"); }
        var reads = 0;
        var current = "localhost:7001";
        var resolver = new SystemProxyResolver(() => { reads++; return new(true, current); });
        var environment = new EnvironmentSettings("ignored:9000", FollowSystemProxy: true);
        var first = DesktopLaunchSpec.Build(environment, "fixture-home", true, [], resolver);
        current = "localhost:7002";
        var second = DesktopLaunchSpec.Build(environment, "fixture-home", true, [], resolver);
        check(reads == 2 && first.Environment!["HTTPS_PROXY"] == "http://localhost:7001" && second.Environment!["HTTP_PROXY"] == "http://localhost:7002", "follow-system resolves each new launch rather than persisted proxy");
        check(second.Arguments.Contains("--proxy-server=http://localhost:7002") && second.Environment!["NO_PROXY"] == "localhost,127.0.0.1,::1", "follow-system proxy injected into Chromium and environment with local-only bypass");
        var explicitSpec = DesktopLaunchSpec.Build(new EnvironmentSettings("localhost:8000"), "fixture-home", true, [], broken);
        check(explicitSpec.Success && explicitSpec.Environment!["HTTPS_PROXY"] == "http://localhost:8000", "dedicated proxy avoids system registry entirely");
        check(!DesktopLaunchSpec.Build(environment, "fixture-home", true, [], broken).Success, "unresolved follow-system fails launch specification");
    }
}
