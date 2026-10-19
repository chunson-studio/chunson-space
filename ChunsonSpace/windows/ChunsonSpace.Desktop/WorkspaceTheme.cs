using System.Windows.Media;

namespace ChunsonSpace.Desktop;

public static class WorkspaceTheme
{
    private static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["CanvasBrush"] = "#101113", ["SurfaceBrush"] = "#202124", ["ElevatedBrush"] = "#2B2D31",
        ["SidebarBrush"] = "#17181A", ["PlayerBrush"] = "#202124", ["InkBrush"] = "#F1F3F8",
        ["MutedBrush"] = "#AEB6C5", ["AccentBrush"] = "#2684FF", ["AccentInkBrush"] = "#FFFFFF",
        ["BrandSecondaryBrush"] = "#2684FF", ["PrimaryBrush"] = "#2684FF",
        ["LineBrush"] = "#303237", ["OutlineBrush"] = "#454950", ["HoverOutlineBrush"] = "#2684FF",
        ["FocusOutlineBrush"] = "#2684FF", ["SelectionBrush"] = "#2A2E35", ["InfoSurfaceBrush"] = "#2B2D31",
        ["ErrorSurfaceBrush"] = "#40272C", ["BadgeBrush"] = "#2A2E35",
        ["SuccessBrush"] = "#43DF83", ["ErrorBrush"] = "#FF5E69",
        ["ScrimBrush"] = "#9920282E", ["DetailScrimBrush"] = "#7720282E",
        ["GardenStemBrush"] = "#8E9E9D", ["GardenLeafBrush"] = "#788E85", ["GardenPaleBrush"] = "#4E6464",
        ["GardenShadeBrush"] = "#6A8390", ["GardenPetalBrush"] = "#D8D6CB", ["GardenGoldBrush"] = "#B3A58D",
        ["GardenCloudBrush"] = "#6A8191", ["GardenMeadowBrush"] = "#29343A"
    };
    private static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["CanvasBrush"] = "#F8F4ED", ["SurfaceBrush"] = "#FCFAF5", ["ElevatedBrush"] = "#F3EFE8",
        ["SidebarBrush"] = "#F4F0EA", ["PlayerBrush"] = "#F4F0EA", ["InkBrush"] = "#173246",
        ["MutedBrush"] = "#5F676E", ["AccentBrush"] = "#477F68", ["AccentInkBrush"] = "#FFFFFF",
        ["BrandSecondaryBrush"] = "#477F68", ["PrimaryBrush"] = "#548E75",
        ["LineBrush"] = "#E4DFD7", ["OutlineBrush"] = "#C3BDB4", ["HoverOutlineBrush"] = "#548E75",
        ["FocusOutlineBrush"] = "#477F68", ["SelectionBrush"] = "#E0E5DA", ["InfoSurfaceBrush"] = "#F3F0E9",
        ["ErrorSurfaceBrush"] = "#F4E8E2", ["BadgeBrush"] = "#E6ECE3",
        ["SuccessBrush"] = "#248950", ["ErrorBrush"] = "#D64049",
        ["ScrimBrush"] = "#66353F48", ["DetailScrimBrush"] = "#44353F48",
        ["GardenStemBrush"] = "#8B9995", ["GardenLeafBrush"] = "#BBC6BB", ["GardenPaleBrush"] = "#D9DFD6",
        ["GardenShadeBrush"] = "#A8BBC6", ["GardenPetalBrush"] = "#FCFAF5", ["GardenGoldBrush"] = "#C5B89F",
        ["GardenCloudBrush"] = "#FCFAF5", ["GardenMeadowBrush"] = "#EDE9E1"
    };
    private static readonly string[] AvatarColors = ["#576F7B", "#66786D", "#7B6D60", "#6C7188"];
    internal static string AvatarColor(int index) => AvatarColors[index % AvatarColors.Length];
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
            ? new LinearGradientBrush(Parse("#354550"), Parse("#272F35"), 24)
            : new LinearGradientBrush(Parse("#F4F0EA"), Parse("#FCFAF5"), 24);
        hero.Freeze(); resources["HeroBrush"] = hero;
        var sky = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(0, 1) };
        sky.GradientStops.Add(new GradientStop(Parse(Current == "dark" ? "#002D3D49" : "#00DDE8F0"), 0));
        sky.GradientStops.Add(new GradientStop(Parse(Current == "dark" ? "#2D3D49" : "#E9F0F4"), 0.45));
        sky.GradientStops.Add(new GradientStop(Parse(Current == "dark" ? "#29343A" : "#F4F0E8"), 1));
        sky.Freeze(); resources["GardenSkyBrush"] = sky;
        var ambient = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(0, 1),
            GradientStops =
            {
                new GradientStop(Parse("#004A7087"), 0),
                new GradientStop(Parse("#004A7087"), 0.58),
                new GradientStop(Parse("#004A7087"), 1)
            }
        };
        ambient.Freeze(); resources["AmbientBrush"] = ambient;
    }
    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
}
