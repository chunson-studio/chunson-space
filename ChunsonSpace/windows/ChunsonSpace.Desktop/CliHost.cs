using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ChunsonSpace.Accounts;

namespace ChunsonSpace.Desktop;

internal static class CliHost
{
    public static async Task<int?> TryRunAsync(IReadOnlyList<string> args)
    {
        var workspaceArgs = new List<string>(); var cliArgs = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] == "--preview") workspaceArgs.Add(args[index]);
            else if (args[index] is "--fixture-root" or "--data-root" or "--active-home" or "--desktop-root" or "--packaged-desktop-root" or "--web-desktop-root")
            {
                workspaceArgs.Add(args[index]);
                if (index + 1 < args.Count) workspaceArgs.Add(args[++index]);
            }
            else cliArgs.Add(args[index]);
        }
        if (cliArgs.Count == 0) return null;
        PrepareConsole();
        if (cliArgs.Count == 2 && cliArgs[0] == "--inspect-login-proxy")
        {
            // Read-only operational inspection of the same resolver used by login.
            // Do not create an account workspace, browser, request, or firewall rule.
            var output = Path.GetFullPath(cliArgs[1]);
            if (File.Exists(output)) { await Console.Out.WriteLineAsync("报告已存在，请使用新的文件路径。"); return 2; }
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var resolver = AutomaticLoginProxy.ForFlClash(Path.Combine(roaming, "com.follow", "clash"),
                token => ChunsonSpace.Windows.LocalProxyListeners.ReadAsync(token));
            object report; int code;
            try { var selected = await resolver.ResolveAsync(default); report = new { resolved = true, selected.Source, selected.Proxy, networkVerified = false }; code = 0; }
            catch (Exception error) { report = new { resolved = false, message = ErrorRedaction.Scrub(error.Message), networkVerified = false }; code = 1; }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, System.Text.Json.JsonSerializer.Serialize(report));
            return code;
        }
        var isAgent = cliArgs.Count == 3 && cliArgs[0] == "--agent";
        var early = isAgent ? null : await AccountCli.TryRunWithoutServicesAsync(cliArgs, Console.Out, "ChunsonSwitch.exe");
        if (early is not null) return early;
        DesktopWorkspace? workspace = null;
        try
        {
            workspace = await DesktopWorkspace.FromArgumentsAsync(workspaceArgs);
            if (workspace?.Accounts is null) { await Console.Out.WriteLineAsync("CLI需要真实工作区或明确隔离工作区；--preview只用于界面演示。"); return 2; }
            if (isAgent)
            {
                using var cancel = new CancellationTokenSource();
                ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
                Console.CancelKeyPress += handler;
                try { return await AgentCli.RunAsync(workspace, cliArgs[1], cliArgs[2], cancel.Token); }
                finally { Console.CancelKeyPress -= handler; }
            }
            if (cliArgs.Contains("--launch") && workspace.Ownership is not null)
            {
                var adopted = await workspace.Ownership.AdoptAsync();
                if (!adopted.Success) { await Console.Out.WriteLineAsync("进程所有权记录未确认，浏览器未启动。"); return 4; }
            }
            var services = workspace.Accounts;
            var command = new AccountCli(services.Repository, services.Login, services.Egress, Console.Out, programName: "ChunsonSwitch.exe");
            return await command.TryRunAsync(cliArgs);
        }
        catch (Exception exception)
        {
            await Console.Out.WriteLineAsync("命令未完成：" + (exception is AccountException or ChunsonSpace.Core.StorageException ? ErrorRedaction.Scrub(exception.Message) : exception.GetType().Name));
            return 4;
        }
        finally
        {
            if (workspace?.Accounts is not null)
            {
                try { await workspace.Accounts.DisposeAsync(); }
                catch (Exception error) { await Console.Error.WriteLineAsync("授权会话清理未确认：" + (error is AccountException ? ErrorRedaction.Scrub(error.Message) : error.GetType().Name)); }
            }
            await Console.Out.FlushAsync();
        }
    }
    private static void PrepareConsole()
    {
        var output = GetStdHandle(-11);
        if (output == IntPtr.Zero || output == new IntPtr(-1)) AttachConsole(uint.MaxValue);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint processId);
}
