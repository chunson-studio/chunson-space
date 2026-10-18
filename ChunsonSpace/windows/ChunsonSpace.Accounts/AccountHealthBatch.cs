using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

public sealed record AccountHealthResult(string Profile, string State, string Reason, AccountMarkMode Mode,
    AccountQuality RetainedQuality, string MarkOutcome, CialloResult? Test = null);
public sealed record AccountHealthProgress(string Profile, string Stage, CialloReply? Reply = null, AccountHealthResult? Result = null);
public sealed record AccountHealthBatchResult(IReadOnlyList<AccountHealthResult> Results, bool Cancelled);

public sealed partial class AccountService
{
    public const int AccountHealthConcurrency = 16;
    private int healthBatchRunning;
    // A bounded account batch, using the same Parallel.ForEachAsync scheduling as node batches.
    // The UI supplies the existing per-account queue shared with the other model-test panels.
    public async Task<AccountHealthBatchResult> TestAccountHealthBatchAsync(IProgress<AccountHealthProgress>? progress = null,
        CancellationToken token = default,
        Func<string, Func<Task<CialloResult>>, CancellationToken, Task<CialloResult>>? enqueue = null)
    {
        if (Interlocked.CompareExchange(ref healthBatchRunning, 1, 0) != 0)
            throw new InvalidOperationException("已有一批账号检测正在执行。");
        try
        {
            var targets = repository.Profiles.ListAccounts().ToArray();
            var frozenAtStart = targets.ToDictionary(a => a.Name, a => repository.Profiles.AreTestsFrozen(a.Name));
            var results = new AccountHealthResult?[targets.Length];
            void Finish(int index, AccountHealthResult row)
            {
                if (Interlocked.CompareExchange(ref results[index], row, null) is not null) return;
                progress?.Report(new(row.Profile, "finished", Result: row));
            }
            AccountHealthResult Row(int index, string state, string reason, string outcome = "原标记保留", CialloResult? test = null)
            {
                var marks = repository.Profiles.ReadAccountMarks(targets[index].Name);
                return new(targets[index].Name, state, reason, marks.Mode, marks.Quality,
                    marks.ReadFailed ? "标记读取失败；原文件保留" : outcome, test);
            }
            async ValueTask Test(int index, CancellationToken workToken)
            {
                var target = targets[index];
                AccountHealthResult row;
                try
                {
                    // Frozen at snapshot stays skipped even if subsequently unfrozen. Check again before queueing.
                    if (frozenAtStart[target.Name] || repository.Profiles.AreTestsFrozen(target.Name))
                        row = Row(index, "frozen", "账号已冻结，本批次不请求");
                    else if (workToken.IsCancellationRequested) row = Row(index, "cancelled", "批次已取消，未请求");
                    else if (!target.HasCredentials) row = Row(index, "inconclusive", "未保存登录资料，未请求");
                    else
                    {
                        var identity = repository.Profiles.ReadAccountIdentityHash(target.Name);
                        progress?.Report(new(target.Name, "queued"));
                        async Task<CialloResult> Run()
                        {
                            workToken.ThrowIfCancellationRequested();
                            repository.RequireTestsAllowed(target.Name);
                            if (identity.Length != 64 || repository.Profiles.ReadAccountIdentityHash(target.Name) != identity)
                                throw new InvalidOperationException("账号身份已变化，未派发请求");
                            return await TestCialloAsync(target.Name, new InlineHealthProgress<CialloReply>(reply =>
                                progress?.Report(new(target.Name, "testing", reply))), workToken, CialloTest.Effort);
                        }
                        var test = enqueue is null ? await Run() : await enqueue(target.Name, Run, workToken);
                        var verdict = AccountEvidence.Classify(test.Replies.Select(r => new AccountTestObservation(
                            r.RequestedModel, r.ResponseModel, r.Effort, r.State == "completed", r.SafetyBuffering)).ToArray(),
                            !test.Cancelled && test.Replies.Count == CialloTest.Models.Count
                            && identity == repository.Profiles.ReadAccountIdentityHash(target.Name));
                        row = Row(index, test.Cancelled ? "cancelled" : verdict.Quality switch { AccountQuality.Normal => "normal", AccountQuality.SuspectedDegraded => "abnormal", _ => "inconclusive" },
                            verdict.Reason, test.MarkOutcome, test);
                    }
                }
                catch (OperationCanceledException) when (workToken.IsCancellationRequested) { row = Row(index, "cancelled", "已取消；未完整结束的结果不产生新结论"); }
                catch (Exception error)
                {
                    // A freeze during queueing/dispatch is not an account-health conclusion.
                    bool frozen;
                    try { frozen = repository.Profiles.AreTestsFrozen(target.Name); }
                    catch (Exception) { frozen = false; }
                    row = Row(index, frozen ? "frozen" : "inconclusive",
                        frozen ? "派发前已冻结，后续请求已跳过" : ErrorRedaction.Scrub(error.Message));
                }
                Finish(index, row);
            }
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, targets.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = AccountHealthConcurrency, CancellationToken = token }, Test);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            // The scheduler does not start more workers after cancellation. Give every unstarted row a terminal state.
            for (var index = 0; index < targets.Length; index++)
            {
                if (results[index] is not null) continue;
                var frozen = frozenAtStart[targets[index].Name];
                Finish(index, Row(index, frozen ? "frozen" : "cancelled", frozen ? "账号已冻结，本批次不请求" : "批次已取消，未请求"));
            }
            return new(results.Select(row => row!).ToArray(), token.IsCancellationRequested);
        }
        finally { Volatile.Write(ref healthBatchRunning, 0); }
    }
    private sealed class InlineHealthProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
}
