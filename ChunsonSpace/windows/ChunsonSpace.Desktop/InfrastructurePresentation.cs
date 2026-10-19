using ChunsonSpace.Windows;

namespace ChunsonSpace.Desktop;

public enum InfrastructureAction { InspectInstallation, InstallCodex, InspectTimezoneChannel, EnableTimezoneChannel }

public sealed partial class ShellViewModel
{
    private string installationDetails = "尚未检查 Codex 安装状态。";
    private string timezoneChannelDetails = "尚未检查静默时区备用通道。";
    public string InstallationDetails => installationDetails;
    public string TimezoneChannelDetails => timezoneChannelDetails;
    public DelegateCommand InspectInstallationCommand { get; private set; } = null!;
    public DelegateCommand InstallCodexCommand { get; private set; } = null!;
    public DelegateCommand InspectTimezoneChannelCommand { get; private set; } = null!;
    public DelegateCommand EnableTimezoneChannelCommand { get; private set; } = null!;
    private void InitializeInfrastructureCommands()
    {
        InspectInstallationCommand = new(_ => CurrentOperation = RunInfrastructureAsync(InfrastructureAction.InspectInstallation), _ => !busy && workspace?.CodexInstaller is not null);
        InstallCodexCommand = new(_ => CurrentOperation = RunInfrastructureAsync(InfrastructureAction.InstallCodex), _ => !busy && workspace?.CodexInstaller is not null);
        InspectTimezoneChannelCommand = new(_ => CurrentOperation = RunInfrastructureAsync(InfrastructureAction.InspectTimezoneChannel), _ => !busy && workspace?.SilentTimezoneChannel is not null);
        EnableTimezoneChannelCommand = new(_ => CurrentOperation = RunInfrastructureAsync(InfrastructureAction.EnableTimezoneChannel), _ => !busy && workspace?.SilentTimezoneChannel is not null);
    }
    private void RefreshInfrastructureCommands()
    {
        foreach (var command in new[] { InspectInstallationCommand, InstallCodexCommand, InspectTimezoneChannelCommand, EnableTimezoneChannelCommand }) command?.Refresh();
    }
    public async Task RunInfrastructureAsync(InfrastructureAction action)
    {
        if (workspace is null || busy) return;
        SetBusy(true);
        try
        {
            bool success = true; string title, message;
            switch (action)
            {
                case InfrastructureAction.InspectInstallation:
                    var plan = await Task.Run(() => workspace.CodexInstaller!.PlanAsync());
                    title = "安装状态已读取";
                    message = plan.State.Status switch { DesktopInstallationStatus.Installed => $"Codex 已安装 · {plan.State.Version}\n包家族：{plan.State.PackageFamily}", DesktopInstallationStatus.NotInstalled => "Codex 尚未安装。可使用下方安装按钮。", _ => "安装状态未确认：" + plan.State.Message };
                    installationDetails = message; Raise(nameof(InstallationDetails)); break;
                case InfrastructureAction.InstallCodex:
                    var installed = await Task.Run(() => workspace.CodexInstaller!.InstallAsync());
                    title = installed.Success ? "Codex 安装已确认" : installed.StoreOpened ? "已打开商店" : "安装尚未确认"; message = installed.Message; success = installed.Success || installed.StoreOpened;
                    installationDetails = message; Raise(nameof(InstallationDetails)); break;
                case InfrastructureAction.InspectTimezoneChannel:
                    var channel = await Task.Run(() => workspace.SilentTimezoneChannel!.InspectAsync());
                    title = "备用通道状态已读取"; message = $"状态：{channel.Status} · 助手可读：{channel.HelperReadable}\n{channel.Message}\n此状态不表示当前切换已使用该备用通道。";
                    timezoneChannelDetails = message; Raise(nameof(TimezoneChannelDetails)); break;
                default:
                    var enabled = await Task.Run(() => workspace.SilentTimezoneChannel!.EnableAsync());
                    title = enabled.Success ? "备用通道已注册" : "备用通道未确认"; message = enabled.Message + "\n注册不会修改当前系统时区。实际执行能力仍需使用时确认。"; success = enabled.Success;
                    timezoneChannelDetails = message; Raise(nameof(TimezoneChannelDetails)); break;
            }
            ShowFeedback((workspace.IsFixture ? "隔离验证：" : "") + title, message, success ? "Info" : "Error");
        }
        catch (Exception error) { ShowFeedback("系统动作未完成", "处理失败：" + error.GetType().Name, "Error"); }
        finally { SetBusy(false); }
    }
}

public sealed class OfflineCodexInstallSystem : ICodexInstallSystem
{
    public DesktopInstallationState State { get; set; } = new(DesktopInstallationStatus.Installed, "fixture-version", "OpenAI.Codex_fixture", "隔离安装替身");
    public Task<DesktopInstallationState> ReadStateAsync(CancellationToken cancellationToken) => Task.FromResult(State);
    public bool HasWinget() => true;
    public Task<InstallCommandReceipt> InstallAsync(CancellationToken cancellationToken) { State = State with { Status = DesktopInstallationStatus.Installed }; return Task.FromResult(new InstallCommandReceipt(true, 0)); }
    public bool OpenStore() => true;
}
public sealed class OfflineSilentTimezoneSystem : ISilentTimezoneSystem
{
    private bool enabled;
    public Task<SilentTimezoneState> InspectAsync(CancellationToken cancellationToken) => Task.FromResult(new SilentTimezoneState(enabled ? SilentTimezoneStatus.Enabled : SilentTimezoneStatus.Disabled, enabled, enabled ? 0 : null, "隔离任务替身"));
    public Task<bool> SetupExplicitlyAsync(CancellationToken cancellationToken) { enabled = true; return Task.FromResult(true); }
}
