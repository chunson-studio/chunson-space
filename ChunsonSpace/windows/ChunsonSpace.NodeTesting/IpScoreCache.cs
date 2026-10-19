using System.Net;
using System.Text.Json;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.NodeTesting;

public sealed record CachedIpScore(string Ip, IpPureScore Score, DateTimeOffset CheckedAt, IpQualityReport? Report = null)
{
    public IpQualityReport Apply(IpQualityReport current)
    {
        if (!EgressSources.SameIp(Ip, current.Ip)) return current;
        // Cached scores never replace fresh address/location evidence.
        return current with { Company = Score.Organization, Asn = Score.Asn,
            Score = Score with { Message = Score.Message + $"\n历史评分：{CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}；本次未重新测分。" },
            Source = current.Source + "；" + Score.Provider + "（历史评分）" };
    }
}

// Public IP attributes only: no node definitions, proxy credentials or account data.
public sealed class IpScoreCache
{
    private readonly string? path;
    private readonly IAccountClock clock;
    private readonly object gate = new();
    private readonly Dictionary<string, CachedIpScore> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedIpScore> pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> removed = new(StringComparer.Ordinal);
    private DateTime loadedAt;
    private long loadedLength = -1;
    public IpScoreCache(string? path = null, IAccountClock? clock = null)
    {
        this.path = path is null ? null : Path.GetFullPath(path); this.clock = clock ?? new SystemAccountClock();
        Refresh();
    }
    private void Refresh(bool force = false)
    {
        if (path is null) return;
        try
        {
            var files = new PhysicalFileStorage();
            if (!files.FileExists(path) || files.Length(path) > 4 * 1024 * 1024) return;
            var stamp = File.GetLastWriteTimeUtc(path); var length = files.Length(path);
            if (!force && stamp == loadedAt && length == loadedLength) return;
            var values = JsonSerializer.Deserialize<CachedIpScore[]>(files.Read(path)) ?? [];
            entries.Clear();
            foreach (var item in values)
                if (Valid(item)) entries[IPAddress.Parse(item.Ip).ToString()] = item;
            foreach (var (ip, value) in pending) entries[ip] = value;
            foreach (var ip in removed) entries.Remove(ip);
            loadedAt = stamp; loadedLength = length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or StorageException) { }
    }
    private bool Valid(CachedIpScore? item) => item is not null && IPAddress.TryParse(item.Ip, out _)
        && item.Score?.Quality is >= 0 and <= 100 && item.CheckedAt != default && item.CheckedAt <= clock.UtcNow
        && (item.Report is null || item.Report.Ip == item.Ip);
    public CachedIpScore? Find(string ip)
    {
        lock (gate) { Refresh(); return entries.TryGetValue(IPAddress.Parse(ip).ToString(), out var item) && Valid(item) ? item : null; }
    }
    public void Put(string ip, IpPureScore score, IpQualityReport? report = null)
    {
        var entry = new CachedIpScore(IPAddress.Parse(ip).ToString(), score, clock.UtcNow, report);
        if (!Valid(entry)) return;
        lock (gate) { entries[entry.Ip] = entry; pending[entry.Ip] = entry; removed.Remove(entry.Ip); }
    }
    public void Forget(string ip)
    {
        lock (gate) { var key = IPAddress.Parse(ip).ToString(); entries.Remove(key); pending.Remove(key); removed.Add(key); }
    }
    public void Save()
    {
        lock (gate)
        {
            if (pending.Count == 0 && removed.Count == 0 || path is null) return;
            // UI and CLI share this file. Serialize only the merge/write, never network work.
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            using var writer = new Mutex(false, "Local\\ChunsonIpScores-" + key);
            var held = false;
            try
            {
                try { held = writer.WaitOne(TimeSpan.FromSeconds(2)); } catch (AbandonedMutexException) { held = true; }
                if (!held) return;
                Refresh(force: true);
                new PhysicalFileStorage().WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(entries.Values.Where(Valid).OrderByDescending(item => item.CheckedAt).Take(10000)));
                pending.Clear(); removed.Clear(); loadedAt = File.GetLastWriteTimeUtc(path); loadedLength = new FileInfo(path).Length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException) { }
            finally { if (held) writer.ReleaseMutex(); }
        }
    }
}
