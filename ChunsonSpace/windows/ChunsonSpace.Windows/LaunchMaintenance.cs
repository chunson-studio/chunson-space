using System.Text.Json;
using ChunsonSpace.Core;

namespace ChunsonSpace.Windows;

public sealed record LaunchReadFailure(string Stage, string ExceptionType, int HResult);

public sealed partial class WindowsDesktopLaunchSystem
{
    public LaunchReadFailure? LastStateReadFailure { get; private set; }

    // Caller owns the runtime lease. The same per-dispatch mutex used by the launcher
    // excludes a late package child. Never run IP/TZ/CDP verification for maintenance.
    public StopResult CancelPendingForMaintenance(CancellationToken token, bool discard = false)
    {
        try
        {
            PhysicalFileStorage.RejectLinks(Path.GetFullPath(stateRoot));
            if (!Directory.Exists(stateRoot)) return new(true, false, "", []);
            foreach (var directory in Directory.EnumerateDirectories(stateRoot))
            {
                token.ThrowIfCancellationRequested();
                var nonce = Path.GetFileName(directory);
                if (!Guid.TryParseExact(nonce, "N", out _)) continue;
                GuardStateDirectory(directory);
                using var mutex = new Mutex(false, "Local\\ChunsonSpaceLaunch_" + nonce);
                if (!Acquire(mutex, 1000)) return new(false, false, "分身仍在启动，请稍后重试。", []);
                try
                {
                    State? state = null;
                    try { state = ReadState(directory, nonce); }
                    catch (Exception error) when (error is JsonException or InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
                    {
                        var path = Path.Combine(directory, "state.json");
                        if (!discard && File.Exists(path))
                        {
                            // Preserve damaged evidence outside the runtime that will be cleared.
                            var recovery = Path.Combine(Path.GetDirectoryName(stateRoot)!, "..", "recovery", "launch-" + nonce + ".json");
                            new PhysicalFileStorage().CopyAtomic(path, Path.GetFullPath(recovery));
                        }
                    }
                    // Reset discovers exact managed roots through the shared process scope.
                    if (!discard && state?.status == "started" && state.pid > 0) RegisterCloneState(state);
                    // A late bootstrap checks the nonce AND pending status under this mutex.
                    WriteState(directory, new State(nonce, "cancelled"));
                }
                finally { mutex.ReleaseMutex(); }
            }
            return new(true, false, "启动请求已取消。", []);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException or JsonException)
        { return new(false, false, "启动记录未能安全取消（" + error.GetType().Name + "）。", []); }
    }
}
