namespace ChunsonSpace.Accounts;

public static class ModelTestParameters
{
    public const string DefaultEffort = "high";
    public static IReadOnlyList<string> Efforts { get; } = ["high"];
    public static IReadOnlyList<string> Models { get; } = ["gpt-6.1-sol", "gpt-6-astra"];
    public static bool AllowedModel(string? model) => model is "gpt-6.1-sol" or "gpt-6-astra";
    public static void RequireModel(string model)
    { if (!AllowedModel(model)) throw new ArgumentException("测试模型仅支持 GPT-6.1 Sol 和 GPT-6 Astra。"); }
    public static string EffortLabel(string? effort) => effort switch { "none" => "不使用", "minimal" => "很低", "low" => "低", "medium" => "中", "high" => "高", "xhigh" => "很高", "max" => "最高", "ultra" => "超高", _ => "未记录" };
    // Historical records retain their original parameters. This is not the request allowlist.
    public static bool ValidEffort(string? effort) => effort is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra";
    public static void RequireEffort(string effort)
    {
        if (effort != DefaultEffort) throw new ArgumentException("产品测试的思考程度固定为高。");
    }
}
