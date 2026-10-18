namespace ChunsonSpace.Core;

public sealed record SwitchStoragePaths(string Root, string ActiveHome, string? PackagedDesktopRoot = null, string? WebDesktopRoot = null);
public sealed record SwitchSnapshot(string Path, SwitchStoragePaths Paths);
public sealed record SwitchApplyResult(IdentityRef ActiveIdentity, IReadOnlyList<FileIssue> Warnings, IReadOnlyList<string> RecoveryPaths);
public sealed record SwitchCompleteResult(bool SnapshotKept, IReadOnlyList<FileIssue> Warnings, IReadOnlyList<string>? DeletedPaths = null);

public interface ISwitchStorage
{
    IdentityRef? ReadActiveIdentity();
    void Preflight(IdentityRef target, bool isolated);
    SwitchSnapshot Snapshot(string action, bool isolated);
    bool ArchiveCurrent(SwitchSnapshot snapshot, IdentityRef? current, bool isolated);
    SwitchApplyResult ApplyTarget(IdentityRef target, IdentityRef? current, bool isolated);
    void Restore(SwitchSnapshot snapshot, bool isolated);
    SwitchCompleteResult Complete(SwitchSnapshot snapshot, bool archived, IReadOnlyList<string>? recoveryPaths = null);
}

public static class SwitchFileRules
{
    public const string PackagedDesktopLayer = "electron";
    public const string WebDesktopLayer = "electron-web-state";
    public static IReadOnlyList<string> StateFiles { get; } = Array.AsReadOnly(new[] { "auth.json", ".codex-global-state.json", ".codex-global-state.json.bak" });
    public static IReadOnlyList<string> IdentityFiles { get; } = Array.AsReadOnly(new[] { "installation_id" });
    public static IReadOnlyList<string> CleanExtraFiles { get; } = Array.AsReadOnly(new[] { "logs_2.sqlite", "logs_2.sqlite-shm", "logs_2.sqlite-wal", "sqlite/codex-dev.db", "sqlite/codex-dev.db-shm", "sqlite/codex-dev.db-wal" });
    public static IReadOnlyList<string> SessionDirectories { get; } = Array.AsReadOnly(new[] { "sessions", "archived_sessions", "memories", "memories_v2", "memories_extensions", "automations" });
    public static IReadOnlyList<string> SessionFiles { get; } = Array.AsReadOnly(new[] { "thread_history_1.sqlite", "thread_history_1.sqlite-shm", "thread_history_1.sqlite-wal", "state_5.sqlite", "state_5.sqlite-shm", "state_5.sqlite-wal", "session_index.jsonl", "sqlite/codex-dev.db", "sqlite/codex-dev.db-shm", "sqlite/codex-dev.db-wal", "memories_1.sqlite", "memories_1.sqlite-shm", "memories_1.sqlite-wal", "goals_1.sqlite", "goals_1.sqlite-shm", "goals_1.sqlite-wal", "queue_1.sqlite", "queue_1.sqlite-shm", "queue_1.sqlite-wal", "logs_2.sqlite", "logs_2.sqlite-shm", "logs_2.sqlite-wal" });
    public static IReadOnlyList<string> TransientItems { get; } = Array.AsReadOnly(new[] { "transcription-history.jsonl", "log/codex-login.log", "browser/sessions", "ambient-suggestions" });
    public static IReadOnlyList<string> ElectronRootDirectories { get; } = Array.AsReadOnly(new[] { "Default", "codex-browser-app", "sentry", "storage-cache" });
    public static IReadOnlyList<string> ElectronRootFiles { get; } = Array.AsReadOnly(new[] { "statsig-state.json", "statsig-state.json.bak", ".codex-tpp-local-executor-installation-id", "browser-sidebar-page-states.json" });
    public static IReadOnlySet<string> ElectronCacheDirectories { get; } = new HashSet<string>(new[] { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "ShaderCache", "GraphiteDawnCache", "component_crx_cache", "extensions_crx_cache", "Crashpad" }, StringComparer.Ordinal);
    public const string InstallationKey = "electron-local-remote-control-installation-id";
    public const string ProviderStateFile = ".state.json";
}
