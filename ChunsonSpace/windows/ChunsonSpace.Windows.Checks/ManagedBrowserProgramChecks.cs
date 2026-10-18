using ChunsonSpace.Windows;

internal static class ManagedBrowserProgramChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunsonSwitch-BrowserProgramChecks", Guid.NewGuid().ToString("N"));
        var application = Path.Combine(root, "Chrome", "Application"); Directory.CreateDirectory(application);
        var source = Path.Combine(application, "chrome.exe"); File.WriteAllText(source, "fake-chrome-program-v1");
        var version = Path.Combine(application, "1.2.3.4"); Directory.CreateDirectory(version);
        File.WriteAllText(Path.Combine(version, "chrome.dll"), "fake-program-library");
        File.WriteAllText(Path.Combine(version, "chrome_pwa_launcher.exe"), "fake-program-child");
        var userData = Path.Combine(root, "Chrome", "User Data"); Directory.CreateDirectory(userData); File.WriteAllText(Path.Combine(userData, "personal-profile.txt"), "must-not-copy");
        var target = Path.Combine(root, "managed-browser");
        var processSystem = new FakeProcessSystem([]);
        var installer = new ManagedChromeInstallation(target, processSystem, source);
        try
        {
            check(installer.Preview().Status == ManagedBrowserProgramStatus.Missing && !Directory.Exists(target), "managed browser preview does not copy programs or profile data");
            var state = await installer.PrepareAsync();
            var physicalTarget = WindowsDesktopLaunchSystem.ResolveRealPath(target);
            check(state.Ready && state.Executable == Path.Combine(physicalTarget, "chrome.exe") && state.ProgramPaths.Count == 2, "explicit browser prepare copies isolated program and child executable at real disk path");
            check(state.Executable == WindowsDesktopLaunchSystem.ResolveRealPath(Path.Combine(target, "chrome.exe")) && state.ProgramPaths.All(path => path.StartsWith(physicalTarget + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "browser launch and firewall paths use the same physical installation after package redirection");
            check(File.Exists(Path.Combine(target, "1.2.3.4", "chrome.dll")) && !File.Exists(Path.Combine(target, "User Data", "personal-profile.txt")), "browser program dependencies copy without user profile data");
            check(installer.FindPreparedChrome() == state.Executable, "prepared browser finder selects only independent executable");
            File.WriteAllText(source, "fake-chrome-program-v2");
            check(installer.Preview().Status == ManagedBrowserProgramStatus.Stale && installer.FindPreparedChrome() is null, "installed browser update makes old program protection stale");
            processSystem.Items.Add(new(42, 1, "chrome.exe", state.Executable, 100));
            check(!(await installer.PrepareAsync()).Ready, "running managed browser prevents replacing its program directory");
            processSystem.Items.Clear();
            check((await installer.PrepareAsync()).Ready, "explicit reprepare refreshes stale independent browser");
            var newVersion = Path.Combine(application, "2.3.4.5"); Directory.CreateDirectory(newVersion);
            check(installer.Preview().Status == ManagedBrowserProgramStatus.Stale, "changed source version directory is detected even when root executable is unchanged");
            Directory.Delete(newVersion);
            File.WriteAllText(Path.Combine(target, "chrome.exe"), "tampered-program");
            check(installer.Preview().Status == ManagedBrowserProgramStatus.Stale, "changed managed executable cannot silently become ready");
        }
        finally { Directory.Delete(root, true); }
    }
}
