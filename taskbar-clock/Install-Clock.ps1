<#
.SYNOPSIS
Validate a previously verified clock MSI and read its exact product registration.
.DESCRIPTION
Default/DryRun never invokes msiexec. Apply installs only an absent product once.
Metadata comes from Build-ClockInstaller.py (including its verified payload hash).
ExecutablePath supplies the corresponding built EXE; defaults to the installed
EXE, then the adjacent EXE. No extraction, build, repair, launch or startup change.
The metadata is a trusted release input, not an independent signature.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [Parameter(Mandatory=$true)][string]$MetadataPath,
    [Parameter(Mandatory=$true)][string]$ReportPath,
    [string]$ExecutablePath,
    [string]$LogPath,
    [ValidateRange(10,900)][int]$TimeoutSeconds=120,
    [switch]$DryRun,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

function Get-LocalPath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    if($full -notmatch '^[A-Za-z]:[\\/]'){throw 'A local drive path is required.'}
    return $full
}
function Release-Com($Object) {
    if($null -ne $Object -and [Runtime.InteropServices.Marshal]::IsComObject($Object)){
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Object)
    }
}
function Get-MsiValue($Database,[string]$Query) {
    $view=$null; $row=$null
    try {
        $view=$Database.GetType().InvokeMember('OpenView','InvokeMethod',$null,$Database,@($Query))
        [void]$view.GetType().InvokeMember('Execute','InvokeMethod',$null,$view,$null)
        $row=$view.GetType().InvokeMember('Fetch','InvokeMethod',$null,$view,$null)
        if($null -eq $row){return $null}
        return $row.GetType().InvokeMember('StringData','GetProperty',$null,$row,@(1))
    } finally {
        Release-Com $row
        if($null -ne $view){try{[void]$view.GetType().InvokeMember('Close','InvokeMethod',$null,$view,$null)}finally{Release-Com $view}}
    }
}
function Read-ClockRegistration($Installer,[string]$ProductCode,[string]$ExpectedHash,[string]$Version) {
    $info=[ordered]@{}; $readErrors=@()
    $state=$Installer.GetType().InvokeMember('ProductState','GetProperty',$null,$Installer,@($ProductCode))
    foreach($property in @('ProductName','VersionString','AssignmentType','InstallLocation','ProductIcon')){
        try{$info[$property]=[string]$Installer.GetType().InvokeMember('ProductInfo','GetProperty',$null,$Installer,@($ProductCode,$property))}
        catch{$info[$property]=$null;$readErrors+= $property}
    }
    $arp=@(); $markers=@()
    # Only this product GUID and this application's own marker, in known views.
    foreach($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)){
        foreach($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryHive]::LocalMachine)){
            $base=$null; $key=$null; $marker=$null
            try{
                $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey($hive,$view)
                $subkey='Software\Microsoft\Windows\CurrentVersion\Uninstall\'+$ProductCode
                $key=$base.OpenSubKey($subkey,$false)
                if($null -ne $key){
                    $arp+=[pscustomobject]@{hive=$hive.ToString();view=$view.ToString();subkey=$subkey;displayName=$key.GetValue('DisplayName');displayVersion=$key.GetValue('DisplayVersion')}
                }
                if($hive -eq [Microsoft.Win32.RegistryHive]::CurrentUser){
                    $marker=$base.OpenSubKey('Software\Chunson\BeijingClock',$false)
                    if($null -ne $marker){
                        $value=[string]$marker.GetValue('InstallPath')
                        if($value){$markers+=[pscustomobject]@{view=$view.ToString();installPath=$value}}
                    }
                }
            } finally {
                if($null -ne $marker){$marker.Dispose()}; if($null -ne $key){$key.Dispose()}; if($null -ne $base){$base.Dispose()}
            }
        }
    }
    $markerPaths=@($markers | ForEach-Object { (Get-LocalPath $_.installPath).TrimEnd('\','/') } | Select-Object -Unique)
    if($markerPaths.Count -gt 1){throw 'Conflicting clock install markers; no installation attempted.'}
    $location=$info.InstallLocation; $locationSource='WindowsInstaller.InstallLocation'
    if([string]::IsNullOrWhiteSpace($location) -and $markerPaths.Count -eq 1){$location=$markerPaths[0];$locationSource='HKCU Software/Chunson/BeijingClock InstallPath'}
    $installedExe=$null; $installedHash=$null
    if(-not [string]::IsNullOrWhiteSpace($location)){
        $installedExe=Join-Path (Get-LocalPath $location) 'BeijingClock.exe'
        if(Test-Path -LiteralPath $installedExe -PathType Leaf){$installedHash=(Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash}
    }
    $icon=$null
    if($info.ProductIcon){$icon=[Environment]::ExpandEnvironmentVariables($info.ProductIcon)}
    $registered=($state -eq 5 -and $info.ProductName -eq 'Chunson Beijing Clock' -and $info.VersionString -eq $Version -and $info.AssignmentType -eq '0')
    return [pscustomobject]@{
        productCode=$ProductCode; productState=$state; nativeProductName=$info.ProductName
        windowsInstaller=[pscustomobject]$info; propertyReadErrors=$readErrors
        arpEntries=$arp; installMarkers=$markers; installLocationSource=$locationSource
        installedExecutable=$installedExe; installedSha256=$installedHash
        executableHashMatches=($null -ne $installedHash -and $installedHash -eq $ExpectedHash)
        registeredPerUser=$registered; productIconExpanded=$icon
        desktopShortcutIconExpected=if($icon){$icon+',0'}else{$null}
        accepted=($registered -and $null -ne $installedHash -and $installedHash -eq $ExpectedHash)
    }
}

if($Apply -and $DryRun){throw 'Apply and DryRun are mutually exclusive.'}
$report=Get-LocalPath $ReportPath
if(Test-Path -LiteralPath $report){throw 'ReportPath must be new; existing receipts are never overwritten.'}
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $report))
# Reserve a new receipt before doing any work; never truncate an existing one.
$reportStream=[IO.File]::Open($report,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$result=[ordered]@{
    schemaVersion=1; timestamp=[DateTimeOffset]::Now.ToString('o')
    mode=if($Apply){'Apply'}else{'DryRun'}; status='VALIDATING'; passed=$false
    msiexecInvocations=0; processStops=0; applicationLaunches=0; loginStartupChanges=0
    scope='Local package/EXE hashes and exact clock registration only; NOT PIXEL PROOF.'
}
$installer=$null; $database=$null; $packageLock=$null; $installProcess=$null
try {
    $package=Get-LocalPath $PackagePath; $metadata=Get-LocalPath $MetadataPath
    if([IO.Path]::GetExtension($package) -ine '.msi'){throw 'PackagePath must be an MSI.'}
    if(-not (Test-Path -LiteralPath $metadata -PathType Leaf)){throw 'MetadataPath must exist.'}
    $meta=Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
    if($meta.msiSha256 -notmatch '^[0-9a-fA-F]{64}$' -or $meta.executableSha256 -notmatch '^[0-9a-fA-F]{64}$'){throw 'Metadata must contain MSI and EXE SHA256.'}
    $productCode=([guid]::Parse($meta.productCode)).ToString('B').ToUpperInvariant()
    $upgradeCode='{8314E183-57D1-5EF2-8460-34721F8C72A7}'
    if($meta.upgradeCode -ne $upgradeCode -or $meta.fixtureExtraction -ne 'PASS'){throw 'Verified clock payload metadata with the known UpgradeCode is required.'}
    $installedDefault=Join-Path $env:LOCALAPPDATA 'Programs/Chunson/BeijingClock/BeijingClock.exe'
    $exe=if($ExecutablePath){Get-LocalPath $ExecutablePath}elseif(Test-Path -LiteralPath $installedDefault -PathType Leaf){$installedDefault}else{Join-Path $PSScriptRoot 'BeijingClock.exe'}
    if([IO.Path]::GetFileName($exe) -ine 'BeijingClock.exe' -or -not (Test-Path -LiteralPath $exe -PathType Leaf)){throw 'ExecutablePath must identify the corresponding built BeijingClock.exe.'}
    $packageLock=[IO.File]::Open($package,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $packageHash=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
    $exeHash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    if($packageHash -ne $meta.msiSha256 -or $exeHash -ne $meta.executableSha256){throw 'MSI or EXE SHA256 does not match metadata.'}
    $installer=New-Object -ComObject WindowsInstaller.Installer
    $database=$installer.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$installer,@($package,0))
    $properties=[ordered]@{}
    foreach($name in @('ProductCode','UpgradeCode','ProductName','ProductVersion','MSIINSTALLPERUSER','ALLUSERS','ARPPRODUCTICON')){
        $properties[$name]=Get-MsiValue $database ('SELECT `Value` FROM `Property` WHERE `Property` = '''+$name+'''')
    }
    if($properties.ProductCode -ne $productCode -or $properties.UpgradeCode -ne $upgradeCode -or $properties.ProductName -ne 'Chunson Beijing Clock' -or $properties.ProductVersion -ne $meta.productVersion -or $properties.MSIINSTALLPERUSER -ne '1' -or $properties.ALLUSERS -or $properties.ARPPRODUCTICON -ne 'ClockIcon'){
        throw 'MSI product identity or per-user properties do not match verified metadata.'
    }
    $fileName=Get-MsiValue $database 'SELECT `FileName` FROM `File` WHERE `File` = ''ClockExe'''
    $fileVersion=Get-MsiValue $database 'SELECT `Version` FROM `File` WHERE `File` = ''ClockExe'''
    $fileSize=Get-MsiValue $database 'SELECT `FileSize` FROM `File` WHERE `File` = ''ClockExe'''
    if($fileName -ne 'BeijingClock.exe' -or $fileVersion -ne $meta.version -or [long]$fileSize -ne (Get-Item -LiteralPath $exe).Length){throw 'MSI EXE row does not match verified EXE metadata.'}
    Release-Com $database; $database=$null
    $result.validation=[pscustomobject]@{packagePath=$package;metadataPath=$metadata;executablePath=$exe;msiSha256=$packageHash;executableSha256=$exeHash;productCode=$productCode;properties=[pscustomobject]$properties;payloadHashEvidence='Trusted builder metadata fixtureExtraction=PASS, bound to this exact MSI SHA256; no new extraction.'}
    $before=Read-ClockRegistration $installer $productCode $meta.executableSha256 $meta.productVersion
    $result.before=$before
    if(-not $Apply){
        $result.status='DRY_RUN_VALIDATED'; $result.passed=$true
        $result.proposedAction=if($before.accepted){'Already installed and verified; Apply would skip msiexec.'}elseif($before.productState -ne -1 -or $before.arpEntries.Count -gt 0 -or $before.installMarkers.Count -gt 0){'Existing or ambiguous registration; Apply would refuse repair/reinstall.'}else{'Apply would install once, then read back this product.'}
    } elseif($before.accepted){
        $result.status='ALREADY_INSTALLED_VERIFIED'; $result.passed=$true
    } else {
        if($before.productState -ne -1 -or $before.arpEntries.Count -gt 0 -or $before.installMarkers.Count -gt 0){throw 'Existing or ambiguous clock registration; refusing repair/reinstall.'}
        $log=if($LogPath){Get-LocalPath $LogPath}else{[IO.Path]::ChangeExtension($report,'.msi.log')}
        if(Test-Path -LiteralPath $log){throw 'LogPath must be new.'}
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $log))
        if($package.Contains('"') -or $log.Contains('"')){throw 'Invalid quote in MSI path.'}
        $arguments='/i "'+$package+'" /passive /norestart REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable /L*v "'+$log+'"'
        $result.command=[pscustomobject]@{filePath=(Join-Path $env:SystemRoot 'System32/msiexec.exe');arguments=$arguments;timeoutSeconds=$TimeoutSeconds;logPath=$log}
        $installProcess=Start-Process -FilePath $result.command.filePath -ArgumentList $arguments -PassThru -WindowStyle Hidden
        $result.msiexecInvocations=1; $result.installerProcessId=$installProcess.Id
        if(-not $installProcess.WaitForExit($TimeoutSeconds*1000)){
            $result.status='TIMED_OUT_UNRESOLVED'
            throw 'Installer wait timed out; it may still be running. No kill, retry or success claim; inspect this receipt and MSI log before further action.'
        }
        $installProcess.Refresh(); $result.installerExitCode=$installProcess.ExitCode
        if($installProcess.ExitCode -notin @(0,3010)){throw ('msiexec failed with exit code '+$installProcess.ExitCode)}
        $result.rebootRequired=($installProcess.ExitCode -eq 3010)
        $after=Read-ClockRegistration $installer $productCode $meta.executableSha256 $meta.productVersion
        $result.after=$after
        if(-not $after.accepted){throw 'Installer completed but exact native registration/EXE readback did not pass; no retry performed.'}
        $result.status='INSTALLED_VERIFIED'; $result.passed=$true
    }
} catch {
    if($result.status -ne 'TIMED_OUT_UNRESOLVED'){$result.status='FAILED'}
    $result.error=$_.Exception.Message
    throw
} finally {
    Release-Com $database; Release-Com $installer
    if($null -ne $packageLock){$packageLock.Dispose()}
    if($null -ne $installProcess){$installProcess.Dispose()}
    try {
        $json=$result | ConvertTo-Json -Depth 10
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes($json)
        $reportStream.Write($bytes,0,$bytes.Length)
    } finally {$reportStream.Dispose()}
}
$json
