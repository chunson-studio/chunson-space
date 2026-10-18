using System.Text.Json;
using ChunsonSpace.Core;

namespace ChunsonSpace.NodeTesting;

// Indexed by the existing route-definition hash. Never stores node credentials or definitions.
public sealed class NodeProbeHistory(string directory)
{
    private readonly PhysicalFileStorage files = new();
    private string EntryPath(string id)
    {
        if (id.Length != 64 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid node ID.");
        return Path.Combine(directory, id.ToLowerInvariant() + ".json");
    }
    public NodeProbeResult? Find(string id)
    {
        var path = EntryPath(id);
        try
        {
            if (!files.FileExists(path) || files.Length(path) > 256 * 1024) return null;
            var result = JsonSerializer.Deserialize<NodeProbeResult>(files.Read(path));
            return result?.Id == id && !result.DelayOnly ? result : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException or JsonException) { return null; }
    }
    public void Put(NodeProbeResult result)
    {
        if (result.DelayOnly) return;
        var path = EntryPath(result.Id);
        Directory.CreateDirectory(directory);
        files.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(result));
    }
    public static bool IsComplete(NodeProbeResult? result) => result is { Success: true, DelayOnly: false, Quality.HasVerifiedExit: true }
        && result.Quality.Score?.Quality is >= 0 and <= 100;
    public IReadOnlyList<NodeCandidate> Pending(IEnumerable<NodeCandidate> nodes) => nodes.Where(NodeScreeningRules.IsAccountCandidate)
        .DistinctBy(node => node.Id).Where(node => !IsComplete(Find(node.Id))).ToArray();
}
