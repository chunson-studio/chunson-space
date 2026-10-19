using System.Diagnostics;
using System.IO;

namespace Chunson.Widgets;

internal static class ClockLauncherChecks
{
    public static void Run(Action<bool, string> check)
    {
        ProcessStartInfo? captured = null; var directory = Path.Combine(Path.GetTempPath(), "widget-fixture");
        ClockLauncher.Start(directory, _ => true, info => captured = info);
        check(captured?.FileName == ClockLauncher.InstalledExecutable, "clock: prefers exact installed helper");
        ClockLauncher.Start(directory, path => path == Path.Combine(directory, "Widgets", "BeijingClock.exe"), info => captured = info);
        check(captured?.FileName == Path.Combine(directory, "Widgets", "BeijingClock.exe"), "clock: bundled fallback uses exact helper");
        check(captured is { UseShellExecute: false, CreateNoWindow: true } && captured.ArgumentList.Count == 0, "clock: launch has no shell, account or system-setting arguments");
        var called = false;
        try { ClockLauncher.Start(directory, _ => false, _ => called = true); check(false, "clock: missing helper rejected"); }
        catch (FileNotFoundException) { check(!called, "clock: missing helper cannot trigger a fallback launch"); }
        var owned = new FakeClock(ClockLauncher.InstalledExecutable, 100);
        var unrelated = new FakeClock(Path.Combine(directory, "other", "BeijingClock.exe"), 100);
        var unknown = new FakeClock(ClockLauncher.InstalledExecutable, 0);
        ClockLauncher.StopManaged(directory, [owned, unrelated, unknown]);
        check(owned.Stopped && !unrelated.Stopped && !unknown.Stopped, "clock: same held identity stops only exact managed path");
    }
    private sealed class FakeClock(string path, long created) : ClockLauncher.IClockProcess
    { public bool Stopped; private bool opened; public (string, long) OpenIdentity() { opened = true; return (path, created); } public void Stop() { if (!opened) throw new InvalidOperationException(); Stopped = true; } public void Dispose() { } }
}
