using System.Text.Json;
using ChunsonSpace.Windows;

internal static class ManagedLaunchRecoveryChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunsonSwitch-LaunchRecoveryChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = new ProcessIdentity(101, 1, "Codex.exe", @"C:\OpenAI\Codex\Codex.exe", 100, true);
            var clone = original with { Pid = 102, CreationTime = 200 };
            var child = new ProcessIdentity(103, clone.Pid, "codex.exe", @"C:\OpenAI\Codex\bin\codex.exe", 201);
            var system = new FakeProcessSystem([original]);
            var store = new FakeOwnedStore();
            var scope = new ManagedCloneProcessScope(system, store);
            var stateRoot = Path.Combine(root, "launch-state");
            var options = new ManagedCloneLaunchOptions(Path.Combine(root, "home"), Path.Combine(root, "electron"), scope);
            WindowsDesktopLaunchSystem Create() => new(stateRoot, system, new FakeCommands(), new FakeClock(), options);
            var recovery = Path.Combine(root, "credentials-snapshot.json");
            File.WriteAllText(recovery, "fixture recovery material");
            string WriteReceipt(string status = "started", long? created = null, string? executable = null)
            {
                var nonce = Guid.NewGuid().ToString("N"); var directory = Path.Combine(stateRoot, nonce);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "state.json"), JsonSerializer.Serialize(new
                {
                    nonce, status, pid = clone.Pid, creation_time = created ?? clone.CreationTime,
                    executable = executable ?? clone.Executable,
                    deadline_ms = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds()
                }));
                return directory;
            }
            void SetRecords(params ProcessIdentity[] identities) => store.Value = identities.Select(identity =>
                new OwnedProcessRecord(identity, identity.Pid == clone.Pid ? OwnedProcessKind.Desktop : OwnedProcessKind.DesktopChild, "fixture", "")).ToArray();

            SetRecords(clone); var directory = WriteReceipt(); var service = Create();
            check(await service.CheckPendingAsync(default) is null && !Directory.Exists(directory), "exited started clone settles its receipt instead of blocking retries forever");
            check(await service.CheckPendingAsync(default) is null && await Create().CheckPendingAsync(default) is null,
                "settled failed startup stays clear on repeat check and manager restart");
            check(new CodexProcessService(system, new FakeClock(), scope).ConfirmStopped(ProcessScope.DesktopAndCli).Success
                && system.StopCalls.Count == 0 && system.Items.SequenceEqual([original]) && File.ReadAllText(recovery) == "fixture recovery material",
                "retry and rebuild stop guard can proceed while original process and credential recovery remain untouched");
            SetRecords(); directory = WriteReceipt();
            check(await Create().CheckPendingAsync(default) is null && !Directory.Exists(directory), "root exiting before ownership registration still settles from the exact started receipt");

            foreach (var live in new[] { clone, clone with { CreationTime = 0 }, clone with { Executable = "" } })
            {
                system.Items.Add(live); SetRecords(clone); directory = WriteReceipt();
                check((await Create().CheckPendingAsync(default))?.Status == LaunchStatus.Pending && Directory.Exists(directory),
                    "live or unreadable root retains started receipt " + live.CreationTime + "/" + live.Executable.Length);
                system.Items.Remove(live);
                check(await Create().CheckPendingAsync(default) is null, "root receipt settles once its exit is known");
            }
            var replacement = clone with { CreationTime = 900, Executable = @"C:\Unrelated\app.exe" };
            system.Items.Add(replacement); SetRecords(clone); directory = WriteReceipt();
            check(await Create().CheckPendingAsync(default) is null && system.Items.Contains(replacement) && system.StopCalls.Count == 0,
                "known PID reuse does not retain or stop the unrelated replacement process");
            system.Items.Remove(replacement);

            foreach (var descendant in new[] { child, child with { CreationTime = 0 }, child with { Executable = "" } })
            {
                system.Items.Add(descendant); SetRecords(clone, child); directory = WriteReceipt();
                check((await Create().CheckPendingAsync(default))?.Status == LaunchStatus.Pending && Directory.Exists(directory),
                    "live or unreadable owned descendant blocks startup settlement " + descendant.CreationTime + "/" + descendant.Executable.Length);
                system.Items.Remove(descendant);
                check(await Create().CheckPendingAsync(default) is null, "started receipt settles after owned descendants exit");
            }
            var grandchild = child with { Pid = 104, ParentPid = child.Pid, CreationTime = 202 };
            system.Items.Add(grandchild); SetRecords(clone, child, grandchild); directory = WriteReceipt();
            check((await Create().CheckPendingAsync(default))?.Status == LaunchStatus.Pending,
                "recorded grandchild survives root and immediate parent without losing startup protection");
            system.Items.Remove(grandchild); check(await Create().CheckPendingAsync(default) is null, "grandchild exit releases startup protection");
            foreach (var orphan in new[] { child, child with { CreationTime = 0 }, child with { Executable = "" } })
            {
                system.Items.Add(orphan); SetRecords(); directory = WriteReceipt();
                check((await Create().CheckPendingAsync(default))?.Status == LaunchStatus.Pending && Directory.Exists(directory),
                    "unregistered possible descendant retains root receipt " + orphan.CreationTime + "/" + orphan.Executable.Length);
                system.Items.Remove(orphan); check(await Create().CheckPendingAsync(default) is null, "unregistered descendant exit releases receipt");
            }

            SetRecords(clone with { CreationTime = 0 }); directory = WriteReceipt();
            check((await Create().CheckPendingAsync(default))?.Status == LaunchStatus.Pending && Directory.Exists(directory), "incomplete owned identity blocks startup settlement");
            SetRecords(clone); check(await Create().CheckPendingAsync(default) is null, "valid restored ownership allows startup settlement");
            directory = WriteReceipt(); system.ThrowSnapshot = true;
            check((await Create().CheckPendingAsync(default))?.Status == LaunchStatus.Pending && Directory.Exists(directory), "unreadable system snapshot never proves root exit");
            system.ThrowSnapshot = false; check(await Create().CheckPendingAsync(default) is null, "readable process snapshot releases failed startup");
            directory = WriteReceipt(); store.ThrowRead = true;
            check((await Create().CheckPendingAsync(default))?.Status == LaunchStatus.Pending && Directory.Exists(directory), "unreadable owned records never prove descendant exit");
            store.ThrowRead = false; check(await Create().CheckPendingAsync(default) is null, "readable owned records release failed startup");

            foreach (var status in new[] { "pending", "launching" })
            {
                directory = WriteReceipt(status);
                check((await Create().CheckPendingAsync(default))?.Status == LaunchStatus.Pending && Directory.Exists(directory), "uncompleted dispatch remains protected " + status);
                Directory.Delete(directory, true);
            }
            foreach (var incomplete in new[] { (Created: 0L, Executable: clone.Executable), (Created: clone.CreationTime, Executable: "") })
            {
                directory = WriteReceipt(created: incomplete.Created, executable: incomplete.Executable);
                check((await Create().CheckPendingAsync(default))?.Status == LaunchStatus.Pending && Directory.Exists(directory), "incomplete started identity cannot be auto-settled");
                Directory.Delete(directory, true);
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
