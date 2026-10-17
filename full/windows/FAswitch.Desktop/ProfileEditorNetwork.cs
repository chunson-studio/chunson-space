using System.IO;
using FAswitch.Accounts;
using FAswitch.Core;

namespace FAswitch.Desktop;

public sealed partial class ProfileEditorViewModel
{
    private NodeSelectionItem? selectedIp;
    public NodeTestingViewModel? IpPool { get; init; }
    internal Func<Task<NodeSelectionItem>>? PickRandomIp { get; init; }
    public bool ShowIpPicker => UseVerifiedEnvironment && IsEnvironmentEditor && !FollowSystemProxy;
    public bool CanEdit => !saving;
    public NodeSelectionItem? SelectedIp
    {
        get => selectedIp;
        // A live cache refresh may remove the ComboBox item. Keep its intent so
        // saving rejects the invalid choice instead of silently keeping the old route.
        set { if (!saving && (value is not null || selectedIp is null) && Set(ref selectedIp, value)) Raise(nameof(NetworkModeDescription)); }
    }
    public DelegateCommand RandomIpCommand { get; }
    private void RefreshEditing()
    {
        SaveCommand.Refresh(); CancelCommand.Refresh(); RandomIpCommand.Refresh(); Raise(nameof(CanEdit));
    }
    private async Task PickRandomIpAsync()
    {
        saving = true; error = ""; RefreshEditing(); Raise(nameof(Error)); Raise(nameof(HasError));
        try
        {
            var item = await PickRandomIp!();
            selectedIp = item; Raise(nameof(SelectedIp)); Raise(nameof(NetworkModeDescription));
        }
        catch (Exception failure) when (failure is AccountException or StorageException or IOException or InvalidOperationException)
        { error = ErrorRedaction.Scrub(failure.Message); Raise(nameof(Error)); Raise(nameof(HasError)); }
        finally { saving = false; RefreshEditing(); }
    }
}
