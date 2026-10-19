using ChunsonSpace.Core;

namespace ChunsonSpace.Windows;

public sealed record WindowsPathInputs(string ExecutableDirectory, string UserProfile, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, string? RegisteredPackageFamily = null)
{
    public static WindowsPathInputs Capture(string? registeredPackageFamily = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "ChunsonSpace_DATA_DIR", "CODEX_HOME", "APPDATA", "LOCALAPPDATA" })
            if (System.Environment.GetEnvironmentVariable(key) is { } value) environment[key] = value;
        return new(AppContext.BaseDirectory, System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), System.Environment.CurrentDirectory, environment, registeredPackageFamily);
    }
}

public interface IWindowsPathProbe
{
    bool DirectoryExists(string path);
    IReadOnlyList<string> Directories(string path);
    bool CanWriteDirectory(string path);
}

public sealed class WindowsPathProbe : IWindowsPathProbe
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public IReadOnlyList<string> Directories(string path) => Directory.GetDirectories(path);
    public bool CanWriteDirectory(string path)
    {
        var probe = Path.Combine(path, ".ChunsonSpace-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally { try { File.Delete(probe); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}

public static class WindowsDataPaths
{
    public static SwitchStoragePaths Resolve(WindowsPathInputs inputs, IWindowsPathProbe probe)
    {
        string? Value(string name) => inputs.Environment.TryGetValue(name, out var value) ? value : null;
        var rootOverride = Value("ChunsonSpace_DATA_DIR");
        string root;
        if (rootOverride is not null)
        {
            if (!Path.IsPathFullyQualified(rootOverride)) throw new ArgumentException("ChunsonSpace_DATA_DIR 必须是绝对路径");
            root = Path.GetFullPath(rootOverride);
        }
        else
        {
            root = Path.GetFullPath(inputs.ExecutableDirectory);
            if (!probe.CanWriteDirectory(root)) root = Path.Combine(string.IsNullOrWhiteSpace(Value("APPDATA")) ? inputs.UserProfile : Value("APPDATA")!, "ChunsonSpace");
        }
        var homeOverride = Value("CODEX_HOME");
        var home = string.IsNullOrEmpty(homeOverride) ? Path.Combine(inputs.UserProfile, ".codex") : Path.GetFullPath(homeOverride, inputs.WorkingDirectory);
        string? packaged = null;
        var local = Value("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(local))
        {
            var packages = Path.Combine(local, "Packages");
            if (probe.DirectoryExists(packages))
            {
                var candidates = probe.Directories(packages).Where(path => Path.GetFileName(path).StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();
                if (inputs.RegisteredPackageFamily is { } family && family.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) && !family.Contains('/') && !family.Contains('\\'))
                {
                    var registered = Path.Combine(packages, family);
                    candidates.RemoveAll(path => path.Equals(registered, StringComparison.OrdinalIgnoreCase));
                    candidates.Insert(0, registered);
                }
                packaged = candidates.Select(path => Path.Combine(path, "LocalCache", "Roaming", "Codex", "web", "Codex")).FirstOrDefault(probe.DirectoryExists);
            }
        }
        var roaming = Value("APPDATA");
        var web = string.IsNullOrWhiteSpace(roaming) ? null : Path.Combine(roaming, "Codex", "web", "Codex");
        return new(Path.GetFullPath(root), Path.GetFullPath(home), packaged, web);
    }
}
