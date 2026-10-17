using System.Windows.Media;

namespace FAswitch.Desktop;

public static class WorkspaceTheme
{
    private static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["CanvasBrush"] = "#1A211C", ["SurfaceBrush"] = "#212B24", ["ElevatedBrush"] = "#2A352C",
        ["SidebarBrush"] = "#182119", ["PlayerBrush"] = "#212B24", ["InkBrush"] = "#EEF5E7",
        ["MutedBrush"] = "#B6C3AE", ["AccentBrush"] = "#A9CC8A", ["AccentInkBrush"] = "#1C2D18",
        ["BrandSecondaryBrush"] = "#A9CC8A", ["PrimaryBrush"] = "#A9CC8A",
        ["LineBrush"] = "#354639", ["OutlineBrush"] = "#788B75", ["HoverOutlineBrush"] = "#A9CC8A",
        ["FocusOutlineBrush"] = "#A9CC8A", ["SelectionBrush"] = "#32472C", ["InfoSurfaceBrush"] = "#2A352C",
        ["ErrorSurfaceBrush"] = "#48352E", ["BadgeBrush"] = "#32472C",
        ["GardenStemBrush"] = "#94AC84", ["GardenLeafBrush"] = "#759664", ["GardenPaleBrush"] = "#526C49",
        ["GardenShadeBrush"] = "#8BAB76", ["GardenPetalBrush"] = "#D5DDBF", ["GardenGoldBrush"] = "#C7B77D",
        ["GardenCloudBrush"] = "#718A90", ["GardenMeadowBrush"] = "#2B402E"
    };
    private static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["CanvasBrush"] = "#F5F7F2", ["SurfaceBrush"] = "#FEFFFB", ["ElevatedBrush"] = "#F0F5EA",
        ["SidebarBrush"] = "#EAF1E5", ["PlayerBrush"] = "#EAF1E5", ["InkBrush"] = "#243329",
        ["MutedBrush"] = "#62705D", ["AccentBrush"] = "#638746", ["AccentInkBrush"] = "#243B19",
        ["BrandSecondaryBrush"] = "#638746", ["PrimaryBrush"] = "#D1E5B9",
        ["LineBrush"] = "#E0E7D8", ["OutlineBrush"] = "#84947C", ["HoverOutlineBrush"] = "#638746",
        ["FocusOutlineBrush"] = "#638746", ["SelectionBrush"] = "#E2EED5", ["InfoSurfaceBrush"] = "#F0F5EA",
        ["ErrorSurfaceBrush"] = "#F7EBE5", ["BadgeBrush"] = "#E2EED5",
        ["GardenStemBrush"] = "#7B8F70", ["GardenLeafBrush"] = "#B6CFA1", ["GardenPaleBrush"] = "#D8E6C5",
        ["GardenShadeBrush"] = "#94B180", ["GardenPetalBrush"] = "#FFFDF2", ["GardenGoldBrush"] = "#C7AE72",
        ["GardenCloudBrush"] = "#FFFFFF", ["GardenMeadowBrush"] = "#E2EDD4"
    };
    public static string Current { get; private set; } = "light";
    public static void Apply(string theme)
    {
        Current = theme == "light" ? "light" : "dark";
        var resources = System.Windows.Application.Current.Resources;
        foreach (var (name, color) in Current == "dark" ? Dark : Light)
        {
            var brush = new SolidColorBrush(Parse(color)); brush.Freeze(); resources[name] = brush;
        }
        var hero = Current == "dark"
            ? new LinearGradientBrush(Parse("#30412C"), Parse("#212B24"), 24)
            : new LinearGradientBrush(Parse("#EAF1E5"), Parse("#FEFFFB"), 24);
        hero.Freeze(); resources["HeroBrush"] = hero;
        var sky = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(0, 1) };
        sky.GradientStops.Add(new GradientStop(Parse(Current == "dark" ? "#00243742" : "#00DCEEFA"), 0));
        sky.GradientStops.Add(new GradientStop(Parse(Current == "dark" ? "#243742" : "#E2F2FA"), 0.45));
        sky.GradientStops.Add(new GradientStop(Parse(Current == "dark" ? "#27382C" : "#EDF5E8"), 1));
        sky.Freeze(); resources["GardenSkyBrush"] = sky;
        var ambient = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(0, 1),
            GradientStops =
            {
                new GradientStop(Parse("#0078A950"), 0),
                new GradientStop(Parse("#0078A950"), 0.58),
                new GradientStop(Parse(Current == "dark" ? "#0078A950" : "#0078A950"), 1)
            }
        };
        ambient.Freeze(); resources["AmbientBrush"] = ambient;
    }
    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
}
