using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FAswitch.Windows;

public sealed class WindowsProcessSystem : IProcessSystem
{
    private string[] registeredExecutables = [];
    // Appx manifest discovery supplies the exact resolved path for relocated WindowsApps installations.
    public void RegisterDesktopExecutable(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || !new[] { "Codex.exe", "ChatGPT.exe" }.Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("无效桌面主程序路径", nameof(executable));
        registeredExecutables = registeredExecutables.Append(executable).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public IReadOnlyList<ProcessIdentity> Snapshot()
    {
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new NativeMethods.ProcessEntry { Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry>(), Name = "" };
        if (!NativeMethods.Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new List<ProcessIdentity>();
        do
        {
            using var handle = NativeMethods.OpenProcess(0x1000, false, entry.Pid);
            string exe = ""; long created = 0;
            if (!handle.IsInvalid)
            {
                var path = new StringBuilder(32768); uint size = (uint)path.Capacity;
                if (NativeMethods.QueryFullProcessImageNameW(handle, 0, path, ref size)) exe = path.ToString();
                if (NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _)) created = creation.Value;
            }
            result.Add(new((int)entry.Pid, (int)entry.ParentPid, entry.Name, exe, created, registeredExecutables.Contains(exe, StringComparer.OrdinalIgnoreCase)));
        } while (NativeMethods.Process32NextW(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error(), "进程快照不完整");
        return result;
    }

    public bool TryStop(ProcessIdentity process, bool force)
    {
        if (process.CreationTime <= 0 || string.IsNullOrWhiteSpace(process.Executable)) return false;
        using var handle = NativeMethods.OpenProcess(0x1000u | (force ? 1u : 0u), false, (uint)process.Pid);
        if (handle.IsInvalid || !NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _) || creation.Value != process.CreationTime) return false;
        var path = new StringBuilder(32768); uint size = (uint)path.Capacity;
        if (!NativeMethods.QueryFullProcessImageNameW(handle, 0, path, ref size) || !path.ToString().Equals(process.Executable, StringComparison.OrdinalIgnoreCase)) return false;
        if (force) return NativeMethods.TerminateProcess(handle, 1);
        bool sent = false;
        if (!NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out var pid);
            if (pid == process.Pid) sent |= NativeMethods.PostMessageW(window, 0x0010, 0, 0);
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return sent;
    }

    public bool HasDesktopWindow(IReadOnlyList<ProcessIdentity> desktopProcesses)
    {
        var pids = desktopProcesses.Where(CodexProcessService.IsDesktop).Select(p => p.Pid).ToHashSet();
        bool found = false;
        if (!NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out var pid);
            if (pids.Contains((int)pid) && (NativeMethods.IsWindowVisible(window) || NativeMethods.GetWindowTextLengthW(window) > 0)) found = true;
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return found;
    }
}
