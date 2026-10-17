using System.IO;
using FAswitch.Core;

namespace FAswitch.Desktop;

public sealed partial class ShellViewModel
{
    public DelegateCommand ToggleTestFreezeCommand { get; private set; } = null!;
    public string TestFreezeLabel => Selected?.TestsFrozen == true ? "解冻账号" : "冻结账号";
    private void InitializeTestFreeze()
    {
        ToggleTestFreezeCommand = new(_ => ToggleTestFreeze(), _ => workspace is not null && Selected?.Kind == "Account" && !busy && !exitDraining);
    }
    private void ToggleTestFreeze()
    {
        if (workspace is null || Selected?.Kind != "Account" || busy) return;
        var name = Selected.Name;
        try
        {
            var frozen = !workspace.Profiles.AreTestsFrozen(name);
            workspace.Profiles.SetTestsFrozen(name, frozen);
            ReloadProfiles(); Selected = identities.Single(item => item.Name == name);
            foreach (var panel in visualPanels) panel.RefreshTestEligibility();
            foreach (var panel in cialloPanels) panel.RefreshTestEligibility();
            ShowFeedback(frozen ? "账号已冻结" : "账号已解冻", frozen ? "该账号不参与任何模型测试，批量测试自动跳过。登录资料与历史结果保留。" : "该账号可以手动开始模型测试，不会自动发起请求。", "Success");
        }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException)
        { ShowFeedback("冻结状态未更新", error.Message, "Error"); }
    }
}
