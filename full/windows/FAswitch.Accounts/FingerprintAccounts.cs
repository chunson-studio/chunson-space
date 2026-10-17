using FAswitch.Core;
using FAswitch.Fingerprint;

namespace FAswitch.Accounts;

public enum FingerprintTestStage { Starting, Completed, Failed, Cancelled }
public sealed record FingerprintReply(int Index, string ChallengeId, string Prompt, string RequestedModel, string ResponseModel,
    string Text, int ExpectedCount, TimeSpan Elapsed, TokenUsage Usage);
public sealed record FingerprintTestProgress(int Index, int Total, string ChallengeId, FingerprintTestStage Stage,
    string Message, FingerprintReply? Reply = null);
public sealed record FingerprintTestFailure(int Index, string ChallengeId, AccountError Error, string Message);
public sealed record FingerprintTestResult(string ProfileName, string RequestedModel, IReadOnlyList<FingerprintReply> Responses,
    FingerprintAnalysis? Analysis, FingerprintTestFailure? Failure, TimeSpan Elapsed, IReadOnlyList<string> Warnings)
{
    // Completed describes three completed requests; the analysis may still have no usable outputs.
    public bool Completed => Failure is null && Responses.Count == 3;
}

public sealed partial class AccountService
{
    public const string FingerprintEffort = "high";
    internal TimeSpan FingerprintQuestionTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public async Task<FingerprintTestResult> TestFingerprintAsync(string profileName, string model,
        IReadOnlyList<FingerprintChallenge> challenges, IProgress<FingerprintTestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenges);
        if (challenges.Count != 3 || challenges.Any(q => q is null || string.IsNullOrWhiteSpace(q.Id) ||
            string.IsNullOrWhiteSpace(q.Prompt) || q.Prompt.Length > 1048576 || q.ExpectedCount <= 0) ||
            challenges.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count() != 3)
            throw new ArgumentException("指纹测试必须明确提供三道不同的完整题目，不会自动补题。", nameof(challenges));
        var questions = challenges.ToArray();
        ArgumentNullException.ThrowIfNull(model);
        model = model.Trim();
        if (model.Length == 0 || model.Length > 200 || model.Any(char.IsControl))
            throw new ArgumentException("请填写有效的请求模型。", nameof(model));
        var engine = FingerprintEngine.Default;
        var warnings = new List<string>
        {
            "指纹结果是与内置样本库的文本特征对比，不构成模型身份或能力认证。",
            "本轮采用空 instructions、high 思考程度与服务默认温度；调用环境和预算差异可能影响结果，不能证明与库采样条件相同。"
        };
        if (!engine.ModelIds.Contains(model, StringComparer.Ordinal))
            warnings.Add("所填模型未收入指纹库；只能展示最接近的库中候选，不能据此认定上游型号。");
        var started = clock.UtcNow;
        var responses = new List<FingerprintReply>();
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        AuthSession session;
        try { session = new(repository.ReadAuth(profileName), repository.Proxy(profileName)); _ = Credentials(session.Auth); CaptureSecrets(session.Auth); }
        catch (AccountException error) { return Failure(1, questions[0], error.Code, error.Message); }
        catch (StorageException) { return Failure(1, questions[0], AccountError.Storage, "无法读取该档案凭据，未发起测试。"); }
        for (var i = 0; i < questions.Length; i++)
        {
            var question = questions[i];
            var index = i + 1;
            var questionStarted = clock.UtcNow;
            if (cancellationToken.IsCancellationRequested) return Failure(index, question, AccountError.Cancelled, "测试已取消，未继续发起题目。");
            progress?.Report(new(index, 3, question.Id, FingerprintTestStage.Starting, $"正在请求第 {index}/3 题。"));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(FingerprintQuestionTimeout);
            try
            {
                var output = await ReadResponses(profileName, model, session, deadline.Token, question.Prompt, instructions: "", effort: FingerprintEffort);
                deadline.Token.ThrowIfCancellationRequested();
                CaptureSecrets(session.Auth);
                var reply = new FingerprintReply(index, question.Id, question.Prompt, model, output.Model,
                    ErrorRedaction.ResponseText(output.Text, secrets.ToArray()),
                    question.ExpectedCount, clock.UtcNow - questionStarted, TokenUsageReader.Read(output.Usage));
                responses.Add(reply);
                progress?.Report(new(index, 3, question.Id, FingerprintTestStage.Completed, $"第 {index}/3 题已收到完整响应。", reply));
            }
            catch (OperationCanceledException)
            {
                return Failure(index, question, cancellationToken.IsCancellationRequested ? AccountError.Cancelled : AccountError.Timeout,
                    cancellationToken.IsCancellationRequested ? "测试已取消，已完成题目保留；不会补题。" : "本题超过 90 秒，未确认成功；不会补题。");
            }
            catch (AccountException error) { return Failure(index, question, error.Code, error.Message); }
            catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException)
            { return Failure(index, question, AccountError.Network, "本题网络响应连接中断，未确认成功；不会补题。"); }
            catch (StorageException) { return Failure(index, question, AccountError.Storage, "读取或刷新保存凭据失败，测试已停止。"); }
        }
        if (cancellationToken.IsCancellationRequested) return Failure(3, questions[2], AccountError.Cancelled, "测试已取消，完整响应保留；未继续计算评分。");
        var analysis = engine.Analyze(responses.Select(r => new FingerprintOutput(r.Text, r.ExpectedCount)));
        if (analysis.UsedOutputs < 3) warnings.Add("部分响应未满足指纹解析条件；请查看逐题解析诊断，不能当作完整三题评分。");
        return new(profileName, model, responses.ToArray(), analysis, null, clock.UtcNow - started, warnings);

        FingerprintTestResult Failure(int index, FingerprintChallenge question, AccountError code, string message)
        {
            var described = $"第 {index}/3 题：" + ErrorRedaction.Scrub(message, secrets.ToArray());
            progress?.Report(new(index, 3, question.Id, code == AccountError.Cancelled ? FingerprintTestStage.Cancelled : FingerprintTestStage.Failed, described));
            return new(profileName, model, responses.ToArray(), null, new(index, question.Id, code, described), clock.UtcNow - started, warnings);
        }
        void CaptureSecrets(System.Text.Json.Nodes.JsonObject auth)
        {
            var tokens = auth["tokens"] as System.Text.Json.Nodes.JsonObject;
            foreach (var key in new[] { "access_token", "refresh_token", "id_token", "account_id" })
                if (AccountJson.Text(tokens, key) is { Length: > 0 } value) secrets.Add(value);
        }
    }
}
