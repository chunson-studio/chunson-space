using System.Text.RegularExpressions;

namespace FAswitch.NodeTesting;

public static class NodeScreeningRules
{
    private const string IpequalSubscriptionPattern = @"(?<![a-z0-9])i[pq]equal(?![a-z])";
    public static bool IsEntertainment(string name) => name.Contains("娱乐", StringComparison.OrdinalIgnoreCase);
    public static bool IsNotice(string name) => Regex.IsMatch(name, "剩余|重置|到期|超时请|官网|公告|套餐|有效期|流量|累计|超过速率|请更新订阅|建议注册多个账号|这是运营商限制");
    public static bool IsAccountCandidate(NodeCandidate node) => node.Supported && !IsEntertainment(node.Name) && !IsNotice(node.Name);
    public static bool IsResidentialName(string name) => name.Contains("家宽", StringComparison.OrdinalIgnoreCase) || name.Contains("住宅", StringComparison.OrdinalIgnoreCase);
    public static bool IsPreferredSubscription(string source) => Regex.IsMatch(source, IpequalSubscriptionPattern, RegexOptions.IgnoreCase);
    public static bool IsScoreExempt(NodeCandidate node, string subscriptionName) => IsAccountCandidate(node)
        && IsPreferredSubscription(subscriptionName)
        && node.Name.Contains("住宅", StringComparison.Ordinal);
    public static FAswitch.Accounts.IpQualityReport ApplyAdmission(FAswitch.Accounts.IpQualityReport report, NodeCandidate node, string subscriptionName)
        => report with { ResidentialScoreExempt = IsScoreExempt(node, subscriptionName) };
}
