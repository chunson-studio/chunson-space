using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChunsonSpace.Desktop;

// Real row event routes, with preview data only. Never opens a popup or activates a window.
internal static class AccountContextVerification
{
    internal static void Run(string directory, Action<string, bool> check, Action<FrameworkElement, int, int> arrange)
    {
        Directory.CreateDirectory(directory);
        var facts = new List<object>();
        var select = typeof(MainWindow).GetMethod("SelectContextIdentity", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var openingConstructor = typeof(ContextMenuEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(object), typeof(bool)], null)!;
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
                void Layout() => arrange(root, size.Width, size.Height);
                void Capture(string name)
                {
                    Layout();
                    var bitmap = new RenderTargetBitmap(size.Width, size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, state + "-" + name + ".png")); encoder.Save(stream);
                }
                Layout();
                var list = (ListBox)window.FindName("ProfileList");
                var original = model.IdentityPageItems[1];
                var target = model.IdentityPageItems[0];
                ListBoxItem Row() => (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(target);
                bool Direct() => (bool)select.Invoke(window, [Row()])!;
                MouseButtonEventArgs MouseRoute()
                {
                    var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                        { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent };
                    Row().RaiseEvent(e); return e;
                }
                ContextMenuEventArgs ContextRoute()
                {
                    // WPF's opening args have an internal constructor. Raise the actual routed event so
                    // the XAML EventSetter and production handler run; no ContextMenu.IsOpen assignment.
                    var e = (ContextMenuEventArgs)openingConstructor.Invoke([Row(), true]);
                    Row().RaiseEvent(e); return e;
                }
                void Reset() { model.Selected = original; Layout(); }
                var active = model.Active;
                Reset();
                check(state + ".ordinary.direct", Direct() && model.Selected == target && ReferenceEquals(list.SelectedItem, target));
                Reset();
                var normalMouse = MouseRoute();
                check(state + ".ordinary.mouse", !normalMouse.Handled && model.Selected == target);
                Reset();
                var normalContext = ContextRoute();
                check(state + ".ordinary.context", !normalContext.Handled && model.Selected == target);
                Reset();
                model.OpenDetailsCommand.Execute(original); Layout();
                check(state + ".detail.open", model.IsDetailOpen && model.Selected == original
                    && ((TextBlock)window.FindName("AccountDetailTitle")).Text == original.Name);
                Capture("detail-initial");
                bool Preserved() => model.IsDetailOpen && model.Selected == original && ReferenceEquals(list.SelectedItem, original)
                    && ((TextBlock)window.FindName("AccountDetailTitle")).Text == original.Name && model.Active == active;
                var accepted = Direct(); Layout();
                facts.Add(new { state, route = "direct", accepted, selected = model.Selected?.Name, expected = original.Name });
                check(state + ".detail.direct.blocked", !accepted && Preserved());
                Reset();
                var mouse = MouseRoute(); Layout();
                facts.Add(new { state, route = "preview-right-click", handled = mouse.Handled, selected = model.Selected?.Name, expected = original.Name });
                check(state + ".detail.mouse.blocked", mouse.Handled && Preserved());
                Reset();
                var context = ContextRoute(); Layout();
                facts.Add(new { state, route = "context-opening", handled = context.Handled, selected = model.Selected?.Name, expected = original.Name });
                check(state + ".detail.context.blocked", context.Handled && Preserved());
                Capture("detail-after-context");
                Reset();
                model.CloseDetailsCommand.Execute(null); Layout();
                var reopened = ContextRoute(); Layout();
                check(state + ".detail.close.restores-context", !model.IsDetailOpen && !reopened.Handled && model.Selected == target
                    && model.Active == active && model.HomeLaunchTarget == "目标：" + target.Name);
                model.OpenModelTestCommand.Execute(null); Layout();
                var chooser = ContextRoute();
                check(state + ".chooser.still-blocks-context", chooser.Handled && model.IsModelTestOpen && model.Selected == target);
                model.CloseModelTestCommand.Execute(null);
                var foreignRow = new ListBoxItem { DataContext = target with { Name = "outside-preview-list" } };
                check(state + ".foreign-row.still-rejected", !(bool)select.Invoke(window, [foreignRow])! && model.Selected == target);
                model.NavigateCommand.Execute("Settings"); Layout();
                check(state + ".other-page.still-rejected", !Direct() && model.Selected == target);
                check(state + ".fixture.nonactivating", !window.IsVisible && !window.IsActive && !window.ShowActivated && !window.ShowInTaskbar);
            }
            finally { window.Close(); model.DarkTheme = false; }
        }
        File.WriteAllText(Path.Combine(directory, "context-facts.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));
    }
}
