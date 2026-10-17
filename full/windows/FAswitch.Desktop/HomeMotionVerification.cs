using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using VectorPath = System.Windows.Shapes.Path;

namespace FAswitch.Desktop;

internal static class HomeMotionVerification
{
    // Uses a real, isolated WPF window. No account services, browser, or system-setting writes.
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        WorkspaceTheme.Apply("light");
        var garden = new BotanicalGarden { Margin = new Thickness(24) };
        var surface = new Grid { Background = (Brush)System.Windows.Application.Current.FindResource("CanvasBrush") };
        surface.Children.Add(garden);
        var window = new Window { Title = "Chunson · 花园互动验证", Width = 1100, Height = 650,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = surface };
        var checks = new List<object>();
        void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) throw new InvalidOperationException(name); }
        void Capture(string name)
        {
            var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
        }
        try
        {
            window.Show(); window.Activate();
            await surface.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("landscape is visible and reserves pointer interaction for its own region", garden.Illustration.Visibility == Visibility.Visible && garden.IsHitTestVisible && !garden.Focusable);
            Check("system animation preference is respected", garden.IsMotionRunning == (window.IsActive && SystemParameters.ClientAreaAnimation));
            // Exercise native animation clocks even on machines whose system preference is off.
            garden.SetMotion(true);
            await Task.Delay(450); var before = garden.MotionValues; var bounds = UiVerification.Bounds(garden, surface); Capture("motion-start");
            await Task.Delay(1250); var after = garden.MotionValues; Capture("motion-later");
            File.WriteAllText(Path.Combine(directory, "motion-samples.json"), JsonSerializer.Serialize(new { before, after, active = window.IsActive, garden.IsMotionRunning }));
            Check("all plant and cloud groups advance on the live WPF clock", before.Length == 10 && before.Zip(after).All(pair => Math.Abs(pair.First - pair.Second) > 0.015));
            Check("animation never changes layout bounds", UiVerification.Bounds(garden, surface) == bounds);
            foreach (var name in new[] { "tree-west", "flowers-east", "cloud-west" })
            {
                var region = garden.Region(name);
                var pointerTarget = region.Children.OfType<VectorPath>().Where(path => path.Fill is not null).SelectMany(path =>
                {
                    var area = path.Data.Bounds;
                    return Enumerable.Range(1, 9).SelectMany(x => Enumerable.Range(1, 9).Select(y => new Point(area.X + area.Width * x / 10, area.Y + area.Height * y / 10)))
                        .Where(point => path.Data.FillContains(point)).Select(point => path.TransformToAncestor(window).Transform(point));
                }).FirstOrDefault(point => window.InputHitTest(point) is DependencyObject hit && region.IsAncestorOf(hit));
                Check(name + " accepts a real WPF hit inside its painted shape", window.InputHitTest(pointerTarget) is DependencyObject target && region.IsAncestorOf(target));
                region.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                Check(name + " reacts to pointer contact without a click", garden.HoveredRegions.Contains(name));
                await Task.Delay(240);
                if (SystemParameters.ClientAreaAnimation) Check(name + " hover grows the vector while layout stays fixed",
                    ((TransformGroup)region.RenderTransform).Children.OfType<ScaleTransform>().Single().ScaleX > 1.01 && UiVerification.Bounds(garden, surface) == bounds);
                Capture("hover-" + name);
                region.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                Check(name + " returns to ambient motion on pointer exit", !garden.HoveredRegions.Contains(name));
            }
            garden.SetMotion(false); var still = garden.MotionValues;
            await Task.Delay(240);
            Check("paused illustration is stable and complete", !garden.IsMotionRunning && still.SequenceEqual(garden.MotionValues) && garden.Illustration.Opacity == 1);
            garden.SetMotion(true); garden.Visibility = Visibility.Collapsed;
            Check("leaving the visible page stops its animation clocks", !garden.IsMotionRunning);
            garden.Visibility = Visibility.Visible;
            window.WindowState = WindowState.Minimized; await Task.Delay(120);
            Check("minimizing stops motion", !garden.IsMotionRunning);
            window.WindowState = WindowState.Normal; window.Activate(); await Task.Delay(120);
            Check("restoring respects animation preference", garden.IsMotionRunning == (window.IsActive && SystemParameters.ClientAreaAnimation));
            window.Hide(); await Task.Delay(80);
            Check("hiding the window stops motion", !garden.IsMotionRunning);
            File.WriteAllText(Path.Combine(directory, "motion-verification.json"), JsonSerializer.Serialize(new { passed = true, checks,
                before, after, systemAnimationsEnabled = SystemParameters.ClientAreaAnimation, accountRequests = 0, systemSettingsChanged = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); }
    }
}
