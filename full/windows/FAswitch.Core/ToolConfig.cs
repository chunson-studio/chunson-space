using System.Text;
using System.Text.RegularExpressions;

namespace FAswitch.Core;

public static partial class TomlManagedKeys
{
    private static readonly HashSet<string> ToolRoots = new(StringComparer.Ordinal)
        { "mcp_servers", "skills", "plugins", "marketplaces" };

    /// <summary>Remove custom tool configuration; preserve account/model/security settings.</summary>
    public static string WithoutTools(string account)
    {
        var kept = ToolParts(account);
        if (kept.ToolsTop.Length == 0 && kept.ToolsTables.Length == 0) return account;
        var newline = Lines(account).FirstOrDefault(line => line.Ending.Length > 0)?.Ending ?? "\n";
        return string.Join(newline, new[] { kept.OtherTop, kept.OtherTables }
            .Where(part => part.Length > 0).Select(part => part.TrimEnd('\r', '\n'))) + newline;
    }

    private static (string ToolsTop, string ToolsTables, string OtherTop, string OtherTables) ToolParts(string text)
    {
        var topTools = new StringBuilder(); var tables = new StringBuilder();
        var otherTop = new StringBuilder(); var otherTables = new StringBuilder();
        var depth = 0; string? quote = null;
        var inTable = false; var selectedTable = false; var selectedValue = false; var pendingValue = false;
        foreach (var line in Lines(text))
        {
            var neutral = depth == 0 && quote is null && !pendingValue;
            if (neutral && Regex.IsMatch(line.Text, @"\A\s*\[\[?.+\]\]?\s*(?:#.*)?\z"))
            {
                inTable = true;
                selectedTable = ToolRoots.Contains(RootKey(line.Text.TrimStart().TrimStart('[')));
            }
            if (!inTable && neutral) selectedValue = ToolRoots.Contains(RootKey(line.Text));
            var destination = inTable ? selectedTable ? tables : otherTables : selectedValue ? topTools : otherTop;
            destination.Append(line.Text).Append(line.Ending);
            Scan(line.Text, ref depth, ref quote);
            if (!string.IsNullOrWhiteSpace(line.Text) && !line.Text.TrimStart().StartsWith('#'))
                pendingValue = line.Text.TrimEnd().EndsWith('=');
        }
        return (topTools.ToString(), tables.ToString(), otherTop.ToString(), otherTables.ToString());
    }

    private static string RootKey(string value)
    {
        var match = Regex.Match(value, "\\A\\s*(?:([A-Za-z0-9_-]+)|\"([^\"]+)\"|'([^']+)')\\s*(?=[.=\\]])");
        return !match.Success ? "" : match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value;
    }
}
