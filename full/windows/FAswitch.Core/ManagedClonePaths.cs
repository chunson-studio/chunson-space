namespace FAswitch.Core;

public sealed class ManagedClonePaths
{
    public string DataRoot { get; }
    public string AccountRoot => Path.Combine(DataRoot, "accounts");
    public string RuntimeRoot => Path.Combine(DataRoot, "runtime");
    public string CodexHome => Path.Combine(RuntimeRoot, "codex-home");
    public string DesktopRoot => Path.Combine(RuntimeRoot, "electron");
    public string LaunchStateRoot => Path.Combine(RuntimeRoot, "launch-state");
    public string RecoveryRoot => Path.Combine(DataRoot, "recovery");
    public SwitchStoragePaths StoragePaths => new(AccountRoot, CodexHome, DesktopRoot);
    public ManagedClonePaths(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(dataRoot) || Path.GetPathRoot(DataRoot)?.TrimEnd(Path.DirectorySeparatorChar) == DataRoot)
            throw new StorageException(StorageError.InvalidValue, dataRoot, "受管数据根不能是磁盘根目录。");
        PhysicalFileStorage.RejectLinks(DataRoot);
    }
}
