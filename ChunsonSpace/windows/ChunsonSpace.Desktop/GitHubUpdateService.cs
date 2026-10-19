using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChunsonSpace.Desktop;

internal sealed record ReleaseAsset(long Id, string Name, long Size, string Url, string? Digest);
internal sealed record AppRelease(Version Version, string Tag, string Page, ReleaseAsset? Archive, ReleaseAsset? Manifest);
internal sealed record UpdateFile(string Path, int Method, long Offset, long CompressedSize, long Size, string Sha256);
internal sealed record UpdateManifest(int Schema, string Version, string AssetName, long AssetSize, string AssetSha256, UpdateFile[] Files);
internal sealed record PreparedUpdate(string Directory, string Version, UpdateFile[] Files, long DownloadBytes);

internal static class UpdateRules
{
    public const string Repository = "https://github.com/chunson-studio/chunson-space";
    public const string Latest = "https://api.github.com/repos/chunson-studio/chunson-space/releases/latest";
    public const string ArchiveName = "Chunson-Full-Windows-x64.zip";
    public const string ManifestName = "Chunson-Update-Windows-x64.json";
    public static Version CurrentVersion => typeof(UpdateRules).Assembly.GetName().Version!;
    public static string DisplayVersion(Version version) => version.ToString(version.Revision > 0 ? 4 : 3);
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, MaxDepth = 16 };
    public static Version ParseVersion(string text)
    {
        if (!Regex.IsMatch(text, @"^v?[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}(\.[0-9]{1,5})?$", RegexOptions.CultureInvariant)) throw new InvalidDataException("版本格式无效。");
        var v = Version.Parse(text.TrimStart('v'));
        if (new[] { v.Major, v.Minor, v.Build, Math.Max(0, v.Revision) }.Any(x => x > 65535)) throw new InvalidDataException("版本超出范围。");
        return new(v.Major, v.Minor, v.Build, Math.Max(0, v.Revision));
    }
    public static bool HashValid(string? hash) => hash is not null && Regex.IsMatch(hash, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    // Bundled for fresh installs, but independently running and versioned during manager updates.
    // Keep the ZIP manifest complete; never download, stage, replace or roll back this component here.
    public static bool IndependentComponent(string path) => string.Equals(path, "Widgets/BeijingClock.exe", StringComparison.OrdinalIgnoreCase);
    public static string RelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 180 || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.Any(c => c < 32 || "<>\"|?*".Contains(c))) throw new InvalidDataException("更新路径无效。");
        var parts = path.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)", RegexOptions.IgnoreCase))) throw new InvalidDataException("更新路径无效。");
        // Only the distribution's application assets. No account store, project, browser or personal data.
        var allowed = parts.Length == 1
            ? path is "ChunsonSwitch.exe" or "createdump.exe" or "ChunsonSwitch.deps.json" or "ChunsonSwitch.runtimeconfig.json" or "LICENSE.txt" or "LICENSE" or "README.md" || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
            : parts[0] is "ThirdParty" or "Resources" || path.StartsWith("scripts/proxy/", StringComparison.Ordinal) || path == "runtimes/mihomo/mihomo.exe" || path.StartsWith("Widgets/", StringComparison.Ordinal) || parts.Length == 2 && Regex.IsMatch(parts[0], "^[a-z]{2}(-[A-Za-z]{2,4})?$") && path.EndsWith(".resources.dll", StringComparison.Ordinal);
        if (!allowed) throw new InvalidDataException("文件不属于应用更新范围。");
        return path;
    }
    public static string SafePath(string root, string relative)
    {
        RelativePath(relative);
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径越界。");
        NoLinks(full);
        return full;
    }
    public static void NoLinks(string path)
    {
        for (var cursor = System.IO.Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = System.IO.Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不支持链接路径。");
    }
    public static AppRelease ParseRelease(byte[] json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var r = doc.RootElement;
        if (r.GetProperty("draft").GetBoolean() || r.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("只检查正式稳定版。");
        var tag = r.GetProperty("tag_name").GetString()!; var version = ParseVersion(tag);
        var page = r.GetProperty("html_url").GetString()!;
        if (page != Repository + "/releases/tag/" + tag) throw new InvalidDataException("发布链接不属于本项目。");
        var assets = r.GetProperty("assets");
        if (assets.GetArrayLength() > 64) throw new InvalidDataException("附件数量超限。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var ids = new HashSet<long>();
        ReleaseAsset? zip = null, manifest = null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.GetProperty("name").GetString()!; var id = a.GetProperty("id").GetInt64(); var size = a.GetProperty("size").GetInt64();
            var url = a.GetProperty("browser_download_url").GetString()!;
            if (!Regex.IsMatch(name, "^[A-Za-z0-9._-]{1,100}$") || !names.Add(name) || id <= 0 || !ids.Add(id) || size <= 0 || size > 2L * 1024 * 1024 * 1024 || url != Repository + "/releases/download/" + tag + "/" + name) throw new InvalidDataException("发布附件无效。");
            string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (digest is not null && (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !HashValid(digest[7..]))) throw new InvalidDataException("附件摘要无效。");
            var asset = new ReleaseAsset(id, name, size, url, digest);
            if (name == ArchiveName) zip = asset;
            if (name == ManifestName) { if (size > 2 * 1024 * 1024) throw new InvalidDataException("更新清单过大。"); manifest = asset; }
        }
        return new(version, tag, page, zip, manifest);
    }
    public static UpdateManifest ParseManifest(byte[] json, AppRelease release)
    {
        var m = JsonSerializer.Deserialize<UpdateManifest>(json, Json) ?? throw new InvalidDataException("更新清单为空。");
        if (m.Schema != 1 || ParseVersion(m.Version) != release.Version || m.AssetName != ArchiveName || m.AssetSize != release.Archive?.Size || !HashValid(m.AssetSha256) || release.Archive.Digest is { } digest && digest != "sha256:" + m.AssetSha256 || m.Files is null || m.Files.Length is < 2 or > 4096) throw new InvalidDataException("更新清单与发布资产不匹配。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0, end = 0;
        foreach (var f in m.Files.OrderBy(f => f.Offset))
        {
            RelativePath(f.Path);
            if (!paths.Add(f.Path) || f.Method is not (0 or 8) || f.Offset < end || f.CompressedSize < 0 || f.CompressedSize > 128 * 1024 * 1024 || f.Size < 0 || f.Size > 256 * 1024 * 1024 || f.Offset > m.AssetSize - f.CompressedSize || !HashValid(f.Sha256) || f.Method == 0 && f.Size != f.CompressedSize || f.Size > 0 && f.CompressedSize == 0) throw new InvalidDataException("更新文件结构无效。");
            end = f.Offset + f.CompressedSize; total += f.Size;
            if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("更新解压大小超限。");
        }
        if (!paths.Contains("ChunsonSwitch.exe") || !paths.Contains("ChunsonSwitch.dll")) throw new InvalidDataException("更新缺少应用入口。");
        foreach (var path in paths)
            for (var parent = System.IO.Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = System.IO.Path.GetDirectoryName(parent))
                if (paths.Contains(parent.Replace('\\', '/'))) throw new InvalidDataException("更新文件与目录冲突。");
        return m;
    }
}

internal sealed class GitHubUpdateService : IDisposable
{
    private readonly HttpClient client;
    public GitHubUpdateService(HttpMessageHandler? handler = null)
    {
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, AutomaticDecompression = DecompressionMethods.None });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ChunsonSpace/" + UpdateRules.CurrentVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }
    public async Task<AppRelease> CheckAsync(CancellationToken token) => UpdateRules.ParseRelease(await ReadAssetAsync(UpdateRules.Latest, 256 * 1024, null, null, token).ConfigureAwait(false));
    private async Task<HttpResponseMessage> SendAsync(string address, long? start, long? length, CancellationToken token)
    {
        var uri = new Uri(address);
        for (var redirect = 0; redirect < 4; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (start is not null) request.Headers.Range = new RangeHeaderValue(start, start + length - 1);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var next = response.Headers.Location; response.Dispose();
            if (next is null) throw new InvalidDataException("下载重定向无效。");
            uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new InvalidDataException("下载重定向不属于 GitHub 资产服务。");
        }
        throw new InvalidDataException("下载重定向过多。");
    }
    private async Task<byte[]> ReadAssetAsync(string url, int bound, (long Start, long Length, long Total)? range, long? exact, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(range is null ? 20 : 60));
        using var response = await SendAsync(url, range?.Start, range?.Length, timeout.Token).ConfigureAwait(false);
        if (range is { } r)
        {
            var h = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || h?.Unit != "bytes" || h.From != r.Start || h.To != r.Start + r.Length - 1 || h.Length != r.Total || response.Content.Headers.ContentLength != r.Length)
                throw new InvalidDataException("服务器未提供所需范围，不能按需更新。请查看发布页；未下载整包。");
        }
        else if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("GitHub 更新请求失败。");
        if (response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > bound || exact is not null && response.Content.Headers.ContentLength is { } known && known != exact) throw new InvalidDataException("响应大小或编码无效。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[65536];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bound + 1L - output.Length)), timeout.Token).ConfigureAwait(false);
            if (count == 0) break;
            output.Write(buffer, 0, count);
            if (output.Length > bound) throw new InvalidDataException("响应超过大小限制。");
        }
        if (exact is not null && output.Length != exact) throw new InvalidDataException("下载不完整。");
        return output.ToArray();
    }
    public async Task<PreparedUpdate> PrepareAsync(AppRelease release, string installRoot, string stage, IProgress<string>? progress, CancellationToken token)
    {
        // Caller dispatches this operation off the UI thread; ConfigureAwait(false) also keeps hashing off it.
        if (release.Manifest is null || release.Archive is null) throw new InvalidDataException("此版本没有增量更新清单，请查看发布页。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(10)); token = timeout.Token;
        var raw = await ReadAssetAsync(release.Manifest.Url, 2 * 1024 * 1024, null, release.Manifest.Size, token).ConfigureAwait(false);
        if (release.Manifest.Digest is { } digest && "sha256:" + Convert.ToHexStringLower(SHA256.HashData(raw)) != digest) throw new InvalidDataException("更新清单摘要不匹配。");
        var manifest = UpdateRules.ParseManifest(raw, release);
        var changed = new List<UpdateFile>();
        progress?.Report("正在比较本机文件…");
        foreach (var file in manifest.Files)
        {
            if (UpdateRules.IndependentComponent(file.Path)) continue;
            token.ThrowIfCancellationRequested(); var path = UpdateRules.SafePath(installRoot, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size || await HashAsync(path, token).ConfigureAwait(false) != file.Sha256) changed.Add(file);
        }
        var bytes = changed.Sum(f => f.CompressedSize);
        progress?.Report($"变化 {changed.Count} 个文件，共 {bytes / 1048576d:F2} MB；未变化文件无需下载，独立时钟保留现有版本。");
        UpdateRules.NoLinks(stage);
        if (Directory.Exists(stage)) throw new IOException("更新暂存目录已存在。");
        Directory.CreateDirectory(stage); long received = 0;
        foreach (var f in changed)
        {
            token.ThrowIfCancellationRequested();
            var compressed = f.CompressedSize == 0 ? [] : await ReadAssetAsync(release.Archive.Url, checked((int)f.CompressedSize), (f.Offset, f.CompressedSize, manifest.AssetSize), f.CompressedSize, token).ConfigureAwait(false);
            using var input = new MemoryStream(compressed);
            using Stream content = f.Method == 8 ? new DeflateStream(input, CompressionMode.Decompress) : input;
            var target = UpdateRules.SafePath(stage, f.Path); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long size = 0;
                while (true)
                {
                    var n = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, f.Size + 1 - size)), token).ConfigureAwait(false);
                    if (n == 0) break; size += n;
                    if (size > f.Size) throw new InvalidDataException("解压大小超限。");
                    await output.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
                }
                if (size != f.Size) throw new InvalidDataException("解压大小不匹配。");
            }
            if (await HashAsync(target, token).ConfigureAwait(false) != f.Sha256) throw new InvalidDataException("更新文件 SHA256 不匹配。");
            received += f.CompressedSize; progress?.Report($"已下载 {received / 1048576d:F2} / {bytes / 1048576d:F2} MB，准备更新…");
        }
        return new(stage, release.Version.ToString(), changed.ToArray(), bytes);
    }
    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
    public void Dispose() => client.Dispose();
}
