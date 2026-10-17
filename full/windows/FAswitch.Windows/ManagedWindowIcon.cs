using System.Runtime.InteropServices;

namespace FAswitch.Windows;

public sealed record IconWindow(nint Handle, int Pid);
public interface IWindowIconSystem
{
    IReadOnlyList<IconWindow> VisibleWindows();
    nint LoadIcon(string path, int size);
    bool SetIcon(nint window, nint icon, bool small);
    bool SetTaskbarIdentity(nint window, WindowTaskbarIdentity identity);
}

public sealed class ManagedWindowIcon(IWindowIconSystem? windows = null)
{
    public int Apply(IReadOnlyList<ProcessIdentity> snapshot, Func<int, bool> ownsPid, string? managerExecutable = null)
    {
        var native = windows ?? new NativeWindowIconSystem(); int applied = 0;
        foreach (var window in native.VisibleWindows())
        {
            if (!ownsPid(window.Pid)) continue;
            var executable = snapshot.FirstOrDefault(process => process.Pid == window.Pid)?.Executable;
            if (string.IsNullOrEmpty(executable)) continue;
            var resources = Path.Combine(Path.GetDirectoryName(executable)!, "resources");
            var path = new[] { "icon-chatgpt.ico", "chatgpt-app-light.ico", "chatgpt-app-dark.ico" }.Select(name => Path.Combine(resources, name)).FirstOrDefault(File.Exists);
            if (path is null) continue;
            var manager = managerExecutable ?? Path.Combine(AppContext.BaseDirectory, "ChunsonSwitch.exe");
            if (File.Exists(manager)) native.SetTaskbarIdentity(window.Handle, new("Chunson.Space.Codex.Managed", path + ",0", WindowsDesktopLaunchSystem.JoinArguments([manager]), "Codex 分身"));
            var large = native.LoadIcon(path, 32); var small = native.LoadIcon(path, 16);
            if (large != 0 && small != 0 && native.SetIcon(window.Handle, large, false) && native.SetIcon(window.Handle, small, true)) applied++;
        }
        return applied;
    }
}

internal sealed class NativeWindowIconSystem : IWindowIconSystem
{
    // HICON must remain alive while an owned window uses it; Windows frees these when the manager exits.
    private static readonly Dictionary<(string Path, int Size), nint> icons = [];
    public IReadOnlyList<IconWindow> VisibleWindows()
    {
        var windows = new List<IconWindow>();
        EnumWindows((handle, _) => { if (IsWindowVisible(handle)) { GetWindowThreadProcessId(handle, out var pid); windows.Add(new(handle, (int)pid)); } return true; }, 0);
        return windows;
    }
    public nint LoadIcon(string path, int size)
    {
        lock (icons)
        {
            if (!icons.TryGetValue((path, size), out var icon))
            { icon = LoadImageW(0, path, 1, size, size, 0x10); if (icon != 0) icons[(path, size)] = icon; }
            return icon;
        }
    }
    public bool SetIcon(nint window, nint icon, bool small)
    {
        var kind = small ? (nuint)0 : 1;
        if (SendMessageTimeoutW(window, 0x80, kind, icon, 2, 300, out _) == 0) return false;
        return SendMessageTimeoutW(window, 0x7f, kind, 0, 2, 300, out var current) != 0 && current == (nuint)icon;
    }
    public bool SetTaskbarIdentity(nint window, WindowTaskbarIdentity identity) => new NativeWindowTaskbarProperties().Apply(window, identity);
    private delegate bool WindowCallback(nint handle, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint window, uint message, nuint word, nint value, uint flags, uint timeout, out nuint result);
}
