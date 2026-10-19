using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChunsonSpace.Accounts;

namespace ChunsonSpace.Desktop;

// Only in-memory preview models: no repository, history files or request services.
internal static class ModelAccessibilityVerification
{
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
            try
            {
                void Capture(string panel)
                {
                    arrange(root, size.Width, size.Height);
                    var bitmap = new RenderTargetBitmap(size.Width, size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, state + "-" + panel + ".png")); encoder.Save(stream);
                }
                void Inspect(string id, string expectedName, Control? control = null)
                {
                    control ??= (Control)window.FindName(id);
                    var peer = UIElementAutomationPeer.CreatePeerForElement(control)!;
                    var combo = control as ComboBox;
                    var pattern = combo is null ? PatternInterface.Invoke : PatternInterface.ExpandCollapse;
                    var name = peer.GetName();
                    var value = peer.GetPattern(PatternInterface.Value) as IValueProvider;
                    check(state + "." + id + ".name", name == expectedName);
                    check(state + "." + id + ".capabilities", control.Focusable && peer.GetPattern(pattern) is not null
                        && peer.IsEnabled() == control.IsEnabled
                        && (combo is not { IsEditable: true } || value is not null && value.Value == combo.Text));
                    facts.Add(new { state, id, name, expectedName, enabled = peer.IsEnabled(),
                        pattern = pattern.ToString(), value = value?.Value, selection = combo?.SelectedItem?.ToString() });
                }
                model.OpenModelTestCommand.Execute(null);
                Capture("chooser");
                var close = Descendants((DependencyObject)window.FindName("ModelTestPanel")).OfType<Button>()
                    .Single(button => button.Command == model.CloseModelTestCommand);
                Inspect("ModelTestCloseButton", "关闭模型测试", close);
                Inspect("ModelTestAccountPicker", "测试账号");
                check(state + ".chooser.empty-guard", model.IsModelTestOpen && model.ModelTestProfiles.Count == 0
                    && !model.ChooseVisualTestCommand.CanExecute("Variation"));
                close.Command!.Execute(null);
                check(state + ".chooser.close-command", !model.IsModelTestOpen);

                model.Selected = model.FilteredItems.First(item => item.Kind == "Account");
                model.OpenVisualTestsCommand.Execute(null);
                // Supply synthetic choices to the real panel template; services stay null.
                var visual = new VisualTestPanelViewModel(null, "fixture-account", VisualTestKind.Variation,
                    _ => { }, () => { }, ["fixture-account", "fixture-second"]);
                PanelContext(window, "VisualTestAccountPicker").DataContext = visual;
                Capture("visual");
                foreach (var item in new[] { ("Account", "测试账号"), ("Model", "测试模型"),
                    ("Variant", "测试题目"), ("History", "历史记录") })
                    Inspect("VisualTest" + item.Item1 + "Picker", item.Item2);
                var visualModel = (ComboBox)window.FindName("VisualTestModelPicker");
                visual.Model = "fixture-custom-model";
                check(state + ".visual.fixed-policy", !visualModel.IsEditable && visual.Model == "gpt-6-astra" && visual.Effort == "high"
                    && ((ComboBox)window.FindName("VisualTestEffortPicker")).Visibility == Visibility.Collapsed);
                check(state + ".visual.preview-guard", !visual.StartCommand.CanExecute(null) && !visual.StartAllCommand.CanExecute(null)
                    && !visual.CanSelectHistory && !visual.IsBusy);
                model.VisualTests!.CloseCommand.Execute(null);

                model.OpenCialloCommand.Execute(null);
                var ciallo = new CialloPanelViewModel(null, ["fixture-account", "fixture-second"], "fixture-account", _ => { }, () => { });
                PanelContext(window, "CialloAccountPicker").DataContext = ciallo;
                Capture("ciallo");
                foreach (var item in new[] { ("Account", "测试账号"), ("Model", "测试模型"), ("History", "历史记录") })
                    Inspect("Ciallo" + item.Item1 + "Picker", item.Item2);
                var cialloModel = (ComboBox)window.FindName("CialloModelPicker");
                ciallo.Model = "fixture-custom-model";
                check(state + ".ciallo.fixed-policy", !cialloModel.IsEditable && ciallo.Model == "全部模型" && ciallo.Effort == "high"
                    && ((ComboBox)window.FindName("CialloEffortPicker")).Visibility == Visibility.Collapsed);
                check(state + ".ciallo.preview-guard", !ciallo.StartCommand.CanExecute(null) && !ciallo.CanSelectHistory && !ciallo.IsBusy);
                check(state + ".hidden", !window.IsVisible && !window.IsActive && !window.ShowActivated && !window.ShowInTaskbar);
            }
            finally { window.Close(); model.DarkTheme = false; }
        }
        File.WriteAllText(Path.Combine(directory, "automation-facts.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static FrameworkElement PanelContext(MainWindow window, string id)
    {
        // Picker row -> panel header -> panel content grid, whose DataContext is the panel model.
        var picker = (FrameworkElement)window.FindName(id);
        return (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(picker)));
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
