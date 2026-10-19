using System.Text.Json.Nodes;

namespace ChunsonSpace.Core;

public enum IdentityKind { Account, Provider }
public sealed record IdentityRef(IdentityKind Kind, string Name)
{
    public bool Equals(IdentityRef? other) => other is not null && Kind == other.Kind && StringComparer.OrdinalIgnoreCase.Equals(Name.Trim(), other.Name.Trim());
    public override int GetHashCode() => HashCode.Combine(Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(Name.Trim()));
}
public sealed record IdentityState(IdentityRef? Selected = null, IdentityRef? Active = null, IdentityRef? Running = null);
public sealed record EnvironmentSettings(string Proxy = "", string Timezone = "", string Locale = "", bool LocaleAuto = false, bool FollowSystemProxy = false);
public sealed record ExitObservation(string Proxy, bool FollowSystemProxy, string Ip, string Timezone, string Location, DateTimeOffset CheckedAt)
{
    public ExitAddresses Addresses { get; init; } = ExitAddresses.Unknown;
    public bool Matches(EnvironmentSettings environment) => Proxy == environment.Proxy && FollowSystemProxy == environment.FollowSystemProxy;
}
public sealed record AccountProfile(string Name, bool HasCredentials, EnvironmentSettings Environment, bool TestsFrozen = false, DateTimeOffset? LoggedInAt = null, string? DisplayName = null);
public sealed record ProviderProfile(string Name, string BaseUrl, string Model, string Created, bool HasKey);
public sealed record ProviderSaveResult(ProviderProfile Profile, string? ArchivedPath);
public sealed record ProviderUpdateResult(ProviderProfile Profile, bool ActiveUpdated, string RecoveryPath, IReadOnlyList<string> Warnings);
public sealed record AppSettings(bool SessionIsolation = true, bool AutoSystemTimezone = true, bool QuitConfirm = true, string WorkspaceTheme = "light", bool CloseToTray = true, string InterfaceMode = "auto");
public enum StorageError { Missing, Corrupt, Io, InvalidName, Conflict, InvalidValue, LoginRequired }
public sealed class StorageException(StorageError code, string path, string message, Exception? inner = null) : Exception(message, inner)
{
    public StorageError Code { get; } = code;
    public string FilePath { get; } = path;
}
public sealed record FileIssue(string Path, StorageError Code, string Message);
public sealed record ArchiveResult(string Directory, IReadOnlyList<string> Moved);
public sealed record TokenUsage(long? CachedInputTokens);
public static class TokenUsageReader
{
    public static TokenUsage Read(JsonObject? usage)
    {
        var value = usage?["cached_input_tokens"] ?? (usage?["input_tokens_details"] as JsonObject)?["cached_tokens"];
        // JSON booleans, strings, decimals and exponent notation are not Python ints.
        if (value is not JsonValue json) return new(null);
        long count;
        if (json.TryGetValue<System.Text.Json.JsonElement>(out var element))
        {
            if (element.ValueKind != System.Text.Json.JsonValueKind.Number || !element.TryGetInt64(out count) ||
                element.GetRawText().IndexOfAny(['.', 'e', 'E']) >= 0) return new(null);
        }
        else if (json.TryGetValue<int>(out var integer)) count = integer;
        else if (!json.TryGetValue<long>(out count)) return new(null);
        return new(count < 0 ? null : count);
    }
}
