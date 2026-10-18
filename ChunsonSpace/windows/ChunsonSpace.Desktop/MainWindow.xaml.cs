using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ChunsonSpace.Desktop;

public partial class MainWindow : Window
{
    private IInputElement? detailReturnFocus;
    private async void CopyErrorReport(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string report || string.IsNullOrWhiteSpace(report)) return;
        if (SupportReport.TryCopy(report, Clipboard.SetText)) { button.Content = "已复制"; await Task.Delay(1800); button.Content = "复制错误信息"; }
        else MessageBox.Show(this, "剪贴板暂时不可用。可在此窗口按 Ctrl+C 复制后提交。\n\n" + report, "错误诊断", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void DiagnosticResultChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox result) result.ScrollToHome();
    }
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { if (DataContext is ShellViewModel model) model.CloseArtwork(); };
        Icon = BrandIcon.CreateImage();
        SelectedExitDetail.RenderTransform = new TranslateTransform();
        WorkspaceContent.SizeChanged += (_, _) => { if (DataContext is ShellViewModel model) model.UpdateViewport(WorkspaceContent.ActualWidth, RootLayout.ActualHeight); };
        AccountViewport.SizeChanged += (_, _) => UpdateHomeCapacity();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ShellViewModel previous) previous.PropertyChanged -= ModelChanged;
            if (e.NewValue is ShellViewModel current)
            {
                current.PropertyChanged += ModelChanged;
                current.UpdateViewport(WorkspaceContent.ActualWidth, RootLayout.ActualHeight);
                UpdateHomeCapacity();
            }
        };
    }
    internal FrameworkElement RenderRoot => RootLayout;
    private void UpdateHomeCapacity()
    {
        // The parent-constrained viewport remains independent of item count and pager state.
        if (DataContext is ShellViewModel model)
            model.UpdateIdentityViewport(AccountViewport.ActualHeight);
    }
    internal bool SelectContextIdentity(ListBoxItem row)
    {
        if (DataContext is not ShellViewModel model || !model.CanSelectIdentity || !model.IsAccountsPage ||
            model.IsMarksEditorOpen || model.IsDetailOpen || model.IsEditorOpen || model.IsDiagnosticOpen || model.IsModelTestOpen || model.IsAccountHealthOpen || model.ProxyPanel.NodeTesting.IsOpen || model.Onboarding is not null || model.VisualTests is not null || model.Ciallo is not null ||
            row.DataContext is not IdentityItem identity || !ProfileList.Items.Contains(identity)) return false;
        ProfileList.SelectedItem = identity;
        model.Selected = identity;
        return true;
    }
    private void SelectAccountForContextMenu(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem row && !SelectContextIdentity(row)) e.Handled = true;
    }
    private void OpenAccountContextMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem row || !SelectContextIdentity(row)) e.Handled = true;
    }
    private void RefreshAccountMenuTheme(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        // Closed popups can retain resource values from the previous theme.
        foreach (var key in new[] { "SurfaceBrush", "InkBrush", "OutlineBrush", "SelectionBrush", "LineBrush" })
            menu.Resources[key] = FindResource(key);
    }
    private void OpenRegionSettings(object sender, RoutedEventArgs e) => SettingsAdvancedToggle.IsChecked = false;
    internal void ShowUsageMenu()
    {
        var menu = UsageMenuButton.ContextMenu;
        menu.PlacementTarget = UsageMenuButton;
        menu.IsOpen = true;
        menu.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (menu.IsOpen && menu.Items.Count > 0 && menu.Items[0] is MenuItem item) item.Focus();
        }));
    }
    private void OpenUsageMenu(object sender, RoutedEventArgs e) => ShowUsageMenu();
    private void UsageMenuKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down) { ShowUsageMenu(); e.Handled = true; }
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key != Key.Escape && HandleShortcut(e.Key, Keyboard.Modifiers)) e.Handled = true;
        base.OnPreviewKeyDown(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Let the focused control dismiss its dropdown before clearing search or closing a modal.
        if (!e.Handled && e.Key == Key.Escape && HandleShortcut(e.Key, Keyboard.Modifiers)) e.Handled = true;
        base.OnKeyDown(e);
    }
    internal bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (DataContext is not ShellViewModel model || UsageMenuButton.ContextMenu.IsOpen) return false;
        if (model.IsMarksEditorOpen) { if (key == Key.Escape) { model.CloseAccountMarksCommand.Execute(null); return true; } return false; }
        if (model.IsArtworkOpen) { if (key == Key.Escape) { model.CloseArtwork(); return true; } return false; }
        if (model.AccountHealth is { } health) { if (key == Key.Escape) { health.CloseCommand.Execute(null); return true; } return false; }
        if (model.Ciallo is { } ciallo) { if (key == Key.Escape && ciallo.CloseCommand.CanExecute(null)) { ciallo.CloseCommand.Execute(null); return true; } return false; }
        if (model.IsDiagnosticOpen) { if (key == Key.Escape) { model.CloseDiagnosticCommand.Execute(null); return true; } return false; }
        if (model.ProxyPanel.NodeTesting.IsOpen) { if (key == Key.Escape && model.ProxyPanel.NodeTesting.CloseCommand.CanExecute(null)) { model.ProxyPanel.NodeTesting.CloseCommand.Execute(null); return true; } return false; }
        if (model.Onboarding is { } onboarding) { if (key == Key.Escape && onboarding.CloseCommand.CanExecute(null)) { onboarding.CloseCommand.Execute(null); return true; } return false; }
        if (model.VisualTests is { } fingerprint) { if (key == Key.Escape && fingerprint.CloseCommand.CanExecute(null)) { fingerprint.CloseCommand.Execute(null); return true; } return false; }
        if (model.IsModelTestOpen) { if (key == Key.Escape && model.CloseModelTestCommand.CanExecute(null)) { model.CloseModelTestCommand.Execute(null); return true; } return false; }
        if (model.IsEditorOpen) { if (key == Key.Escape) { model.CloseEditor(); return true; } return false; }
        if (model.IsDetailOpen)
        {
            if (key == Key.Escape && model.CloseDetailsCommand.CanExecute(null)) { model.CloseDetailsCommand.Execute(null); return true; }
            // Keep home shortcuts from refreshing or focusing controls behind this modal.
            return key == Key.F5 || key == Key.F && modifiers == ModifierKeys.Control;
        }
        if (key == Key.F5 && model.RefreshCommand.CanExecute(null)) { model.RefreshCommand.Execute(null); return true; }
        if (!model.IsIdentityPage) return false;
        if (key == Key.F && modifiers == ModifierKeys.Control) { if (IsVisible) SearchBox.Focus(); SearchBox.SelectAll(); return true; }
        if (key == Key.Escape && modifiers == ModifierKeys.None) { model.ClearSearchCommand.Execute(null); return true; }
        return false;
    }
    private IInputElement? marksReturnFocus;
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsMarksEditorOpen) && sender is ShellViewModel marksModel)
        {
            if (marksModel.IsMarksEditorOpen) { AccountMarkDetails.IsExpanded = false; AccountMarksFormScroll.ScrollToTop(); }
            if (marksModel.IsMarksEditorOpen && IsVisible && IsActive)
            {
                marksReturnFocus = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
                { if (marksModel.IsMarksEditorOpen && IsVisible && IsActive) AccountQualityPicker.Focus(); }));
            }
            else if (!marksModel.IsMarksEditorOpen)
            {
                if (IsVisible && IsActive && marksReturnFocus is UIElement { IsVisible: true, IsEnabled: true } previous) previous.Focus();
                marksReturnFocus = null;
            }
        }
        if (e.PropertyName == nameof(ShellViewModel.IsDetailOpen) && sender is ShellViewModel model)
        {
            if (model.IsDetailOpen && IsVisible && IsActive)
            {
                detailReturnFocus = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
                {
                    if (model.IsDetailOpen && IsVisible && IsActive) AccountDetailCloseButton.Focus();
                }));
            }
            else if (!model.IsDetailOpen)
            {
                if (IsVisible && IsActive && detailReturnFocus is UIElement { IsVisible: true, IsEnabled: true } previous)
                    previous.Focus();
                detailReturnFocus = null;
            }
        }
        if (e.PropertyName == nameof(ShellViewModel.Selected) && IsVisible && IsActive && SystemParameters.ClientAreaAnimation)
        {
            ((TranslateTransform)SelectedExitDetail.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(3, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            SelectedExitDetail.BeginAnimation(OpacityProperty, new DoubleAnimation(0.75, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
        }
    }
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ToggleMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void BrowseProxyDataDirectory(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel { ProxyPanel.CanEdit: true } model) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择代理客户端数据目录", Multiselect = false };
        if (dialog.ShowDialog(this) == true) { model.ProxyPanel.DataRoot = dialog.FolderName; model.ProxyPanel.InspectCommand.Execute(null); }
    }
    private void BrowseFlClashApplication(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel { ProxyPanel.CanEdit: true } model) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择 FlClash 程序", Filter = "Windows 程序 (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) { model.ProxyPanel.ApplicationPath = dialog.FileName; model.ProxyPanel.InspectCommand.Execute(null); }
    }
}
