using System.Diagnostics;

namespace FAswitch.Proxy;

public enum ProxyClientState { Stopped, Running, Unknown }
public interface IClashVergeState { ProxyClientState Read(); }
public sealed class WindowsClashVergeState : IClashVergeState
{
    public ProxyClientState Read()
    {
        try
        {
            foreach (var name in new[] { "clash-verge", "clash-verge-rev" })
            {
                var processes = Process.GetProcessesByName(name);
                try { if (processes.Length > 0) return ProxyClientState.Running; }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            return ProxyClientState.Stopped;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { return ProxyClientState.Unknown; }
    }
}
