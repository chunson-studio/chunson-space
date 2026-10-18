namespace ChunsonSpace.Accounts;

public sealed class LocalVisualRenderer(Func<string> findBrowser)
{
    public async Task RenderAsync(string page, string screenshot, CancellationToken token)
    {
        ChunsonSpace.Core.PhysicalFileStorage.RejectLinks(page);
        if (new FileInfo(page).Length > 16_000_000) throw new IOException("本地预览页面过大。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var session = await LocalArtworkSession.OpenDocumentAsync(findBrowser(), await File.ReadAllTextAsync(page, deadline.Token), deadline.Token);
            await Task.Delay(2000, deadline.Token);
            await File.WriteAllBytesAsync(screenshot, await session.CaptureAsync(1, deadline.Token), deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("本地作品渲染未完成，未发送检查请求。模型请求本身不设总超时。"); }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }
    internal static bool TryDeletePrivateDirectory(string path, string parent, string prefix)
    {
        try
        {
            var resolved = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith(prefix, StringComparison.Ordinal)
                || !Directory.Exists(resolved) || Directory.EnumerateFileSystemEntries(resolved, "*", SearchOption.AllDirectories).Append(resolved).Any(item => (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)) return false;
            Directory.Delete(resolved, true); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
