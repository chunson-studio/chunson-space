using FAswitch.Windows;

internal static class WindowIconChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunsonSwitch-IconChecks", Guid.NewGuid().ToString("N"));
        var resources = Path.Combine(root, "resources"); Directory.CreateDirectory(resources);
        var icon = Path.Combine(resources, "icon-chatgpt.ico"); File.WriteAllText(icon, "fixture only");
        var manager = Path.Combine(root, "ChunsonSwitch.exe"); File.WriteAllText(manager, "inert fixture");
        try
        {
            var main = new ProcessIdentity(10, 1, "ChatGPT.exe", Path.Combine(root, "ChatGPT.exe"), 100);
            var clone = main with { Pid = 20, CreationTime = 200 };
            var native = new FakeWindows(); var service = new ManagedWindowIcon(native);
            check(service.Apply([main, clone], pid => pid == 20, manager) == 1 && native.Sets.All(set => set.Window == 22) && native.Sets.Count == 2, "managed icon updates both icon sizes only for an owned clone window");
            check(native.Taskbar.Count == 1 && native.Taskbar[0].Window == 22 && native.Taskbar[0].Identity.IconResource == icon + ",0"
                && native.Taskbar[0].Identity.AppId == "Chunson.Space.Codex.Managed" && native.Taskbar[0].Identity.RelaunchCommand == "\"" + manager + "\"",
                "owned taskbar gets its own application identity, installed icon and safe manager-only relaunch");
            check(native.Loaded.All(path => path == icon), "clone icon reuses the installed application icon resource");
            var writes = native.Sets.Count;
            check(service.Apply([main, clone], _ => false, manager) == 0 && native.Sets.Count == writes && native.Taskbar.Count == 1, "missing or reused process ownership never modifies a host window icon or taskbar properties");
            File.Delete(icon);
            check(service.Apply([main, clone], pid => pid == 20) == 0 && native.Sets.Count == writes, "absent optional icon resource cannot block launch or modify another window");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class FakeWindows : IWindowIconSystem
    {
        public List<(nint Window, bool Small)> Sets { get; } = [];
        public List<string> Loaded { get; } = [];
        public List<(nint Window, WindowTaskbarIdentity Identity)> Taskbar { get; } = [];
        public IReadOnlyList<IconWindow> VisibleWindows() => [new(11, 10), new(22, 20)];
        public nint LoadIcon(string path, int size) { Loaded.Add(path); return size; }
        public bool SetIcon(nint window, nint icon, bool small) { Sets.Add((window, small)); return true; }
        public bool SetTaskbarIdentity(nint window, WindowTaskbarIdentity identity) { Taskbar.Add((window, identity)); return true; }
    }
}
