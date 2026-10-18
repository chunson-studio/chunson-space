using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChunsonSpace.Desktop;

// Real WPF peers on the null-service proxy panel; no configuration discovery or system actions.
internal static class ProxyAccessibilityVerification
{
    // Legacy Verge controls remain fixture-only; never widen the production client choices.
    internal static async Task RunExportAsync(string directory, Action<string, bool> check, Action<FrameworkElement, int, int> arrange)
    {
        Directory.CreateDirectory(directory);
        var facts = new List<object>();
        foreach (var dark in new[] { false, true })
        foreach (var size in new[] { (Width: 1040, Height: 670), (Width: 1280, Height: 850) })
        {
            var state = $"{(dark ? "dark" : "light")}-{size.Width}x{size.Height}";
            var fixture = Path.Combine(directory, state + "-fixture");
            var home = Path.Combine(fixture, "home"); Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(fixture, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
            var workspace = new DesktopWorkspace(fixture, home, null, null, fixture: true);
            var model = new ShellViewModel(workspace) { DarkTheme = dark };
            var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft - 4000, Top = SystemParameters.VirtualScreenTop - 4000 };
            var root = (FrameworkElement)window.FindName("RootLayout");
            var panel = model.ProxyPanel;
            try
            {
                void Arrange() => arrange(root, size.Width, size.Height);
                void Capture(string stage)
                {
                    Arrange();
                    var bitmap = new RenderTargetBitmap(size.Width, size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, state + "-" + stage + ".png")); encoder.Save(stream);
                }
                model.NavigateCommand.Execute("Proxy"); await panel.CurrentOperation;
                ((CheckBox)window.FindName("ProxySimpleAdvancedToggle")).IsChecked = true; Arrange();
                var input = Descendants(root).OfType<TextBox>().Single(control =>
                    BindingOperations.GetBinding(control, TextBox.TextProperty)?.Path.Path == "ExportPath");
                var section = (FrameworkElement)((FrameworkElement)input.Parent).Parent;
                check(state + ".export.flclash-hidden", section.Visibility == Visibility.Collapsed
                    && !new ProxyPanelViewModel(null).Clients.Contains(ChunsonSpace.Proxy.ProxyClientKind.ClashVerge));
                panel.Client = ChunsonSpace.Proxy.ProxyClientKind.ClashVerge; await panel.CurrentOperation; Arrange();
                Capture("export");
                var peer = UIElementAutomationPeer.CreatePeerForElement(input)!;
                var value = peer.GetPattern(PatternInterface.Value) as IValueProvider;
                var bounds = input.TransformToAncestor(root).TransformBounds(new Rect(input.RenderSize));
                check(state + ".export.name", peer.GetName() == "导出全局脚本到新文件");
                check(state + ".export.capabilities", input.Focusable && peer.IsEnabled() && value is not null && !value.IsReadOnly);
                check(state + ".export.layout", section.Visibility == Visibility.Visible && bounds.Width > 0 && bounds.Height > 0
                    && new Rect(0, 0, size.Width, size.Height).Contains(bounds));
                var path = Path.Combine(fixture, "synthetic-export.js");
                value!.SetValue(path); Arrange();
                check(state + ".export.edit", panel.ExportPath == path && input.Text == path && value.Value == path);
                check(state + ".export.unprepared-guard", !panel.ExportReportCommand.CanExecute(null) && !panel.PreviewReady);
                value.SetValue(""); Arrange();
                panel.SetHostBusy(true); Arrange();
                Capture("busy-export");
                check(state + ".export.busy-guard", !input.IsEnabled && !peer.IsEnabled() && !panel.ExportReportCommand.CanExecute(null));
                panel.ExportPath = Path.Combine(fixture, "blocked.js");
                check(state + ".export.busy-rejects-edit", panel.ExportPath.Length == 0);
                panel.SetHostBusy(false); Arrange();
                check(state + ".export.busy-recovery", input.IsEnabled && peer.IsEnabled() && input.Text.Length == 0
                    && !panel.ExportReportCommand.CanExecute(null));
                value.SetValue(""); Arrange();
                check(state + ".export.empty-guard", panel.ExportPath.Length == 0 && !panel.ExportReportCommand.CanExecute(null));
                check(state + ".export.no-side-effects", !File.Exists(path) && workspace.Accounts!.OfflineTransport!.RequestCount == 0
                    && ((OfflineUrlOpener)workspace.ProxyServices.Urls).Opened.Count == 0
                    && workspace.ProxyServices.FixtureProtectionRunner!.Actions.Count == 0
                    && workspace.ProxyServices.FixtureBrowserUdp!.Calls == 0);
                check(state + ".export.hidden-window", !window.IsVisible && !window.IsActive && !window.ShowActivated && !window.ShowInTaskbar);
                facts.Add(new { state, name = peer.GetName(), bounds = bounds.ToString(), fixtureOnly = true });
            }
            finally { window.Close(); model.DarkTheme = false; }
        }
        File.WriteAllText(Path.Combine(directory, "automation-facts.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void Run(string directory, Action<string, bool> check, Action<FrameworkElement, int, int> arrange)
    {
        Directory.CreateDirectory(directory);
        var facts = new List<object>();
        foreach (var dark in new[] { false, true })
        foreach (var size in new[] { (Width: 1040, Height: 670), (Width: 1280, Height: 850) })
        {
            var state = $"{(dark ? "dark" : "light")}-{size.Width}x{size.Height}";
            var model = new ShellViewModel { DarkTheme = dark };
            var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft - 4000, Top = SystemParameters.VirtualScreenTop - 4000 };
            var root = (FrameworkElement)window.FindName("RootLayout");
            var panel = model.ProxyPanel;
            try
            {
                void Arrange() => arrange(root, size.Width, size.Height);
                void Capture(string stage)
                {
                    Arrange();
                    var bitmap = new RenderTargetBitmap(size.Width, size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, state + "-" + stage + ".png")); encoder.Save(stream);
                }
                T Bound<T>(DependencyProperty property, string path) where T : Control => Descendants(root).OfType<T>()
                    .Single(control => BindingOperations.GetBinding(control, property)?.Path.Path == path
                        && ReferenceEquals(control.DataContext, panel));
                void Inspect(string id, string expectedName, Control control)
                {
                    var peer = UIElementAutomationPeer.CreatePeerForElement(control)!;
                    var pattern = control is ComboBox ? PatternInterface.ExpandCollapse
                        : control is TextBox ? PatternInterface.Value : PatternInterface.Invoke;
                    var value = peer.GetPattern(PatternInterface.Value) as IValueProvider;
                    var name = peer.GetName();
                    check(state + "." + id + ".name", name == expectedName);
                    check(state + "." + id + ".capabilities", control.Focusable && peer.GetPattern(pattern) is not null
                        && peer.IsEnabled() == control.IsEnabled
                        && (control is not TextBox text || value is not null && !value.IsReadOnly && value.Value == text.Text)
                        && (control is not ComboBox { IsEditable: true } combo || value is not null && value.Value == combo.Text));
                    var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
                    check(state + "." + id + ".layout", control.Visibility == Visibility.Visible
                        && bounds.Width > 0 && bounds.Height > 0 && new Rect(0, 0, size.Width, size.Height).Contains(bounds));
                    facts.Add(new { state, id, name, expectedName, enabled = peer.IsEnabled(),
                        pattern = pattern.ToString(), value = value?.Value, bounds = bounds.ToString() });
                }
                bool ServicesBlocked() => !panel.InspectCommand.CanExecute(null) && !panel.PreviewCommand.CanExecute(null)
                    && !panel.ApplyCommand.CanExecute(null) && !panel.ConfigureNetworkCommand.CanExecute(null)
                    && !panel.RestoreCommand.CanExecute(null) && !panel.ExportReportCommand.CanExecute(null)
                    && panel.CurrentOperation.IsCompletedSuccessfully && !panel.IsBusy;

                model.NavigateCommand.Execute("Proxy");
                var advanced = (CheckBox)window.FindName("ProxySimpleAdvancedToggle");
                advanced.IsChecked = true;
                Capture("configuration");
                var client = Bound<ComboBox>(Selector.SelectedItemProperty, "Client");
                var group = (ComboBox)window.FindName("ProxyGroupBox");
                var backup = Bound<TextBox>(TextBox.TextProperty, "BackupPath");
                Inspect("client", "代理客户端", client);
                Inspect("group", "代理分组", group);
                Inspect("backup", "恢复备份目录", backup);
                check(state + ".empty-group-guard", !group.IsEnabled && group.Items.Count == 0 && !panel.HasGroups);
                backup.Text = Path.Combine(directory, "synthetic-backup");
                Arrange();
                check(state + ".backup-edit", panel.BackupPath == backup.Text
                    && UIElementAutomationPeer.CreatePeerForElement(backup)!.GetPattern(PatternInterface.Value) is IValueProvider backupValue
                    && backupValue.Value == backup.Text);

                var paths = (Expander)window.FindName("ProxyAdvancedToggle");
                paths.IsExpanded = true;
                Capture("paths");
                var data = Bound<TextBox>(TextBox.TextProperty, "DataRoot");
                var application = (ComboBox)window.FindName("ProxyApplicationBox");
                var browseData = ((Panel)data.Parent).Children.OfType<Button>().Single();
                var browseApplication = ((Panel)application.Parent).Children.OfType<Button>().Single();
                Inspect("data", "代理数据目录", data);
                Inspect("application", "FlClash 应用位置", application);
                Inspect("browse-data", "浏览代理数据目录", browseData);
                Inspect("browse-application", "浏览 FlClash 应用", browseApplication);

                data.Text = Path.Combine(directory, "synthetic-data");
                data.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Arrange();
                check(state + ".data-edit", panel.DataRoot == data.Text && panel.ReadStatus == "路径已更改，请重新读取"
                    && UIElementAutomationPeer.CreatePeerForElement(data)!.GetPattern(PatternInterface.Value) is IValueProvider dataValue
                    && dataValue.Value == data.Text);
                application.Text = Path.Combine(directory, "synthetic-FlClash.exe");
                application.GetBindingExpression(ComboBox.TextProperty)!.UpdateSource();
                Arrange();
                check(state + ".application-edit", panel.ApplicationPath == application.Text
                    && UIElementAutomationPeer.CreatePeerForElement(application)!.GetPattern(PatternInterface.Value) is IValueProvider applicationValue
                    && applicationValue.Value == application.Text);
                check(state + ".preview-service-guards", ServicesBlocked());
                paths.IsExpanded = false; backup.Text = ""; Arrange();
                panel.SetHostBusy(true); Arrange();
                Capture("busy-configuration");
                check(state + ".backup-busy-guard", !backup.IsEnabled
                    && !UIElementAutomationPeer.CreatePeerForElement(backup)!.IsEnabled());
                check(state + ".host-busy-guard", !client.IsEnabled && !group.IsEnabled && !data.IsEnabled
                    && !application.IsEnabled && !browseData.IsEnabled && !browseApplication.IsEnabled && ServicesBlocked());
                panel.SetHostBusy(false); Arrange();
                check(state + ".backup-busy-recovery", backup.IsEnabled
                    && UIElementAutomationPeer.CreatePeerForElement(backup)!.IsEnabled());
                check(state + ".host-busy-recovery", client.IsEnabled && !group.IsEnabled && data.IsEnabled
                    && application.IsEnabled && browseData.IsEnabled && browseApplication.IsEnabled && ServicesBlocked());
                paths.IsExpanded = false; advanced.IsChecked = false; Arrange();
                check(state + ".simple-mode-restored", ((FrameworkElement)window.FindName("SimpleProxyPanel")).Visibility == Visibility.Visible
                    && ((FrameworkElement)window.FindName("AdvancedProxyPanel")).Visibility == Visibility.Collapsed);
                check(state + ".hidden", !window.IsVisible && !window.IsActive && !window.ShowActivated && !window.ShowInTaskbar);
            }
            finally { window.Close(); model.DarkTheme = false; }
        }
        File.WriteAllText(Path.Combine(directory, "automation-facts.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
