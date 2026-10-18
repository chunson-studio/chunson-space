using ChunsonSpace.Core;

internal static class BackupStreamingChecks
{
    public static int Run(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "profiles", "历史"));
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, "{\"version\":1}");
        var payload = Path.Combine(root, "profiles", "历史", "history.sqlite");
        using (var output = File.Create(payload))
        {
            var buffer = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
            for (var block = 0; block < 128; block++) output.Write(buffer);
            output.Write(buffer, 0, 17);
        }
        var files = new StreamingOnlyStorage();
        var service = new BackupService(root, files);
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); count++; }
        var backup = service.Create("streaming");
        Check(backup.Ok && backup.Bytes == new FileInfo(payload).Length + new FileInfo(settings).Length, "large backup copies without ReadAllBytes and reports byte count");
        Check(files.Hash(payload) == files.Hash(Path.Combine(backup.Path, "profiles", "历史", "history.sqlite")), "large backup hash preserves complete payload");

        File.WriteAllText(settings, "{\"version\":2}");
        var changed = false;
        files.BeforeCopy = (_, destination) =>
        {
            if (!changed && destination.StartsWith(Path.Combine(root, "backups"), StringComparison.OrdinalIgnoreCase) && !destination.StartsWith(backup.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                changed = true;
                File.WriteAllText(Path.Combine(backup.Path, "settings.json"), "{\"tampered\":true}");
            }
        };
        var restored = service.Restore(backup.Name);
        Check(restored.Ok && changed && File.ReadAllText(settings) == "{\"version\":1}", "restore uses frozen files when original backup changes after verification");
        Check(restored.StagingPath.Length == 0 && Directory.GetDirectories(Path.Combine(root, "xy_clean", "CodexX-01", "Boss")).Length == 0, "successful restoration removes only its staging directory");
        files.BeforeCopy = null;

        var stable = service.Create("stable");
        File.WriteAllText(settings, "{\"version\":3}");
        files.BeforeCopy = (source, destination) =>
        {
            if (destination.Contains("backup-restore-stage-", StringComparison.Ordinal) && source == Path.Combine(stable.Path, "settings.json"))
                File.WriteAllText(source, "{\"changedDuringStaging\":true}");
        };
        var unstable = service.Restore(stable.Name);
        Check(!unstable.Ok && unstable.Restored == 0 && unstable.PreBackup.Length == 0 && File.ReadAllText(settings) == "{\"version\":3}", "changed source during staging prevents all live writes");
        Check(unstable.StagingPath.Length > 0 && Directory.Exists(unstable.StagingPath), "staging failure returns its material path");
        files.BeforeCopy = null;

        stable = service.Create("partial");
        File.WriteAllText(settings, "{\"version\":4}");
        files.BeforeCopy = (_, destination) => { if (destination == settings) throw new IOException("fixture target locked"); };
        var partial = service.Restore(stable.Name);
        Check(!partial.Ok && partial.Restored > 0 && partial.PreBackup.Length > 0 && Directory.Exists(partial.StagingPath) && File.ReadAllText(settings) == "{\"version\":4}", "partial restore retains pre-backup and verified staging");
        files.BeforeCopy = null;
        files.FailStagingCleanup = true;
        var kept = service.Restore(stable.Name);
        Check(kept.Ok && kept.StagingPath.Length > 0 && Directory.Exists(kept.StagingPath), "successful restore reports failed staging cleanup without false rollback");
        return count;
    }

    private sealed class StreamingOnlyStorage : IFileStorage
    {
        private readonly PhysicalFileStorage files = new();
        public Action<string, string>? BeforeCopy { get; set; }
        public bool FailStagingCleanup { get; set; }
        public byte[] Read(string path) => files.Length(path) > 1048576 ? throw new InvalidOperationException("payload must not be buffered") : files.Read(path);
        public long Length(string path) => files.Length(path);
        public string Hash(string path) => files.Hash(path);
        public void CopyAtomic(string source, string destination) { BeforeCopy?.Invoke(source, destination); files.CopyAtomic(source, destination); }
        public bool FileExists(string path) => files.FileExists(path);
        public bool DirectoryExists(string path) => files.DirectoryExists(path);
        public void WriteAtomic(string path, byte[] content) => files.WriteAtomic(path, content);
        public void CreateDirectory(string path) => files.CreateDirectory(path);
        public IReadOnlyList<string> Files(string path, bool recursive) => files.Files(path, recursive);
        public IReadOnlyList<string> Directories(string path) => files.Directories(path);
        public void MoveDirectory(string source, string destination) => files.MoveDirectory(source, destination);
        public void DeleteFile(string path) => files.DeleteFile(path);
        public void DeleteDirectory(string path, bool recursive)
        {
            if (FailStagingCleanup && Path.GetFileName(path).StartsWith("backup-restore-stage-", StringComparison.Ordinal)) throw new IOException("fixture cleanup locked");
            files.DeleteDirectory(path, recursive);
        }
    }
}
