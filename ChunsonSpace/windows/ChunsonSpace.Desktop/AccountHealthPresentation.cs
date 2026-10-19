using System.IO;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

public sealed class AccountHealthRow(string profile) : ObservableObject
{
    private readonly HashSet<string> startedModels = [];
    public string Profile { get; } = profile;
    public string Status { get; private set; } = "等待检测";
    public string Detail { get; private set; } = "";
    public AccountHealthResult? Result { get; private set; }
    public IReadOnlyList<CialloRow> Models { get; } = CialloTest.Models.Select(m => new CialloRow(m)).ToArray();
    public bool Finished => Result is not null;
    public void Update(AccountHealthProgress progress)
    {
        if (Finished) return;
        if (progress.Reply is { } reply)
        {
            startedModels.Add(reply.RequestedModel);
            Models.Single(r => r.Model == reply.RequestedModel).Update(reply);
        }
        if (progress.Result is { } result)
        {
            Result = result;
            foreach (var model in Models.Where(m => !m.Finished))
            {
                if (startedModels.Contains(model.Model))
                    model.Update(new(model.Model, "", CialloTest.Effort, result.State == "cancelled" ? "cancelled" : "failed", "本次没判断成功", TimeSpan.Zero));
                else model.SkipBeforeRequest(result.State == "frozen"
                    ? "账号已冻结，此模型未发送请求。" : result.State == "cancelled" ? "本轮已取消，此模型未发送请求。" : "本次未取得此模型的完整结果，未追加请求。");
            }
            Status = result.State switch { "normal" => "账号正常", "abnormal" => "账号异常", "frozen" => "跳过冻结", "cancelled" => "已取消", _ => "本次没判断成功" };
            Detail = "";
        }
        else Status = progress.Stage == "queued" ? "等待账号测试队列" : progress.Reply?.RequestedModel + (progress.Reply?.State == "running" ? " · 正在回复…" : " · 已返回");
        Raise(nameof(Status)); Raise(nameof(Detail)); Raise(nameof(Finished));
    }
}

public sealed class AccountHealthPanelViewModel : ObservableObject
{
    private readonly DesktopAccountServices services;
    private readonly AccountTestQueue queue;
    private readonly TestElapsedClock elapsed;
    private CancellationTokenSource? cancellation;
    public bool IsBusy { get; private set; }
    public IReadOnlyList<AccountHealthRow> Rows { get; private set; } = [];
    public string Status { get; private set; } = "";
    public string ProgressSummary => $"已结束 {Rows.Count(r => r.Finished)}/{Rows.Count} 个账号 · {elapsed.Text}";
    public string Summary => $"正常 {Rows.Count(r => r.Result?.State == "normal")} · 异常 {Rows.Count(r => r.Result?.State == "abnormal")} · 本次没判断成功 {Rows.Count(r => r.Result?.State == "inconclusive")} · 跳过冻结 {Rows.Count(r => r.Result?.State == "frozen")} · 已取消 {Rows.Count(r => r.Result?.State == "cancelled")}";
    public const string QuotaNotice = "并行检测未冻结账号，每个账号最多 2 次模型请求，使用各自额度。GPT-6.1 Sol / GPT-6 Astra，思考固定为高。";
    public string Quota => QuotaNotice;
    public DelegateCommand CancelCommand { get; }
    public DelegateCommand CloseCommand { get; }
    public Task CurrentOperation { get; private set; } = Task.CompletedTask;
    public AccountHealthPanelViewModel(DesktopAccountServices services, AccountTestQueue queue, Action close)
    {
        this.services = services; this.queue = queue;
        elapsed = new(() => Raise(nameof(ProgressSummary)));
        CancelCommand = new(_ => cancellation?.Cancel(), _ => IsBusy);
        CloseCommand = new(_ => close());
    }
    public void Start(IReadOnlyList<string>? profiles = null) { if (!IsBusy && queue.Accepting) CurrentOperation = RunAsync(profiles); }
    private async Task RunAsync(IReadOnlyList<string>? profiles)
    {
        IsBusy = true; using var cancel = new CancellationTokenSource(); cancellation = cancel;
        Rows = [];
        Status = "正在并行检测；收起后可从后台任务查看。"; elapsed.Start(); Refresh(lifecycle: true);
        try
        {
            var names = await Task.Run(() => services.Repository.Profiles.ListAccounts().Where(a => profiles is null || profiles.Contains(a.Name)).Select(a => a.Name).ToArray());
            Rows = names.Select(name => new AccountHealthRow(name)).ToArray();
            var rowsByName = Rows.ToDictionary(row => row.Profile, StringComparer.OrdinalIgnoreCase);
            Raise(nameof(Rows)); Refresh();
            // Progress<T> posts back to WPF; ignore delayed callbacks after completion.
            var progress = new Progress<AccountHealthProgress>(p =>
            {
                if (cancellation != cancel) return;
                if (rowsByName.TryGetValue(p.Profile, out var row)) row.Update(p);
                Refresh();
            });
            var result = await Task.Run(() => services.Accounts.TestAccountHealthBatchAsync(progress, cancel.Token,
                (name, run, token) => queue.RunAsync(name, run, token), profiles));
            foreach (var row in result.Results)
            {
                if (!rowsByName.TryGetValue(row.Profile, out var target) || target.Finished) continue;
                if (row.Test is { } test) foreach (var reply in test.Replies) target.Update(new(row.Profile, "testing", reply));
                target.Update(new(row.Profile, "finished", Result: row));
            }
            Status = result.Cancelled ? "已取消" : "检测结束";
        }
        catch (Exception error) when (error is IOException or StorageException or AccountException or UnauthorizedAccessException or InvalidOperationException)
        { Status = "本次没判断成功"; }
        finally { cancellation = null; IsBusy = false; elapsed.Stop(); Refresh(lifecycle: true); }
    }
    private void Refresh(bool lifecycle = false)
    {
        Raise(nameof(ProgressSummary)); Raise(nameof(Summary));
        if (lifecycle)
        {
            Raise(nameof(Rows)); Raise(nameof(Status)); Raise(nameof(IsBusy));
            CancelCommand.Refresh();
        }
    }
}

public sealed partial class ShellViewModel
{
    private AccountHealthPanelViewModel? retainedHealth;
    private AccountHealthPanelViewModel? health;
    public AccountHealthPanelViewModel? AccountHealth => health;
    public bool IsAccountHealthOpen => health is not null;
    public string AccountHealthQuota => AccountHealthPanelViewModel.QuotaNotice;
    public DelegateCommand TestAllAccountsCommand { get; private set; } = null!;
    private void HideAccountHealth() { health = null; Raise(nameof(AccountHealth)); Raise(nameof(IsAccountHealthOpen)); }
    private void InitializeAccountHealth()
    {
        TestAllAccountsCommand = new(_ =>
        {
            if (!TestAllAccountsCommand.CanExecute(null)) return;
            retainedHealth ??= new(workspace!.Accounts!, accountTests, HideAccountHealth);
            retainedHealth.PropertyChanged -= BackgroundModelChanged;
            retainedHealth.PropertyChanged += BackgroundModelChanged;
            modelTestOpen = false; ciallo = null; visualTests = null; health = retainedHealth; lastBackgroundPanel = health;
            foreach (var name in new[] { nameof(IsModelTestOpen), nameof(Ciallo), nameof(IsCialloOpen), nameof(VisualTests), nameof(IsVisualTestsOpen), nameof(AccountHealth), nameof(IsAccountHealthOpen) }) Raise(name);
            health.Start(IsSingleAccountMode ? DisplayedAccount is { } shown ? new[] { shown.Name } : Array.Empty<string>() : null); RefreshBackgroundModel();
        }, _ => !operationBusy && !exitDraining && retainedHealth?.IsBusy != true && workspace?.Accounts is not null
            && identities.Any(a => modelTestAccountNames.Contains(a.Name) && !a.TestsFrozen
                && (!IsSingleAccountMode || a.Name == DisplayedAccount?.Name)));
    }
}
