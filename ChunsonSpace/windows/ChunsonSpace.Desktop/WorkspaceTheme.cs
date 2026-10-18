using System.Windows.Media;

namespace ChunsonSpace.Desktop;

public static class WorkspaceTheme
{
    private static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["CanvasBrush"] = "#20272D", ["SurfaceBrush"] = "#272F35", ["ElevatedBrush"] = "#303A42",
        ["SidebarBrush"] = "#1D252B", ["PlayerBrush"] = "#252E35", ["InkBrush"] = "#F1EEE8",
        ["MutedBrush"] = "#B4C0C8", ["AccentBrush"] = "#ACC5D5", ["AccentInkBrush"] = "#213A49",
        ["BrandSecondaryBrush"] = "#ACC5D5", ["PrimaryBrush"] = "#ACC5D5",
        ["LineBrush"] = "#3A454D", ["OutlineBrush"] = "#657581", ["HoverOutlineBrush"] = "#ACC5D5",
        ["FocusOutlineBrush"] = "#ACC5D5", ["SelectionBrush"] = "#354550", ["InfoSurfaceBrush"] = "#303A42",
        ["ErrorSurfaceBrush"] = "#493B38", ["BadgeBrush"] = "#354550",
        ["SuccessBrush"] = "#8ED6A0", ["ErrorBrush"] = "#FF9D9D",
        ["ScrimBrush"] = "#9920282E", ["DetailScrimBrush"] = "#7720282E",
        ["GardenStemBrush"] = "#8E9E9D", ["GardenLeafBrush"] = "#788E85", ["GardenPaleBrush"] = "#4E6464",
        ["GardenShadeBrush"] = "#6A8390", ["GardenPetalBrush"] = "#D8D6CB", ["GardenGoldBrush"] = "#B3A58D",
        ["GardenCloudBrush"] = "#6A8191", ["GardenMeadowBrush"] = "#29343A"
    };
    private static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["CanvasBrush"] = "#F8F4ED", ["SurfaceBrush"] = "#FCFAF5", ["ElevatedBrush"] = "#F3EFE8",
        ["SidebarBrush"] = "#F4F0EA", ["PlayerBrush"] = "#F4F0EA", ["InkBrush"] = "#343C43",
        ["MutedBrush"] = "#5F676E", ["AccentBrush"] = "#4A7087", ["AccentInkBrush"] = "#2F4B5C",
        ["BrandSecondaryBrush"] = "#4A7087", ["PrimaryBrush"] = "#DAE5EB",
        ["LineBrush"] = "#E4DFD7", ["OutlineBrush"] = "#C3BDB4", ["HoverOutlineBrush"] = "#7391A3",
        ["FocusOutlineBrush"] = "#4A7087", ["SelectionBrush"] = "#E8E4DE", ["InfoSurfaceBrush"] = "#F3F0E9",
        ["ErrorSurfaceBrush"] = "#F4E8E2", ["BadgeBrush"] = "#E6EDF0",
        ["SuccessBrush"] = "#216B39", ["ErrorBrush"] = "#B4232C",
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
