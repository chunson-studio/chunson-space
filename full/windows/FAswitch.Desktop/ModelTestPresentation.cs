using FAswitch.Accounts;

namespace FAswitch.Desktop;

public sealed partial class ShellViewModel
{
    private VisualTestPanelViewModel? visualTests;
    private VisualTestPanelViewModel? retainedVisualTests;
    public VisualTestPanelViewModel? VisualTests => visualTests;
    public bool IsVisualTestsOpen => visualTests is not null;
    public DelegateCommand OpenVisualTestsCommand { get; private set; } = null!;
    public DelegateCommand OpenModelTestCommand { get; private set; } = null!;
    private bool modelTestOpen;
    private string? modelTestProfile;
    public bool IsModelTestOpen => modelTestOpen;
    public IReadOnlyList<string> ModelTestProfiles => workspace?.Profiles.ListAccounts().Where(account => account.HasCredentials).Select(account => account.Name).ToArray() ?? [];
    public string? ModelTestProfile { get => modelTestProfile; set { if (!operationBusy && !exitDraining && value is not null && ModelTestProfiles.Contains(value) && Set(ref modelTestProfile, value)) RefreshModelTestCommands(); } }
    public bool HasModelTestProfiles => ModelTestProfiles.Count > 0;
    public DelegateCommand CloseModelTestCommand { get; private set; } = null!;
    public DelegateCommand ChooseCialloCommand { get; private set; } = null!;
    public DelegateCommand ChooseVisualTestCommand { get; private set; } = null!;
    private void InitializeVisualTestCommands()
    {
        InitializeCialloCommands();
        accountTests.Changed += RefreshBusyState;
        OpenBackgroundModelCommand = new(_ => ResumeBackgroundModel(), _ => !operationBusy && !exitDraining && HasBackgroundModel);
        OpenVisualTestsCommand = new(_ => OpenVisualTests(Selected?.Kind == "Account" ? Selected.Name : null, VisualTestKind.Static), _ => !operationBusy && !exitDraining && Selected?.Kind == "Account");
        OpenModelTestCommand = new(_ =>
        {
            visualTests = null; ciallo = null;
            Raise(nameof(VisualTests)); Raise(nameof(IsVisualTestsOpen)); Raise(nameof(Ciallo)); Raise(nameof(IsCialloOpen));
            var selected = Selected?.Kind == "Account" ? Selected.Name : null;
            modelTestProfile = ModelTestProfiles.Contains(selected) ? selected : ModelTestProfiles.FirstOrDefault();
            modelTestOpen = true;
            foreach (var property in new[] { nameof(ModelTestProfiles), nameof(ModelTestProfile), nameof(HasModelTestProfiles), nameof(IsModelTestOpen) }) Raise(property);
            RefreshModelTestCommands();
        }, _ => !operationBusy && !exitDraining);
        CloseModelTestCommand = new(_ => { modelTestOpen = false; Raise(nameof(IsModelTestOpen)); }, _ => !operationBusy && !exitDraining);
        ChooseCialloCommand = new(_ => { CloseModelTestCommand.Execute(null); OpenCiallo(modelTestProfile); }, _ => !operationBusy && !exitDraining && ModelTestProfiles.Contains(modelTestProfile));
        ChooseVisualTestCommand = new(value => { CloseModelTestCommand.Execute(null); OpenVisualTests(modelTestProfile, Enum.TryParse<VisualTestKind>(value?.ToString(), out var kind) ? kind : VisualTestKind.Static); }, _ => !operationBusy && !exitDraining && ModelTestProfiles.Contains(modelTestProfile));
    }
    private void RefreshModelTestCommands() { ChooseCialloCommand?.Refresh(); ChooseVisualTestCommand?.Refresh(); CloseModelTestCommand?.Refresh(); }
    private void OpenVisualTests(string? profileName, VisualTestKind kind)
    {
        if (operationBusy || exitDraining) return;
        retainedVisualTests = visualPanels.LastOrDefault(panel => panel.Kind == kind && panel.SelectedProfile == profileName && (panel.IsBusy || panel.Profiles.SequenceEqual(ModelTestProfiles)));
        if (retainedVisualTests is null || retainedVisualTests.Kind != kind || !retainedVisualTests.Profiles.SequenceEqual(ModelTestProfiles))
        {
            retainedVisualTests = new(workspace?.Accounts, profileName, kind, _ => RefreshBusyState(), () => { visualTests = null; Raise(nameof(VisualTests)); Raise(nameof(IsVisualTestsOpen)); }, ModelTestProfiles, accountTests);
            visualPanels.Add(retainedVisualTests);
            retainedVisualTests.PropertyChanged += BackgroundModelChanged;
        }
        lastBackgroundPanel = retainedVisualTests;
        ciallo = null; Raise(nameof(Ciallo)); Raise(nameof(IsCialloOpen));
        visualTests = retainedVisualTests;
        if (profileName is not null) visualTests.SelectedProfile = profileName;
        RefreshBackgroundModel();
        Raise(nameof(VisualTests)); Raise(nameof(IsVisualTestsOpen));
    }
}
