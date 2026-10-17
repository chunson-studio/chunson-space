using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Chunson.Setup;

internal sealed record FlClashInstallation(string? Executable, bool Running);
internal sealed record FlClashReadiness(FlClashInstallation Installation, bool Ready, string Message);
internal sealed record FlClashInstaller(string Url, string Sha256, long Bytes)
{
    public static readonly FlClashInstaller Official = new(
        "https://github.com/chen08209/FlClash/releases/download/v0.9.0/FlClash-0.9.0-windows-amd64-setup.exe",
        "edf27cc9ce9a36f63ae94551006763209ebae89ea9811dced281ddbcd6f3395b", 42807184);
}

internal sealed class FlClashSetupService
{
    private readonly Func<FlClashInstallation> discover;
    private readonly Func<CancellationToken, Task> checkRoute;
    private readonly Action<string> launch;
    private readonly Func<HttpClient> createClient;
    private readonly FlClashInstaller installer;
    private readonly string downloadRoot;

    public FlClashSetupService(Func<CancellationToken, Task> checkRoute,
        Func<FlClashInstallation>? discover = null, Action<string>? launch = null,
        Func<HttpClient>? createClient = null, FlClashInstaller? installer = null, string? downloadRoot = null)
    {
        this.checkRoute = checkRoute;
        this.discover = discover ?? Discover;
        this.launch = launch ?? (path => { using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); });
        this.createClient = createClient ?? (() => new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
        this.installer = installer ?? FlClashInstaller.Official;
        this.downloadRoot = downloadRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChunsonSetup", "downloads");
    }

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

    public async Task InstallAsync(IProgress<int>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        token = deadline.Token;
        token.ThrowIfCancellationRequested();
        // An existing installation is reused, never overwritten by this setup flow.
        if (discover().Executable is not null) throw new InvalidOperationException("已检测到 FlClash，请打开现有版本。");
        Directory.CreateDirectory(downloadRoot);
        var folder = Path.Combine(downloadRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "FlClash-setup.exe");
        var launched = false;
        try
        {
            using var client = createClient();
            using var response = await client.GetAsync(installer.Url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != installer.Bytes)
                throw new IOException("安装包大小不符，请重试或从 FlClash 官方发布页安装。");
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long written = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    written += count;
                    if (written > installer.Bytes) throw new IOException("安装包大小不符，已停止下载。");
                    await destination.WriteAsync(buffer.AsMemory(0, count), token);
                    progress?.Report((int)(written * 100 / installer.Bytes));
                }
                if (written != installer.Bytes) throw new IOException("安装包下载不完整，请重试。");
            }
            await using (var saved = File.OpenRead(path))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(saved, token));
                if (!digest.Equals(installer.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("安装包校验失败，未运行文件。请重试或从官方发布页安装。");
                token.ThrowIfCancellationRequested();
                // Keep the verified file read-locked through process creation.
                launch(path);
                launched = true;
            }
        }
        finally
        {
            if (!launched)
            {
                try { File.Delete(path); Directory.Delete(folder); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
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
