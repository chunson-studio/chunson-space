using System.ComponentModel;

namespace ChunsonSpace.Desktop;

public sealed partial class ShellViewModel
{
    private readonly AccountTestQueue accountTests = new();
    private readonly List<VisualTestPanelViewModel> visualPanels = [];
    private readonly List<CialloPanelViewModel> cialloPanels = [];
    private object? lastBackgroundPanel;
    public bool IsModelTestRunning => visualPanels.Any(panel => panel.IsBusy) || cialloPanels.Any(panel => panel.IsBusy) || retainedHealth?.IsBusy == true;
    private static bool HasTask(object? panel) => panel switch
    {
        VisualTestPanelViewModel p => p.IsBusy || p.Result is not null || p.HasBatchResults,
        CialloPanelViewModel p => p.IsBusy || p.Rows.Any(row => row.Finished),
        AccountHealthPanelViewModel p => p.IsBusy || p.Rows.Count > 0,
        _ => false
    };
    private IEnumerable<object> BackgroundPanels => visualPanels.Cast<object>().Concat(cialloPanels).Concat(retainedHealth is null ? [] : new object[] { retainedHealth }).Where(HasTask);
    private object? CurrentBackgroundPanel => HasTask(lastBackgroundPanel) ? lastBackgroundPanel : BackgroundPanels.LastOrDefault();
    private int BackgroundModelCount => BackgroundPanels.Count();
    public bool HasBackgroundModel => BackgroundModelCount > 0;
    public bool HasMultipleBackgroundModels => BackgroundModelCount > 1;
    private int RunningModelCount => visualPanels.Count(panel => panel.IsBusy) + cialloPanels.Count(panel => panel.IsBusy) + (retainedHealth?.IsBusy == true ? 1 : 0);
    public string BackgroundModelTitle => "模型测试 · " + (IsModelTestRunning ? $"{RunningModelCount} 项进行中" : "查看结果");
    public string BackgroundModelStatus => (CurrentBackgroundPanel switch { AccountHealthPanelViewModel panel => panel.ProgressSummary + "\n" + panel.Summary, CialloPanelViewModel panel => panel.ProgressSummary + "\n" + panel.Status, VisualTestPanelViewModel panel => panel.ProgressSummary + "\n" + panel.Status, _ => "" })
        + (HasMultipleBackgroundModels ? " · 再次点击切换任务" : "");
    public DelegateCommand OpenBackgroundModelCommand { get; private set; } = null!;

    private void BackgroundModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Status" or "IsBusy" or "ProgressSummary") RefreshBackgroundModel();
        if (e.PropertyName == "IsBusy") { RefreshBusyState(); if (workspace is not null) ReloadProfiles(); }
    }
    private void RefreshBackgroundModel()
    {
        Raise(nameof(IsModelTestRunning)); Raise(nameof(HasBackgroundModel)); Raise(nameof(HasMultipleBackgroundModels));
        Raise(nameof(BackgroundModelTitle)); Raise(nameof(BackgroundModelStatus));
        OpenBackgroundModelCommand.Refresh();
    }
    private void ResumeBackgroundModel()
    {
        var panels = BackgroundPanels.ToArray();
        if (panels.Length == 0) return;
        var visible = (object?)health ?? (object?)ciallo ?? visualTests;
        lastBackgroundPanel = visible is not null ? panels[(Array.IndexOf(panels, visible) + 1) % panels.Length]
            : lastBackgroundPanel is not null && panels.Contains(lastBackgroundPanel) ? lastBackgroundPanel : panels[^1];
        modelTestOpen = false;
        ProxyPanel.NodeTesting.CloseCommand.Execute(null);
        ciallo = lastBackgroundPanel as CialloPanelViewModel;
        visualTests = lastBackgroundPanel as VisualTestPanelViewModel;
        health = lastBackgroundPanel as AccountHealthPanelViewModel;
        foreach (var name in new[] { nameof(IsModelTestOpen), nameof(Ciallo), nameof(IsCialloOpen), nameof(VisualTests), nameof(IsVisualTestsOpen), nameof(AccountHealth), nameof(IsAccountHealthOpen) }) Raise(name);
        RefreshBackgroundModel();
    }
}
