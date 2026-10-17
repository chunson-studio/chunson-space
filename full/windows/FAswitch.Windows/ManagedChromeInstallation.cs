using System.Security.Cryptography;
using System.Text.Json;
using FAswitch.Core;

namespace FAswitch.Windows;

public enum ManagedBrowserProgramStatus { Missing, Prepared, Stale, Failed }
public sealed record ManagedBrowserProgramState(ManagedBrowserProgramStatus Status, string Executable, IReadOnlyList<string> ProgramPaths, string Message)
{
    public bool Ready => Status == ManagedBrowserProgramStatus.Prepared;
}

/// <summary>Copies application binaries only, on explicit preparation. Never reads Chrome user data.</summary>
public sealed class ManagedChromeInstallation(string programRoot, IProcessSystem? processes = null, string? sourceExecutable = null)
{
    private sealed record Manifest(int Schema, string SourceExecutable, string SourceHash, string SourceLayout, IReadOnlyDictionary<string, string> ExecutableHashes);
    private string Root => WindowsDesktopLaunchSystem.ResolveRealPath(Path.GetFullPath(programRoot));
    private const string Marker = ".chunson-managed-browser.json";
    private string? Source()
    {
        if (sourceExecutable is not null) return Path.GetFullPath(sourceExecutable);
        foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" })
        {
            var directory = Environment.GetEnvironmentVariable(variable);
            if (directory is null) continue;
            var candidate = Path.Combine(directory, "Google", "Chrome", "Application", "chrome.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
    public ManagedBrowserProgramState Preview()
    {
        var executable = Path.Combine(Root, "chrome.exe");
        try
        {
            PhysicalFileStorage.RejectLinks(Root);
            if (!File.Exists(Path.Combine(Root, Marker))) return new(ManagedBrowserProgramStatus.Missing, executable, [], "专用授权浏览器尚未准备，请先一键配置保护。");
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(Root, Marker))) ?? throw new InvalidDataException();
            if (manifest.Schema != 1 || manifest.ExecutableHashes.Count == 0) throw new InvalidDataException();
            var source = Source();
            if (source is null || !File.Exists(source) || !source.Equals(manifest.SourceExecutable, StringComparison.OrdinalIgnoreCase) || Hash(source) != manifest.SourceHash
                || Layout(Path.GetDirectoryName(source)!) != manifest.SourceLayout)
                return new(ManagedBrowserProgramStatus.Stale, executable, [], "已安装 Chrome 版本发生变化，请重新准备专用授权浏览器和防火墙规则。");
            var paths = new List<string>();
            foreach (var pair in manifest.ExecutableHashes)
            {
                var path = Path.GetFullPath(Path.Combine(Root, pair.Key));
                if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || Hash(path) != pair.Value)
                    return new(ManagedBrowserProgramStatus.Stale, executable, [], "专用浏览器程序发生变化，禁止使用未确认程序。");
                PhysicalFileStorage.RejectLinks(path); paths.Add(path);
            }
            if (!paths.Contains(executable, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException();
            return new(ManagedBrowserProgramStatus.Prepared, executable, paths.Order(StringComparer.OrdinalIgnoreCase).ToArray(), "独立浏览器程序已准备；程序文件核验通过，浏览器网络行为尚未验证。");
        }
        catch (Exception) { return new(ManagedBrowserProgramStatus.Failed, executable, [], "无法确认独立浏览器程序，未使用普通 Chrome 作为替代。"); }
    }
    public string? FindPreparedChrome() { var state = Preview(); return state.Ready ? state.Executable : null; }
    public Task<ManagedBrowserProgramState> PrepareAsync(CancellationToken token = default) => Task.Run(() => Prepare(token), token);
    private ManagedBrowserProgramState Prepare(CancellationToken token)
    {
        var current = Preview();
        if (current.Ready) return current;
        string staging = Root + ".prepare-" + Guid.NewGuid().ToString("N");
        string backup = Root + ".previous-" + Guid.NewGuid().ToString("N");
        bool moved = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var source = Source();
            if (source is null || !File.Exists(source) || !Path.GetFileName(source).Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException();
            var sourceRoot = Path.GetDirectoryName(source)!;
            if (!Path.GetFileName(sourceRoot).Equals("Application", StringComparison.OrdinalIgnoreCase)) throw new IOException("源目录必须是 Chrome Application 程序目录");
            PhysicalFileStorage.RejectLinks(sourceRoot); PhysicalFileStorage.RejectLinks(Root);
            if (sourceRoot.Equals(Root, StringComparison.OrdinalIgnoreCase) || Root.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException();
            if (Directory.Exists(Root) && !File.Exists(Path.Combine(Root, Marker))) throw new IOException("拒绝覆盖没有受管标记的目录");
            if ((processes ?? new WindowsProcessSystem()).Snapshot().Any(process => process.Executable.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                return new(ManagedBrowserProgramStatus.Failed, current.Executable, [], "专用授权浏览器仍在运行，请关闭该授权窗口后重新配置。");
            Directory.CreateDirectory(staging);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(sourceRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            {
                token.ThrowIfCancellationRequested(); PhysicalFileStorage.RejectLinks(path);
                var relative = Path.GetRelativePath(sourceRoot, path);
                var destination = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination, false);
                if (Hash(path) != Hash(destination)) throw new IOException("浏览器副本核验失败");
                if (destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) hashes[relative] = Hash(destination);
            }
            if (!File.Exists(Path.Combine(staging, "chrome.exe"))) throw new IOException();
            File.WriteAllText(Path.Combine(staging, Marker), JsonSerializer.Serialize(new Manifest(1, source, Hash(source), Layout(sourceRoot), hashes)));
            if (Directory.Exists(Root)) { Directory.Move(Root, backup); moved = true; }
            try { Directory.Move(staging, Root); }
            catch { if (moved) Directory.Move(backup, Root); moved = false; throw; }
            if (moved) { PhysicalFileStorage.RejectLinks(backup); Directory.Delete(backup, true); moved = false; }
            return Preview();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(ManagedBrowserProgramStatus.Failed, Path.Combine(Root, "chrome.exe"), [], "独立浏览器准备失败，未启动普通 Chrome 或修改系统设置。"); }
        finally { if (Directory.Exists(staging)) { PhysicalFileStorage.RejectLinks(staging); Directory.Delete(staging, true); } }
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Layout(string root) => string.Join("|", Directory.EnumerateDirectories(root).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
}
