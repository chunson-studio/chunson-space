param([Parameter(Mandatory=$true)][string]$PlanPath)
$ErrorActionPreference='Stop'
# This helper is embedded in the installed application, never obtained as remote script text.
function Assert-NoLink([string]$Path) {
    $cursor=[IO.Path]::GetFullPath($Path)
    while($cursor) {
        if(Test-Path -LiteralPath $cursor) { if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked update path.'} }
        $cursor=Split-Path -Parent $cursor
    }
}
function Safe-File([string]$Root,[string]$Relative) {
    if(!$Relative -or $Relative.Length -gt 180 -or $Relative -match '[\\:<>"|?*\x00-\x1f]' -or $Relative.StartsWith('/')) { throw 'Invalid update path.' }
    $parts=$Relative.Split('/')
    foreach($part in $parts) { if(!$part -or $part -in @('.','..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)'){throw 'Invalid update path component.'} }
    $allowed=if($parts.Count -eq 1){$Relative -ceq 'ChunsonSwitch.exe' -or $Relative -cin @('createdump.exe','ChunsonSwitch.deps.json','ChunsonSwitch.runtimeconfig.json','LICENSE.txt','LICENSE','README.md') -or $Relative -match '\.(dll|pdb)$'}else{$parts[0] -cin @('ThirdParty','Resources') -or $Relative.StartsWith('scripts/proxy/') -or $Relative -ceq 'runtimes/mihomo/mihomo.exe' -or $Relative.StartsWith('Widgets/') -or ($parts.Count -eq 2 -and $parts[0] -cmatch '^[a-z]{2}(-[A-Za-z]{2,4})?$' -and $Relative.EndsWith('.resources.dll'))}
    if(!$allowed){throw 'Not an application asset.'}
    $full=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if(!$full.StartsWith($Root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Update path escaped root.'}
    Assert-NoLink $full
    return $full
}
function Hash([string]$Path){
    $stream=[IO.File]::OpenRead($Path);$sha=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}
    finally{$stream.Dispose();$sha.Dispose()}
}
$work=Split-Path -Parent ([IO.Path]::GetFullPath($PlanPath))
Assert-NoLink $PlanPath
if((Get-Item -LiteralPath $PlanPath).Length -gt 2097152){throw 'Plan too large.'}
$reportPath=Join-Path $work 'result.json'
if(Test-Path -LiteralPath $reportPath){throw 'Report already exists.'}
$result=[ordered]@{installed=$false;rolledBack=$false;restarted=$false;stoppedProcesses=0;error=$null;rollbackErrors=@()}
$touched=[Collections.Generic.List[object]]::new()
$held=$null
$committedExit=$false
try {
    $plan=Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if($plan.schema -ne 1 -or $plan.waitSeconds -lt 1 -or $plan.waitSeconds -gt 60 -or @($plan.files).Count -gt 4096){throw 'Invalid update plan.'}
    $root=[IO.Path]::GetFullPath($plan.installRoot).TrimEnd('\')
    $stage=[IO.Path]::GetFullPath($plan.stage).TrimEnd('\')
    $entry=Join-Path $root 'ChunsonSwitch.exe'
    Assert-NoLink $root; Assert-NoLink $stage
    if($root -ieq $stage -or $stage.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or $root.StartsWith($stage+'\',[StringComparison]::OrdinalIgnoreCase) -or $work.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Update staging overlaps installation.'}
    if($plan.managerPath -ine $entry -or !(Test-Path -LiteralPath $entry -PathType Leaf)){throw 'Manager path mismatch.'}
    # A single Process instance holds the original handle throughout identity verification and waiting.
    $held=Get-Process -Id ([int]$plan.managerPid) -ErrorAction Stop
    $null=$held.Handle
    if($held.Path -ine $entry -or $held.StartTime.ToUniversalTime().Ticks -ne [long]::Parse($plan.managerCreatedUtcTicks,[Globalization.CultureInfo]::InvariantCulture)){throw 'Manager identity mismatch.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $files=@(foreach($f in $plan.files){
        if($f.path -ieq 'Widgets/BeijingClock.exe'){throw 'Independent clock is outside manager update plans.'}
        if(!$seen.Add([string]$f.path) -or $f.sha256 -cnotmatch '^[a-f0-9]{64}$' -or $f.size -lt 0 -or $f.size -gt 268435456){throw 'Invalid update file.'}
        $source=Safe-File $stage $f.path; $target=Safe-File $root $f.path
        if(!(Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -ne $f.size -or (Hash $source) -cne $f.sha256){throw 'Staged bytes mismatch.'}
        [pscustomobject]@{relative=$f.path;source=$source;target=$target;sha256=$f.sha256;size=$f.size;existed=(Test-Path -LiteralPath $target -PathType Leaf);backup=$null;originalHash=$null}
    })
    [IO.File]::WriteAllText((Join-Path $work 'ready'), 'identity verified')
    if(!$held.WaitForExit([int]$plan.waitSeconds*1000)){throw 'Manager exit wait timed out; no files replaced.'}
    $commit=Join-Path $work 'commit'; Assert-NoLink $commit
    if(!(Test-Path -LiteralPath $commit -PathType Leaf) -or (Get-Item -LiteralPath $commit).Length -gt 16 -or [IO.File]::ReadAllText($commit) -cne 'install'){throw 'Update was not committed by the exiting manager; no files replaced.'}
    $committedExit=$true
    $backup=Join-Path $work 'backup'
    [void][IO.Directory]::CreateDirectory($backup)
    # Preflight every destination and retain all original bytes before changing any destination.
    foreach($f in $files){
        Assert-NoLink $f.target
        if($f.existed){
            $stream=[IO.File]::Open($f.target,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $stream.Dispose()
            $f.backup=Safe-File $backup $f.relative
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $f.backup))
            [IO.File]::Copy($f.target,$f.backup,$false); $f.originalHash=Hash $f.backup
        } elseif(Test-Path -LiteralPath $f.target){throw 'Destination is not a file.'}
    }
    foreach($f in $files){
        Assert-NoLink $f.target
        if($f.existed -and (Hash $f.target) -cne $f.originalHash){throw 'Destination changed before replacement.'}
        if((Hash $f.source) -cne $f.sha256){throw 'Staged bytes changed.'}
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $f.target))
        $pending=$f.target+'.chunson-update-'+[Guid]::NewGuid().ToString('N')
        [IO.File]::Copy($f.source,$pending,$false)
        $touched.Add($f)
        if($f.existed){[IO.File]::Replace($pending,$f.target,($f.backup+'.replaced'))}else{[IO.File]::Move($pending,$f.target)}
        if((Hash $f.target) -cne $f.sha256){throw 'Installed bytes mismatch.'}
    }
    foreach($f in $files){if((Hash $f.target) -cne $f.sha256){throw 'Final verification failed.'}}
    $result.installed=$true
} catch {
    $result.error=$_.Exception.Message
    for($i=$touched.Count-1;$i -ge 0;$i--){
        $f=$touched[$i]
        try {
            Assert-NoLink $f.target
            if($f.existed){[IO.File]::Copy($f.backup,$f.target,$true); if((Hash $f.target) -cne $f.originalHash){throw 'Rollback bytes mismatch.'}}
            elseif(Test-Path -LiteralPath $f.target){[IO.File]::Delete($f.target)} # only a new file created by this transaction
        }catch{$result.rollbackErrors+=@([string]$f.relative)}
    }
    $result.rolledBack=$touched.Count -gt 0 -and $result.rollbackErrors.Count -eq 0
} finally {
    if($held){$held.Dispose()}
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
}
if($result.installed){
    try {
        Start-Process -FilePath $entry -ArgumentList '--update-complete' -WorkingDirectory $root -WindowStyle Hidden | Out-Null
        $result.restarted=$true
    }catch{$result.error='Installed, but automatic restart failed: '+$_.Exception.Message}
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
}
elseif($committedExit -and $result.rollbackErrors.Count -eq 0){
    try {
        Start-Process -FilePath $entry -ArgumentList '--update-failed' -WorkingDirectory $root -WindowStyle Hidden | Out-Null
        $result.restarted=$true; $result.restartedOriginal=$true
    }catch{$result.restartError=$_.Exception.Message}
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
}
if(!$result.installed){exit 1}
