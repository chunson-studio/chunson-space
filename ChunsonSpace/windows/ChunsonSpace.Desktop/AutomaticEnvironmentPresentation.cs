using System.ComponentModel;
using System.IO;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using System.Windows.Threading;

namespace ChunsonSpace.Desktop;

public sealed partial class ShellViewModel
{
    private LocalEnvironmentMonitor? environmentMonitor;
    private CancellationTokenSource environmentLifetime = new();
    private CancellationTokenSource? environmentProbe;
    private long environmentGeneration;
    private bool environmentQueued, environmentManual, environmentFull, environmentRunning, environmentStarted;
    private DateTimeOffset? lastSuccessfulEnvironmentSync;
    private LocalEnvironmentReport? currentLocalEnvironment;
    private string environmentSyncState = "待检测";
    internal Func<DateTimeOffset> EnvironmentNow { get; set; } = () => DateTimeOffset.UtcNow;
    internal LocalEnvironmentMonitor? EnvironmentMonitor => environmentMonitor;
    public string LastEnvironmentSyncLabel => lastSuccessfulEnvironmentSync is { } time
        ? $"上次成功同步 {time.ToLocalTime():MM-dd HH:mm:ss}" : "尚无成功同步记录";
    public string EnvironmentSyncLabel => checkingLocalEnvironment ? "同步中…" : "同步时区";
    internal async Task StartLocalEnvironmentAsync()
    {
        if (environmentStarted) return;
        environmentStarted = true;
        await ImportLocalAccountAtStartupAsync();
        if (workspace?.AutomaticEnvironmentOnStartup != true || environmentLifetime.IsCancellationRequested) return;
        StartEnvironmentMonitor();
        await RequestEnvironmentAsync(full: true);
    }
    internal void StartEnvironmentMonitor(TimeProvider? clock = null)
    {
        if (workspace?.AutomaticEnvironmentOnStartup != true || environmentMonitor is not null) return;
        if (environmentLifetime.IsCancellationRequested) { environmentLifetime.Dispose(); environmentLifetime = new(); }
        var dispatcher = Dispatcher.CurrentDispatcher;
        environmentMonitor = new(workspace.ProxyServices.FlClashRoot, InvalidateEnvironment,
            () => dispatcher.BeginInvoke(new Action(() =>
            {
                if (!environmentLifetime.IsCancellationRequested) _ = RequestEnvironmentAsync();
            })), clock, native: !workspace.IsFixture);
    }
    internal void InvalidateEnvironment()
    {
        Interlocked.Increment(ref environmentGeneration);
        try { environmentProbe?.Cancel(); } catch (ObjectDisposedException) { }
    }
    internal void StopEnvironmentMonitor()
    {
        environmentMonitor?.Dispose(); environmentMonitor = null;
        environmentQueued = false;
        environmentLifetime.Cancel(); InvalidateEnvironment();
    }
    private async Task WaitForEnvironmentIdleAsync(CancellationToken token)
    {
        while (busy)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(IsBusy)) ready.TrySetResult(); }
            PropertyChanged += Changed;
            try { if (busy) await ready.Task.WaitAsync(token); }
            finally { PropertyChanged -= Changed; }
        }
        token.ThrowIfCancellationRequested();
    }
    internal Task RequestEnvironmentAsync(bool manual = false, bool full = false)
    {
        if (workspace?.LocalEnvironment is null || environmentLifetime.IsCancellationRequested) return Task.CompletedTask;
        // Repeated requests share the in-flight operation; only a new event/generation
        // or explicit manual retry schedules another pass.
        if (environmentRunning)
        {
            if (manual || environmentProbe?.IsCancellationRequested == true)
            { environmentQueued = true; environmentManual |= manual; environmentFull |= manual || full; }
            return LocalEnvironmentOperation;
        }
        environmentQueued = true; environmentManual |= manual; environmentFull |= full || manual;
        environmentRunning = true;
        return LocalEnvironmentOperation = RunEnvironmentQueueAsync();
    }
    private async Task RunEnvironmentQueueAsync()
    {
        try
        {
            while (environmentQueued && !environmentLifetime.IsCancellationRequested)
            {
                var manual = environmentManual; var full = environmentFull;
                environmentQueued = environmentManual = environmentFull = false;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(environmentLifetime.Token);
                environmentProbe = attempt;
                var generation = Interlocked.Read(ref environmentGeneration);
                bool Current() => generation == Interlocked.Read(ref environmentGeneration) && !attempt.IsCancellationRequested;
                var token = attempt.Token;
                var ownsBusy = false;
                checkingLocalEnvironment = true; environmentSyncState = "正在检测并同步…";
                localEnvironmentStatus = "正在检测本机出口、地区与时区…";
                RefreshLocalEnvironmentCommands();
                try
                {
                    await WaitForEnvironmentIdleAsync(token);
                    var service = workspace!.LocalEnvironment!;
                    var report = await service.CheckAsync(token, lightweight: !full);
                    token.ThrowIfCancellationRequested();
                    if (!Current()) continue;
                    var previous = currentLocalEnvironment;
                    ApplyLocalEnvironmentReport(report);
                    if (!report.Verified) { EnvironmentFailed(report.Message, manual); continue; }
                    var needsWrite = !report.Matches;
                    if (needsWrite)
                    {
                        await WaitForEnvironmentIdleAsync(token);
                        SetBusy(true); ownsBusy = true;
                        SetRegionStatus("正在应用并回读地区与时区…");
                        using var lease = workspace.Ownership is { } ownership ? await ownership.BeginLaunchAsync(token) : null;
                        var nativeTimezone = workspace.NativeTimezone;
                        var freshPrimary = true;
                        var configured = await Task.Run(() => workspace.Accounts!.Region.ConfigureExplicitlyAsync(report.CountryCode, report.ExitTimezone, token: token,
                            baselinePreflight: nativeTimezone is null ? null : nativeTimezone.ValidateExplicitBaselineAsync,
                            commitBaseline: nativeTimezone is null ? null : nativeTimezone.CommitExplicitBaselineAsync,
                            allowElevation: manual, stillCurrent: async t => freshPrimary = Current() && await service.StillCurrentAsync(report, t)), token);
                        token.ThrowIfCancellationRequested();
                        if (!Current()) continue;
                        report = service.RereadSystemEnvironment(report);
                        ApplyLocalEnvironmentReport(report);
                        if (!configured.Success || !report.Matches)
                        {
                            EnvironmentFailed(configured.Message + (report.Matches ? "" : " 系统回读尚未匹配出口。"), manual);
                            if (!freshPrimary) environmentMonitor?.SignalChange();
                            continue;
                        }
                    }
                    report = service.RereadSystemEnvironment(report);
                    if (!Current()) continue;
                    ApplyLocalEnvironmentReport(report);
                    if (!report.Matches) { EnvironmentFailed("系统回读尚未匹配出口，请重试。", manual); continue; }
                    if (lastSuccessfulEnvironmentSync is null || needsWrite || previous is not null && previous.Ip != report.Ip)
                    {
                        var time = EnvironmentNow();
                        workspace.Profiles.SaveLastEnvironmentSync(time);
                        lastSuccessfulEnvironmentSync = time;
                    }
                    environmentSyncState = "已同步";
                    SetRegionStatus("已同步 · " + report.CountryCode + " · " + report.ExitTimezone + "\n" + LastEnvironmentSyncLabel);
                    if (manual) ShowFeedback("地区与时区同步完成", RegionStatus, "Success");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    environmentSyncState = environmentLifetime.IsCancellationRequested ? "同步已取消" : "出口变化，等待重新检测";
                    localEnvironmentStatus = environmentSyncState;
                    SetRegionStatus(environmentSyncState);
                    // The debounce may have fired while cancellation was unwinding.
                    if (!environmentLifetime.IsCancellationRequested && environmentQueued) environmentFull |= full;
                }
                catch (Exception error) when (error is AccountException or StorageException or IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
                { EnvironmentFailed(ErrorRedaction.Scrub(error.Message), manual); }
                finally
                {
                    environmentProbe = null;
                    if (ownsBusy) SetBusy(false);
                    checkingLocalEnvironment = false; RefreshLocalEnvironmentCommands();
                }
            }
        }
        finally { environmentRunning = false; }
    }
    private void EnvironmentFailed(string message, bool manual)
    {
        environmentSyncState = "同步未完成";
        localEnvironmentStatus = "同步未完成 · 本次未确认";
        SetRegionStatus("同步未完成：" + message);
        localEnvironmentDetail += "\n" + RegionStatus;
        if (manual) ShowFeedback("地区与时区同步未完成", RegionStatus, "Error");
    }
}
