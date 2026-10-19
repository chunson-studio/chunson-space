using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
namespace Chunson.Widgets;
public static class ClockLauncher
{
    public static string InstalledExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Chunson", "BeijingClock", "BeijingClock.exe");
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? type, string title);
    public static IEnumerable<string> CandidatePaths(string? applicationDirectory = null)
    {
        yield return InstalledExecutable;
        var root = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        yield return Path.Combine(root, "Widgets", "BeijingClock.exe");
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        {
            if (directory.Name.Equals("ChunsonSpace", StringComparison.OrdinalIgnoreCase) && directory.Parent?.Name.Equals("tools", StringComparison.OrdinalIgnoreCase) == true)
            {
                yield return Path.Combine(directory.Parent.FullName, "taskbar-clock", "BeijingClock.exe");
                yield return Path.Combine(directory.FullName, "tools", "taskbar-clock", "BeijingClock.exe");
            }
        }
    }
    public static void Start(string? applicationDirectory = null, Func<string, bool>? exists = null, Action<ProcessStartInfo>? launch = null)
    {
        // Injected fixture calls never inspect or launch a real widget.
        if (launch is null)
        {
            if (FindWindow(null, "Chun Beijing Taskbar Clock") != 0) return;
            if (Mutex.TryOpenExisting("Local\\Chunson.BeijingClock", out var running)) { running.Dispose(); return; }
        }
        var executable = CandidatePaths(applicationDirectory).FirstOrDefault(exists ?? File.Exists)
            ?? throw new FileNotFoundException("北京时间组件未安装。请安装小组件或保留 Widgets 文件夹。");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        if (launch is not null) launch(info);
        else { using var process = Process.Start(info) ?? throw new IOException("北京时间组件未能启动。"); }
    }
    public interface IClockProcess : IDisposable
    {
        (string Path, long Created) OpenIdentity();
        void Stop();
    }
    private sealed class ClockProcess(Process process) : IClockProcess
    {
        public (string Path, long Created) OpenIdentity()
        { _ = process.SafeHandle; return (process.MainModule?.FileName ?? "", process.StartTime.ToUniversalTime().ToFileTimeUtc()); }
        public void Stop() => process.Kill();
        public void Dispose() => process.Dispose();
    }
    public static void StopManaged(string? applicationDirectory = null, IEnumerable<IClockProcess>? candidates = null)
    {
        var paths = CandidatePaths(applicationDirectory).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var process in candidates ?? Process.GetProcesses().Select(p => (IClockProcess)new ClockProcess(p)))
        using (process)
        {
            try
            {
                // Retain the handle before identity checks; Kill uses that same handle.
                var identity = process.OpenIdentity();
                if (identity.Created <= 0 || identity.Path.Length == 0 || !paths.Contains(Path.GetFullPath(identity.Path))) continue;
                process.Stop();
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
    }
}
