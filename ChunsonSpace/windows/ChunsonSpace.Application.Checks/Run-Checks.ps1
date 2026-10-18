param([string]$DotnetPath = $env:CHUNSONSPACE_DOTNET, [string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path -Parent $PSScriptRoot
$sdk = if ($DotnetPath) { [IO.Path]::GetFullPath($DotnetPath) } else { Join-Path (Split-Path -Parent $windowsRoot) 'runtime/dotnet/dotnet.exe' }
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.cli'
$env:TEMP = Join-Path $PSScriptRoot '.temp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_NOLOGO = '1'
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
$evidence = Join-Path $PSScriptRoot 'verification'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
Push-Location $windowsRoot
try {
    & $sdk build (Join-Path $PSScriptRoot 'ChunsonSpace.Application.Checks.csproj') -c $Configuration --nologo --disable-build-servers -m:1 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'build.txt')
    if ($LASTEXITCODE -ne 0) { throw "Application build failed ($LASTEXITCODE)." }
    & $sdk (Join-Path $PSScriptRoot "bin/$Configuration/net10.0-windows/ChunsonSpace.Application.Checks.dll") 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'checks.txt')
    if ($LASTEXITCODE -ne 0) { throw "Application isolated checks failed ($LASTEXITCODE)." }
} finally { Pop-Location }
