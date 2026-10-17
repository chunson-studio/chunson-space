using System.Diagnostics;
using System.IO;

namespace Chunson.Widgets;

internal static class ClockLauncherChecks
{
    public static void Run(Action<bool, string> check)
    {
        ProcessStartInfo? captured = null; var directory = Path.Combine(Path.GetTempPath(), "widget-fixture");
        ClockLauncher.Start(directory, _ => true, info => captured = info);
        check(captured?.FileName == Path.Combine(directory, "Widgets", "BeijingClock.exe"), "clock: launches only bundled helper");
        check(captured is { UseShellExecute: false, CreateNoWindow: true } && captured.ArgumentList.Count == 0, "clock: launch has no shell, account or system-setting arguments");
        var called = false;
        try { ClockLauncher.Start(directory, _ => false, _ => called = true); check(false, "clock: missing helper rejected"); }
        catch (FileNotFoundException) { check(!called, "clock: missing helper cannot trigger a fallback launch"); }
    }
}
