using System.Text.Json;
using ChunsonSpace.Windows;

internal static class BrowserUdpChecks
{
    public static void Run(Action<bool, string> check)
    {
        static string Report(string action = "Apply", string status = "Configured", bool verified = true,
            bool network = false, string[]? paths = null) => JsonSerializer.Serialize(new
            { action, status, rulesVerified = verified, networkVerified = network,
                programPaths = paths ?? [@"C:\fixture\chrome.exe", @"C:\fixture\msedge.exe"] });
        var valid = WindowsBrowserUdpProtection.Parse(0, Report(), "fixture-report.json");
        check(valid.RulesVerified && valid.ProgramPaths.Count == 2 && valid.ReportPath == "fixture-report.json"
            && valid.Message.Contains("网页检测结果需另行确认"), "browser UDP parser accepts effective Chrome and Edge rules without claiming network proof");
        check(!WindowsBrowserUdpProtection.Parse(1, Report()).RulesVerified, "browser UDP nonzero process exit cannot report effective protection");
        foreach (var action in new[] { "Preview", "Restore", "apply" })
            check(!WindowsBrowserUdpProtection.Parse(0, Report(action: action)).RulesVerified, "browser UDP parser requires exact completed Apply action");
        foreach (var status in new[] { "PreviewReady", "Failed", "Restored" })
            check(!WindowsBrowserUdpProtection.Parse(0, Report(status: status)).RulesVerified, "browser UDP parser rejects unconfirmed rule status");
        check(!WindowsBrowserUdpProtection.Parse(0, Report(verified: false)).RulesVerified, "browser UDP configured label without effective reread is rejected");
        check(!WindowsBrowserUdpProtection.Parse(0, Report(network: true)).RulesVerified, "browser UDP report cannot claim a network verification the script never performs");
        foreach (var paths in new[] { Array.Empty<string>(), new[] { "chrome.exe" }, new[] { @"C:\fixture\node.exe" } })
            check(!WindowsBrowserUdpProtection.Parse(0, Report(paths: paths)).RulesVerified, "browser UDP parser rejects absent relative or nonbrowser targets");
        foreach (var json in new[] { "{", "{}", "[]", "null" })
            check(!WindowsBrowserUdpProtection.Parse(0, json).RulesVerified, "browser UDP malformed report fails closed without launching native commands");
        static string Failure(string error) => JsonSerializer.Serialize(new
            { action = "Apply", status = "Failed", rulesVerified = false, networkVerified = false,
                programPaths = Array.Empty<string>(), error });
        foreach (var (error, detail) in new[]
        {
            ("Chrome or Edge was not found. Supply exact ProgramPaths.", "未找到 Chrome 或 Edge"),
            ("Windows Firewall is disabled or local rules are not accepted.", "不接受本地规则"),
            ("An existing rule conflicts with this repair; no rule was replaced.", "未覆盖"),
            ("Saved and effective firewall rules did not match.", "有效规则中确认"),
            ("Expected an existing absolute chrome.exe or msedge.exe path.", "程序位置无效")
        })
        {
            var failure = WindowsBrowserUdpProtection.Parse(1, Failure(error), "fixture-report.json");
            check(!failure.RulesVerified && failure.Message.Contains(detail) && failure.Message.Contains("fixture-report.json"),
                "browser UDP failure retains actionable localized cause and result path");
        }
        var sensitiveFailure = WindowsBrowserUdpProtection.Parse(1, Failure("denied https://fixture-user:account-49971c0f84@example.invalid/path Bearer fixture-bearer-value access_token=fixture-access-value \"password\":\"fixture-password-value\" account-c7cfa07dce@example.invalid " + new string('a', 80) + "\n" + new string('x', 1000)));
        check(!sensitiveFailure.RulesVerified && sensitiveFailure.Message.Contains("具体原因")
            && !new[] { "fixture-user", "fixture-secret", "fixture-bearer-value", "fixture-access-value", "fixture-password-value", "fixture-mail", new string('a', 80) }.Any(sensitiveFailure.Message.Contains)
            && !sensitiveFailure.Message.Any(char.IsControl) && sensitiveFailure.Message.Length < 350,
            "browser UDP unknown failure removes credential email token and control content and limits displayed error length");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? canonical = null;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "Set-BrowserUdpProtection.ps1");
            if (File.Exists(candidate)) { canonical = candidate; break; }
            directory = directory.Parent;
        }
        check(canonical is not null && WindowsBrowserUdpProtection.Script == File.ReadAllText(canonical),
            "browser UDP embedded resource is the exact shared canonical Chrome and Edge protection script");
    }
}
