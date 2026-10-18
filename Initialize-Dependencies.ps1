param([string]$Root = $PSScriptRoot, [string]$ExistingExecutable)
$ErrorActionPreference = 'Stop'
$metadata = Get-Content (Join-Path $Root 'ChunsonSpace/windows/ThirdParty/Mihomo/SOURCE.json') -Raw | ConvertFrom-Json
if ($metadata.downloadUrl -notlike 'https://github.com/MetaCubeX/mihomo/releases/download/*') { throw 'Unexpected Mihomo download host/path' }
$destination = Join-Path $Root 'ChunsonSpace/runtime/mihomo/mihomo.exe'
if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $metadata.executableSha256) { return }
if ($ExistingExecutable) {
    $existing = Get-Item -LiteralPath $ExistingExecutable
    if (($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath $existing.FullName -Algorithm SHA256).Hash -ne $metadata.executableSha256) { throw 'Existing Mihomo does not match pinned hash' }
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $existing.FullName -Destination $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $metadata.executableSha256) { throw 'Cached dependency copy mismatch' }
    return
}
$temp = Join-Path ([IO.Path]::GetTempPath()) ('chunson-dependency-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$archive = Join-Path $temp 'mihomo.zip'
Invoke-WebRequest -Uri $metadata.downloadUrl -OutFile $archive -UseBasicParsing
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $metadata.archiveSha256) { throw 'Mihomo archive checksum mismatch' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $entries = @($zip.Entries | Where-Object { $_.Name -eq 'mihomo.exe' -or $_.Name -eq 'mihomo-windows-amd64-compatible.exe' })
    if ($entries.Count -ne 1) { throw 'Expected exactly one Mihomo executable' }
    $candidate = Join-Path $temp 'mihomo.exe'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $candidate)
} finally { $zip.Dispose() }
if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $metadata.executableSha256) { throw 'Mihomo executable checksum mismatch' }
New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
Copy-Item -LiteralPath $candidate -Destination $destination
Remove-Item -LiteralPath $candidate, $archive
Remove-Item -LiteralPath $temp
