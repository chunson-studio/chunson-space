using System.Text.Json;
using System.Text.Json.Serialization;

namespace FAswitch.Windows;

public sealed record TimezoneReadResult(bool Success, string Id, string Message = "");
public sealed record TimezoneResult(bool Success, bool Changed, bool Skipped, string Id, string Message);
public sealed record TimezoneRestoreRecord(
    [property: JsonPropertyName("saved")] string Saved,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("account")] string Account,
    [property: JsonPropertyName("iana")] string Iana,
    [property: JsonPropertyName("at")] string At);

public interface ITimezoneSystem
{
    string? ResolveWindowsId(string value);
    Task<TimezoneReadResult> ReadAsync(CancellationToken cancellationToken);
    Task<bool> SetAsync(string windowsId, CancellationToken cancellationToken);
}
public interface ITimezoneRestoreStore
{
    // Corrupt/inaccessible records throw; absence returns null. Never silently discard recovery material.
    TimezoneRestoreRecord? Read();
    bool TryWrite(TimezoneRestoreRecord record);
}

public sealed class JsonTimezoneRestoreStore(string path) : ITimezoneRestoreStore
{
    public TimezoneRestoreRecord? Read()
    {
        TimezoneRestoreRecord? value;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            value = JsonSerializer.Deserialize<TimezoneRestoreRecord>(stream);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        return value is { Saved.Length: > 0 } ? value : throw new InvalidDataException("时区恢复记录损坏");
    }
    public bool TryWrite(TimezoneRestoreRecord record)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}

public sealed class SystemTimezoneService(ITimezoneSystem system, ITimezoneRestoreStore store)
{
    private static TimezoneRestoreRecord Record(string saved, string target = "", string account = "", string iana = "") => new(saved, target, account, iana, DateTimeOffset.UtcNow.ToString("O"));
    private static TimezoneResult Failure(string message, string id = "") => new(false, false, true, id, message);

    public async Task<TimezoneResult> BackupBaselineAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled) return new(true, false, true, "", "系统时区自动切换已关闭");
        try
        {
            if (store.Read() is not null) return new(true, false, true, "", "已有原系统时区记录");
            var current = await system.ReadAsync(cancellationToken);
            if (!current.Success) return Failure("无法读取原系统时区");
            return store.TryWrite(Record(current.Id)) ? new(true, false, false, current.Id, "原系统时区已保存") : Failure("恢复记录写入失败，系统时区没有修改");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return Failure("无法读取时区恢复记录"); }
    }

    private TimezoneRestoreRecord? ReadExplicitRecord()
    {
        var record = store.Read();
        if (record is not null && (string.IsNullOrWhiteSpace(record.Saved) || record.Target is null || system.ResolveWindowsId(record.Saved) != record.Saved
            || record.Target.Length > 0 && system.ResolveWindowsId(record.Target) != record.Target))
            throw new InvalidDataException("时区恢复记录包含未知系统时区");
        return record;
    }
    public Task<TimezoneResult> ValidateExplicitBaselineAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { _ = ReadExplicitRecord(); return Task.FromResult(new TimezoneResult(true, false, true, "", "原时区记录可读取")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return Task.FromResult(Failure("无法读取原时区恢复记录，未修改国家/地区或系统时区。")); }
    }

    // Called under the existing ownership launch/restore lease after explicit
    // region configuration; a manual persistent choice replaces the old baseline.
    public async Task<TimezoneResult> CommitExplicitBaselineAsync(string expectedWindowsId, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = ReadExplicitRecord();
            if (system.ResolveWindowsId(expectedWindowsId) != expectedWindowsId) return Failure("新的系统时区基线不合法");
            var before = await system.ReadAsync(cancellationToken);
            if (!before.Success || before.Id != expectedWindowsId) return Failure("系统时区后来改变，未提交新基线。", before.Id);
            var baseline = Record(expectedWindowsId);
            if (!store.TryWrite(baseline)) return Failure("新时区基线写入失败，环境配置未确认。", before.Id);
            var saved = store.Read();
            var after = await system.ReadAsync(cancellationToken);
            if (saved != baseline || !after.Success || after.Id != expectedWindowsId)
                return Failure("新时区基线或系统时区回读未确认，环境配置未完成。", after.Id);
            return new(true, false, false, expectedWindowsId, "当前时区已提交为新基线，旧自动恢复目标已清空并回读确认。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return Failure("无法提交或回读新的时区基线，环境配置未完成。"); }
    }

    public async Task<TimezoneResult> SwitchAsync(string account, string timezone, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled || string.IsNullOrWhiteSpace(timezone)) return new(true, false, true, "", enabled ? "未配置时区" : "系统时区自动切换已关闭");
        var target = system.ResolveWindowsId(timezone);
        if (target is null) return Failure("时区没有对应的 Windows 时区名称");
        var current = await system.ReadAsync(cancellationToken);
        if (!current.Success) return Failure("无法读取当前系统时区");
        if (current.Id == target) return new(true, false, true, target, "系统时区已在目标值");
        TimezoneRestoreRecord? previous;
        try { previous = store.Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return Failure("无法读取时区恢复记录，系统时区没有修改"); }
        var saved = previous is not null && (current.Id == previous.Saved || current.Id == previous.Target) ? previous.Saved : current.Id;
        if (!store.TryWrite(Record(saved, target, account, timezone))) return Failure("恢复记录写入失败，系统时区没有修改");
        bool requested = await system.SetAsync(target, cancellationToken);
        var after = await system.ReadAsync(cancellationToken);
        if (after.Success && after.Id == target) return new(true, true, false, target, "系统时区已切换并复读确认");
        if (after.Success && after.Id == saved) store.TryWrite(Record(saved));
        return Failure(requested ? "时区修改请求完成，但复读没有确认目标值" : "系统时区修改失败", after.Id);
    }

    public async Task<TimezoneResult> EnsureForLaunchAsync(string account, string timezone, bool enabled, CancellationToken cancellationToken = default)
    {
        var result = await SwitchAsync(account, timezone, enabled, cancellationToken);
        if (!enabled || string.IsNullOrWhiteSpace(timezone) || !result.Success) return result;
        var current = await system.ReadAsync(cancellationToken);
        return current.Success && current.Id == system.ResolveWindowsId(timezone) ? result : Failure("启动前无法确认系统时区在目标值", current.Id);
    }

    // Exit, rollback and startup recovery must never reset the user's system timezone.
    public Task<TimezoneResult> RestoreAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new TimezoneResult(true, false, true, "", "保留当前系统时区，不自动恢复原时区或北京时间。"));

    public async Task<TimezoneResult> RecoverAtStartupAsync(bool trackedDesktopAlive, bool pendingLaunch, CancellationToken cancellationToken = default)
        => trackedDesktopAlive || pendingLaunch ? new(true, false, true, "", "启动或运行仍待确认，保留时区恢复记录") : await RestoreAsync(cancellationToken);

    public Task<TimezoneResult> ReassertAfterLaunchAsync(string account, string timezone, bool enabled, CancellationToken cancellationToken = default)
        => Task.FromResult(new TimezoneResult(true, false, true, "", "启动后保留当前系统时区，不自动重新设置。"));
}
