param([string]$DotnetPath = $env:CHUNSONSPACE_DOTNET, [string]$Configuration='Debug',[string]$EvidenceDirectory=(Join-Path $PSScriptRoot 'verification'),[string]$NodePath=(Get-Command node -ErrorAction Stop).Source)
$ErrorActionPreference='Stop'
$windowsRoot=Split-Path -Parent $PSScriptRoot
$sdk = if ($DotnetPath) { [IO.Path]::GetFullPath($DotnetPath) } else { Join-Path (Split-Path -Parent $windowsRoot) 'runtime/dotnet/dotnet.exe' }
$env:DOTNET_CLI_HOME=Join-Path $PSScriptRoot '.cli'; $env:TEMP=Join-Path $PSScriptRoot '.temp'; $env:TMP=$env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'; $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'; $env:DOTNET_NOLOGO='1'
New-Item -ItemType Directory -Force -Path $EvidenceDirectory,$env:TEMP|Out-Null
Push-Location $windowsRoot
try {
    & $sdk build (Join-Path $PSScriptRoot 'ChunsonSpace.Fingerprint.Checks.csproj') -c $Configuration --nologo --disable-build-servers -m:1 2>&1 | Tee-Object (Join-Path $EvidenceDirectory 'build.txt')
    if($LASTEXITCODE -ne 0) { throw 'Fingerprint compilation failed.' }
    & $sdk (Join-Path $PSScriptRoot "bin/$Configuration/net10.0-windows/ChunsonSpace.Fingerprint.Checks.dll") ([IO.Path]::GetFullPath($EvidenceDirectory)) 2>&1 | Tee-Object (Join-Path $EvidenceDirectory 'checks.txt')
    if($LASTEXITCODE -ne 0) { throw 'Fingerprint isolated assertions failed.' }
    & $NodePath (Join-Path $PSScriptRoot 'compare-original.mjs') ([IO.Path]::GetFullPath($EvidenceDirectory)) 2>&1 | Tee-Object (Join-Path $EvidenceDirectory 'node-parity.txt')
    if($LASTEXITCODE -ne 0) { throw 'Original JS parity failed.' }
} finally { Pop-Location }
