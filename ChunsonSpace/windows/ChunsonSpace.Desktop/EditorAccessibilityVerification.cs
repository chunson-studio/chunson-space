using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

// Actual editor controls with synthetic view models; no workspace, save or system operations.
internal static class EditorAccessibilityVerification
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
            var description = (FrameworkElement)window.FindName("EditorDescriptionText");
            var content = (Grid)((FrameworkElement)description.Parent).Parent;
            var overlay = (Grid)((Border)content.Parent).Parent;
            var callbacks = 0;
            var original = model.Selected!;
            var originalName = original.Name;
            try
            {
                void Arrange() => arrange(root, size.Width, size.Height);
                foreach (var mode in new[] { ProfileEditMode.Create, ProfileEditMode.Edit, ProfileEditMode.Rename, ProfileEditMode.SaveCurrent })
                {
                    var editor = new ProfileEditorViewModel(mode, IdentityKind.Account, mode == ProfileEditMode.Create ? null : original,
                        _ => { callbacks++; return Task.CompletedTask; }, () => callbacks++)
                    { Name = "离线测试档案", Proxy = "http://127.0.0.1:7890", Timezone = "Asia/Shanghai", Locale = "zh-CN" };
                    content.DataContext = editor;
                    overlay.Visibility = Visibility.Visible;
                    Arrange();
                    var bitmap = new RenderTargetBitmap(size.Width, size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(directory, state + "-" + mode + ".png"))) encoder.Save(stream);

                    TextBox Bound(string property) => Descendants(content).OfType<TextBox>()
                        .Single(control => BindingOperations.GetBinding(control, TextBox.TextProperty)?.Path.Path == property);
                    var fields = new List<(string Path, string Name)> { ("Name", "档案名称") };
                    if (editor.IsEnvironmentEditor)
                        fields.AddRange([("Proxy", "HTTP / HTTPS 代理"), ("Timezone", "时区（IANA 名称，可留空）"), ("Locale", "界面语言（例如 zh-CN / en-US）")]);
                    foreach (var field in fields)
                    {
                        var control = Bound(field.Path);
                        var peer = UIElementAutomationPeer.CreatePeerForElement(control)!;
                        var value = peer.GetPattern(PatternInterface.Value) as IValueProvider;
                        var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
                        var id = state + "." + mode + "." + field.Path;
                        check(id + ".name", peer.GetName() == field.Name);
                        check(id + ".capabilities", control.Focusable && peer.GetAutomationControlType() == AutomationControlType.Edit
                            && peer.IsEnabled() == control.IsEnabled && value is not null && !value.IsReadOnly && value.Value == control.Text);
                        check(id + ".layout", control.IsVisible == window.IsVisible && control.Visibility == Visibility.Visible
                            && bounds.Width > 0 && bounds.Height > 0 && new Rect(0, 0, size.Width, size.Height).Contains(bounds));
                        facts.Add(new { state, mode = mode.ToString(), field = field.Path, name = peer.GetName(), expectedName = field.Name,
                            enabled = peer.IsEnabled(), value = value?.Value, bounds = bounds.ToString() });
                    }
                    foreach (var field in fields.Where(field => Bound(field.Path).IsEnabled))
                    {
                        var control = Bound(field.Path);
                        ((IValueProvider)UIElementAutomationPeer.CreatePeerForElement(control)!.GetPattern(PatternInterface.Value)!).SetValue("fixture-" + field.Path);
                    }
                    Arrange();
                    check(state + "." + mode + ".editable-values", fields.All(field =>
                        typeof(ProfileEditorViewModel).GetProperty(field.Path)!.GetValue(editor)?.ToString() == Bound(field.Path).Text
                        && (!Bound(field.Path).IsEnabled || Bound(field.Path).Text == "fixture-" + field.Path)));
                    check(state + "." + mode + ".mode-guards", Bound("Name").IsEnabled == (mode != ProfileEditMode.Edit)
                        && editor.ShowOverwrite == (mode == ProfileEditMode.SaveCurrent) && !editor.Overwrite && editor.Mode == mode);
                    if (editor.IsEnvironmentEditor)
                    {
                        var follow = (CheckBox)window.FindName("AccountFollowNetworkToggle");
                        follow.IsChecked = true; Arrange();
                        var blocked = !Bound("Proxy").IsEnabled && editor.FollowSystemProxy;
                        follow.IsChecked = false; Arrange();
                        check(state + "." + mode + ".network-mode-guard", blocked && Bound("Proxy").IsEnabled && !editor.FollowSystemProxy);
                    }
                }
                check(state + ".no-operations", callbacks == 0 && original.Name == originalName && !model.IsEditorOpen
                    && !model.NewProfileCommand.CanExecute(null) && !model.EditSelectedCommand.CanExecute(null));
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
