namespace ChunsonSpace.Proxy;

public enum ProxyError { InvalidConfig, GroupRequired, UnsafeGroup, Conflict, StalePreview, Io, RestoreConflict }
public sealed class ProxyException(ProxyError code, string message, string? path = null, string? recoveryPath = null) : Exception(message)
{
    public ProxyError Code { get; } = code;
    public string? Path { get; } = path;
    public string? RecoveryPath { get; } = recoveryPath;
}
public enum ProxyClientKind { FlClash, ClashVerge }
public sealed record ProxyPortCandidate(string Source, string Address, bool RequiresAuthentication);
public sealed record ProxyGroup(string Name, string Type, bool Usable, string Reason);
public sealed record ProxyInspection(IReadOnlyList<ProxyPortCandidate> Ports, IReadOnlyList<ProxyGroup> Groups,
    string? SuggestedGroup, IReadOnlyList<string> Warnings);
public sealed record DnsChange(string Setting, string Description);
public sealed record ClashVergeRepairRequest(string DataRoot, string? ProxyGroup = null, bool EnableTun = true);
public sealed class ClashVergeRepairPlan
{
    internal ClashVergeRepairPlan(ClashVergeRepairRequest request, ProxyInspection inspection, string group,
        IReadOnlyList<DnsChange> changes, IReadOnlyList<string> warnings, IReadOnlyList<PlannedFile> files,
        IReadOnlyDictionary<string, string?> observed)
        => (Request, Inspection, ProxyGroup, Changes, Warnings, Files, Observed) = (request, inspection, group, changes, warnings, files, observed);
    public ClashVergeRepairRequest Request { get; }
    public ProxyInspection Inspection { get; }
    public string ProxyGroup { get; }
    public IReadOnlyList<DnsChange> Changes { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool RequiresClientStopped => true;
    internal IReadOnlyList<PlannedFile> Files { get; }
    internal IReadOnlyDictionary<string, string?> Observed { get; }
}
public sealed record ProxySaveResult(bool SettingsSaved, string BackupPath, IReadOnlyList<string> Warnings)
{
    public bool RuntimeVerified => false;
}
internal sealed record PlannedFile(string RelativePath, byte[]? Before, byte[] After, IReadOnlyList<FieldChange>? Fields = null);
internal sealed record FieldChange(string[] Keys, bool BeforeExists, string? BeforeJson, string AfterJson);
