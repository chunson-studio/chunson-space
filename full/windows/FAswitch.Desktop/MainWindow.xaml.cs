using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FAswitch.Desktop;

public partial class MainWindow : Window
{
    private void DiagnosticResultChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox result) result.ScrollToHome();
    }
    public MainWindow()
    {
        InitializeComponent();
        Icon = BrandIcon.CreateImage();
        SelectedExitDetail.RenderTransform = new TranslateTransform();
        WorkspaceContent.SizeChanged += (_, _) => { if (DataContext is ShellViewModel model) model.UpdateViewport(WorkspaceContent.ActualWidth, RootLayout.ActualHeight); };
        WorkspaceBody.SizeChanged += (_, _) => UpdateHomeCapacity();
        HomeLoginStrip.SizeChanged += (_, _) => UpdateHomeCapacity();
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
        // Capacity comes from the available workspace, never from the content-sized list.
        if (DataContext is ShellViewModel model)
            model.UpdateIdentityViewport(WorkspaceBody.ActualHeight - 98 - HomeLoginStrip.ActualHeight - 36);
    }
    internal bool SelectContextIdentity(ListBoxItem row)
    {
        if (DataContext is not ShellViewModel model || !model.CanSelectIdentity || !model.IsAccountsPage ||
            model.IsEditorOpen || model.IsDiagnosticOpen || model.IsModelTestOpen || model.ProxyPanel.NodeTesting.IsOpen || model.Onboarding is not null || model.VisualTests is not null || model.Ciallo is not null ||
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
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (HandleShortcut(e.Key, Keyboard.Modifiers)) e.Handled = true;
        base.OnPreviewKeyDown(e);
    }
    internal bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (DataContext is not ShellViewModel model) return false;
        if (model.Ciallo is { } ciallo) { if (key == Key.Escape && ciallo.CloseCommand.CanExecute(null)) { ciallo.CloseCommand.Execute(null); return true; } return false; }
        if (model.IsDiagnosticOpen) { if (key == Key.Escape) { model.CloseDiagnosticCommand.Execute(null); return true; } return false; }
        if (model.ProxyPanel.NodeTesting.IsOpen) { if (key == Key.Escape && model.ProxyPanel.NodeTesting.CloseCommand.CanExecute(null)) { model.ProxyPanel.NodeTesting.CloseCommand.Execute(null); return true; } return false; }
        if (model.Onboarding is { } onboarding) { if (key == Key.Escape && onboarding.CloseCommand.CanExecute(null)) { onboarding.CloseCommand.Execute(null); return true; } return false; }
        if (model.VisualTests is { } fingerprint) { if (key == Key.Escape && fingerprint.CloseCommand.CanExecute(null)) { fingerprint.CloseCommand.Execute(null); return true; } return false; }
        if (model.IsModelTestOpen) { if (key == Key.Escape && model.CloseModelTestCommand.CanExecute(null)) { model.CloseModelTestCommand.Execute(null); return true; } return false; }
        if (model.IsEditorOpen) { if (key == Key.Escape) { model.CloseEditor(); return true; } return false; }
        if (model.IsDetailOpen && key == Key.Escape && model.CloseDetailsCommand.CanExecute(null)) { model.CloseDetailsCommand.Execute(null); return true; }
        if (key == Key.F5 && model.RefreshCommand.CanExecute(null)) { model.RefreshCommand.Execute(null); return true; }
        if (!model.IsIdentityPage) return false;
        if (key == Key.F && modifiers == ModifierKeys.Control) { if (IsVisible) SearchBox.Focus(); SearchBox.SelectAll(); return true; }
        if (key == Key.Escape && modifiers == ModifierKeys.None) { model.ClearSearchCommand.Execute(null); return true; }
        return false;
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
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
