using System.Text;
using System.Text.RegularExpressions;

namespace ChunsonSpace.Core;

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

    public static string ForCleanManagedRuntime(string account)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "model", "model_provider", "model_providers", "model_reasoning_effort",
            "model_reasoning_summary", "model_verbosity", "service_tier", "approval_policy", "sandbox_mode", "sandbox_workspace_write" };
        var parts = ToolParts(account, key => key.Length > 0 && !allowed.Contains(key));
        if (parts.ToolsTop.Length == 0 && parts.ToolsTables.Length == 0) return account;
        return string.Join("\n", new[] { parts.OtherTop, parts.OtherTables }.Where(s => s.Length > 0).Select(s => s.TrimEnd('\r', '\n'))) + "\n";
    }
    public static string MergeCleanManagedRuntime(string saved, string runtime)
    {
        var clean = ForCleanManagedRuntime(runtime);
        if (ForCleanManagedRuntime(saved) == clean) return saved;
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "model", "model_provider", "model_providers", "model_reasoning_effort",
            "model_reasoning_summary", "model_verbosity", "service_tier", "approval_policy", "sandbox_mode", "sandbox_workspace_write" };
        var old = ToolParts(saved, key => key.Length > 0 && !allowed.Contains(key));
        if (old.ToolsTop.Length == 0 && old.ToolsTables.Length == 0) return clean;
        var current = ToolParts(clean, _ => false);
        return string.Join("\n", new[] { current.OtherTop, old.ToolsTop, current.OtherTables, old.ToolsTables }.Where(s => s.Length > 0).Select(s => s.TrimEnd('\r', '\n'))) + "\n";
    }
    private static (string ToolsTop, string ToolsTables, string OtherTop, string OtherTables) ToolParts(string text, Func<string, bool>? remove = null)
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
                selectedTable = (remove ?? ToolRoots.Contains)(RootKey(line.Text.TrimStart().TrimStart('[')));
            }
            if (!inTable && neutral) selectedValue = (remove ?? ToolRoots.Contains)(RootKey(line.Text));
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
