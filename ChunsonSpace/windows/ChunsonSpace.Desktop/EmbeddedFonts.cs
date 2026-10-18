using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ChunsonSpace.Desktop;

internal sealed record EmbeddedFontFace(string Family, string Uri, bool Embedded);
internal sealed record EmbeddedFontEvidence(IReadOnlyList<EmbeddedFontFace> Regular, IReadOnlyList<EmbeddedFontFace> Bold)
{
    public bool UsesEmbeddedEnglishAndChinese => new[] { Regular, Bold }.All(faces =>
        faces.Any(face => face.Family.StartsWith("Inter", StringComparison.Ordinal) && face.Embedded)
        && faces.Any(face => face.Family.StartsWith("Source Han Sans CN", StringComparison.Ordinal) && face.Embedded));
}
internal static class EmbeddedFonts
{
    public static EmbeddedFontEvidence Inspect()
    {
        var family = (FontFamily)System.Windows.Application.Current.FindResource("AppFont");
        return new(Draw(FontWeights.Normal), Draw(FontWeights.Bold));
        IReadOnlyList<EmbeddedFontFace> Draw(FontWeight weight)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
                drawing.DrawText(new FormattedText("Chunson Space 中文测试", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), 20, Brushes.Black, 1), new Point());
            return Glyphs(VisualTreeHelper.GetDrawing(visual)).Select(glyph =>
            {
                var face = glyph.GlyphRun.GlyphTypeface;
                var name = face.FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var english) ? english : face.FamilyNames.Values.First();
                var uri = face.FontUri.AbsoluteUri;
                return new EmbeddedFontFace(name, uri, uri.StartsWith("pack://", StringComparison.OrdinalIgnoreCase) && uri.Contains("/Assets/Fonts/", StringComparison.OrdinalIgnoreCase));
            }).Distinct().ToArray();
        }
    }
    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing? drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var nested in Glyphs(child)) yield return nested;
    }
}
