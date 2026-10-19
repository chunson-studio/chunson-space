using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ChunsonSpace.Desktop;

public sealed class ArtworkCanvas : Grid
{
    public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(nameof(Picture), typeof(ImageSource), typeof(ArtworkCanvas), new PropertyMetadata(null, (d, e) => ((ArtworkCanvas)d).picture.Source = (ImageSource?)e.NewValue));
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(ArtworkCanvas), new PropertyMetadata(1.0, (d, _) => ((ArtworkCanvas)d).Resize()));
    public ImageSource? Picture { get => (ImageSource?)GetValue(PictureProperty); set => SetValue(PictureProperty, value); }
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    private readonly Image picture = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false };
    private Point? origin; private double startX, startY;
    internal event Action<ArtworkCanvas, double, double>? Panned;
    internal double ArtworkWidth => picture.Width;
    internal double ArtworkHeight => picture.Height;
    internal Point PanPosition => new(scroll.HorizontalOffset, scroll.VerticalOffset);
    public ArtworkCanvas()
    {
        ClipToBounds = true; Background = new SolidColorBrush(Color.FromRgb(237, 240, 235));
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        scroll.Content = picture; Children.Add(scroll); SizeChanged += (_, _) => Resize();
        scroll.ScrollChanged += (_, _) => Panned?.Invoke(this, scroll.ScrollableWidth == 0 ? 0 : scroll.HorizontalOffset / scroll.ScrollableWidth, scroll.ScrollableHeight == 0 ? 0 : scroll.VerticalOffset / scroll.ScrollableHeight);
        PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { origin = e.GetPosition(this); startX = scroll.HorizontalOffset; startY = scroll.VerticalOffset; CaptureMouse(); e.Handled = true; } };
        PreviewMouseMove += (_, e) => { if (origin is { } point) { var next = e.GetPosition(this); scroll.ScrollToHorizontalOffset(startX + point.X - next.X); scroll.ScrollToVerticalOffset(startY + point.Y - next.Y); } };
        PreviewMouseUp += async (_, e) =>
        {
            if (origin is not null) { origin = null; ReleaseMouseCapture(); e.Handled = true; return; }
            if (e.ChangedButton == MouseButton.Left && DataContext is ArtworkPaneViewModel pane && pane.IsInteractive)
            { var at = e.GetPosition(picture); if (at.X >= 0 && at.Y >= 0 && at.X < picture.ActualWidth && at.Y < picture.ActualHeight) await pane.ClickAsync(at.X * 960 / picture.ActualWidth, at.Y * 600 / picture.ActualHeight); }
        };
    }
    private void Resize()
    {
        var width = Math.Max(1, Math.Min(Math.Max(1, ActualWidth - 18), Math.Max(1, ActualHeight - 18) * 1.6));
        picture.Width = width * Zoom; picture.Height = width * Zoom / 1.6;
        if (Zoom == 1) { scroll.ScrollToHorizontalOffset(0); scroll.ScrollToVerticalOffset(0); }
    }
    internal void PanTo(double x, double y) { scroll.ScrollToHorizontalOffset(x * scroll.ScrollableWidth); scroll.ScrollToVerticalOffset(y * scroll.ScrollableHeight); }
}
