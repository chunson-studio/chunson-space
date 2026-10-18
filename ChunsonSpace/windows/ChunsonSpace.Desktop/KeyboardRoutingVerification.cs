using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ChunsonSpace.Desktop;

// Offline routed-event checks. Models are in-memory previews without service/repository access.
internal static class KeyboardRoutingVerification
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
                WindowStartupLocation = WindowStartupLocation.Manual, Width = size.Width, Height = size.Height,
                Left = SystemParameters.VirtualScreenLeft - 4000, Top = SystemParameters.VirtualScreenTop - 4000 };
            var root = (FrameworkElement)window.FindName("RootLayout");
            try
            {
                arrange(root, size.Width, size.Height);
                // Suppress only popup HWNDs: Windows would clamp them onto the visible desktop.
                // Keep real ComboBox state, templates, child elements and routed event handlers.
                foreach (var id in new[] { "AccountFilterBox", "ModelTestAccountPicker", "VisualTestModelPicker" })
                {
                    var combo = (ComboBox)window.FindName(id);
                    combo.ApplyTemplate();
                    ((Popup)combo.Template.FindName("PART_Popup", combo)).IsOpen = false;
                }
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                arrange(root, size.Width, size.Height);
                void Capture(string name)
                {
                    arrange(root, size.Width, size.Height);
                    var bitmap = new RenderTargetBitmap(size.Width, size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, state + "-" + name + ".png")); encoder.Save(stream);
                }
                (bool PreviewHandled, bool Handled) Route(UIElement origin, Key key)
                {
                    var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    origin.RaiseEvent(e);
                    var previewHandled = e.Handled;
                    e.RoutedEvent = Keyboard.KeyDownEvent;
                    origin.RaiseEvent(e);
                    return (previewHandled, e.Handled);
                }
                void Scenario(string name, string id, Action setup, Func<bool> preserved, Func<bool> dismissed, bool editable = false)
                {
                    setup(); arrange(root, size.Width, size.Height);
                    var combo = (ComboBox)window.FindName(id);
                    var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                    var origin = editable ? (UIElement)combo.Template.FindName("PART_EditableTextBox", combo) : combo;
                    var selected = combo.SelectedItem;
                    var text = combo.Text;
                    Capture(name);
                    combo.IsDropDownOpen = true;
                    check(state + "." + name + ".fixture-ready", combo.IsLoaded && combo.IsDropDownOpen && !popup.IsOpen && combo.IsEnabled && preserved());
                    var first = Route(origin, Key.Escape);
                    var preservedAfter = preserved();
                    // Editable ComboBox handles Escape in its own preview handler; both routes are valid.
                    check(state + "." + name + ".first-escape", first.Handled && !combo.IsDropDownOpen
                        && preservedAfter && Equals(selected, combo.SelectedItem) && text == combo.Text);
                    facts.Add(new { state, name, origin = origin.GetType().Name, first.PreviewHandled, first.Handled,
                        dropdownOpenAfter = combo.IsDropDownOpen, preservedAfter, searchAfter = model.Search,
                        textBefore = text, textAfter = combo.Text, selectionPreserved = Equals(selected, combo.SelectedItem),
                        modelChooserOpenAfter = model.IsModelTestOpen, visualPanelOpenAfter = model.VisualTests is not null });
                    combo.IsDropDownOpen = false;
                    // Restore baseline state to test ordinary dismissal independently of its first Escape.
                    setup(); arrange(root, size.Width, size.Height);
                    var second = Route(origin, Key.Escape);
                    check(state + "." + name + ".ordinary-escape", second.Handled && dismissed());
                }
                Scenario("filter", "AccountFilterBox", () => model.Search = "工作",
                    () => model.Search == "工作", () => model.Search == "");
                Scenario("model-chooser", "ModelTestAccountPicker", () => model.OpenModelTestCommand.Execute(null),
                    () => model.IsModelTestOpen, () => !model.IsModelTestOpen);
                void OpenVisual()
                {
                    model.Selected = model.FilteredItems.First(item => item.Kind == "Account");
                    model.OpenVisualTestsCommand.Execute(null);
                    model.VisualTests!.Model = "gpt-6-astra";
                }
                Scenario("model-selection", "VisualTestModelPicker", OpenVisual,
                    () => model.VisualTests?.Model == "gpt-6-astra", () => model.VisualTests is null);
                var search = (TextBox)window.FindName("SearchBox");
                model.Search = "工作";
                KeyEventHandler consume = (_, e) => { if (e.Key == Key.Escape) e.Handled = true; };
                search.KeyDown += consume;
                var handled = Route(search, Key.Escape);
                search.KeyDown -= consume;
                check(state + ".child-handled", !handled.PreviewHandled && handled.Handled && model.Search == "工作");
                Route(search, Key.Escape);
                check(state + ".search-escape", model.Search == "");
                model.OpenModelTestCommand.Execute(null);
                var feedback = model.FeedbackTitle;
                Route((ComboBox)window.FindName("ModelTestAccountPicker"), Key.F5);
                check(state + ".modal-f5-guard", model.IsModelTestOpen && model.FeedbackTitle == feedback);
                model.CloseModelTestCommand.Execute(null);
                var canRefresh = model.RefreshCommand.CanExecute(null);
                var refresh = Route(search, Key.F5);
                check(state + ".home-f5", refresh.PreviewHandled == canRefresh && refresh.Handled == canRefresh);
                var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                check(state + ".offscreen", !window.IsActive && !window.ShowActivated && !window.ShowInTaskbar
                    && !new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight).IntersectsWith(screen));
            }
            finally { window.Close(); model.DarkTheme = false; }
        }
        File.WriteAllText(Path.Combine(directory, "routing-facts.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));
    }
}
