using System.IO;
using System.Windows.Threading;
using FAswitch.Accounts;
using FAswitch.Core;

namespace FAswitch.Desktop;

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
    private FAswitch.Application.SwitchRequest? loginSwitchRequest;
    private bool newAccountLogin;
    private AccountOnboardingViewModel? onboarding;
    public bool IsNewAccountLogin => newAccountLogin;
    public AccountOnboardingViewModel? Onboarding => onboarding;
    public bool IsOnboardingOpen => onboarding is not null;
    private async Task ImportLocalAccountAsync()
    {
        if (workspace?.Accounts is null || workspace.OriginalHome is null || globalBusy || IsLoggingIn) return;
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => workspace.Accounts.Repository.ImportLocalLogin(workspace.OriginalHome));
            Search = ""; AccountFilter = "全部账号";
            Navigate("Accounts"); ReloadProfiles(); SyncActive();
            Selected = FilteredItems.SingleOrDefault(item => item.Identity == new IdentityRef(IdentityKind.Account, result.Profile));
            if (Selected is null) throw new InvalidOperationException("授权已保存，请刷新账号列表查看。");
            ShowFeedback(result.Created ? "本机账号已导入" : "本机账号授权已更新",
                $"{result.Profile}。可到「模型测试」选择该账号开始测试。" +
                (result.Created ? "新账号默认跟随本机网络与时区。" : "") + string.Join("；", result.Warnings) +
                "仅复制本地授权，未登录、切号或发送测试请求。授权是否仍有效，以实际请求结果为准。", "Success");
        }
        catch (Exception error) when (error is AccountException or StorageException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { ShowFeedback("本机账号未导入", ErrorRedaction.Scrub(error.Message), "Error"); }
        finally { SetBusy(false); }
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
    public string LoginStageLabel => workspace?.Accounts?.Login.Status.Stage switch { LoginStage.Checking => "正在打开本机浏览器并确认出口（复用已存评分）", LoginStage.Browser => "等待浏览器授权 · 不自动超时", LoginStage.Token => "正在交换凭据", LoginStage.Save => "正在保存登录", _ => "准备登录" };
    private void RefreshLoginProgress()
    {
        Raise(nameof(IsLoggingIn)); Raise(nameof(LoginStageLabel)); CancelLoginCommand.Refresh(); ResumeLoginCommand.Refresh();
        Raise(nameof(EmptyTitle)); Raise(nameof(EmptyDescription)); Raise(nameof(EmptyDetailDescription));
        if (workspace?.Accounts?.Login.Status is { Active: true, Stage: LoginStage.Browser, BrowserOpened: true }
            && FeedbackTitle is not ("等待浏览器授权" or "正在取消登录" or "授权页面未打开"))
            ShowFeedback("等待浏览器授权", "已打开 ChatGPT 和 Codex 授权两个标签页。先在 ChatGPT 登录，再切到授权页刷新并完成授权，账号才会自动保存。找不到授权页时点击「继续本次授权」，沿用本次会话。这里不会自动超时；关闭本次浏览器或取消即可结束。", "Info");
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
        ResumeLoginCommand = new(async _ => await ResumeLoginAsync(), _ => workspace?.Accounts?.Login.CanResumeBrowser == true);
        CancelLoginCommand = new(_ => { ClearLoginIntent(); ShowFeedback("正在取消登录", "未完成的登录凭据不会保存。", "Info"); workspace?.Accounts?.Login.Cancel(); }, _ => workspace?.Accounts?.Login.Status.Stage is LoginStage.Checking or LoginStage.Browser && IsLoggingIn);
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
                    details = $"方案：{quota.PlanType}\n请求允许：{BooleanLabel(quota.Allowed)} · 已达上限：{BooleanLabel(quota.LimitReached)}\n" + QuotaLabel("主窗口", quota.Primary) + "\n" + QuotaLabel("次窗口", quota.Secondary) + $"\n重置额度：{quota.ResetCredits.Count}\n" + string.Join("\n", quota.ResetCredits.Credits.Select(c => $"到期：{c.ExpiresAt ?? "未知"} · 状态：{c.Status ?? "未知"} · 剩余：{(c.SecondsLeft is { } seconds ? seconds + " 秒" : "未知")}")) + $"\n余额：{CreditBalance(quota.CreditsBalance)} · 有余额：{BooleanLabel(quota.HasCredits)}\n读取时间：{quota.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm}"; break;
                case AccountAction.Connectivity:
                    var test = await Task.Run(() => services.Accounts.TestAccountAsync(target.Name));
                    details = $"响应模型：{ErrorRedaction.Scrub(test.Model)}\n响应：{test.Reply}\n耗时：{test.Elapsed.TotalSeconds:N1} 秒"; break;
                case AccountAction.Refresh:
                    details = (await Task.Run(() => services.Accounts.RefreshAsync(target.Name))).Message; break;
                case AccountAction.Egress:
                    var egress = await Task.Run(() => services.Egress.ProbeAsync(target.Name));
                    success = egress.Ok;
                    if (egress.Ok) RememberExit(target, egress.Ip, egress.Timezone, string.Join(" · ", new[] { egress.Country, egress.City }.Where(part => !string.IsNullOrWhiteSpace(part))));
                    details = egress.Ok ? $"出口 IP：{egress.Ip}\n地区：{egress.Country} · {egress.City}\n时区：{egress.Timezone}" : "出口查询未完成，请检查档案代理。"; break;
                case AccountAction.IpQuality:
                    var quality = await Task.Run(() => workspace!.ProxyServices.NodeTesting!.ProbeAccountExitAsync(services.Repository.Proxy(target.Name), CancellationToken.None));
                    success = quality.Success;
                    if (quality.HasVerifiedExit) RememberExit(target, quality.Ip, quality.Timezone, string.Join(" · ", new[] { quality.Country, quality.City }.Where(part => !string.IsNullOrWhiteSpace(part))));
                    details = quality.HasVerifiedExit
                        ? $"实际出口 IP：{QualityValue(quality.Ip)}\n地区：{QualityValue(quality.Country)} · {QualityValue(quality.City)}\n时区：{QualityValue(quality.Timezone)}\n网络公司：{QualityValue(quality.Company)}\nASN：{QualityValue(quality.Asn)}\n数据中心：{BooleanLabel(quality.IsDatacenter)}\n代理标记：{BooleanLabel(quality.IsProxy)} · VPN 标记：{BooleanLabel(quality.IsVpn)}\nTor 标记：{BooleanLabel(quality.IsTor)} · 滥用标记：{BooleanLabel(quality.IsAbuser)}\n读取时间：{quality.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n来源：{quality.Source}\n{quality.Message}\n标记缺失显示未知；本次结果不作为账号能力、登录成功或 IP 无风险证明。"
                        : quality.Message + "\n未显示未经同出口核对的风险标记。";
                    details = (quality.Score?.Summary ?? "IP 质量分：未知，不放行") + "\n分组：" + quality.NetworkGroup + "\n" + quality.Admission.Message + "\n\n" + details;
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
            ShowDiagnostic(target.Identity, ActionLabel(action) + (success ? "完成" : "未完成"), details);
            ShowFeedback((workspace.IsFixture ? "隔离验证：" : "") + ActionLabel(action) + (success ? "完成" : "未完成"), $"{target.Name} 的结果已更新。" + (workspace.IsFixture ? "请求由明确的离线网络替身处理。" : ""), success ? "Success" : "Error");
        }
        catch (Exception error) when (error is AccountException or StorageException or IOException or UnauthorizedAccessException)
        { ShowDiagnostic(target.Identity, ActionLabel(action) + "未完成", ErrorRedaction.Scrub(error.Message)); ShowFeedback(ActionLabel(action) + "未完成", ErrorRedaction.Scrub(error.Message), "Error"); }
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
            var result = await Task.Run(() => services.Login.LoginAsync(target.Name));
            if (result.Ok)
            {
                ReloadProfiles(); SyncActive(); Selected = FilteredItems.FirstOrDefault(i => i.Identity == target.Identity);
                resume = loginSwitchRequest?.Target == target.Identity;
            }
            ShowLoginResult(result, false);
        }
        catch (Exception error)
        { ShowFeedback("登录未完成", error is AccountException or StorageException ? ErrorRedaction.Scrub(error.Message) : "登录处理失败：" + error.GetType().Name, "Error"); }
        finally { timer.Stop(); SetBusy(false); RefreshLoginProgress(); }
        if (resume && loginSwitchRequest is { } request) { loginSwitchRequest = null; await ExecuteSwitchRequestAsync(request); }
    }
    public async Task LoginNewAccountAsync(LoginProxySelection input, CancellationToken token)
    {
        if (workspace?.Accounts is null) return;
        var proxy = input.Proxy;
        ILoginBrowser? authorizationBrowser = null;
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
            var result = await Task.Run(() => workspace.Accounts.Login.LoginNewAsync(explicitProxy: proxy, authorizationBrowser: authorizationBrowser, cancellationToken: token));
            if (result.Ok)
            {
                Navigate("Accounts"); ReloadProfiles(); SyncActive();
                Selected = FilteredItems.SingleOrDefault(i => i.Identity == new IdentityRef(IdentityKind.Account, result.Profile));
                if (Selected is null) throw new InvalidOperationException("授权已保存，但账号列表未能读取该档案。请刷新列表。");
            }
            ShowLoginResult(result, true);
        }
        catch (Exception error)
        { ShowFeedback("登录未完成", error is AccountException or StorageException or InvalidOperationException or FAswitch.NodeTesting.MihomoProbeRuntimeException ? ErrorRedaction.Scrub(error.Message) : "登录处理失败：" + error.GetType().Name, "Error"); }
        finally
        {
            timer.Stop();
            try
            {
                if (authorizationBrowser is null) await workspace.Accounts.ReleaseUnusedAuthorizationRouteAsync(proxy);
                else
                {
                    var cleanup = await workspace.Accounts.ReleaseAuthorizationBrowserAsync(authorizationBrowser);
                    if (!cleanup.Success) ShowFeedback("登录会话清理未完成", cleanup.Message + "\n账号库中的授权资料保留。", "Error");
                }
            }
            catch (Exception error) when (error is AccountException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { ShowFeedback("登录会话清理未完成", ErrorRedaction.Scrub(error.Message) + "\n未删除未经确认的会话缓存，账号库保持保留。", "Error"); }
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
        ShowFeedback(result.Ok ? newAccount ? "账号已登录并保存" : "登录已保存" : cancelled ? "登录已取消" : "登录未完成",
            result.Message + (result.Warnings?.Count > 0 ? "。" + string.Join("；", result.Warnings) : "")
                + (workspace!.IsFixture ? "（隔离授权替身；未发真实请求）" : ""),
            result.Ok ? "Success" : cancelled ? "Info" : "Error");
    }
    private static string ActionLabel(AccountAction action) => action switch { AccountAction.Quota => "用量读取", AccountAction.Connectivity => "连通性检测", AccountAction.Refresh => "授权更新", AccountAction.Egress => "出口查询", AccountAction.IpQuality => "IP 检测", AccountAction.SuggestTimezone => "时区建议读取", AccountAction.ApplyDefaults => "时区与环境匹配", _ => "账号操作" };
    private static string QualityValue(object? value) => value?.ToString() is { Length: > 0 } text ? text : "未知";
    private void ClearLoginIntent() { loginSwitchRequest = null; LoginTarget = null; Raise(nameof(LoginTarget)); Raise(nameof(NeedsLogin)); }
    private static string QuotaLabel(string label, QuotaWindow? window) => window is null ? label + "：未知" : $"{label}（{window.WindowSeconds / 3600.0:N1} 小时）：剩余 {window.RemainingPercent:N1}% · {window.ResetAfterSeconds:N0} 秒后重置";
    private static string BooleanLabel(bool? value) => value is null ? "未知" : value.Value ? "是" : "否";
    private static string CreditBalance(System.Text.Json.Nodes.JsonNode? value) => value is System.Text.Json.Nodes.JsonValue scalar && scalar.TryGetValue<double>(out var number) ? number.ToString("N2", System.Globalization.CultureInfo.CurrentCulture) : "未知";
}
