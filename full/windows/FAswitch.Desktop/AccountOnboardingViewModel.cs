using System.IO;
using FAswitch.Accounts;
using FAswitch.Core;

namespace FAswitch.Desktop;

public sealed class AccountOnboardingViewModel : ObservableObject, IDisposable
{
    private string error = "";
    private string status = "默认沿用当前网络，也可选择独立 IP。";
    private bool starting;
    private bool dedicated;
    private NodeSelectionItem? selectedIp;
    private CancellationTokenSource? cancellation;
    public AccountOnboardingViewModel(Func<CancellationToken, Task<LoginProxySelection>> resolve,
        Func<LoginProxySelection, CancellationToken, Task> authorize, Action<bool> setBusy, Action close,
        NodeTestingViewModel? pool = null, Func<NodeSelectionItem, CancellationToken, Task<LoginProxySelection>>? createDedicated = null)
    {
        Pool = pool;
        AuthorizeCommand = new(_ => CurrentOperation = StartAsync(), _ => !starting && (!dedicated || selectedIp?.CanPrepareLogin == true && createDedicated is not null));
        CloseCommand = new(_ => { if (starting) cancellation?.Cancel(); else close(); });
        if (pool is not null) pool.LoginChoices.CollectionChanged += PoolChanged;
        async Task StartAsync()
        {
            using var request = new CancellationTokenSource(); cancellation = request;
            starting = true; setBusy(true); Refresh(); error = ""; status = dedicated ? "正在准备所选线路，复用已有评分…" : "正在沿用 FlClash 当前出口…"; Notify();
            try
            {
                await Task.Yield();
                var selected = dedicated ? await createDedicated!(selectedIp!, request.Token) : await resolve(request.Token);
                // Authorization owns cleanup once a route has been allocated, including cancellation at this boundary.
                if (!request.IsCancellationRequested)
                { status = $"已识别 {selected.Source}，正在打开本机浏览器的英文独立配置，随后确认出口并进入登录页…"; Notify(); }
                await authorize(selected, request.Token);
            }
            catch (OperationCanceledException) { error = "已取消或代理识别超时。可以重试。"; Notify(); }
            catch (Exception failure) when (failure is AccountException or StorageException or InvalidOperationException or IOException or UnauthorizedAccessException)
            { error = ErrorRedaction.Scrub(failure.Message); Notify(); }
            finally { cancellation = null; starting = false; setBusy(false); Refresh(); }
        }
    }
    public NodeTestingViewModel? Pool { get; }
    public string BrowserLabel { get; } = (InstalledBrowsers.Find().FirstOrDefault()?.Name ?? "需要安装浏览器") + " · English · 独立配置";
    public bool UseCurrentNetwork { get => !dedicated; set { if (value) UseDedicatedIp = false; } }
    public bool UseDedicatedIp { get => dedicated; set { if (!starting && Set(ref dedicated, value)) { if (value && selectedIp is null) SelectedIp = DefaultIp(); Raise(nameof(UseCurrentNetwork)); AuthorizeCommand.Refresh(); } } }
    public NodeSelectionItem? SelectedIp { get => selectedIp; set { if (!starting && selectedIp != value) { if (selectedIp is not null) selectedIp.PropertyChanged -= SelectionChanged; Set(ref selectedIp, value); if (selectedIp is not null) selectedIp.PropertyChanged += SelectionChanged; AuthorizeCommand.Refresh(); } } }
    private void SelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => AuthorizeCommand.Refresh();
    private void PoolChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        if (!starting && (selectedIp is null || Pool?.LoginChoices.Contains(selectedIp) != true))
            SelectedIp = selectedIp is not null ? Pool?.LoginChoices.FirstOrDefault(item => item.SubscriptionId == selectedIp.SubscriptionId) ?? DefaultIp() : DefaultIp();
        AuthorizeCommand.Refresh();
    }
    private NodeSelectionItem? DefaultIp() => Pool?.LoginChoices.FirstOrDefault(item => item.Eligible) ?? Pool?.LoginChoices.FirstOrDefault();
    public void Dispose() { if (Pool is not null) Pool.LoginChoices.CollectionChanged -= PoolChanged; if (selectedIp is not null) selectedIp.PropertyChanged -= SelectionChanged; }
    public string Status => status;
    public bool IsStarting => starting;
    public bool CanEdit => !starting;
    public bool CanRetry => !starting && HasError;
    public string Error => error;
    public bool HasError => error.Length > 0;
    public DelegateCommand AuthorizeCommand { get; }
    public DelegateCommand CloseCommand { get; }
    public Task CurrentOperation { get; private set; } = Task.CompletedTask;
    private void Notify() { Raise(nameof(Status)); Raise(nameof(Error)); Raise(nameof(HasError)); Raise(nameof(CanRetry)); }
    private void Refresh() { AuthorizeCommand.Refresh(); CloseCommand.Refresh(); Raise(nameof(IsStarting)); Raise(nameof(CanRetry)); Raise(nameof(CanEdit)); }
}
