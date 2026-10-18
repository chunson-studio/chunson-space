using System.Globalization;
using System.Text.Json.Nodes;

namespace ChunsonSpace.Core;

public enum AccountQuality { Unmarked, Normal, SuspectedDegraded }
public enum AccountMarkMode { Automatic, Manual }
public enum AccountIssue { None, LoginExpired, LoginMissing, TemporaryConnection, RequestRejected, RestoreFailed, OperationFailed }
public sealed record AccountMarks(AccountQuality Quality = AccountQuality.Unmarked, bool Problem = false,
    bool Recovered = false, string RecoverySource = "", DateTimeOffset? RecoveryAt = null, DateTimeOffset? MarkedAt = null,
    AccountIssue Issue = AccountIssue.None, string IssueSource = "", DateTimeOffset? IssueAt = null, bool ReadFailed = false,
    AccountMarkMode Mode = AccountMarkMode.Automatic, string QualitySource = "", bool Inconclusive = false,
    DateTimeOffset? EvidenceAt = null, string EvidenceModel = "", string EvidenceId = "", string EvidenceReason = "");

public sealed partial class FileProfileStore
{
    public const string AccountMarksFile = "account-marks.json";
    private static readonly object marksGate = new();
    public AccountMarks ReadAccountMarks(string name)
    {
        lock (marksGate)
        {
            try
            {
                var value = JsonFiles.Read(files, MarksPath(name), true);
                var identity = JsonFiles.Text(value, "identityHash");
                if (identity.Length > 0 && identity != ReadAccountIdentityHash(name)) return new(ReadFailed: true);
                return ParseMarks(value);
            }
            catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException)
            { return new(ReadFailed: true); } // Optional marks must never hide an account or invent a quality.
        }
    }
    public void SetAccountMarks(string name, AccountQuality quality, bool problem, bool recovered, DateTimeOffset? now = null, AccountMarkMode mode = AccountMarkMode.Manual, AccountMarks? expected = null)
    {
        if (!Enum.IsDefined(quality)) throw new StorageException(StorageError.InvalidValue, "", "账号标记无效。");
        UpdateMarks(name, value =>
        {
            var old = ParseMarks(value); var at = (now ?? DateTimeOffset.UtcNow).ToString("O");
            if (expected is not null && old != expected)
                throw new StorageException(StorageError.Conflict, "", "标记已有新结果，请重新打开后编辑。");
            value["identityHash"] = ReadAccountIdentityHash(name);
            value["quality"] = quality.ToString(); value["problem"] = problem; value["markedAt"] = at;
            value["mode"] = mode.ToString(); value["modeAt"] = at;
            if (mode == AccountMarkMode.Manual || old.Quality != quality) value["qualitySource"] = "manual";
            value["inconclusive"] = false;
            // Keeping an existing badge keeps its authoritative source/time. Clearing it never regenerates it on reload.
            if (old.Recovered != recovered)
            {
                value["recovered"] = recovered;
                value["recoverySource"] = recovered ? "manual" : "";
                value["recoveryAt"] = recovered ? at : null;
            }
        });
    }
    public static string AccountIdentityHash(string accountId) => string.IsNullOrWhiteSpace(accountId) ? ""
        : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(accountId)));
    public string ReadAccountIdentityHash(string name) => files.FileExists(Path.Combine(ProfileDirectory(new(IdentityKind.Account, name)), "auth.json"))
        ? AccountIdentityHash(ReadAccountCredentials(name)["tokens"]?["account_id"]?.ToString() ?? "") : "";
    // Called only with a classified, explicitly captured test. History display never calls this method.
    public string ApplyAccountEvidence(string name, string identityHash, DateTimeOffset started, string evidenceId,
        AccountQuality? verdict, string source, string models, string reason)
    {
        if (identityHash.Length != 64 || evidenceId.Length != 64 || !identityHash.All(Uri.IsHexDigit) || !evidenceId.All(Uri.IsHexDigit)
            || source is not ("software" or "trace-import") || models.Length > 180 || reason.Length > 160
            || verdict is not (null or AccountQuality.Normal or AccountQuality.SuspectedDegraded))
            throw new StorageException(StorageError.InvalidValue, "", "测试记录无效。");
        var outcome = "未更新";
        UpdateMarks(name, value =>
        {
            if (ReadAccountIdentityHash(name) != identityHash) { outcome = "账号身份不一致"; return; }
            if (JsonFiles.Text(value, "identityHash") is { Length: > 0 } savedIdentity && savedIdentity != identityHash)
            { outcome = "标记属于旧账号，请重新确认"; return; }
            var previous = ParseMarks(value);
            if (previous.Mode == AccountMarkMode.Manual) { outcome = "保留手动标记"; return; }
            if (DateTimeOffset.TryParse(JsonFiles.Text(value, "modeAt"), out var modeAt) && started <= modeAt
                || previous.EvidenceAt is { } prior && started <= prior || previous.EvidenceId == evidenceId)
            { outcome = "旧记录或重复记录未应用"; return; }
            value["evidenceAt"] = started.ToString("O"); value["evidenceId"] = evidenceId;
            value["mode"] = AccountMarkMode.Automatic.ToString();
            value["identityHash"] = identityHash;
            value["evidenceModel"] = models; value["evidenceReason"] = reason;
            value["inconclusive"] = verdict is null;
            if (verdict is { } quality)
            {
                if (previous.Quality == AccountQuality.SuspectedDegraded && previous.QualitySource is "software" or "trace-import" && quality == AccountQuality.Normal)
                { value["recovered"] = true; value["recoverySource"] = "signal"; value["recoveryAt"] = started.ToString("O"); }
                value["quality"] = quality.ToString(); value["qualitySource"] = source; value["markedAt"] = started.ToString("O");
            }
            outcome = verdict is null ? "本次没判断成功" : "自动标记已更新";
        });
        return outcome;
    }
    public void RecordAccountIssue(string name, AccountIssue issue, string source, DateTimeOffset? now = null, string? expectedIdentityHash = null)
    {
        if (!Enum.IsDefined(issue) || source is not ("Quota" or "Connectivity" or "Refresh" or "Login" or "Restore" or "Egress" or "IpQuality" or "SuggestTimezone" or "ApplyDefaults" or "ModelTest"))
            throw new StorageException(StorageError.InvalidValue, "", "操作记录无效。");
        UpdateMarks(name, value =>
        {
            if (expectedIdentityHash is not null && ReadAccountIdentityHash(name) != expectedIdentityHash) return;
            if (JsonFiles.Text(value, "identityHash") is { Length: > 0 } savedIdentity && savedIdentity != ReadAccountIdentityHash(name)) return;
            var previous = ParseMarks(value);
            if (issue == AccountIssue.None && previous.IssueSource != source &&
                !(previous.Issue is AccountIssue.LoginExpired or AccountIssue.LoginMissing && source is "Quota" or "Connectivity" or "Refresh" or "Login" or "ModelTest")) return;
            value["issue"] = issue.ToString(); value["issueSource"] = issue == AccountIssue.None ? "" : source;
            value["issueAt"] = issue == AccountIssue.None ? null : (now ?? DateTimeOffset.UtcNow).ToString("O");
        });
    }
    private string MarksPath(string name) => Path.Combine(ProfileDirectory(new(IdentityKind.Account, name)), AccountMarksFile);
    private void UpdateMarks(string name, Action<JsonObject> update)
    {
        lock (marksGate)
        {
            var path = MarksPath(name);
            if (!files.DirectoryExists(Path.GetDirectoryName(path)!)) throw new StorageException(StorageError.Missing, path, "账号已不存在，标记未保存。");
            var value = JsonFiles.Read(files, path, true);
            _ = ParseMarks(value); // Refuse to silently overwrite malformed/future data.
            var before = value.ToJsonString();
            update(value);
            if (before == value.ToJsonString()) return;
            value["version"] = 2;
            JsonFiles.Write(files, path, value);
        }
    }
    private static AccountMarks ParseMarks(JsonObject value)
    {
        StorageException Invalid() => new(StorageError.Corrupt, "", "标记记录无法读取，请保留原文件并检查。");
        if (value["version"] is { } version && (version is not JsonValue v || !v.TryGetValue<int>(out var number) || number is not (1 or 2))) throw Invalid();
        T EnumValue<T>(string key, T fallback) where T : struct, Enum
        {
            if (value[key] is null) return fallback;
            if (value[key] is JsonValue node && node.TryGetValue<string>(out var text) && Enum.TryParse<T>(text, out var parsed) && Enum.IsDefined(parsed) && Enum.GetName(parsed) == text) return parsed;
            throw Invalid();
        }
        bool Flag(string key)
        { if (value[key] is null) return false; if (value[key] is JsonValue node && node.TryGetValue<bool>(out var flag)) return flag; throw Invalid(); }
        DateTimeOffset? Time(string key)
        { if (value[key] is null) return null; if (DateTimeOffset.TryParse(JsonFiles.Text(value, key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)) return time; throw Invalid(); }
        var recovered = Flag("recovered"); var source = JsonFiles.Text(value, "recoverySource"); var recoveryAt = Time("recoveryAt");
        if (recovered && (source is not ("manual" or "prepared-login" or "signal") || recoveryAt is null)) throw Invalid();
        return new(EnumValue("quality", AccountQuality.Unmarked), Flag("problem"), recovered, source, recoveryAt, Time("markedAt"),
            EnumValue("issue", AccountIssue.None), JsonFiles.Text(value, "issueSource"), Time("issueAt"), false,
            EnumValue("mode", value["quality"] is null ? AccountMarkMode.Automatic : AccountMarkMode.Manual),
            JsonFiles.Text(value, "qualitySource"), Flag("inconclusive"), Time("evidenceAt"), JsonFiles.Text(value, "evidenceModel"), JsonFiles.Text(value, "evidenceId"), JsonFiles.Text(value, "evidenceReason"));
    }
}
