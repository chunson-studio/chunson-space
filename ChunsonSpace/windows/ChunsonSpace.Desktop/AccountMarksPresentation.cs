using System.IO;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

public static class AccountMarkDisplay
{
    public static string Quality(AccountQuality quality) => quality switch { AccountQuality.Normal => "账号正常", AccountQuality.SuspectedDegraded => "账号异常", _ => "未判断" };
    public static string Issue(AccountIssue issue) => issue switch
    {
        AccountIssue.LoginExpired => "登录已失效", AccountIssue.LoginMissing => "需要登录",
        AccountIssue.TemporaryConnection => "暂时连接失败", AccountIssue.RequestRejected => "请求被拒绝",
        AccountIssue.RestoreFailed => "恢复未完成", AccountIssue.OperationFailed => "操作未完成", _ => ""
    };
    public static string Details(AccountMarks marks) => marks.ReadFailed ? "标记读取失败；没有把账号当成正常。"
        : $"标记：{Quality(marks.Quality)} · {(marks.Mode == AccountMarkMode.Automatic ? "自动模式" : "手动模式")}\n"
        + $"来源：{(marks.QualitySource == "software" ? "软件测试" : marks.QualitySource == "trace-import" ? "导入的测试记录" : "手动设置或尚未判断")}\n"
        + (marks.MarkedAt is { } at ? $"标记时间：{at.ToLocalTime():yyyy-MM-dd HH:mm}\n" : "")
        + (marks.EvidenceAt is { } evidenceAt ? $"最近测试：{evidenceAt.ToLocalTime():yyyy-MM-dd HH:mm} · {marks.EvidenceModel}\n{marks.EvidenceReason}\n" : "")
        + (marks.Inconclusive ? "本次没判断成功；保留此前标记。\n" : "")
        + (marks.Problem ? "手动提醒：有问题\n" : "")
        + (marks.Recovered ? $"已恢复：{(marks.RecoverySource == "signal" ? "自动判断从异常变为正常" : marks.RecoverySource == "prepared-login" ? "历史授权恢复记录" : "用户确认")} · {marks.RecoveryAt?.ToLocalTime():yyyy-MM-dd HH:mm}\n恢复历史与当前标记分别保存。\n" : "")
        + (marks.Issue != AccountIssue.None ? $"上次操作：{Issue(marks.Issue)} · {marks.IssueAt?.ToLocalTime():yyyy-MM-dd HH:mm}\n这是上次操作记录，不代表持续故障。" : "尚无未解决的操作故障记录；不代表已经检测正常。");
    public static AccountIssue Classify(Exception error) => AccountEvidence.FailureIssue(error);
}

public sealed class AccountMarksEditor : ObservableObject
{
    public string Name { get; }
    internal string IdentityStamp { get; }
    internal AccountMarks OriginalMarks { get; }
    private string quality;
    private string mode;
    private bool problem;
    private bool recovered;
    private string error = "";
    public IReadOnlyList<string> QualityChoices { get; } = ["未判断", "账号正常", "账号异常"];
    public IReadOnlyList<string> ModeChoices { get; } = ["自动", "手动"];
    public string Mode { get => mode; set { if (ModeChoices.Contains(value)) Set(ref mode, value); } }
    public string Quality { get => quality; set { if (QualityChoices.Contains(value) && Set(ref quality, value)) Mode = "手动"; } }
    public bool Problem { get => problem; set => Set(ref problem, value); }
    public bool Recovered { get => recovered; set => Set(ref recovered, value); }
    public string Details { get; }
    public string Error { get => error; internal set => Set(ref error, value); }
    public AccountMarksEditor(string name, string identityStamp, AccountMarks marks)
    { Name = name; IdentityStamp = identityStamp; OriginalMarks = marks; quality = AccountMarkDisplay.Quality(marks.Quality); mode = marks.Mode == AccountMarkMode.Automatic ? "自动" : "手动"; problem = marks.Problem; recovered = marks.Recovered; Details = AccountMarkDisplay.Details(marks); }
}

public sealed partial class ShellViewModel
{
    private AccountMarksEditor? marksEditor;
    public AccountMarksEditor? MarksEditor { get => marksEditor; private set { if (Set(ref marksEditor, value)) { Raise(nameof(IsMarksEditorOpen)); SaveAccountMarksCommand?.Refresh(); } } }
    public bool IsMarksEditorOpen => MarksEditor is not null;
    public DelegateCommand EditAccountMarksCommand { get; private set; } = null!;
    public DelegateCommand SaveAccountMarksCommand { get; private set; } = null!;
    public DelegateCommand ClearAccountMarksCommand { get; private set; } = null!;
    public DelegateCommand CloseAccountMarksCommand { get; private set; } = null!;
    private void InitializeAccountMarks()
    {
        EditAccountMarksCommand = new(value =>
        {
            var account = value as IdentityItem ?? Selected;
            if (workspace is null || account?.Kind != "Account" || operationBusy || exitDraining) return;
            try
            {
                var current = identities.SingleOrDefault(i => i.Identity == account.Identity);
                if (current is null) return;
                var marks = workspace.Profiles.ReadAccountMarks(current.Name);
                Selected = current;
                MarksEditor = new(current.Name, MarkIdentityStamp(current.Name), marks);
            }
            catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException)
            { ShowFeedback("标记未打开", "账号资料无法读取，请刷新列表。", "Error"); }
        }, value => workspace is not null && (value as IdentityItem ?? Selected)?.Kind == "Account" && !operationBusy && !exitDraining);
        SaveAccountMarksCommand = new(_ => SaveAccountMarks(), _ => MarksEditor is not null && !operationBusy && !exitDraining);
        ClearAccountMarksCommand = new(_ => { if (MarksEditor is { } editor) { editor.Quality = "未判断"; editor.Mode = "手动"; editor.Problem = false; editor.Recovered = false; } });
        CloseAccountMarksCommand = new(_ => MarksEditor = null);
    }
    private string MarkIdentityStamp(string name)
    {
        var account = workspace!.Profiles.ListAccounts().SingleOrDefault(a => a.Name == name) ?? throw new IOException("Account missing");
        var id = account.HasCredentials ? workspace.Profiles.ReadAccountCredentials(name)["tokens"]?["account_id"]?.ToString() ?? "" : "";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name + "\n" + id)));
    }
    private void SaveAccountMarks()
    {
        if (workspace is null || MarksEditor is not { } editor || operationBusy || exitDraining) return;
        try
        {
            if (MarkIdentityStamp(editor.Name) != editor.IdentityStamp) { editor.Error = "账号身份已变化，请关闭并重新打开标记。"; return; }
            if (workspace.Profiles.ReadAccountMarks(editor.Name) != editor.OriginalMarks)
            { editor.Error = "标记已有新结果，请关闭并重新打开后编辑。"; return; }
            var quality = editor.Quality switch { "账号正常" => AccountQuality.Normal, "账号异常" => AccountQuality.SuspectedDegraded, _ => AccountQuality.Unmarked };
            workspace.Profiles.SetAccountMarks(editor.Name, quality, editor.Problem, editor.Recovered, mode: editor.Mode == "自动" ? AccountMarkMode.Automatic : AccountMarkMode.Manual, expected: editor.OriginalMarks);
            MarksEditor = null; ReloadProfiles(); Selected = identities.Single(i => i.Name == editor.Name);
            ShowFeedback("标记已保存", "只保存你的标记，没有发送账号请求。", "Success");
        }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException)
        { editor.Error = "标记未保存；请检查账号是否仍存在及标记文件是否可读。"; }
    }
    private string RememberAccountOperation(string name, string source, AccountIssue issue)
    {
        try { workspace!.Profiles.RecordAccountIssue(name, issue, source); ReloadProfiles(); return ""; }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException)
        { return "\n操作结果已保留，但列表中的故障记录未保存。"; }
    }
}
