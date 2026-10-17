using FAswitch.Core;
using FAswitch.Application;
using System.IO;

namespace FAswitch.Desktop;

public sealed partial class ShellViewModel
{
    private DesktopWorkspace? workspace;
    private ProfileEditorViewModel? editor;
    private bool autoSystemTimezone = true;
    private bool quitConfirm = true;
    private bool operationBusy;
    private bool busy => operationBusy || exitDraining || accountTests.IsBusy(Selected?.Name);
    private bool globalBusy => operationBusy || exitDraining || IsModelTestRunning;
    private IdentityRef? runningIdentity;
    private bool launchInProgress;
    private bool runtimeExited;
    private bool runtimeProcessAlive;
    private OperationOutcome? lastOutcome;
    private bool darkTheme;
    private bool trayAvailable;
    public IdentityRef? LoginTarget { get; private set; }
    public bool NeedsLogin => LoginTarget is not null;
    public bool TrayAvailable => trayAvailable;
    public string TrayDescription => trayAvailable ? "关闭窗口后留在托盘。使用托盘菜单或下方按钮退出。" : "当前验证/演示模式不显示系统托盘。";
    public event EventHandler? ExitRequested;
    public DelegateCommand ExitCommand { get; private set; } = null!;
    public bool DarkTheme { get => darkTheme; set { if (Set(ref darkTheme, value)) WorkspaceTheme.Apply(value ? "dark" : "light"); } }
    public bool IsPreview => workspace is null;
    public string WorkspaceBadge => workspace is null ? "演示环境" : workspace.IsFixture ? "隔离工作区" : "本地工作区";
    public string WorkspaceModeLabel => workspace is null ? "原生界面 · 演示模式" : workspace.IsFixture ? "真实存储 · 隔离验证" : "真实存储 · 显式目录";
    public string WorkspaceDescription => workspace is null ? "当前界面为演示模式。它不读取账号凭据，不连接客户端，也不更改系统环境。" : $"账号库：{workspace.AccountRoot}。授权资料、登录配置与备份保存在此目录。";
    public string ManagedCloneDescription => workspace?.ManagedPaths is { } paths ? $"唯一受管分身：{paths.RuntimeRoot}\n切换时重建分身并载入所选账号，不导入本体 Skills、MCP 或插件。账号和聊天独立。" : workspace is null ? "唯一受管分身 · 演示状态" : "隔离兼容目录；未连接实际客户端。";
    public string ManagedCloneStatus => "受管分身 · " + RunningLabel;
    public string LaunchEgressDescription => workspace?.Switching?.LastEgressEvidence is { } evidence
        ? LaunchEgressRules.Verified(evidence) ? $"{(workspace.IsFixture ? "离线出口替身" : "最近启动前验证")} · 出口 IP：{evidence.Ip} · 时区：{evidence.Timezone}" : "启动前出口验证未完成：" + evidence.Message
        : "启动前出口与时区：尚未验证";
    public bool UsesManagedClone => workspace?.ManagedPaths is not null;
    public bool CanSaveCurrentArchive => !UsesManagedClone;
    public string EnvironmentSummary => UsesManagedClone ? "唯一干净受管分身 · 授权资料独立保留" : workspace is null ? "环境与会话隔离 · 演示配置" : IsolateSessions ? "独立会话与桌面数据" : "共享会话与桌面数据";
    public string SwitchDescription => workspace is null ? "模拟动作只改变界面中的演示状态。" : UsesManagedClone ? "启动所选账号；账号和聊天保持独立，不导入本体 Skills、MCP 或插件。" : workspace.IsFixture ? "实际迁移隔离目录中的文件；进程与时区使用替身。" : "先关闭客户端，再迁移档案与环境。";
    public string LaunchActionLabel => workspace is null ? "⇄   模拟切换并启动" : "⇄   切换并启动";
    public string SwitchActionLabel => workspace is null ? "模拟切换" : "只切换";
    public string BackupActionLabel => workspace is null ? "模拟创建备份" : "创建备份";
    public string RestoreActionLabel => workspace is null ? "模拟恢复" : "恢复此备份";
    public string BackupDescription => workspace is null ? "演示快照仅在本窗口展示；不会读取或写入文件。" : BackupService.BackupNote;
    public string SettingsDescription => workspace is null ? "演示选项，仅在当前窗口内保存。" : UsesManagedClone ? "设置保存在账号库。地区和时区按所选出口设置；退出或失败不自动恢复。" : "设置保存到当前工作区。会话隔离同时管理桌面数据。";
    public string SettingsSaveLabel => workspace is null ? "应用演示设置" : "保存设置";
    public bool AutoSystemTimezone { get => autoSystemTimezone; set => Set(ref autoSystemTimezone, value); }
    public bool QuitConfirm { get => quitConfirm; set => Set(ref quitConfirm, value); }
    public bool IsBusy => busy;
    public bool IsEditorOpen => editor is not null;
    public ProfileEditorViewModel? Editor => editor;
    public DelegateCommand NewProfileCommand { get; private set; } = null!;
    public DelegateCommand NewAccountLoginCommand { get; private set; } = null!;
    public DelegateCommand ImportLocalAccountCommand { get; private set; } = null!;
    public DelegateCommand EditSelectedCommand { get; private set; } = null!;
    public DelegateCommand RenameSelectedCommand { get; private set; } = null!;
    public DelegateCommand ArchiveSelectedCommand { get; private set; } = null!;
    public DelegateCommand CreateBackupCommand { get; private set; } = null!;
    public DelegateCommand RestoreBackupCommand { get; private set; } = null!;
    public DelegateCommand SwitchCommand { get; private set; } = null!;
    public DelegateCommand LaunchCommand { get; private set; } = null!;
    public DelegateCommand LaunchCurrentCommand { get; private set; } = null!;
    public DelegateCommand SaveCurrentAccountCommand { get; private set; } = null!;
    public DelegateCommand LogoutCommand { get; private set; } = null!;
    public DelegateCommand RebuildCommand { get; private set; } = null!;
    public DelegateCommand RebuildOriginalCommand { get; private set; } = null!;
    public DelegateCommand RefreshCommand { get; private set; } = null!;
    public Task CurrentOperation { get; private set; } = Task.CompletedTask;

    private void InitializeWorkspace(DesktopWorkspace? value)
    {
        workspace = value;
        InitializeAccountCommands();
        InitializeInfrastructureCommands();
        InitializeProxyCommands();
        InitializeVisualTestCommands();
        ExitCommand = new(_ => ExitRequested?.Invoke(this, EventArgs.Empty), _ => !exitDraining);
        NewProfileCommand = new(_ => OpenEditor(ProfileEditMode.Create), _ => workspace is not null && !busy && !UsesManagedClone);
        NewAccountLoginCommand = new(_ => OpenAccountOnboarding(), _ => workspace?.Accounts is not null && !busy);
        ImportLocalAccountCommand = new(_ => CurrentOperation = ImportLocalAccountAsync(), _ => workspace?.Accounts is not null && workspace.OriginalHome is not null && !globalBusy && !IsLoggingIn);
        EditSelectedCommand = new(_ => OpenEditor(ProfileEditMode.Edit), _ => workspace is not null && Selected is not null && !busy);
        RenameSelectedCommand = new(_ => OpenEditor(ProfileEditMode.Rename), _ => workspace is not null && Selected is not null && !SelectedIsActive && !busy);
        ArchiveSelectedCommand = new(_ => OpenEditor(ProfileEditMode.Archive), _ => workspace is not null && Selected is not null && !SelectedIsActive && !busy);
        CreateBackupCommand = new(_ => { if (workspace is null) SimulateBackupCommand.Execute(null); else CurrentOperation = CreateBackupAsync(); }, _ => !globalBusy);
        RestoreBackupCommand = new(p => { if (workspace is null) SimulateRestoreCommand.Execute(null); else if (p is BackupItem item) CurrentOperation = RestoreBackupAsync(item); }, _ => !globalBusy && (workspace is null || workspace.Maintenance is not null));
        SwitchCommand = new(_ => { if (workspace is null) SimulateSwitchCommand.Execute(null); else CurrentOperation = SwitchSelectedAsync(false); }, _ => !busy && Selected is not null && (workspace is null || workspace.Switching is not null));
        LaunchCommand = new(_ => { if (workspace is null) SimulateLaunchCommand.Execute(null); else CurrentOperation = SwitchSelectedAsync(true); }, _ => !busy && Selected is not null && (workspace is null || workspace.Switching is not null));
        LaunchCurrentCommand = new(_ => { if (workspace is null) ShowFeedback("模拟启动当前身份", $"演示目标：{ActiveLabel}。没有启动真实客户端。", "Info"); else CurrentOperation = LaunchCurrentAsync(); }, _ => !busy && Active is not null && (workspace is null || workspace.Switching is not null));
        SaveCurrentAccountCommand = new(_ => OpenEditor(ProfileEditMode.SaveCurrent), _ => !busy && !UsesManagedClone && workspace?.Maintenance is not null);
        LogoutCommand = new(_ => OpenEditor(ProfileEditMode.Logout), _ => !globalBusy && workspace?.Maintenance is not null);
        RebuildCommand = new(_ => OpenEditor(ProfileEditMode.Rebuild), _ => !globalBusy && workspace?.Maintenance is not null);
        RebuildOriginalCommand = new(_ => OpenEditor(ProfileEditMode.RebuildOriginal), _ => !globalBusy && workspace?.OriginalMaintenance is not null && workspace.HasOriginalRebuild);
        RefreshCommand = new(_ => { try { ReloadProfiles(); SyncActive(); RefreshBackups(); ShowFeedback("列表已刷新", "已重新读取档案与活动身份，未发送账号请求。", "Info"); } catch (Exception error) { ShowFeedback("列表未刷新", "读取失败：" + error.GetType().Name, "Error"); } }, _ => !busy && workspace is not null);
        InitializeWorkspaceCards();
        InitializeSimpleActions();
        if (workspace is not null)
        {
            ReloadProfiles();
            SyncActive();
            var settings = workspace.Profiles.ReadSettings();
            isolateSessions = settings.SessionIsolation; autoSystemTimezone = settings.AutoSystemTimezone; quitConfirm = settings.QuitConfirm;
            darkTheme = settings.WorkspaceTheme == "dark";
            RefreshBackups();
            ShowFeedback("工作区已连接", workspace.IsFixture ? "实际读写隔离工作区；客户端进程与系统时区均使用离线替身。" : "账号档案与客户端环境已读取。选择目标后可切换或启动。", "Info");
        }
        WorkspaceTheme.Apply(darkTheme ? "dark" : "light");
    }
    public void OpenEditor(ProfileEditMode mode)
    {
        if (workspace is null || busy) return;
        if (UsesManagedClone && mode == ProfileEditMode.SaveCurrent) return;
        if (UsesManagedClone && mode == ProfileEditMode.Create) { OpenAccountOnboarding(); return; }
        var kind = IdentityKind.Account;
        var original = mode is ProfileEditMode.Create or ProfileEditMode.Logout or ProfileEditMode.Rebuild or ProfileEditMode.RebuildOriginal ? null : mode == ProfileEditMode.SaveCurrent ? Active : Selected;
        if (mode is ProfileEditMode.Edit or ProfileEditMode.Rename or ProfileEditMode.Archive && original is null) return;
        editor = new(mode, kind, original, SaveEditorAsync, CloseEditor)
        {
            UseVerifiedEnvironment = UsesManagedClone,
            IpPool = UsesManagedClone ? ProxyPanel.NodeTesting : null,
            PickRandomIp = UsesManagedClone && original is not null && kind == IdentityKind.Account ? async () =>
            {
                var current = await workspace.Accounts!.Routes!.AssignedIpAsync(original.Proxy);
                if (current.Length == 0) current = workspace.Profiles.ReadExitObservation(original.Identity)?.Ip ?? "";
                return await ProxyPanel.NodeTesting.ChooseIndependentAsync(current, random: true);
            } : null
        };
        if (mode == ProfileEditMode.Rebuild && workspace.ManagedPaths is { } paths)
        {
            editor.Details = "仅清空重建：\n" + paths.RuntimeRoot + "\n\n保留账号库：\n" + paths.AccountRoot + "\n\n保留独立 Codex 本体及用户项目。";
        }
        else if (mode is ProfileEditMode.Rebuild or ProfileEditMode.RebuildOriginal)
        {
            try {
            var plan = mode == ProfileEditMode.RebuildOriginal ? workspace.OriginalMaintenance!.PreviewRebuild(workspace.OriginalRebuild!) : workspace.Maintenance!.PreviewRebuild(workspace.Rebuild);
            editor.Details = $"将归档 {plan.Files} 个文件，{plan.Bytes:N0} 字节。\n\n" + string.Join("\n", plan.Items.Select(i => i.Label + "\n" + i.Path)) + "\n\n保留：\n" + string.Join("\n", plan.Kept.Select(i => i.Label + "\n" + i.Path));
            } catch (StorageException error) { CloseEditor(); ShowFeedback("无法预览环境重建", error.Message, "Error"); return; }
        }
        Raise(nameof(Editor)); Raise(nameof(IsEditorOpen));
    }
    public void CloseEditor() { editor = null; Raise(nameof(Editor)); Raise(nameof(IsEditorOpen)); }
    private async Task SaveEditorAsync(ProfileEditorViewModel input)
    {
        if (workspace is null) throw new InvalidOperationException("未连接本地存储。");
        if (operationBusy || exitDraining) throw new InvalidOperationException("当前操作未完成，请稍后保存。");
        if (accountTests.IsBusy(input.Original?.Name ?? input.Name)) throw new InvalidOperationException("此账号正在测试，请等待测试结束后保存。");
        if (IsModelTestRunning && input.Mode is ProfileEditMode.Logout or ProfileEditMode.Rebuild or ProfileEditMode.RebuildOriginal)
            throw new InvalidOperationException("后台测试尚未结束，请等待或取消测试后再执行此操作。");
        SetBusy(true);
        try
        {
        SyncActive();
        var store = workspace.Profiles;
        MaintenanceResult? maintenance = null;
        string extraFeedback = "";
        switch (input.Mode)
        {
            case ProfileEditMode.Create when input.Kind == IdentityKind.Account:
                input.Name = ProfileRules.Name(input.Name);
                store.CreateBoundAccount(input.Name, new(input.FollowSystemProxy ? "" : input.Proxy, input.Timezone, input.Locale, FollowSystemProxy: input.FollowSystemProxy));
                break;
            case ProfileEditMode.Edit when input.Kind == IdentityKind.Account:
                if (UsesManagedClone) extraFeedback = await SaveAccountNetworkModeAsync(input);
                else store.SaveEnvironment(input.Original!.Name, new(input.FollowSystemProxy ? "" : input.Proxy, input.Timezone, input.Locale, FollowSystemProxy: input.FollowSystemProxy));
                break;
            case ProfileEditMode.Rename:
                if (input.Original!.Identity == Active?.Identity) throw new InvalidOperationException("请先切换活动身份再重命名。");
                store.Rename(input.Original.Identity, input.Name); break;
            case ProfileEditMode.Archive:
                if (input.Original!.Identity == Active?.Identity) throw new InvalidOperationException("请先切换活动身份再归档。");
                maintenance = await Task.Run(() => workspace.Maintenance!.DeleteAccountAsync(workspace.ProfileMaintenance, input.Original.Name));
                break;
            case ProfileEditMode.SaveCurrent:
                maintenance = await Task.Run(() => workspace.Maintenance!.SaveCurrentAccountAsync(workspace.ProfileMaintenance, input.Name, input.Overwrite, IsolateSessions)); break;
            case ProfileEditMode.Logout:
                maintenance = await Task.Run(() => workspace.SwitchStorage is ManagedCloneStorage logoutClone ? workspace.Maintenance!.ResetManagedRuntimeAsync(logoutClone) : workspace.Maintenance!.LogoutAsync(workspace.ProfileMaintenance)); break;
            case ProfileEditMode.Rebuild:
                maintenance = await Task.Run(() => workspace.SwitchStorage is ManagedCloneStorage clone ? workspace.Maintenance!.ResetManagedRuntimeAsync(clone) : workspace.Maintenance!.RebuildAsync(workspace.Rebuild)); break;
            case ProfileEditMode.RebuildOriginal:
                maintenance = await Task.Run(() => workspace.OriginalMaintenance!.RebuildAsync(workspace.OriginalRebuild!)); break;
        }
        if (maintenance is { Success: false }) throw new InvalidOperationException(maintenance.Message + (maintenance.RecoveryPaths.Count > 0 ? "。恢复材料：" + string.Join("；", maintenance.RecoveryPaths) : ""));
        if (maintenance is not null && input.Mode != ProfileEditMode.RebuildOriginal) { runningIdentity = null; runtimeProcessAlive = false; runtimeExited = false; lastOutcome = null; ClearExitObservations(); Raise(nameof(RunningLabel)); Raise(nameof(ManagedCloneStatus)); NotifyHomeDetails(); }
        var target = input.Mode == ProfileEditMode.Archive ? null : input.Name;
        CloseEditor(); ReloadProfiles(); SyncActive();
        Selected = FilteredItems.FirstOrDefault(i => i.Name == target && i.Identity.Kind == input.Kind) ?? FilteredItems.FirstOrDefault();
        ShowFeedback(input.Mode switch { ProfileEditMode.Logout => "分身登录已退出", ProfileEditMode.Rebuild => "受管分身已清理", ProfileEditMode.RebuildOriginal => "Codex 本体已清理", ProfileEditMode.Archive => "档案已归档", _ => "档案已保存" }, maintenance is null ? "已写入当前工作区，并从存储重新读取。" + extraFeedback : maintenance.Message + (maintenance.RecoveryPaths.Count > 0 ? "。恢复材料：" + string.Join("；", maintenance.RecoveryPaths) : ""), "Success");
        }
        finally { SetBusy(false); }
    }
    private void ReloadProfiles()
    {
        if (workspace is null) return;
        var accounts = workspace.Profiles.ListAccounts().Select((a, index) => new IdentityItem(a.Name, "Account", a.HasCredentials ? "已保存登录资料" : "待登录", Initial(a.Name), AvatarColor(index), a.Environment.Proxy, a.Environment.Timezone, a.Environment.Locale, "Codex 默认", a.Environment.FollowSystemProxy));
        identities = accounts.ToArray();
        LoadExitObservations();
        Selected = null; UpdateItems(); Selected = FilteredItems.FirstOrDefault();
    }
    private static string Initial(string name) => name.Length > 0 ? System.Globalization.StringInfo.GetNextTextElement(name) : "?";
    private static string AvatarColor(int index) => new[] { "#527545", "#49725E", "#687B4D", "#54766C" }[index % 4];
    private void SaveSettings()
    {
        if (workspace is null) { ShowFeedback("演示设置已应用", "这些演示设置仅在当前窗口保存。", "Info"); return; }
        try
        {
            var existing = workspace.Profiles.ReadSettings();
            workspace.Profiles.SaveSettings(existing with { SessionIsolation = IsolateSessions, AutoSystemTimezone = AutoSystemTimezone, QuitConfirm = QuitConfirm, WorkspaceTheme = DarkTheme ? "dark" : "light" });
            ShowFeedback("设置已保存", "已写入当前工作区；系统时区未改变。", "Success");
            Raise(nameof(EnvironmentSummary));
        }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException) { ShowFeedback("设置未保存", error.Message, "Error"); }
    }
    private void RefreshBackups()
    {
        if (workspace is null) return;
        var result = workspace.BackupService.List();
        Backups = result.Items.Select(b => new BackupItem(b.Name, b.Time, $"{b.Count} 个文件 · {b.Bytes:N0} 字节")).ToArray();
        Raise(nameof(Backups));
        ResetBackupPage();
        if (result.Issues.Count > 0) ShowFeedback("部分备份无法读取", string.Join("；", result.Issues.Select(i => i.Message)), "Error");
    }
    public async Task CreateBackupAsync()
    {
        if (workspace is null || globalBusy) return;
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => workspace.BackupService.Create("manual"));
            RefreshBackups();
            ShowFeedback(result.Ok ? "备份已创建" : "备份未完成", result.Ok ? $"{result.Count} 个文件已保存，{result.Bytes:N0} 字节。" : string.Join("；", result.Failed.Select(f => f.Message)), result.Ok ? "Success" : "Error");
        }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException) { ShowFeedback("备份未完成", error.Message, "Error"); }
        finally { SetBusy(false); }
    }
    public async Task RestoreBackupAsync(BackupItem item)
    {
        if (workspace is null || globalBusy || workspace.Maintenance is null) return;
        SetBusy(true);
        try
        {
            ShowFeedback("正在准备恢复", "先确认客户端已停止，再校验并恢复备份。", "Info");
            var result = await Task.Run(() => workspace.Maintenance.RestoreBackupAsync(workspace.BackupService, item.Name));
            if (result.Restore is null) { ShowFeedback("恢复未开始", result.Message + "。未写入恢复文件。", "Error"); return; }
            runningIdentity = null; runtimeProcessAlive = false; runtimeExited = false; lastOutcome = null; ClearExitObservations();
            Raise(nameof(RunningLabel)); Raise(nameof(ManagedCloneStatus)); Raise(nameof(IdentityState)); NotifyHomeDetails();
            ReloadProfiles(); RefreshBackups(); SyncActive();
            var restored = result.Restore;
            var materials = $"恢复前快照：{restored.PreBackup}。" + (restored.StagingPath.Length > 0 ? "恢复暂存目录：" + restored.StagingPath + "。" : "");
            ShowFeedback(result.Success ? "备份已恢复" : "恢复未完成", result.Success ? $"已恢复 {restored.Restored} 个文件；{materials}" : $"已恢复 {restored.Restored} 个文件。{materials}" + string.Join("；", restored.Failed.Select(f => f.Message)), result.Success ? "Success" : "Error");
        }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException) { ShowFeedback("恢复未完成", error.Message, "Error"); }
        finally { SetBusy(false); }
    }
    private void SetBusy(bool value)
    {
        operationBusy = value;
        RefreshBusyState();
    }
    private void RefreshBusyState()
    {
        Raise(nameof(IsBusy));
        ProxyPanel?.SetHostBusy(globalBusy);
        NavigateCommand.Refresh(); OpenBackgroundModelCommand?.Refresh();
        RefreshAccountCommands();
        RefreshInfrastructureCommands();
        RefreshPagingCommands();
        RefreshSimpleActions();
        CloseDetailsCommand?.Refresh(); SwitchCardCommand?.Refresh(); UseProxyCandidateCommand?.Refresh();
        OpenVisualTestsCommand?.Refresh(); OpenModelTestCommand?.Refresh(); OpenCialloCommand?.Refresh();
        RefreshModelTestCommands();
        foreach (var command in new[] { NewAccountLoginCommand, ImportLocalAccountCommand, NewProfileCommand, EditSelectedCommand, RenameSelectedCommand, ArchiveSelectedCommand, CreateBackupCommand, RestoreBackupCommand, SwitchCommand, LaunchCommand, LaunchCurrentCommand, SaveCurrentAccountCommand, LogoutCommand, RebuildCommand, RebuildOriginalCommand, SaveSettingsCommand, ExitCommand, RefreshCommand }) command.Refresh();
    }
    public void SetTrayAvailable(bool value) { trayAvailable = value; Raise(nameof(TrayAvailable)); Raise(nameof(TrayDescription)); }
    public void ReportLifecycleFailure(string title, string message) => ShowFeedback(title, message, "Error");
    public void ApplyOwnedState(FAswitch.Windows.OwnedLifecycleResult state)
    {
        if (launchInProgress && !state.DesktopAlive) return;
        var wasRunning = runningIdentity is not null || runtimeProcessAlive;
        if (!state.Success)
        {
            ShowFeedback("运行环境未确认", state.Message + "。" + string.Join("；", state.Warnings), "Error");
            return;
        }
        runtimeProcessAlive = state.DesktopAlive;
        var resolvedPending = lastOutcome == OperationOutcome.PendingLaunch && !state.LaunchPending;
        if (resolvedPending) lastOutcome = null;
        if (!state.DesktopAlive && !state.LaunchPending)
        {
            if (wasRunning && state.Success) runtimeExited = true;
            runningIdentity = null; Raise(nameof(RunningLabel)); Raise(nameof(ManagedCloneStatus)); Raise(nameof(IdentityState)); NotifyHomeDetails();
            if (wasRunning && state.Success && !busy && lastOutcome == OperationOutcome.Success)
                ShowFeedback("分身已退出", "启动时的窗口确认已通过，分身随后退出；当前未运行。账号资料和当前系统时区保留。", "Info");
        }
        else { runtimeExited = false; Raise(nameof(RunningLabel)); Raise(nameof(ManagedCloneStatus)); NotifyHomeDetails(); }
        if (resolvedPending) ShowFeedback(state.DesktopAlive ? "分身已启动" : "分身未运行",
            state.DesktopAlive ? "后台已确认分身运行，可继续使用或关闭分身。" : "上次启动已结束，可以重新启动；账号资料和恢复材料保留。", state.DesktopAlive ? "Success" : "Info");
        RefreshSimpleActions();
    }
    private async Task ApplyOperationRuntimeAsync(OperationResult result)
    {
        lastOutcome = result.Outcome;
        if (result.RunningIdentity is not null)
        {
            runningIdentity = result.RunningIdentity; runtimeProcessAlive = true;
            return;
        }
        if (result.PendingLaunch)
        {
            runningIdentity = null; runtimeProcessAlive = false;
            return;
        }
        // A failed preflight does not say that the previous desktop stopped.
        // Read current ownership instead of treating an absent result identity as exit.
        if (result.ActiveIdentity != runningIdentity) runningIdentity = null;
        if (workspace?.Ownership is { } ownership) ApplyOwnedState(await ownership.PollAsync());
        else if (workspace?.Runtime is { } runtime)
        {
            var stopped = await runtime.ConfirmStoppedAsync(FAswitch.Windows.ProcessScope.DesktopAndCli, CancellationToken.None);
            if (stopped.Success) { runningIdentity = null; runtimeProcessAlive = false; }
            else if (stopped.RemainingPids.Count > 0) runtimeProcessAlive = true;
        }
    }
    private void RefreshSelectionCommands()
    {
        Raise(nameof(IsBusy)); ProxyPanel?.SetHostBusy(globalBusy);
        RefreshSimpleActions();
        OpenVisualTestsCommand?.Refresh();
        OpenCialloCommand?.Refresh();
        foreach (var command in new[] { EditSelectedCommand, RenameSelectedCommand, ArchiveSelectedCommand, SwitchCommand, LaunchCommand, LaunchCurrentCommand, SaveCurrentAccountCommand }) command?.Refresh();
        RefreshAccountCommands();
    }
    private void SyncActive()
    {
        if (workspace is null) return;
        var identity = workspace.SwitchStorage.ReadActiveIdentity();
        active = identities.FirstOrDefault(i => i.Identity == identity);
        Raise(nameof(Active)); Raise(nameof(ActiveLabel)); Raise(nameof(SelectedIsActive)); Raise(nameof(IdentityState)); RefreshSelectionCommands();
        NotifyHomeDetails();
        if (accountFilter != "全部账号") UpdateItems();
    }
    public async Task SwitchSelectedAsync(bool launch)
    {
        if (workspace?.Switching is null || Selected is null || busy) return;
        ClearLoginIntent();
        await ExecuteSwitchRequestAsync(new(Selected.Identity, launch, IsolateSessions, UsesManagedClone ? false : AutoSystemTimezone, Selected.Environment, workspace.ActiveHome));
    }
    private async Task ExecuteSwitchRequestAsync(SwitchRequest request)
    {
        if (workspace?.Switching is null || busy) return;
        SetBusy(true);
        runtimeExited = false; SetLaunchInProgress(request.Launch);
        try
        {
            var progress = new Progress<OperationPhase>(phase => ShowFeedback("正在执行：" + PhaseLabel(phase), workspace.IsFixture ? "文件实际迁移；进程与时区使用隔离替身。" : "当前工作区操作进行中。", "Info"));
            var result = await Task.Run(() => workspace.Switching.SwitchAsync(request, progress));
            SetLaunchInProgress(false);
            await ApplyOperationRuntimeAsync(result);
            LoginTarget = result.LoginTarget; Raise(nameof(LoginTarget)); Raise(nameof(NeedsLogin));
            if (result.NeedsLogin) loginSwitchRequest = request;
            ReloadProfiles(); SyncActive(); Selected = FilteredItems.FirstOrDefault(i => i.Identity == request.Target);
            if (Selected is { } launchedItem && workspace.Switching.LastEgressEvidence is { } launchEvidence && LaunchEgressRules.Verified(launchEvidence)) RememberExit(launchedItem, launchEvidence.Ip, launchEvidence.Timezone);
            Raise(nameof(RunningLabel)); Raise(nameof(ManagedCloneStatus)); Raise(nameof(LaunchEgressDescription)); Raise(nameof(IdentityState));
            ShowFeedback((workspace.IsFixture ? "隔离验证：" : "") + OutcomeLabel(result.Outcome), result.Message + (result.RecoveryPaths.Count > 0 ? "。恢复材料：" + string.Join("；", result.RecoveryPaths) : "") + (result.Warnings.Count > 0 ? "。提示：" + string.Join("；", result.Warnings) : ""), result.Success ? "Success" : result.PendingLaunch ? "Info" : "Error");
        }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException) { ShowFeedback("操作未完成", error.Message, "Error"); }
        finally { SetLaunchInProgress(false); SetBusy(false); }
    }
    public async Task LaunchCurrentAsync()
    {
        if (workspace?.Switching is null || Active is null || busy) return;
        var target = Active;
        ClearLoginIntent();
        SetBusy(true);
        SetLaunchInProgress(true);
        try
        {
            var result = await Task.Run(() => workspace.Switching.LaunchCurrentAsync(new(UsesManagedClone ? false : AutoSystemTimezone, workspace.ActiveHome)));
            SetLaunchInProgress(false);
            await ApplyOperationRuntimeAsync(result); SyncActive();
            if (workspace.Switching.LastEgressEvidence is { } launchEvidence && LaunchEgressRules.Verified(launchEvidence)) RememberExit(target, launchEvidence.Ip, launchEvidence.Timezone);
            Raise(nameof(RunningLabel)); Raise(nameof(ManagedCloneStatus)); Raise(nameof(LaunchEgressDescription)); Raise(nameof(IdentityState));
            ShowFeedback((workspace.IsFixture ? "隔离验证：" : "") + OutcomeLabel(result.Outcome), result.Message + (result.RecoveryPaths.Count > 0 ? "。恢复材料：" + string.Join("；", result.RecoveryPaths) : "") + (result.Warnings.Count > 0 ? "。提示：" + string.Join("；", result.Warnings) : ""), result.Success ? "Success" : result.PendingLaunch ? "Info" : "Error");
        }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException) { ShowFeedback("启动未完成", error.Message, "Error"); }
        finally { SetLaunchInProgress(false); SetBusy(false); }
    }
    private void SetLaunchInProgress(bool value)
    {
        launchInProgress = value; if (value) runtimeExited = false;
        Raise(nameof(RunningLabel)); Raise(nameof(ManagedCloneStatus)); NotifyHomeDetails();
    }
    private static string OutcomeLabel(OperationOutcome outcome) => outcome switch { OperationOutcome.Success => "已完成", OperationOutcome.Unchanged => "当前身份已是目标", OperationOutcome.RolledBack => "原身份已恢复", OperationOutcome.RecoveryFailed => "恢复未完成", OperationOutcome.PendingLaunch => "启动待确认", OperationOutcome.NeedLogin => "目标档案需要登录", _ => "操作失败" };
    private static string PhaseLabel(OperationPhase phase) => phase switch { OperationPhase.Preflight => "检查环境", OperationPhase.ProbeEgress => "验证代理实际出口与时区", OperationPhase.PrepareProfile => "准备档案", OperationPhase.Stop => "停止客户端", OperationPhase.Snapshot => "保存快照", OperationPhase.Archive => "归档当前环境", OperationPhase.Apply => "迁移目标档案", OperationPhase.Timezone => "检查时区", OperationPhase.Launch => "确认启动", OperationPhase.Rollback => "恢复原状态", _ => "完成" };
}
