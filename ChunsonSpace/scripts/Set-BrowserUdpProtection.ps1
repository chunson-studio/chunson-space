[CmdletBinding()]
param(
    [ValidateSet('Preview','Apply','Restore')][string]$Action='Preview',
    [string[]]$ProgramPaths,
    [Parameter(Mandatory=$true)][string]$ReportPath
)
$ErrorActionPreference='Stop'
# Retain the original ownership IDs so the first Chrome rule is reused.
$group='ChunsonSwitch.ChromeUdpProtection.v1'
$prefix='ChunsonSwitch.ChromeUdp.v1.'
$report=[ordered]@{action=$Action;status='Failed';programPaths=@();rulesVerified=$false;networkVerified=$false;error=$null}
$code=1
function Rule-Name([string]$path) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $hash=$sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($path.ToLowerInvariant())) }
    finally { $sha.Dispose() }
    return $prefix+([BitConverter]::ToString($hash).Replace('-','').Substring(0,24))
}
function Rule-Matches($rule,[string]$path) {
    if($rule.Group -cne $group -or [string]$rule.Direction -ne 'Outbound' -or [string]$rule.Action -ne 'Block' -or [string]$rule.Enabled -ne 'True' -or [string]$rule.Profile -ne 'Any') { return $false }
    $app=@($rule | Get-NetFirewallApplicationFilter -ErrorAction Stop)
    $port=@($rule | Get-NetFirewallPortFilter -ErrorAction Stop)
    $address=@($rule | Get-NetFirewallAddressFilter -ErrorAction Stop)
    return $app.Count -eq 1 -and $port.Count -eq 1 -and $address.Count -eq 1 -and
        ([string]$app[0].Program).Equals($path,[StringComparison]::OrdinalIgnoreCase) -and
        [string]$port[0].Protocol -in @('UDP','17') -and [string]$port[0].LocalPort -eq 'Any' -and [string]$port[0].RemotePort -eq 'Any' -and
        [string]$address[0].LocalAddress -eq 'Any' -and [string]$address[0].RemoteAddress -eq 'Any'
}
try {
    if(-not $ProgramPaths) {
        $candidates=@(
            (Join-Path $env:ProgramFiles 'Google/Chrome/Application/chrome.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Google/Chrome/Application/chrome.exe'),
            (Join-Path $env:LOCALAPPDATA 'Google/Chrome/Application/chrome.exe'),
            (Join-Path $env:ProgramFiles 'Microsoft/Edge/Application/msedge.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Microsoft/Edge/Application/msedge.exe'),
            (Join-Path $env:LOCALAPPDATA 'Microsoft/Edge/Application/msedge.exe')
        )
        $ProgramPaths=@($candidates | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf})
    }
    $paths=@($ProgramPaths | ForEach-Object {
        if(-not [IO.Path]::IsPathRooted($_) -or [IO.Path]::GetFileName($_) -inotmatch '^(chrome|msedge|brave)\.exe$' -or -not [IO.File]::Exists($_)) { throw 'Expected an existing absolute chrome.exe, msedge.exe or brave.exe path.' }
        [IO.Path]::GetFullPath($_)
    } | Sort-Object -Unique)
    if($paths.Count -eq 0) { throw 'Chrome or Edge was not found. Supply exact ProgramPaths.' }
    $report.programPaths=$paths
    $saved=@(Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction Stop)
    foreach($path in $paths) {
        $name=Rule-Name $path
        $existing=@($saved | Where-Object {$_.Name -eq $name})
        if($existing.Count -gt 1 -or ($existing.Count -eq 1 -and -not (Rule-Matches $existing[0] $path))) { throw 'An existing rule conflicts with this repair; no rule was replaced.' }
    }
    if($Action -eq 'Restore') {
        foreach($path in $paths) {
            $name=Rule-Name $path
            $saved | Where-Object {$_.Name -eq $name -and $_.Group -ceq $group} | Remove-NetFirewallRule -ErrorAction Stop
        }
        $remaining=@(Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction Stop)
        $effective=@(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction Stop)
        foreach($path in $paths) {
            if(@($remaining | Where-Object {$_.Name -eq (Rule-Name $path)}).Count -gt 0 -or @($effective | Where-Object {$_.Name -eq (Rule-Name $path)}).Count -gt 0) { throw 'Rule removal was not confirmed in both stores.' }
        }
        $report.status='Restored'
    } else {
        $profiles=@(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop)
        if($profiles.Count -ne 3 -or @($profiles | Where-Object {[string]$_.Enabled -ne 'True' -or [string]$_.AllowLocalFirewallRules -eq 'False'}).Count -gt 0) { throw 'Windows Firewall is disabled or local rules are not accepted.' }
        if($Action -eq 'Apply') {
            foreach($path in $paths) {
                $name=Rule-Name $path
                if(@($saved | Where-Object {$_.Name -eq $name}).Count -eq 0) {
                    New-NetFirewallRule -PolicyStore PersistentStore -Name $name -DisplayName 'Chunson Switch - Browser UDP protection' -Group $group -Direction Outbound -Action Block -Enabled True -Profile Any -Program $path -Protocol UDP -LocalPort Any -RemotePort Any -LocalAddress Any -RemoteAddress Any | Out-Null
                }
            }
        }
        $saved=@(Get-NetFirewallRule -PolicyStore PersistentStore -ErrorAction Stop)
        $active=@(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction Stop)
        $verified=$true
        foreach($path in $paths) {
            $name=Rule-Name $path
            if(@($saved | Where-Object {$_.Name -eq $name -and (Rule-Matches $_ $path)}).Count -ne 1 -or @($active | Where-Object {$_.Name -eq $name -and (Rule-Matches $_ $path)}).Count -ne 1) { $verified=$false }
        }
        if($Action -eq 'Apply' -and -not $verified) { throw 'Saved and effective firewall rules did not match.' }
        $report.rulesVerified=$verified
        $report.status=if($verified) {'Configured'} else {'PreviewReady'}
    }
    $code=0
} catch { $report.error=$_.Exception.Message }
$report['observedUtc']=[DateTime]::UtcNow.ToString('o')
$report['scope']='Targets all profiles/users of the specified Chrome/Edge executables or explicitly supplied Brave executables, outbound IPv4/IPv6 UDP, all ports. Default discovery remains Chrome/Edge. TCP is unchanged. See rulesVerified for effective rule status.'
$destination=[IO.Path]::GetFullPath($ReportPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
[IO.File]::WriteAllText($destination,($report|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$report | ConvertTo-Json -Depth 5
exit $code
