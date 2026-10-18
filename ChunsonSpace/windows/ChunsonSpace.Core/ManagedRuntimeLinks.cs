namespace ChunsonSpace.Core;

/// <summary>Recognize Codex's cache aliases without traversing their targets.</summary>
internal static class ManagedRuntimeLinks
{
    internal sealed record CacheAlias(string Path, string Target);

    public static IReadOnlyList<CacheAlias> Inspect(ManagedClonePaths paths, IFileStorage files)
    {
        var aliases = new List<CacheAlias>();
        var pending = new Stack<string>();
        pending.Push(paths.RuntimeRoot);
        while (pending.TryPop(out var directory))
        {
            PhysicalFileStorage.RejectLinks(directory);
            _ = files.Files(directory, false); // Ordinary files, including linked files, remain strict.
            foreach (var child in Directory.EnumerateDirectories(directory, "*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 }))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) aliases.Add(InspectAlias(paths, child));
                else pending.Push(child);
            }
        }
        return aliases;
    }

    public static void RemoveAliases(ManagedClonePaths paths, IReadOnlyList<CacheAlias> aliases)
    {
        foreach (var alias in aliases)
        {
            var current = InspectAlias(paths, alias.Path);
            if (!Same(current.Target, alias.Target)) throw Rejected(alias.Path, "插件缓存链接在清理前发生变化，未继续清理。");
            Directory.Delete(alias.Path, recursive: false); // Remove only the alias, never its target.
        }
    }

    private static CacheAlias InspectAlias(ManagedClonePaths paths, string path)
    {
        PhysicalFileStorage.RejectLinks(paths.RuntimeRoot);
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        PhysicalFileStorage.RejectLinks(parent);
        var relative = Path.GetRelativePath(paths.CodexHome, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Length != 5 || relative[0] != "plugins" || relative[1] != "cache" || relative[4] != "latest")
            throw Rejected(path, "运行目录包含不允许清理的目录链接，未访问其目标。");
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0 || info.LinkTarget is not { Length: > 0 } rawTarget)
            throw Rejected(path, "插件缓存链接无法确认，未继续清理。");
        var target = Path.GetFullPath(rawTarget, parent);
        var targetParent = Path.GetDirectoryName(target);
        if (targetParent is null || !Same(targetParent, parent) || Same(target, path))
            throw Rejected(path, "插件缓存链接指向缓存目录之外，未访问其目标。");
        PhysicalFileStorage.RejectLinks(target); // Reject chains; an already-removed sibling version is safe to unlink.
        if (File.Exists(target)) throw Rejected(path, "插件缓存链接目标不是版本目录，未继续清理。");
        return new(path, target);
    }

    private static bool Same(string left, string right) => Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static StorageException Rejected(string path, string reason) => new(StorageError.Io, path, reason + " 位置：" + path);
}
