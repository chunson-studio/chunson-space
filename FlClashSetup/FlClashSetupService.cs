using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Chunson.Setup;

internal sealed record FlClashInstallation(string? Executable, bool Running);
internal sealed record FlClashReadiness(FlClashInstallation Installation, bool Ready, string Message);

internal sealed class FlClashSetupService
{
    private readonly Func<FlClashInstallation> discover;
    private readonly Func<CancellationToken, Task> checkRoute;
    private readonly Action<string> launch;
    public const string DownloadUrl = "https://github.com/chen08209/FlClash/releases/latest";

    public FlClashSetupService(Func<CancellationToken, Task> checkRoute,
        Func<FlClashInstallation>? discover = null, Action<string>? launch = null)
    {
        this.checkRoute = checkRoute;
        this.discover = discover ?? Discover;
        this.launch = launch ?? (path => { using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); });
    }

    public void OpenDownloadPage() => launch(DownloadUrl);

    public async Task<FlClashReadiness> InspectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var found = discover();
        if (found.Executable is null) return new(found, false, "需要先安装 FlClash。本工具只适配 FlClash。");
        if (!found.Running) return new(found, false, "已找到 FlClash，请先打开并载入自己的订阅。");
        try
        {
            await checkRoute(token);
            return new(found, true, "FlClash 已运行，主网络入口可用。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(found, false, "FlClash 已打开，网络入口尚未就绪。" + ex.Message); }
    }

    public void OpenInstalled(string path)
    {
        if (!IsFlClash(path)) throw new InvalidOperationException("请选择已安装或解压的 FlClash.exe。");
        launch(Path.GetFullPath(path));
    }

    internal static bool IsFlClash(string path)
    {
        try
        {
            return File.Exists(path) && Path.GetFileName(path).Equals("FlClash.exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(FileVersionInfo.GetVersionInfo(path).FileDescription, "FlClash", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static FlClashInstallation Discover()
    {
        foreach (var process in Process.GetProcessesByName("FlClash"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && IsFlClash(path)) return new(path, true);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FlClash", "FlClash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FlClash", "FlClash.exe")
        };
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var entry = uninstall!.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string display || !display.StartsWith("FlClash", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
                        candidates.Add(Path.Combine(location, "FlClash.exe"));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        }
        return new(candidates.FirstOrDefault(IsFlClash), false);
    }
}
