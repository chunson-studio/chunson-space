using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ChunsonSpace.Desktop;

internal static class AppUpdateInstaller
{
    public static async Task<string> StartAsync(PreparedUpdate update, string installRoot, CancellationToken token)
    {
        using var manager = Process.GetCurrentProcess();
        var entry = Path.Combine(Path.GetFullPath(installRoot), "ChunsonSwitch.exe");
        if (!string.Equals(manager.MainModule?.FileName, entry, StringComparison.OrdinalIgnoreCase)) throw new IOException("请从 ChunsonSwitch.exe 正常入口运行后更新。");
        var work = Path.GetDirectoryName(update.Directory)!;
        var helper = Path.Combine(work, "Install-AppUpdate.ps1");
        using (var resource = typeof(AppUpdateInstaller).Assembly.GetManifestResourceStream("ChunsonSpace.Install-AppUpdate.ps1")!)
        await using (var file = new FileStream(helper, FileMode.CreateNew)) await resource.CopyToAsync(file, token);
        var planPath = Path.Combine(work, "plan.json");
        await using (var file = new FileStream(planPath, FileMode.CreateNew))
            await JsonSerializer.SerializeAsync(file, new { schema = 1, installRoot = Path.GetFullPath(installRoot), stage = update.Directory, managerPid = manager.Id, managerPath = entry, managerCreatedUtcTicks = manager.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), waitSeconds = 45, files = update.Files }, UpdateRules.Json, token);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper, "-PlanPath", planPath }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新帮助程序。");
        for (var i = 0; i < 100; i++)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(work, "ready"))) return Path.Combine(work, "commit");
            if (process.HasExited) throw new IOException("更新帮助程序未确认身份，请查看暂存目录报告。");
            await Task.Delay(100, token);
        }
        throw new IOException("更新帮助程序准备超时；当前程序尚未退出。");
    }
}
