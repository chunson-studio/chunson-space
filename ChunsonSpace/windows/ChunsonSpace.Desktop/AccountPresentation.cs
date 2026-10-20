using System.IO;
using System.Windows.Threading;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

public enum AccountAction { Quota, Connectivity, Refresh, Egress, IpQuality, SuggestTimezone, ApplyDefaults }

public sealed partial class ShellViewModel
{
    private IdentityRef? diagnosticIdentity;
    private string diagnosticTitle = "";
    private string diagnosticDetails = "";
    private bool diagnosticOpen;
    public bool IsDiagnosticOpen => diagnosticOpen && HasDiagnostics;
    public DelegateCommand CloseDiagnosticCommand { get; private set; } = null!;
    public DelegateCommand OpenDiagnosticCommand { get; private set; } = null!;
    public string DiagnosticAccount => diagnosticIdentity?.Name ?? "";
    private ChunsonSpace.Application.SwitchRequest? loginSwitchRequest;
    private bool newAccountLogin;
    private AccountOnboardingViewModel? onboarding;
    public bool IsNewAccountLogin => newAccountLogin;
    public AccountOnboardingViewModel? Onboarding => onboarding;
    public bool IsOnboardingOpen => onboarding is not null;
    private bool startupLocalImportAttempted;
    private bool importingLocalAccount;
    public string ImportLocalAccountLabel => importingLocalAccount ? "正在导入…" : "导入本机账号";
    internal Task ImportLocalAccountAtStartupAsync()
    {
        if (workspace?.ImportLocalAccountOnStartup != true || startupLocalImportAttempted) return Task.CompletedTask;
        startupLocalImportAttempted = true;
        // An explicit action that got here first owns the UI and its operation task.
        // Do not queue a second import or dismiss an editor/login started by the user.
        if (globalBusy || IsLoggingIn || IsEditorOpen || IsOnboardingOpen || IsDetailOpen) return Task.CompletedTask;
        return CurrentOperation = ImportLocalAccountAsync(automatic: true);
    }
    private async Task ImportLocalAccountAsync(bool automatic = false)
    {
        if (workspace?.Accounts is null || workspace.OriginalHome is null || globalBusy || IsLoggingIn) return;
        startupLocalImportAttempted = true;
        var single = IsSingleAccountMode;
        var previousFeedback = (feedbackTitle, feedback);
        bool CanReport() => !automatic || previousFeedback == (feedbackTitle, feedback);
        importingLocalAccount = true; Raise(nameof(ImportLocalAccountLabel));
        SetBusy(true);
        ImportedLogin? imported = null;
        try
        {
            var result = await Task.Run(() => workspace.Accounts.Repository.ImportLocalLogin(workspace.OriginalHome));
            imported = result;
            // Resolve the initial automatic layout for this session before account count changes.
            // Importing a second account must not change a single-account home into multi mode.
            if (interfaceMode == "auto") interfaceMode = single ? "single" : "multi";
            if (!automatic) { Search = ""; AccountFilter = "全部账号"; }
            ReloadProfiles(); RefreshActiveReference();
            if (!automatic)
            {
                if (!single) Navigate("Accounts");
                Selected = identities.SingleOrDefault(item => item.Identity == new IdentityRef(IdentityKind.Account, result.Profile));
                if (Selected is null) throw new InvalidOperationException("授权已保存，请刷新账号列表查看。");
            }
            if (CanReport() && (!automatic || result.Changed))
                ShowFeedback(result.Created ? "本机账号已导入" : result.Changed ? "本机账号授权已更新" : "本机账号已是最新",
                    result.Warnings.Any(w => w.Contains("清理")) ? "授权已保存，临时保存材料尚未清理。" : "", "Success");
        }
        catch (Exception error) when (error is AccountException or StorageException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (CanReport())
            {
                if (imported is not null) ShowFeedback("本机授权已保存，列表未刷新", "请刷新账号列表。", "Info");
                else if (automatic) ShowFeedback(error is AccountException { Code: AccountError.MissingCredentials }
                    ? "未找到可导入的本机账号" : "本机账号未导入", "可点击「导入本机账号」重试。", "Info");
                else ShowFeedback("本机账号未导入", ErrorRedaction.Scrub(error.Message), "Error");
            }
        }
        finally { importingLocalAccount = false; Raise(nameof(ImportLocalAccountLabel)); SetBusy(false); }
    }
    public void OpenAccountOnboarding()
    {
        if (workspace?.Accounts is null || busy) return;
        ClearLoginIntent(); IsDetailOpen = false;
        onboarding = new(workspace.Accounts.ResolveLoginProxyAsync, TrackNewAccountLoginAsync, SetBusy, CloseAccountOnboarding,
            ProxyPanel.NodeTesting, async (item, token) =>
            {
                if (!item.CanPrepareLogin) throw new InvalidOperationException("此线路不符合准入规则，请选择其他 IP。");
                if (item.Quality is { } measured && measured.Admission.Allowed)
                    return await workspace.ProxyServices.NodeTesting!.PrepareDedicatedLoginAsync(item.SubscriptionId, item.Node, measured, token);
                throw new InvalidOperationException("这条线路尚未完成评分，请等待后台检测或选择已有分数的线路。");
            });
        Raise(nameof(Onboarding)); Raise(nameof(IsOnboardingOpen));
    }
    public void CloseAccountOnboarding() { onboarding?.Dispose(); onboarding = null; Raise(nameof(Onboarding)); Raise(nameof(IsOnboardingOpen)); }
    public string DiagnosticTitle => diagnosticTitle;
    public string DiagnosticDetails => diagnosticDetails;
    public bool HasDiagnostics => diagnosticIdentity == Selected?.Identity && diagnosticDetails.Length > 0;
    public bool IsLoggingIn => workspace?.Accounts?.Login.Status.Active == true;
    public bool IsAccountPreparation => workspace?.Accounts?.Login.Status.Stage is LoginStage.AccountPreparation or LoginStage.ReadyToAuthorize && IsLoggingIn;
    public string LoginStageLabel => workspace?.Accounts?.Login.Status.Stage switch { LoginStage.Checking => "正在确认所选出口（复用已存评分）", LoginStage.AccountPreparation => "第一步：完成操作后手动关闭浏览器", LoginStage.ReadyToAuthorize => "临时浏览器已清理 · 可以开始第二步", LoginStage.Browser => "正在登录并授权 · 不自动超时", LoginStage.Token => "已收到网页授权，正在换取登录凭据", LoginStage.Save => "正在保存登录", _ => "准备登录" };
    public string LoginContinueLabel => IsAccountPreparation ? "重新登录并授权" : "继续本次授权";
    public string LoginContinueHelp => IsAccountPreparation
        ? "先重置密码、登录并退出所有设备，然后手动关闭第一步浏览器。进程和临时资料清理完成后，才能开始新的授权。"
        : "返回本次新建的 Codex 授权页，沿用已选浏览器与出口。";
    private void RefreshLoginProgress()
    {
        Raise(nameof(IsLoggingIn)); Raise(nameof(LoginStageLabel)); CancelLoginCommand.Refresh(); ResumeLoginCommand.Refresh(); CloseDetailsCommand?.Refresh();
        Raise(nameof(LoginContinueLabel)); Raise(nameof(LoginContinueHelp));
        Raise(nameof(IsAccountPreparation));
        Raise(nameof(EmptyTitle)); Raise(nameof(EmptyDescription)); Raise(nameof(EmptyDetailDescription));
        if (workspace?.Accounts?.Login.Status is { Active: true, Stage: LoginStage.AccountPreparation, BrowserOpened: true }
            && FeedbackTitle is not ("先重置密码与退出设备" or "正在取消登录" or "正在退出"))
            ShowFeedback("先重置密码与退出设备", "在官方网页登录页点「忘记密码」，用新密码登录后退出所有设备，然后手动关闭这个浏览器的所有窗口。项目会结束本次后台进程并删除临时资料，清理完成后才开放第二步。此时尚未发起 Codex 授权。", "Info");
        if (workspace?.Accounts?.Login.Status is { Active: true, Stage: LoginStage.ReadyToAuthorize }
            && FeedbackTitle is not ("第一步已清理" or "正在取消登录" or "正在退出"))
            ShowFeedback("第一步已清理", "临时浏览器进程已退出，临时资料已删除。点击「重新登录并授权」开始第二步；新的授权不会沿用第一步的登录会话。", "Info");
        if (workspace?.Accounts?.Login.Status is { Active: true, Stage: LoginStage.Browser, BrowserOpened: true }
            && FeedbackTitle is not ("等待浏览器授权" or "正在取消登录" or "授权页面未打开"))
            ShowFeedback("等待浏览器授权", "已创建新的 Codex 授权。请完成登录与授权，项目提示保存成功才算完成。此阶段若还需重置密码或退出设备，请先取消本次授权，再重新开始账号准备。找不到授权页可点击「继续本次授权」。", "Info");
    }
    public DelegateCommand LoginCommand { get; private set; } = null!;
    public DelegateCommand CancelLoginCommand { get; private set; } = null!;
    public DelegateCommand ResumeLoginCommand { get; private set; } = null!;
    public DelegateCommand OpenBrowserCommand { get; private set; } = null!;
    public DelegateCommand QuotaCommand { get; private set; } = null!;
    public DelegateCommand ConnectivityCommand { get; private set; } = null!;
    public DelegateCommand RefreshAccountCommand { get; private set; } = null!;
    public DelegateCommand EgressCommand { get; private set; } = null!;
    public DelegateCommand IpQualityCommand { get; private set; } = null!;
    public DelegateCommand SuggestTimezoneCommand { get; private set; } = null!;
    public DelegateCommand ApplyDefaultsCommand { get; private set; } = null!;
    public bool CanShowAccountActions => Selected?.Kind == "Account";
    private void InitializeAccountCommands()
    {
        CloseDiagnosticCommand = new(_ => { diagnosticOpen = false; Raise(nameof(IsDiagnosticOpen)); });
        OpenDiagnosticCommand = new(_ => { diagnosticOpen = true; Raise(nameof(IsDiagnosticOpen)); }, _ => !busy && HasDiagnostics);
        bool AccountReady() => !busy && workspace?.Accounts is not null && Selected?.Kind == "Account";
        ConfigureRegionCommand = new(_ => CurrentOperation = ConfigureRegionAsync(), _ => workspace?.LocalEnvironment is not null ? !busy && !checkingLocalEnvironment : AccountReady());
        SyncLocalTimezoneCommand = new(_ => CurrentOperation = ConfigureRegionAsync(), _ => !busy && !checkingLocalEnvironment && workspace?.Accounts is not null && workspace.LocalEnvironment is not null);
        RecheckLocalEnvironmentCommand = new(_ => LocalEnvironmentOperation = RefreshLocalEnvironmentAsync(), _ => !busy && !checkingLocalEnvironment && workspace?.LocalEnvironment is not null);
        LoginCommand = new(_ => CurrentOperation = LoginSelectedAsync(), _ => AccountReady());
        ResumeLoginCommand = new(async _ => await ResumeLoginAsync(), _ => workspace?.Accounts?.Login is { } login && (login.CanBeginAuthorization || login.CanResumeBrowser));
        CancelLoginCommand = new(_ => { ClearLoginIntent(); ShowFeedback("正在取消登录", "未完成的登录凭据不会保存。", "Info"); workspace?.Accounts?.Login.Cancel(); }, _ => workspace?.Accounts?.Login.Status.Stage is LoginStage.Checking or LoginStage.AccountPreparation or LoginStage.ReadyToAuthorize or LoginStage.Browser && IsLoggingIn);
        OpenBrowserCommand = new(_ => CurrentOperation = OpenBrowserAsync(), _ => AccountReady());
        QuotaCommand = new(_ => CurrentOperation = RunAccountActionAsync(AccountAction.Quota), _ => AccountReady());
        ConnectivityCommand = new(_ => CurrentOperation = RunAccountActionAsync(AccountAction.Connectivity), _ => AccountReady());
        RefreshAccountCommand = new(_ => CurrentOperation = RunAccountActionAsync(AccountAction.Refresh), _ => AccountReady());
        EgressCommand = new(_ => CurrentOperation = RunAccountActionAsync(AccountAction.Egress), _ => AccountReady());
        IpQualityCommand = new(_ => CurrentOperation = RunAccountActionAsync(AccountAction.IpQuality), _ => AccountReady());
        SuggestTimezoneCommand = new(_ => CurrentOperation = RunAccountActionAsync(AccountAction.SuggestTimezone), _ => AccountReady());
        ApplyDefaultsCommand = new(_ => CurrentOperation = RunAccountActionAsync(AccountAction.ApplyDefaults), _ => AccountReady());
    }
    private void RefreshAccountCommands()
    {
        foreach (var command in new[] { RecheckLocalEnvironmentCommand, SyncLocalTimezoneCommand, ConfigureRegionCommand, LoginCommand, CancelLoginCommand, ResumeLoginCommand, OpenBrowserCommand, QuotaCommand, ConnectivityCommand, RefreshAccountCommand, EgressCommand, IpQualityCommand, SuggestTimezoneCommand, ApplyDefaultsCommand }) command?.Refresh();
        Raise(nameof(CanShowAccountActions)); Raise(nameof(HasDiagnostics)); Raise(nameof(IsDiagnosticOpen)); OpenDiagnosticCommand?.Refresh();
    }
    private async Task ResumeLoginAsync()
    {
        if (workspace?.Accounts is null) return;
        try
        {
            if (workspace.Accounts.Login.CanBeginAuthorization)
            {
                if (workspace.Accounts.Login.BeginAuthorization()) ShowFeedback("正在创建新的授权", "账号准备阶段已结束，正在打开新的 Codex 授权页。", "Info");
                return;
            }
            if (await workspace.Accounts.Login.ResumeBrowserAsync() && IsLoggingIn)
                ShowFeedback("等待浏览器授权", "已在原来的英文浏览器档案中打开本次 Codex 授权页。完成授权后账号会自动保存；没有重新测分。", "Info");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoggingIn) ShowFeedback("授权页面未打开", error is AccountException ? ErrorRedaction.Scrub(error.Message) : "无法重新打开本次授权页，请确认档案浏览器仍在运行。", "Error");
        }
        finally { ResumeLoginCommand.Refresh(); }
    }
    public async Task RunAccountActionAsync(AccountAction action)
    {
        if (workspace?.Accounts is null || Selected is null || busy) return;
        var target = Selected; var services = workspace.Accounts;
        SetBusy(true);
        try
        {
            string details; bool success = true;
            switch (action)
            {
                case AccountAction.Quota:
                    var quota = await Task.Run(() => services.Accounts.FetchQuotaAsync(target.Name));
                    details = $"方案：{quota.PlanType}\n请求允许：{BooleanLabel(quota.Allowed)} · 已达上限：{BooleanLabel(quota.LimitReached)}\n" + QuotaLabel("主窗口", quota.Primary) + "\n" + QuotaLabel("次窗口", quota.Secondary) + $"\n重置额度：{quota.ResetCredits.Count}\n" + string.Join("\n", quota.ResetCredits.Credits.Select(c => $"到期：{c.ExpiresAt ?? "未知"} · 状态：{c.Status ?? "未知"} · 剩余：{(c.SecondsLeft is { } seconds ? seconds + " 秒" : "未知")}")) + $"\n余额：{CreditBalance(quota.CreditsBalance)} · 有余额：{BooleanLabel(quota.HasCredits)}\n读取时间：{quota.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n授权令牌：{(quota.Refreshed ? "已按需刷新" : "使用已保存授权")}"; break;
                case AccountAction.Connectivity:
                    var test = await Task.Run(() => services.Accounts.TestAccountAsync(target.Name));
                    details = $"响应模型：{ErrorRedaction.Scrub(test.Model)}\n响应：{test.Reply}\n耗时：{test.Elapsed.TotalSeconds:N1} 秒"; break;
                case AccountAction.Refresh:
                    details = (await Task.Run(() => services.Accounts.RefreshAsync(target.Name))).Message; break;
                case AccountAction.Egress:
                    var egress = await Task.Run(() => services.Egress.ProbeAsync(target.Name));
                    success = egress.Ok;
                    if (egress.Ok) RememberExit(target, egress.Ip, egress.Timezone, string.Join(" · ", new[] { egress.Country, egress.City }.Where(part => !string.IsNullOrWhiteSpace(part))), egress.Addresses);
                    details = egress.Addresses.Summary + "\n" + (egress.Ok ? $"主出口：{egress.Ip}\n地区：{egress.Country} · {egress.City}\n时区：{egress.Timezone}" : egress.Message); break;
                case AccountAction.IpQuality:
                    var quality = await Task.Run(() => workspace!.ProxyServices.NodeTesting!.ProbeAccountExitAsync(services.Repository.Proxy(target.Name), CancellationToken.None));
                    success = quality.Success;
                    if (quality.HasVerifiedExit) RememberExit(target, quality.Ip, quality.Timezone, string.Join(" · ", new[] { quality.Country, quality.City }.Where(part => !string.IsNullOrWhiteSpace(part))), quality.Addresses);
                    details = quality.HasVerifiedExit
                        ? $"实际出口 IP：{QualityValue(quality.Ip)}\n地区：{QualityValue(quality.Country)} · {QualityValue(quality.City)}\n时区：{QualityValue(quality.Timezone)}\n网络公司：{QualityValue(quality.Company)}\nASN：{QualityValue(quality.Asn)}\n数据中心：{BooleanLabel(quality.IsDatacenter)}\n代理标记：{BooleanLabel(quality.IsProxy)} · VPN 标记：{BooleanLabel(quality.IsVpn)}\nTor 标记：{BooleanLabel(quality.IsTor)} · 滥用标记：{BooleanLabel(quality.IsAbuser)}\n读取时间：{quality.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n来源：{quality.Source}\n{quality.Message}\n标记缺失显示未知；本次结果不作为账号能力、登录成功或 IP 无风险证明。"
                        : quality.Message + "\n未显示未经同出口核对的风险标记。";
                    details = quality.Addresses.Summary + "\n" + (quality.Score?.Summary ?? "IP 质量分：未知，不放行") + "\n分组：" + quality.NetworkGroup + "\n" + quality.Admission.Message + "\n\n" + details;
                    break;
                case AccountAction.SuggestTimezone:
                    var suggestion = await Task.Run(() => services.Egress.SuggestTimezoneAsync(target.Name));
                    details = suggestion.Current is not null ? "已配置时区：" + suggestion.Current : $"出口地区：{suggestion.CountryCode}\n" + string.Join("\n", suggestion.Candidates.Select(c => c.Label + " · " + c.Timezone)) + "\n在编辑配置中选择所需时区。"; break;
                case AccountAction.ApplyDefaults:
                    var defaults = await Task.Run(() => services.Egress.MatchEnvironmentAsync(target.Name));
                    success = defaults.Egress.Ok;
                    details = success ? $"出口 IP：{defaults.Egress.Ip}\n国家：{CountryNames.Display(defaults.Egress.CountryCode, defaults.Egress.Country)}\n账号时区：{defaults.Environment.Timezone}\n界面语言：{defaults.Environment.Locale}\n"
                        + (defaults.Changed.Count == 0 ? "档案已匹配，无需修改。" : "已更新账号档案：" + string.Join("、", defaults.Changed)) + "\nWindows 系统设置未在此操作中修改。"
                        : "无法确认实际出口时区，未修改账号档案。";
                    ReloadProfiles(); SyncActive(); Selected = FilteredItems.FirstOrDefault(i => i.Identity == target.Identity); break;
                default: throw new ArgumentOutOfRangeException(nameof(action));
            }
            details += RememberAccountOperation(target.Name, action.ToString(), success ? AccountIssue.None : AccountIssue.OperationFailed);
            ShowDiagnostic(target.Identity, ActionLabel(action) + (success ? "完成" : "未完成"), details);
            ShowFeedback((workspace.IsFixture ? "隔离验证：" : "") + ActionLabel(action) + (success ? "完成" : "未完成"), $"{target.Name} 的结果已更新。" + (workspace.IsFixture ? "请求由明确的离线网络替身处理。" : ""), success ? "Success" : "Error");
        }
        catch (Exception error) when (error is AccountException or StorageException or IOException or UnauthorizedAccessException)
        { var saved = RememberAccountOperation(target.Name, action.ToString(), AccountMarkDisplay.Classify(error)); ShowDiagnostic(target.Identity, ActionLabel(action) + "未完成", ErrorRedaction.Scrub(error.Message) + saved); ShowFeedback(ActionLabel(action) + "未完成", ErrorRedaction.Scrub(error.Message), "Error"); }
        finally { SetBusy(false); }
    }
    public async Task OpenBrowserAsync()
    {
        if (workspace?.Accounts is null || Selected?.Kind != "Account" || busy) return;
        var target = Selected; SetBusy(true);
        try
        {
            var opened = await Task.Run(() => workspace.Accounts.OpenProfileBrowserAsync(target.Name));
            ShowFeedback(workspace.IsFixture ? "隔离浏览器流程完成" : "档案浏览器已打开", target.Name + "。" + string.Join("；", opened.Warnings), "Info");
        }
        catch (Exception error) when (error is AccountException or StorageException or IOException or UnauthorizedAccessException)
        {
            var detail = ErrorRedaction.Scrub(error.Message);
            if (workspace.Accounts.Browser is IBrowserSessionCleanup cleanup)
            {
                var ended = await cleanup.CleanupAsync(null, CancellationToken.None);
                if (!ended.Success) detail += " " + ended.Message;
            }
            ShowFeedback("浏览器未打开", detail, "Error");
        }
        finally { SetBusy(false); }
    }
    public async Task LoginSelectedAsync()
    {
        if (workspace?.Accounts is null || Selected?.Kind != "Account" || busy) return;
        var target = Selected; var services = workspace.Accounts; SetBusy(true);
        if (loginSwitchRequest is { } oldIntent && oldIntent.Target != target.Identity) ClearLoginIntent();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => RefreshLoginProgress();
        timer.Start(); bool resume = false;
        try
        {
            var result = await Task.Run(() => services.Login.LoginAsync(target.Name, prepareAccount: true));
            if (result.Ok)
            {
                ReloadProfiles(); SyncActive(); Selected = FilteredItems.FirstOrDefault(i => i.Identity == target.Identity);
                resume = loginSwitchRequest?.Target == target.Identity;
            }
            ReloadProfiles();
            ShowLoginResult(result, false);
        }
        catch (Exception error)
        { ShowFeedback("登录未完成", error is AccountException or StorageException ? ErrorRedaction.Scrub(error.Message) : "登录处理失败：" + error.GetType().Name, "Error"); }
        finally { timer.Stop(); SetBusy(false); RefreshLoginProgress(); }
        if (resume && loginSwitchRequest is { } request) { loginSwitchRequest = null; await ExecuteSwitchRequestAsync(request); }
    }
    public async Task LoginNewAccountAsync(LoginProxySelection input, CancellationToken token, bool prepareAccount = true)
    {
        if (workspace?.Accounts is null) return;
        var proxy = input.Proxy;
        ILoginBrowser? authorizationBrowser = null;
        LoginResult? loginResult = null;
        void ReportCleanupIssue(string message)
        {
            var title = loginResult?.Ok == true ? "账号已保存，浏览器清理待完成"
                : loginResult?.Error == AccountError.Cancelled || loginResult?.Callback is CallbackKind.Cancelled or CallbackKind.BrowserClosed
                    ? "登录已取消，浏览器清理待完成" : FeedbackTitle;
            ShowFeedback(title, Feedback + "\n会话清理：" + ErrorRedaction.Scrub(message), "Error",
                feedbackDiagnostics + "\n会话清理：未确认");
        }
        CloseAccountOnboarding();
        ClearLoginIntent(); SetBusy(true); newAccountLogin = true; Raise(nameof(IsNewAccountLogin));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => RefreshLoginProgress();
        timer.Start();
        try
        {
            token.ThrowIfCancellationRequested();
            authorizationBrowser = workspace.Accounts.CreateAuthorizationBrowser(proxy, input.IndependentBrowserTimezone);
            ShowFeedback("正在准备英文浏览器", "已识别 " + input.Source + "。浏览器先显示英文准备页，确认出口并通过线路准入后进入登录。Windows 继续使用中文。", "Info");
            var result = await Task.Run(() => workspace.Accounts.Login.LoginNewAsync(explicitProxy: proxy, authorizationBrowser: authorizationBrowser, cancellationToken: token, prepareAccount: prepareAccount));
            loginResult = result;
            if (result.Ok)
            {
                Navigate("Accounts"); ReloadProfiles(); SyncActive();
                Selected = FilteredItems.SingleOrDefault(i => i.Identity == new IdentityRef(IdentityKind.Account, result.Profile));
                if (Selected is null) throw new InvalidOperationException("授权已保存，但账号列表未能读取该档案。请刷新列表。");
            }
            ShowLoginResult(result, true);
        }
        catch (Exception error)
        {
            if (loginResult?.Ok == true)
            {
                ShowLoginResult(loginResult, true);
                ShowFeedback("账号已保存，列表刷新未完成", Feedback + "\n请刷新账号列表，无需重复授权。", "Error", feedbackDiagnostics);
            }
            else ShowFeedback("登录未完成", error is AccountException or StorageException or InvalidOperationException or ChunsonSpace.NodeTesting.MihomoProbeRuntimeException ? ErrorRedaction.Scrub(error.Message) : "登录处理失败：" + error.GetType().Name, "Error");
        }
        finally
        {
            timer.Stop();
            try
            {
                if (authorizationBrowser is null) await workspace.Accounts.ReleaseUnusedAuthorizationRouteAsync(proxy);
                else
                {
                    var cleanup = await workspace.Accounts.ReleaseAuthorizationBrowserAsync(authorizationBrowser);
                    if (!cleanup.Success) ReportCleanupIssue(cleanup.Message + "\n账号库中的授权资料保留。");
                }
            }
            catch (Exception error) when (error is AccountException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { ReportCleanupIssue(error.Message + "\n未删除未经确认的会话缓存，账号库保持保留。"); }
            newAccountLogin = false; Raise(nameof(IsNewAccountLogin)); SetBusy(false);
            RefreshLoginProgress();
        }
    }
    private void ShowDiagnostic(IdentityRef identity, string title, string details)
    {
        diagnosticIdentity = identity; diagnosticTitle = title; diagnosticDetails = details;
        diagnosticOpen = true;
        Raise(nameof(DiagnosticTitle)); Raise(nameof(DiagnosticAccount)); Raise(nameof(DiagnosticDetails)); Raise(nameof(HasDiagnostics)); Raise(nameof(IsDiagnosticOpen)); OpenDiagnosticCommand.Refresh();
    }
    private void ShowLoginResult(LoginResult result, bool newAccount)
    {
        var cancelled = result.Callback is CallbackKind.Cancelled or CallbackKind.BrowserClosed || result.Error == AccountError.Cancelled;
        var diagnostics = "\n登录阶段：" + (result.FailedStage?.ToString() ?? (result.Ok ? "Done" : "Unknown"))
            + "\n回调状态：" + (result.Callback?.ToString() ?? "None")
            + "\n错误类别：" + (result.Error?.ToString() ?? "None")
            + (result.TransportFailure is null ? "" : "\n传输分类：" + System.Text.Json.JsonSerializer.Serialize(result.TransportFailure));
        ShowFeedback(result.Ok ? newAccount ? "账号已登录并保存" : "登录已保存" : cancelled ? "登录已取消"
                : result.FailedStage == LoginStage.Token ? "网页授权已收到，凭据未保存" : result.FailedStage == LoginStage.Save ? "登录凭据保存未完成" : "登录未完成",
            result.Message + (result.Warnings?.Count > 0 ? "\n" + string.Join("；", result.Warnings) : "")
                + (workspace!.IsFixture ? "（隔离授权替身；未发真实请求）" : ""),
            result.Ok ? "Success" : cancelled ? "Info" : "Error", diagnostics);
    }
    private static string ActionLabel(AccountAction action) => action switch { AccountAction.Quota => "用量读取", AccountAction.Connectivity => "连通性检测", AccountAction.Refresh => "授权更新", AccountAction.Egress => "出口查询", AccountAction.IpQuality => "IP 检测", AccountAction.SuggestTimezone => "时区建议读取", AccountAction.ApplyDefaults => "时区与环境匹配", _ => "账号操作" };
    private static string QualityValue(object? value) => value?.ToString() is { Length: > 0 } text ? text : "未知";
    private void ClearLoginIntent() { loginSwitchRequest = null; LoginTarget = null; Raise(nameof(LoginTarget)); Raise(nameof(NeedsLogin)); }
    private static string QuotaLabel(string label, QuotaWindow? window) => window is null ? label + "：未知" : $"{label}（{window.WindowSeconds / 3600.0:N1} 小时）：剩余 {window.RemainingPercent:N1}% · {window.ResetAfterSeconds:N0} 秒后重置";
    private static string BooleanLabel(bool? value) => value is null ? "未知" : value.Value ? "是" : "否";
    private static string CreditBalance(System.Text.Json.Nodes.JsonNode? value) => value is System.Text.Json.Nodes.JsonValue scalar && scalar.TryGetValue<double>(out var number) ? number.ToString("N2", System.Globalization.CultureInfo.CurrentCulture) : "未知";
}
