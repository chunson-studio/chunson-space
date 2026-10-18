namespace ChunsonSpace.Windows;

public sealed partial class WindowsDesktopLaunchSystem
{
    public async Task<DesktopLaunchPreparation> PrepareAsync(DesktopTarget target, DesktopLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request = request with { Target = target, Preparation = null };
        if (clone is not null)
        {
            try { request = clone.Bind(request, createDirectories: false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            { return new(false, request, "受管分身环境或 HTTP 代理不符合要求；当前账号和分身保持不变。"); }
            if (clone.ProtectionPreflight is not null)
            {
                try
                {
                    var rejection = await clone.ProtectionPreflight(target, request, cancellationToken);
                    if (rejection is not null) return new(false, request, rejection);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { return new(false, request, "无法确认程序防直连保护状态；当前账号和分身保持不变。"); }
            }
        }
        if (!(clone is null && request.Environment is null && AumidPattern().IsMatch(target.Aumid)) && !LaunchableTarget(target))
            return new(false, request, "Codex 桌面程序路径不可用；当前账号和分身保持不变。");
        return new(true, request with { Preparation = new PreparedLaunch(this, target, request) });
    }

    private static bool LaunchableTarget(DesktopTarget target) => Path.IsPathFullyQualified(target.Executable) && File.Exists(target.Executable)
        && new[] { "Codex.exe", "ChatGPT.exe" }.Contains(Path.GetFileName(target.Executable), StringComparer.OrdinalIgnoreCase)
        && (AumidPattern().IsMatch(target.Aumid) || CodexProcessService.IsDesktop(new(0, 0, Path.GetFileName(target.Executable), target.Executable, 1)));

    // One native system, one exact request, one dispatch. The snapshot cannot be
    // mutated by changing a caller-owned dictionary after the early check.
    private sealed class PreparedLaunch(WindowsDesktopLaunchSystem owner, DesktopTarget target, DesktopLaunchRequest request)
    {
        private readonly string[] arguments = request.Arguments.ToArray();
        private readonly Dictionary<string, string>? environment = request.Environment?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        private readonly string identity = request.IdentityName;
        private readonly string ip = request.ExpectedEgressIp;
        private int used;
        public bool Take(WindowsDesktopLaunchSystem system, DesktopTarget destination, DesktopLaunchRequest candidate)
            => Interlocked.Exchange(ref used, 1) == 0 && ReferenceEquals(owner, system) && target == destination
                && identity == candidate.IdentityName && ip == candidate.ExpectedEgressIp && arguments.SequenceEqual(candidate.Arguments)
                && (environment is null ? candidate.Environment is null : candidate.Environment is not null && environment.Count == candidate.Environment.Count
                    && environment.All(pair => candidate.Environment.TryGetValue(pair.Key, out var value) && value == pair.Value));
    }
}
