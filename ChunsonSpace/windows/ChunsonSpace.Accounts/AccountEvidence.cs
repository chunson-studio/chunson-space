using System.Security.Cryptography;
using System.Text;
using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

public sealed record AccountTestObservation(string RequestedModel, string ResponseModel, string Effort,
    bool Completed, SafetyBufferingObservation? SafetyBuffering);
public sealed record AccountEvidenceVerdict(AccountQuality? Quality, string Models, string Reason);

// User-selected empirical rule. This does not identify a backend model or measure intelligence.
public static class AccountEvidence
{
    public static AccountIssue FailureIssue(Exception error) => error is AccountException account ? account.Code switch
    {
        AccountError.MissingCredentials => AccountIssue.LoginMissing,
        AccountError.Network or AccountError.Timeout or AccountError.ExitUnavailable => AccountIssue.TemporaryConnection,
        AccountError.Http when account.Status == 401 => AccountIssue.LoginExpired,
        AccountError.Http when account.Status is 403 or 429 => AccountIssue.RequestRejected,
        _ => AccountIssue.OperationFailed
    } : AccountIssue.OperationFailed;
    public const string Policy = "sol61-astra-high-sse-v2";
    public static bool Supported(string model) => ModelTestParameters.AllowedModel(model);
    public static string Id(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Label(AccountQuality? quality) => quality switch
    { AccountQuality.Normal => "账号正常", AccountQuality.SuspectedDegraded => "账号异常", _ => "本次没判断成功" };
    public static string Summary(AccountTestObservation observation) => Label(Classify([observation]).Quality);
    internal static AccountQuality? FieldQuality(SafetyBufferingObservation? observed)
    {
        if (!SafetyBufferingDisplay.Valid(observed) || observed!.Capture != "complete") return null;
        if (observed.Values.Count == 0) return AccountQuality.SuspectedDegraded;
        return observed.Values.All(v => v.Source == "field" && v.Path is "$.safety_buffering" or "$.response.safety_buffering"
            && v.Kind == "boolean" && v.Boolean == false && v.Fingerprint == Id("false")) ? AccountQuality.Normal : null;
    }
    public static AccountEvidenceVerdict Classify(IReadOnlyList<AccountTestObservation> observations, bool finished = true)
    {
        var supported = observations.Where(o => Supported(o.RequestedModel)).ToArray();
        var models = string.Join("、", supported.Select(o => o.RequestedModel).Distinct().Order());
        AccountEvidenceVerdict Unknown(string reason) => new(null, models, reason);
        if (!finished) return Unknown("测试未完成");
        if (supported.Length == 0) return Unknown("模型不在已验证范围内");
        var qualities = new HashSet<AccountQuality>();
        foreach (var item in supported)
        {
            var observed = item.SafetyBuffering;
            if (!item.Completed || item.Effort != "high" || item.ResponseModel != item.RequestedModel)
                return Unknown("测试未完成，或模型、思考档位不在已验证范围内");
            if (!SafetyBufferingDisplay.Valid(observed) || observed!.Capture != "complete") return Unknown("这次没读完整");
            if (FieldQuality(observed) is not { } quality) return Unknown("返回的信息不在已确定规则内");
            qualities.Add(quality);
        }
        return qualities.Count != 1 ? Unknown("这次多个结果不一致")
            : new(qualities.Single(), models, qualities.Contains(AccountQuality.Normal) ? "完整结果返回 false" : "完整结果未返回该字段");
    }
    public static string Apply(FileProfileStore profiles, string name, string identity, DateTimeOffset started, string id,
        IReadOnlyList<AccountTestObservation> observations, bool finished = true, string source = "software")
    {
        var verdict = Classify(observations, finished);
        try { return profiles.ApplyAccountEvidence(name, identity, started, id, verdict.Quality, source, verdict.Models, verdict.Reason); }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException)
        { return "标记未保存；测试结果和原标记文件保留。"; }
    }
    // Optional list annotations must not turn a completed reply into a failed model request.
    // ReadAccountMarks separately exposes damaged/future records as unreadable; never repair them implicitly.
    internal static void RecordIssue(FileProfileStore profiles, string name, AccountIssue issue, string identity)
    {
        try { profiles.RecordAccountIssue(name, issue, "ModelTest", expectedIdentityHash: identity); }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException) { }
    }
}
