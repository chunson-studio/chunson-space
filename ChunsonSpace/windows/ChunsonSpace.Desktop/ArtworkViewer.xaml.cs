using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace ChunsonSpace.Desktop;
public partial class ArtworkViewer : UserControl
{
    private bool synchronizing;
    public ArtworkViewer() { InitializeComponent(); }
    private void ZoomWheel(object sender, MouseWheelEventArgs e)
    {
        if (Canvases(this).Any(c => c.IsMouseOver) && DataContext is ArtworkViewerViewModel model)
        { model.Zoom = e.Delta > 0 ? model.Zoom * 1.15 : model.Zoom / 1.15; e.Handled = true; }
    }
    private void CanvasLoaded(object sender, RoutedEventArgs e) { var canvas = (ArtworkCanvas)sender; canvas.Panned -= Sync; canvas.Panned += Sync; }
    private void Sync(ArtworkCanvas source, double x, double y)
    {
        if (synchronizing) return;
        synchronizing = true;
        try { foreach (var canvas in Canvases(this).Where(c => c != source)) canvas.PanTo(x, y); }
        finally { synchronizing = false; }
    }
    private static IEnumerable<ArtworkCanvas> Canvases(DependencyObject parent)
    {
        for (var i=0; i<VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is ArtworkCanvas canvas) yield return canvas;
            foreach(var nested in Canvases(child)) yield return nested;
        }
    }
}
