using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChunsonLogin;

public sealed record BrowserInstallation(string Name, string Executable, string ProfileKey);

public static class BrowserLocator
{
    public static IReadOnlyList<BrowserInstallation> Find(IEnumerable<string>? roots = null)
    {
        var searchRoots = (roots ?? new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        }).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);
        var found = new List<BrowserInstallation>();
        foreach (var (name, key, relative) in new[]
        {
            ("Chrome", "chrome", @"Google\Chrome\Application\chrome.exe"),
            ("Edge", "edge", @"Microsoft\Edge\Application\msedge.exe")
        })
        {
            var executable = searchRoots.Select(root => Path.Combine(root, relative)).FirstOrDefault(File.Exists);
            if (executable is not null) found.Add(new(name, Path.GetFullPath(executable), key));
        }
        return found;
    }
}

/// <summary>Opens a persistent profile owned by this tool. It never closes browsers or removes web data.</summary>
public sealed class DedicatedBrowser(string dataRoot, Func<BrowserInstallation, string, bool?>? profileRunning = null)
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public ProcessStartInfo Prepare(BrowserInstallation browser, string proxy)
    {
        var executable = ValidateBrowser(browser);
        var endpoint = ValidateProxy(proxy);
        if (!Path.IsPathFullyQualified(dataRoot)) throw new IOException("浏览器资料目录必须使用绝对路径。");
        var root = Path.GetFullPath(dataRoot);
        var profile = Path.Combine(root, "browser-profiles", browser.ProfileKey);
        RejectLinks(profile);
        var created = PrepareEnglishProfile(profile);
        ConfirmProfileRoute(browser, profile, endpoint, created);

        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[]
        {
            "--user-data-dir=" + profile,
            "--profile-directory=Default",
            "--lang=en-US",
            "--accept-lang=en-US,en",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-sync",
            "--disable-features=Translate",
            "--proxy-server=" + endpoint,
            "https://chatgpt.com/",
            "https://ippure.com/claude"
        }) start.ArgumentList.Add(argument);
        return start;
    }

    public void Open(BrowserInstallation browser, string proxy)
    {
        var executable = ValidateBrowser(browser);
        if (!File.Exists(executable)) throw new FileNotFoundException("未找到所选浏览器，请先安装 Chrome 或 Edge。", executable);
        // A new Chromium process may hand these URLs to its existing profile and exit immediately.
        // That exit is not a failed browser launch and must never trigger cleanup of the live profile.
        using var process = Process.Start(Prepare(browser, proxy))
            ?? throw new IOException("未能打开专用浏览器。");
    }

    private static string ValidateBrowser(BrowserInstallation browser)
    {
        var expected = browser.ProfileKey switch { "chrome" => "chrome.exe", "edge" => "msedge.exe", _ => null };
        if (expected is null || !Path.IsPathFullyQualified(browser.Executable)
            || !Path.GetFileName(browser.Executable).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("浏览器类型或程序路径无效。", nameof(browser));
        return Path.GetFullPath(browser.Executable);
    }

    private static string ValidateProxy(string proxy)
    {
        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttp || endpoint.UserInfo.Length != 0
            || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.Port is < 1 or > 65535
            || !(endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)))
            throw new ArgumentException("需要不含账号密码的本机 HTTP 代理端口。", nameof(proxy));
        return endpoint.GetLeftPart(UriPartial.Authority);
    }

    private static bool PrepareEnglishProfile(string profile)
    {
        var localState = Path.Combine(profile, "Local State");
        var preferences = Path.Combine(profile, "Default", "Preferences");
        RejectLinks(localState);
        RejectLinks(preferences);
        if (Directory.Exists(profile))
        {
            // Existing profiles can be in use. Read only: Chromium owns subsequent preference writes.
            ValidateJsonObject(localState);
            ValidateJsonObject(preferences);
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(preferences)!);
        WriteNew(localState, "{\"intl\":{\"app_locale\":\"en-US\"}}");
        WriteNew(preferences, "{\"intl\":{\"accept_languages\":\"en-US,en\",\"selected_languages\":\"en-US,en\"},\"translate\":{\"enabled\":false}}");
        return true;
    }

    private void ConfirmProfileRoute(BrowserInstallation browser, string profile, string proxy, bool created)
    {
        var path = Path.Combine(profile, "chunson-login-route.json");
        RejectLinks(path);
        if (!created)
        {
            ValidateJsonObject(path);
            var saved = JsonNode.Parse(File.ReadAllText(path, Utf8))!.AsObject();
            if (saved["proxy"] is not JsonValue proxyValue || !proxyValue.TryGetValue<string>(out var previousProxy)
                || saved["executable"] is not JsonValue executableValue || !executableValue.TryGetValue<string>(out var previousExecutable))
                throw new IOException("专用浏览器网络记录不完整，已保留原资料。");
            if (previousProxy == proxy && string.Equals(previousExecutable, Path.GetFullPath(browser.Executable), StringComparison.OrdinalIgnoreCase)) return;
            // Chromium ignores new startup proxy flags when an existing profile accepts a URL handoff.
            // Only a positively stopped profile can receive a changed network or executable.
            var running = (profileRunning ?? IsProfileRunning)(browser, profile);
            if (running != false)
                throw new IOException(running == true
                    ? "专用浏览器仍在使用原代理端口。请关闭它的全部窗口后重试，网页登录资料会保留。"
                    : "无法确认专用浏览器是否已关闭。请关闭它的全部窗口后重试；未更改浏览器网络记录。");
        }
        var content = new JsonObject { ["proxy"] = proxy, ["executable"] = Path.GetFullPath(browser.Executable) }.ToJsonString();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        WriteNew(temporary, content);
        File.Move(temporary, path, true);
    }

    private static bool? IsProfileRunning(BrowserInstallation browser, string profile)
    {
        // Filter locally and emit only state, never browser URLs or command lines.
        var name = Path.GetFileName(browser.Executable).Replace("'", "''", StringComparison.Ordinal);
        var pattern = ProfileCommandPattern(profile).Replace("'", "''", StringComparison.Ordinal);
        var script = "$ErrorActionPreference='Stop'; "
            + "$pattern='" + pattern + "'; "
            + "$unknown=$false; $found=$false; "
            + "foreach($item in @(Get-CimInstance Win32_Process -Filter \"Name='" + name + "'\")){ "
            + "if([string]::IsNullOrWhiteSpace($item.CommandLine)){$unknown=$true;continue}; "
            + "if($item.CommandLine -match $pattern){$found=$true;break} }; "
            + "if($found){'running'}elseif($unknown){'unknown'}else{'stopped'}";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(8000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            _ = errors.GetAwaiter().GetResult();
            if (process.ExitCode != 0) return null;
            return output.GetAwaiter().GetResult().Trim() switch { "running" => true, "stopped" => false, _ => null };
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    internal static string ProfileCommandPattern(string profile) =>
        "(?i)(?:^|\\s)\"?--user-data-dir(?:=|\\s+)\"?" + Regex.Escape(profile) + "\"?(?=\\s|$)";

    private static void ValidateJsonObject(string path)
    {
        if (!File.Exists(path)) throw new IOException("专用浏览器资料尚未完整建立，已保留原目录。请关闭该专用浏览器后检查资料目录。");
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path, Utf8)) is not JsonObject)
                throw new IOException("专用浏览器偏好格式无效，已保留原文件。");
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new IOException("专用浏览器偏好无法读取，已保留原文件。", error);
        }
    }

    private static void WriteNew(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Utf8.GetBytes(content));
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("专用浏览器资料路径不能经过符号链接或目录联接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
