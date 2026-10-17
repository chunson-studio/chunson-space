using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Xml.Linq;

namespace FAswitch.Desktop;

// The owned SVG uses paths, nested groups, matrix placement and rotate/translate motion.
public sealed class BotanicalGarden : Grid
{
    private sealed record Motion(string Id, Canvas Layer, Animatable Target, DependencyProperty Property,
        double From, double To, double Seconds, ScaleTransform Bloom)
    { public bool Hovered { get; set; } }
    private readonly Viewbox illustration;
    private readonly Canvas clouds = new() { Width = 1200, Height = 360 };
    private readonly List<Motion> motions = [];
    private Window? owner;
    private bool hasAppeared;
    private Path? vine;
    private double vineLength;
    internal bool IsMotionRunning { get; private set; }
    internal double[] MotionValues => motions.Select(m => (double)m.Target.GetValue(m.Property)).ToArray();
    internal string[] HoveredRegions => motions.Where(m => m.Hovered).Select(m => m.Id).ToArray();
    internal Viewbox Illustration => illustration;
    internal Canvas Region(string id) => motions.Single(m => m.Id == id).Layer;

    public BotanicalGarden()
    {
        Focusable = false; ClipToBounds = true;
        var sky = new Border { IsHitTestVisible = false };
        sky.SetResourceReference(Border.BackgroundProperty, "GardenSkyBrush"); Children.Add(sky);
        var canvas = new Canvas { Width = 1200, Height = 360 };
        illustration = new Viewbox { Child = canvas, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom, Stretch = Stretch.Uniform };
        Children.Add(new Viewbox { Child = clouds, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center, Stretch = Stretch.Uniform });
        Children.Add(illustration);
        using var stream = System.Windows.Application.GetResourceStream(new Uri("/ChunsonSwitch;component/Assets/HomeGarden.svg", UriKind.Relative))!.Stream;
        var svg = XElement.Load(stream);
        AddGroups(svg, canvas);
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => RefreshMotion();
        SizeChanged += (_, _) => RefreshMotion();
    }
    private void AddGroups(XElement parent, Canvas container)
    {
        XNamespace ns = "http://www.w3.org/2000/svg";
        foreach (var group in parent.Elements(ns + "g"))
        {
            if ((string?)group.Attribute("id") == "sky-background") continue;
            var layer = new Canvas { Width = 1200, Height = 360 };
            if ((string?)group.Attribute("transform") is { } placement)
            {
                var matrix = Numbers(placement.Replace("matrix(", "").TrimEnd(')'));
                layer.RenderTransform = new MatrixTransform(new Matrix(matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5]));
            }
            var bounds = Rect.Empty;
            foreach (var element in group.Elements(ns + "path"))
            {
                var path = new Path { Data = Geometry.Parse((string)element.Attribute("d")!),
                    StrokeThickness = Number((string?)element.Attribute("stroke-width"), 1),
                    Opacity = Number((string?)element.Attribute("opacity"), 1),
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
                if ((string?)element.Attribute("data-fill") is { } fill) path.SetResourceReference(Shape.FillProperty, fill);
                if ((string?)element.Attribute("data-stroke") is { } stroke) path.SetResourceReference(Shape.StrokeProperty, stroke);
                bounds.Union(path.Data.Bounds);
                if ((string?)element.Attribute("id") == "vine-line")
                {
                    vine = path;
                    foreach (var figure in path.Data.GetFlattenedPathGeometry().Figures)
                    {
                        var point = figure.StartPoint;
                        foreach (var segment in figure.Segments.OfType<PolyLineSegment>())
                            foreach (var next in segment.Points) { vineLength += (next - point).Length; point = next; }
                    }
                    vineLength /= path.StrokeThickness;
                    path.StrokeDashArray = new DoubleCollection { vineLength, vineLength };
                }
                layer.Children.Add(path);
            }
            if (group.Element(ns + "animateTransform") is { } animation)
            {
                var values = ((string)animation.Attribute("values")!).Split(';').Select(Numbers).ToArray();
                var rotation = (string?)animation.Attribute("type") == "rotate";
                Transform transform = rotation ? new RotateTransform(0, values[0][1], values[0][2]) : new TranslateTransform();
                var bloom = new ScaleTransform(1, 1, rotation ? values[0][1] : bounds.X + bounds.Width / 2,
                    rotation ? values[0][2] : bounds.Y + bounds.Height / 2);
                layer.RenderTransform = new TransformGroup { Children = { transform, bloom } };
                var motion = new Motion((string)group.Attribute("id")!, layer, transform,
                    rotation ? RotateTransform.AngleProperty : TranslateTransform.XProperty, values[0][0], values[1][0],
                    Number(((string)animation.Attribute("dur")!).TrimEnd('s'), 10) / 2, bloom);
                motions.Add(motion);
                layer.Cursor = Cursors.Hand;
                layer.ToolTip = (string?)group.Attribute("data-hover") == "cloud" ? "碰一碰，让白云飘起来" : "碰一碰，花草随风轻摆";
                layer.MouseEnter += (_, _) => SetHovered(motion, true);
                layer.MouseLeave += (_, _) => SetHovered(motion, false);
            }
            else layer.IsHitTestVisible = group.Attribute("transform") is not null;
            AddGroups(group, layer);
            ((string?)group.Attribute("data-hover") == "cloud" ? clouds : container).Children.Add(layer);
        }
    }
    private static double Number(string? value, double fallback) => double.TryParse(value, CultureInfo.InvariantCulture, out var result) ? result : fallback;
    private static double[] Numbers(string value) => value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(value => Number(value, 0)).ToArray();
    private void SetHovered(Motion motion, bool entered)
    {
        motion.Hovered = entered;
        if (!IsMotionRunning || !SystemParameters.ClientAreaAnimation || motion.Layer.Visibility != Visibility.Visible) return;
        if (entered) Animate(motion);
        else
        {
            foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
                motion.Bloom.BeginAnimation(property, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
            var settle = new DoubleAnimation(motion.From, TimeSpan.FromMilliseconds(600))
                { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            settle.Completed += (_, _) => { if (IsMotionRunning && !motion.Hovered && motion.Layer.Visibility == Visibility.Visible) Animate(motion); };
            motion.Target.BeginAnimation(motion.Property, settle);
        }
    }
    private static void Animate(Motion motion)
    {
        var cloud = motion.Property == TranslateTransform.XProperty;
        var duration = motion.Hovered ? cloud ? 0.8 : 0.65 : motion.Seconds;
        var animation = new DoubleAnimation { To = motion.To * (motion.Hovered ? cloud ? 1.5 : 2.5 : 1),
            Duration = TimeSpan.FromSeconds(duration), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        if (!motion.Hovered) animation.From = motion.From;
        motion.Target.BeginAnimation(motion.Property, animation);
        foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
            motion.Bloom.BeginAnimation(property, new DoubleAnimation(motion.Hovered ? 1.025 : 1, TimeSpan.FromMilliseconds(200)));
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        owner = Window.GetWindow(this);
        if (owner is not null) { owner.Activated += OwnerChanged; owner.Deactivated += OwnerChanged; owner.StateChanged += OwnerChanged; }
        SystemParameters.StaticPropertyChanged += SystemMotionChanged; RefreshMotion();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (owner is not null) { owner.Activated -= OwnerChanged; owner.Deactivated -= OwnerChanged; owner.StateChanged -= OwnerChanged; }
        SystemParameters.StaticPropertyChanged -= SystemMotionChanged; owner = null; SetMotion(false);
    }
    private void OwnerChanged(object? sender, EventArgs args) => RefreshMotion();
    private void SystemMotionChanged(object? sender, PropertyChangedEventArgs args) => RefreshMotion();
    private void RefreshMotion()
    {
        foreach (var motion in motions.Where(m => m.Id is "tree-east" or "flowers-east" or "grass-east"))
        {
            var visibility = ActualWidth < 900 || ActualHeight < 540 ? Visibility.Hidden : Visibility.Visible;
            if (motion.Layer.Visibility == visibility) continue;
            motion.Layer.Visibility = visibility;
            if (IsMotionRunning) ApplyMotion(motion, visibility == Visibility.Visible);
        }
        var fits = ActualHeight >= 120 && ActualWidth >= 240;
        illustration.Visibility = fits ? Visibility.Visible : Visibility.Hidden;
        SetMotion(fits && IsLoaded && IsVisible && owner is { IsActive: true, WindowState: not WindowState.Minimized } && SystemParameters.ClientAreaAnimation);
    }
    internal void SetMotion(bool enabled)
    {
        if (IsMotionRunning == enabled) return;
        IsMotionRunning = enabled;
        foreach (var motion in motions) ApplyMotion(motion, enabled && motion.Layer.Visibility == Visibility.Visible);
        if (enabled && !hasAppeared)
        {
            hasAppeared = true;
            illustration.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)) { FillBehavior = FillBehavior.Stop });
            vine?.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(vineLength, 0, TimeSpan.FromSeconds(1.55))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop });
        }
        else if (!enabled) { illustration.BeginAnimation(OpacityProperty, null); vine?.BeginAnimation(Shape.StrokeDashOffsetProperty, null); }
    }
    private static void ApplyMotion(Motion motion, bool enabled)
    {
        if (enabled) Animate(motion);
        else
        {
            motion.Target.BeginAnimation(motion.Property, null);
            motion.Bloom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            motion.Bloom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }
}
