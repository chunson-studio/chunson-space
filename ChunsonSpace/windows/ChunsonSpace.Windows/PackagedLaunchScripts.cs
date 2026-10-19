using System.Text;
using System.Text.Json;

namespace ChunsonSpace.Windows;

// Data is encoded, never interpolated into executable PowerShell syntax.
public static class PackagedLaunchScripts
{
    public static string Build(object payload)
    {
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        var inner = Convert.ToBase64String(Encoding.UTF8.GetBytes(Inner));
        return "$d = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "')) | ConvertFrom-Json\n"
            + "$innerCode = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + inner + "'))\n" + Outer;
    }

    private const string Inner = """
        $ErrorActionPreference = 'Stop'
        function Write-LaunchState($value) {
            $target = [string]$d.state
            $temp = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($value | ConvertTo-Json -Depth 8 -Compress))
            try {
                $stream = [IO.File]::Open($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
                if ([IO.File]::Exists($target)) { [IO.File]::Replace($temp, $target, $null) } else { [IO.File]::Move($temp, $target) }
            } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
        }
        $mutex = [Threading.Mutex]::new($false, [string]$d.mutex)
        $locked = $false
        $attempted = $false
        $stage = 'state'
        try {
            try { $locked = $mutex.WaitOne(30000) } catch [Threading.AbandonedMutexException] { $locked = $true }
            if (-not $locked -or -not [IO.File]::Exists([string]$d.state)) { return }
            $s = [IO.File]::ReadAllText([string]$d.state) | ConvertFrom-Json
            if ($s.nonce -ne $d.nonce -or $s.status -ne 'pending') { return }
            if ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() -ge [long]$s.deadline_ms) {
                $s.status = 'cancelled'
                Write-LaunchState $s
                return
            }
            $stage = 'environment'
            foreach ($key in @('HTTP_PROXY','HTTPS_PROXY','NO_PROXY','TZ','CODEX_HOME','CODEX_SQLITE_HOME','CODEX_ELECTRON_USER_DATA_PATH')) {
                $property = $d.env.PSObject.Properties[$key]
                $value = if ($property -and $null -ne $property.Value) { [string]$property.Value } else { $null }
                [Environment]::SetEnvironmentVariable($key, $value, 'Process')
            }
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = [string]$d.exe
            $start.Arguments = [string]$d.arguments
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $s.status = 'launching'
            Write-LaunchState $s
            $attempted = $true
            $stage = 'process'
            try { $process = [Diagnostics.Process]::Start($start) }
            catch {
                if ($_.Exception.GetBaseException() -is [ComponentModel.Win32Exception]) { $attempted = $false }
                throw
            }
            $started = @{nonce=$d.nonce; status='started'; pid=$process.Id; creation_time=$process.StartTime.ToUniversalTime().ToFileTimeUtc(); executable=[string]$d.exe; deadline_ms=$d.deadline_ms; timezone=[string]$d.env.TZ; expected_ip=[string]$d.expected_ip}
            Write-LaunchState $started
            $process.Dispose()
        } catch {
            if (-not $attempted -and $locked -and [IO.File]::Exists([string]$d.state)) {
                $failed = @{nonce=$d.nonce; status='failed'; stage=$stage}
                Write-LaunchState $failed
            }
        } finally { if ($locked) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
        """;

    private const string Outer = """
        $ErrorActionPreference = 'Stop'
        function Write-LaunchState($value) {
            $target = [string]$d.state
            $temp = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($value | ConvertTo-Json -Depth 8 -Compress))
            try {
                $stream = [IO.File]::Open($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
                if ([IO.File]::Exists($target)) { [IO.File]::Replace($temp, $target, $null) } else { [IO.File]::Move($temp, $target) }
            } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
        }
        $stage = 'registration'
        try {
            $p = Get-AppxPackage -Name OpenAI.Codex | Where-Object { $_.PackageFamilyName -eq $d.family } | Sort-Object Version -Descending | Select-Object -First 1
            if (-not $p) { throw 'package-not-registered' }
            $stage = 'manifest'
            $m = Get-AppxPackageManifest -Package $p.PackageFullName
            $a = $m.Package.Applications.Application | Where-Object { $_.Id -eq 'App' -and $_.Id -eq $d.app_id } | Select-Object -First 1
            if (-not $a -or $a.EntryPoint -ne 'Windows.FullTrustApplication') { throw 'not-full-trust-main-app' }
            $exe = [IO.Path]::GetFullPath((Join-Path $p.InstallLocation ([string]$a.Executable)))
            $root = [IO.Path]::GetFullPath($p.InstallLocation).TrimEnd([char]92) + [char]92
            if (-not $exe.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($exe) -notin @('ChatGPT.exe','Codex.exe') -or -not [IO.File]::Exists($exe)) { throw 'invalid-main-executable' }
            $d | Add-Member -NotePropertyName exe -NotePropertyValue $exe
            $data = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($d | ConvertTo-Json -Depth 5 -Compress)))
            $inner = '$d = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(''' + $data + ''')) | ConvertFrom-Json' + "`n" + $innerCode
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
            $ps = (Get-Command powershell.exe -CommandType Application).Source
            $systemPS = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
            if (-not [string]::Equals([IO.Path]::GetFullPath($ps), [IO.Path]::GetFullPath($systemPS), [StringComparison]::OrdinalIgnoreCase)) { throw 'not-system-powershell' }
            $stage = 'dispatch'
            Invoke-CommandInDesktopPackage -PackageFamilyName $p.PackageFamilyName -AppId $a.Id -Command $ps -Args ('-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand ' + $encoded) -PreventBreakaway
        } catch {
            $mutex = [Threading.Mutex]::new($false, [string]$d.mutex)
            $locked = $false
            try {
                try { $locked = $mutex.WaitOne(30000) } catch [Threading.AbandonedMutexException] { $locked = $true }
                if ($locked -and [IO.File]::Exists([string]$d.state)) {
                    $s = [IO.File]::ReadAllText([string]$d.state) | ConvertFrom-Json
                    if ($s.nonce -eq $d.nonce -and $s.status -eq 'pending') {
                        $failed = @{nonce=$d.nonce; status='failed'; stage=$stage}
                        Write-LaunchState $failed
                    }
                }
            } finally { if ($locked) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
        }
        """;
}
