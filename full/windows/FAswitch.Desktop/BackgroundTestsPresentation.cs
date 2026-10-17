using System.ComponentModel;

namespace FAswitch.Desktop;

public sealed partial class ShellViewModel
{
    private readonly AccountTestQueue accountTests = new();
    private readonly List<VisualTestPanelViewModel> visualPanels = [];
    private readonly List<CialloPanelViewModel> cialloPanels = [];
    private object? lastBackgroundPanel;
    public bool IsModelTestRunning => visualPanels.Any(panel => panel.IsBusy) || cialloPanels.Any(panel => panel.IsBusy);
    public bool HasBackgroundModel => visualPanels.Count + cialloPanels.Count > 0;
    public bool HasMultipleBackgroundModels => visualPanels.Count + cialloPanels.Count > 1;
    private int RunningModelCount => visualPanels.Count(panel => panel.IsBusy) + cialloPanels.Count(panel => panel.IsBusy);
    public string BackgroundModelTitle => "模型测试 · " + (IsModelTestRunning ? $"{RunningModelCount} 项进行中" : "查看结果");
    public string BackgroundModelStatus => (lastBackgroundPanel switch { CialloPanelViewModel panel => panel.Status, VisualTestPanelViewModel panel => panel.Status, _ => "" })
        + (visualPanels.Count + cialloPanels.Count > 1 ? " · 再次点击切换任务" : "");
    public DelegateCommand OpenBackgroundModelCommand { get; private set; } = null!;

    private void BackgroundModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Status" or "IsBusy") RefreshBackgroundModel();
        if (e.PropertyName == "IsBusy") RefreshBusyState();
    }
    private void RefreshBackgroundModel()
    {
        Raise(nameof(IsModelTestRunning)); Raise(nameof(HasBackgroundModel)); Raise(nameof(HasMultipleBackgroundModels));
        Raise(nameof(BackgroundModelTitle)); Raise(nameof(BackgroundModelStatus));
        OpenBackgroundModelCommand.Refresh();
    }
    private void ResumeBackgroundModel()
    {
        var panels = visualPanels.Cast<object>().Concat(cialloPanels).ToArray();
        if (panels.Length == 0) return;
        var visible = (object?)ciallo ?? visualTests;
        lastBackgroundPanel = visible is not null ? panels[(Array.IndexOf(panels, visible) + 1) % panels.Length]
            : lastBackgroundPanel is not null && panels.Contains(lastBackgroundPanel) ? lastBackgroundPanel : panels[^1];
        modelTestOpen = false;
        ProxyPanel.NodeTesting.CloseCommand.Execute(null);
        ciallo = lastBackgroundPanel as CialloPanelViewModel;
        visualTests = lastBackgroundPanel as VisualTestPanelViewModel;
        foreach (var name in new[] { nameof(IsModelTestOpen), nameof(Ciallo), nameof(IsCialloOpen), nameof(VisualTests), nameof(IsVisualTestsOpen) }) Raise(name);
        RefreshBackgroundModel();
    }
}
