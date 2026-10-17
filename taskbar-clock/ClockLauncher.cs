using System.Diagnostics;
using System.IO;

namespace Chunson.Widgets;

public static class ClockLauncher
{
    public static void Start(string? applicationDirectory = null, Func<string, bool>? exists = null, Action<ProcessStartInfo>? launch = null)
    {
        var executable = Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, "Widgets", "BeijingClock.exe");
        if (!(exists ?? File.Exists)(executable)) throw new FileNotFoundException("北京时间组件缺失，请保留完整软件文件夹。", executable);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        if (launch is not null) launch(info);
        else { using var process = Process.Start(info) ?? throw new IOException("北京时间组件未能启动。"); }
    }
}
