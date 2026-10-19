param([Parameter(Mandatory = $true)][string]$Directory)
$ErrorActionPreference = 'Stop'
foreach ($name in @('packaged-launch.ps1', 'packaged-launch-inner.ps1', 'silent-timezone-setup.ps1', 'silent-timezone-helper.ps1', 'direct-connection-protection.ps1')) {
    $path = Join-Path $Directory $name
    $tokens = $null
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw ($name + ': ' + (($parseErrors | ForEach-Object { $_.Message }) -join ' | ')) }
    Write-Output "PASS syntax-only $name"
}
