using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChunsonSpace.Desktop;

public static class BrandIcon
{
    public static BitmapSource CreateImage(int size = 64)
    {
        var mark = (ImageSource)System.Windows.Application.Current.Resources["BrandMark"];
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(mark, new Rect(0, 0, size, size));
        }
        var image = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
    public static byte[] CreateIco()
    {
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var entries = sizes.Select(size =>
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(CreateImage(size)));
            using var buffer = new MemoryStream(); encoder.Save(buffer); return buffer.ToArray();
        }).ToArray();
        using var result = new MemoryStream(); using var writer = new BinaryWriter(result);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)entries.Length);
        var offset = 6 + 16 * entries.Length;
        for (var index = 0; index < entries.Length; index++)
        {
            writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index])); writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(entries[index].Length); writer.Write(offset); offset += entries[index].Length;
        }
        foreach (var entry in entries) writer.Write(entry);
        return result.ToArray();
    }
    public static System.Drawing.Icon CreateTrayIcon()
    {
        using var data = new MemoryStream(CreateIco()); using var icon = new System.Drawing.Icon(data); return (System.Drawing.Icon)icon.Clone();
    }
}
