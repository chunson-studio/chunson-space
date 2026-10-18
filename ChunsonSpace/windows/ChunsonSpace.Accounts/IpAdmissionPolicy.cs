using ChunsonSpace.Application;
using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

public sealed record IpAdmissionDecision(bool Allowed, int? QualityScore, int MinimumScore, string Message);

public static class IpAdmissionPolicy
{
    public const int MinimumScore = 85;
    public static IpAdmissionDecision Evaluate(IpQualityReport report)
    {
        var score = report.HasVerifiedExit ? report.Score?.Quality : null;
        if (report.HasVerifiedExit && report.ResidentialScoreExempt)
            return new(true, score, MinimumScore, "IPEqual 住宅线路：免评分门槛；实际出口已确认。" + (score is { } value ? $"评分 {value}/100，仅供参考。" : "评分暂不可用，未虚构分数。"));
        bool allowed = score >= MinimumScore;
        return new(allowed, score, MinimumScore, score is null ? "IP 质量分未知，未放行；请重新检测。"
            : allowed ? $"IP 质量 {score}/100，达到 {MinimumScore} 分门槛。" : $"IP 质量 {score}/100，低于 {MinimumScore} 分，不能登录或启动。请在 FlClash 换用达标节点。" );
    }
}

/// <summary>Fresh checks on the resolved route, shared by GUI, CLI login and managed launch.</summary>
public sealed class IpAdmissionService(Func<string, CancellationToken, Task<IpQualityReport>> probe) : ILaunchEgressProbe
{
    private Task<IpQualityReport> ProbeQualityAsync(string proxy, CancellationToken token)
        => probe(proxy, token);
    public async Task RequireProxyAsync(string proxy, CancellationToken token)
        => _ = await RequireVerifiedProxyAsync(proxy, token);
    public async Task<EgressResult> RequireVerifiedProxyAsync(string proxy, CancellationToken token)
    {
        var report = await ProbeQualityAsync(proxy, token);
        if (!report.Admission.Allowed) throw new AccountException(AccountError.ExitUnavailable, report.Admission.Message);
        return new(true, report.Ip, report.Country, report.CountryCode, report.City, report.Timezone);
    }
    public async Task<LaunchEgressEvidence> ProbeAsync(IdentityRef identity, string resolvedProxy, CancellationToken token)
    {
        var report = await ProbeQualityAsync(resolvedProxy, token);
        return new(report.Admission.Allowed, report.Ip, report.Timezone, report.Admission.Message);
    }
}
