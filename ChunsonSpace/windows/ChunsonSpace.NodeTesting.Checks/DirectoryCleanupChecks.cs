using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using ChunsonSpace.NodeTesting;

static class DirectoryCleanupChecks
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "job-delayed-directory-release");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, WindowsMihomoProbeRuntime.JobMarker),
            new JsonObject { ["kind"] = "chunson-node-probe", ["root"] = root, ["job"] = directory }.ToJsonString());
        File.WriteAllText(Path.Combine(directory, "config.yaml"), "synthetic-only");
        using var held = CreateFileW(directory, 0, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (held.IsInvalid) throw new IOException("Could not create synthetic working-directory hold.");
        var release = Task.Run(async () => { await Task.Delay(350); held.Dispose(); });
        try { WindowsMihomoProbeRuntime.Cleanup(root, directory); }
        finally { await release; }
        check(!Directory.Exists(directory), "terminated-core directory hold is retried after files are cleared");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
