using System.Diagnostics;
using System.IO;

internal static class LinkFixtures
{
    private static int directorySymbols, directoryJunctions, fileSymbols;
    private static readonly List<string> skipped = [];

    public static void CreateDirectory(string fixtureRoot, string link, string target, string label)
    {
        ValidatePaths(fixtureRoot, link, target);
        string kind;
        try { Directory.CreateSymbolicLink(link, target); directorySymbols++; kind = "directory symbolic link"; }
        catch (IOException error) when (MissingSymbolicLinkPrivilege(error))
        {
            var command = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) command.ArgumentList.Add(argument);
            using var process = Process.Start(command) ?? throw new IOException("Isolated junction fixture command did not start.");
            if (!process.WaitForExit(10000)) throw new IOException("Isolated junction fixture creation did not complete.");
            if (process.ExitCode != 0) throw new IOException("Isolated junction fixture creation failed: " + process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
            directoryJunctions++; kind = "NTFS directory junction (symbolic-link privilege 1314 unavailable)";
        }
        VerifyLink(new DirectoryInfo(link), target);
        Console.WriteLine("FIXTURE " + label + ": " + kind);
    }

    public static bool TryCreateFile(string fixtureRoot, string link, string target, string label)
    {
        ValidatePaths(fixtureRoot, link, target);
        try
        {
            File.CreateSymbolicLink(link, target);
            VerifyLink(new FileInfo(link), target);
            fileSymbols++;
            Console.WriteLine("FIXTURE " + label + ": file symbolic link");
            return true;
        }
        catch (IOException error) when (MissingSymbolicLinkPrivilege(error))
        {
            skipped.Add(label);
            Console.WriteLine("SKIP " + label + ": file symbolic-link privilege 1314 unavailable; a directory junction cannot cover this file-link case.");
            return false;
        }
    }

    public static void Report()
    {
        Console.WriteLine($"LINK COVERAGE: {directorySymbols} directory symbolic links, {directoryJunctions} NTFS directory junctions, {fileSymbols} file symbolic links; {skipped.Count} skipped checks.");
        foreach (var label in skipped) Console.WriteLine("SKIP SUMMARY: " + label);
    }

    private static bool MissingSymbolicLinkPrivilege(IOException error) => (error.HResult & 0xffff) == 1314;
    private static void ValidatePaths(string fixtureRoot, params string[] paths)
    {
        var root = Path.GetFullPath(fixtureRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.IndexOfAny(['"', '\r', '\n', '&', '|', '<', '>', '^', '%', '!']) >= 0)
                throw new InvalidDataException("Link fixture paths must remain inside the explicit test root and be safe for the fixed mklink command.");
        }
    }
    private static void VerifyLink(FileSystemInfo link, string target)
    {
        // Read the reparse metadata without following the link. Restricted test
        // hosts may intentionally deny traversal even for an isolated junction.
        var rawTarget = link.LinkTarget;
        var actualTarget = rawTarget is null ? "" : Path.GetFullPath(rawTarget, Path.GetDirectoryName(link.FullName)!);
        if ((link.Attributes & FileAttributes.ReparsePoint) == 0
            || !actualTarget.Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Link fixture did not resolve to its exact isolated target.");
    }
}
