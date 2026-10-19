using System.Text.RegularExpressions;

namespace ChunsonSpace.Core;

// Binding is separate from env.json: backups intentionally exclude browser cookies.
// A newly authorized browser keeps its original directory while Chrome is running.
public sealed class AccountBrowserProfiles(string root, IFileStorage? storage = null)
{
    public const string PoolName = ".web-sessions";
    private readonly IFileStorage files = storage ?? new PhysicalFileStorage();
    private readonly string browserRoot = Path.GetFullPath(Path.Combine(root, "browser-envs"));
    private string Binding(string name) => Path.Combine(browserRoot, ProfileRules.Name(name), "browser-profile.json");
    public string Create()
    {
        var path = Path.Combine(browserRoot, PoolName, "auth-" + Guid.NewGuid().ToString("N"));
        PhysicalFileStorage.RejectLinks(path); files.CreateDirectory(path); return path;
    }
    public string? Read(string name) => Read(name, deleting: false);
    internal string? ReadForDeletion(string name) => Read(name, deleting: true);
    private string? Read(string name, bool deleting)
    {
        var path = Binding(name);
        if (!files.FileExists(path)) return null;
        var relative = JsonFiles.Text(JsonFiles.Read(files, path), "directory");
        var profile = Validate(Path.Combine(browserRoot, relative));
        // A prior permanent deletion may have finished the browser before another target failed.
        if (deleting && !files.DirectoryExists(profile)) return null;
        if (deleting && !files.FileExists(Path.Combine(profile, "chunson-browser-owner.json"))
            && files.Files(profile, false).Count == 0 && files.Directories(profile).Count == 0) return profile;
        if (!files.FileExists(Path.Combine(profile, "chunson-browser-owner.json"))
            || JsonFiles.Text(JsonFiles.Read(files, Path.Combine(profile, "chunson-browser-owner.json")), "account") != name)
            throw new IOException("浏览器档案所属账号不一致。");
        return profile;
    }
    public string Validate(string profile)
    {
        profile = Path.GetFullPath(profile);
        var relative = Path.GetRelativePath(browserRoot, profile);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length != 2 || (parts[0] != PoolName && ProfileRules.Name(parts[0]) != parts[0])
            || !Regex.IsMatch(parts[1], "\\Aauth-[a-f0-9]{32}\\z"))
            throw new IOException("浏览器档案目录无效。");
        PhysicalFileStorage.RejectLinks(profile);
        return profile;
    }
    public void Bind(string name, string profile)
    {
        name = ProfileRules.Name(name); profile = Validate(profile);
        if (!files.DirectoryExists(profile)) throw new IOException("浏览器档案不存在。");
        var previous = Read(name);
        if (previous is not null && !previous.Equals(profile, StringComparison.OrdinalIgnoreCase))
            throw new IOException("此账号已有浏览器档案，请从原档案重新授权。");
        foreach (var directory in files.Directories(browserRoot))
        {
            var other = Path.GetFileName(directory);
            if (other.Equals(PoolName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!other.Equals(name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Read(other), profile, StringComparison.OrdinalIgnoreCase))
                throw new IOException("浏览器档案已属于其他账号。");
        }
        files.CreateDirectory(Path.GetDirectoryName(Binding(name))!);
        JsonFiles.Write(files, Path.Combine(profile, "chunson-browser-owner.json"), new() { ["account"] = name });
        JsonFiles.Write(files, Binding(name), new() { ["directory"] = Path.GetRelativePath(browserRoot, profile) });
    }
    public void RenameBinding(string oldName, string newName, string? oldProfile)
    {
        if (oldProfile is null) return;
        var oldAccount = Path.Combine(browserRoot, ProfileRules.Name(oldName));
        var profile = oldProfile.StartsWith(oldAccount + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(browserRoot, ProfileRules.Name(newName), Path.GetRelativePath(oldAccount, oldProfile)) : oldProfile;
        profile = Validate(profile);
        var binding = Binding(newName); var owner = Path.Combine(profile, "chunson-browser-owner.json");
        var oldBinding = files.Read(binding); var oldOwner = files.Read(owner);
        try
        {
            JsonFiles.Write(files, owner, new() { ["account"] = newName });
            JsonFiles.Write(files, binding, new() { ["directory"] = Path.GetRelativePath(browserRoot, profile) });
        }
        catch { files.WriteAtomic(owner, oldOwner); files.WriteAtomic(binding, oldBinding); throw; }
    }
}
