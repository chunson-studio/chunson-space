param([string]$FixtureBase = (Join-Path $PSScriptRoot '.temp/flclash-dns'), [string]$EvidenceDirectory = (Join-Path $PSScriptRoot 'verification'), [string]$NodePath = (Get-Command node -ErrorAction Stop).Source)
$ErrorActionPreference='Stop'
$fixtureRoot=[IO.Path]::GetFullPath($FixtureBase)
$allowed=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.temp')).TrimEnd('\')+'\'
if(-not $fixtureRoot.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'FixtureBase must remain inside this check project .temp directory.' }
$ancestor=$fixtureRoot
while($ancestor) {
    if(Test-Path -LiteralPath $ancestor) {
        if(((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'FixtureBase must not contain a filesystem link.' }
    }
    $ancestor=[IO.Path]::GetDirectoryName($ancestor)
}
New-Item -ItemType Directory -Force -Path $fixtureRoot,$EvidenceDirectory | Out-Null
$env:TEMP=$fixtureRoot; $env:TMP=$fixtureRoot
$env:PSModuleAnalysisCachePath=Join-Path $fixtureRoot 'ModuleAnalysisCache'
$toolRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$checkPath=Join-Path $PSScriptRoot 'FlClashDnsScriptChecks.ps1'
function Quote([string]$value) { return "'"+$value.Replace("'","''")+"'" }
$command='& ([ScriptBlock]::Create([IO.File]::ReadAllText('+(Quote $checkPath)+'))) -FixtureBase '+(Quote $fixtureRoot)+' -ScriptPath '+(Quote (Join-Path $toolRoot 'scripts/proxy/FlClash-DNS.ps1'))+' -PolicyPath '+(Quote (Join-Path $toolRoot 'windows/ChunsonSpace.Proxy/Resources/dns-policy.json'))+' -LibraryRoot '+(Quote (Join-Path $toolRoot 'windows/ThirdParty/YamlDotNet'))+' -HelperPath '+(Quote (Join-Path $toolRoot 'scripts/proxy/lib/FlDns.cs'))
$command+=' -NodePath '+(Quote $NodePath)+' -RuntimeFixture '+(Quote (Join-Path $PSScriptRoot 'FlClashDnsRuntimeFixture.js'))
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$start=[Diagnostics.ProcessStartInfo]::new()
$start.FileName=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$start.Arguments='-NoLogo -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand '+$encoded
$start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
$process=[Diagnostics.Process]::Start($start)
try {
    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    ($stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()) | Tee-Object -FilePath (Join-Path $EvidenceDirectory 'flclash-dns-script-checks.txt')
    if($process.ExitCode -ne 0) { throw "FlClash offline script checks failed ($($process.ExitCode))." }
} finally { $process.Dispose() }
