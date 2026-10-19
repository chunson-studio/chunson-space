using System.ComponentModel;

namespace ChunsonSpace.Windows;

internal sealed class OwnedProcessScopeException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

internal static class OwnedProcessDiagnostics
{
    public static string Describe(string stage, Exception error)
    {
        // Do not expose exception messages, stack traces, account labels or absolute paths.
        // These targets are logical aliases, not claims about the physical path that failed.
        var target = stage switch
        {
            "ownership-lease" => "ownership-record.lock（数据根已隐藏）",
            "ownership-read" or "ownership-merge" => "ownership-record（数据根已隐藏）",
            "clone-scope" => "ownership-record + process-snapshot（数据根已隐藏）",
            "system-snapshot" => "process-snapshot",
            _ => "运行环境"
        };
        var reason = error is OwnedProcessScopeException scope ? $"；原因={scope.Code}（{scope.Message}）" : "";
        var native = error is Win32Exception win32 ? $"；Win32={win32.NativeErrorCode}" : "";
        return $"阶段={stage}；类型={error.GetType().Name}；HResult=0x{error.HResult:X8}{native}{reason}；对象={target}";
    }
}
