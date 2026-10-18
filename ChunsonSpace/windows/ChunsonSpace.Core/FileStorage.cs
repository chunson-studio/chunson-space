using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChunsonSpace.Core;

public interface IFileStorage
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    byte[] Read(string path);
    long Length(string path) => Read(path).LongLength;
    void WriteAtomic(string path, byte[] content);
    void CreateDirectory(string path);
    IReadOnlyList<string> Files(string path, bool recursive);
    IReadOnlyList<string> Directories(string path);
    void MoveDirectory(string source, string destination);
    void DeleteFile(string path);
    void DeleteDirectory(string path, bool recursive);
    void CopyAtomic(string source, string destination) => WriteAtomic(destination, Read(source));
    string Hash(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Read(path)));
}

public sealed class PhysicalFileStorage : IFileStorage
{
    public bool FileExists(string path) { RejectLinks(path); return File.Exists(path); }
    public bool DirectoryExists(string path) { RejectLinks(path); return Directory.Exists(path); }
    public byte[] Read(string path) { RejectLinks(path); return File.ReadAllBytes(path); }
    public long Length(string path) { RejectLinks(path); return new FileInfo(path).Length; }
    public void CreateDirectory(string path) { RejectLinks(path); Directory.CreateDirectory(path); }
    public void MoveDirectory(string source, string destination) { RejectLinks(source); RejectLinks(destination); Directory.Move(source, destination); }
    public void DeleteFile(string path) { RejectLinks(path); if (File.Exists(path)) File.Delete(path); }
    public void DeleteDirectory(string path, bool recursive)
    {
        RejectLinks(path);
        if (!Directory.Exists(path)) return;
        if (recursive) { foreach (var file in Files(path, true)) RejectLinks(file); }
        Directory.Delete(path, recursive);
    }
    public IReadOnlyList<string> Files(string path, bool recursive)
    {
        RejectLinks(path);
        if (!Directory.Exists(path)) return [];
        var result = Directory.GetFiles(path, "*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 }).ToList();
        foreach (var file in result) RejectLinks(file);
        if (recursive) foreach (var directory in Directories(path)) result.AddRange(Files(directory, true));
        return result.Order(StringComparer.Ordinal).ToArray();
    }
    public IReadOnlyList<string> Directories(string path)
    {
        RejectLinks(path);
        if (!Directory.Exists(path)) return [];
        var result = Directory.GetDirectories(path, "*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 }).Order(StringComparer.Ordinal).ToArray();
        foreach (var directory in result) RejectLinks(directory);
        return result;
    }
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new StorageException(StorageError.Io, current, "拒绝通过符号链接或目录联接访问文件：" + current);
        }
    }
    public void WriteAtomic(string path, byte[] content)
    {
        RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void CopyAtomic(string source, string destination)
    {
        RejectLinks(source);
        RejectLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1048576, FileOptions.SequentialScan))
            { input.CopyTo(output, 1048576); output.Flush(true); }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public string Hash(string path)
    {
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }
}

internal static class JsonFiles
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static JsonObject Read(IFileStorage files, string path, bool missingAllowed = false)
    {
        try
        {
            if (!files.FileExists(path))
            {
                if (missingAllowed) return new();
                throw new StorageException(StorageError.Missing, path, "文件不存在。");
            }
            return JsonNode.Parse(files.Read(path)) as JsonObject ?? throw new JsonException("JSON 根节点必须是对象。");
        }
        catch (JsonException ex) { throw new StorageException(StorageError.Corrupt, path, "JSON 文件损坏：" + ex.Message, ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StorageException(StorageError.Io, path, "无法读取文件：" + ex.Message, ex); }
    }
    public static void Write(IFileStorage files, string path, JsonObject value)
    {
        try { files.WriteAtomic(path, Encoding.UTF8.GetBytes(value.ToJsonString(Options))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StorageException(StorageError.Io, path, "无法保存文件：" + ex.Message, ex); }
    }
    public static string Text(JsonObject value, string key) => value[key] is JsonValue node && node.TryGetValue<string>(out var text) ? text : "";
    public static bool Boolean(JsonObject value, string key, bool fallback = false) => value[key] is JsonValue node && node.TryGetValue<bool>(out var flag) ? flag : fallback;
}
