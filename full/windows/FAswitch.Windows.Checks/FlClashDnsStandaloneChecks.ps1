[CmdletBinding()]
param(
    [string]$FixtureBase,
    [Parameter(Mandatory=$true)][string]$ScriptPath,
    [Parameter(Mandatory=$true)][string]$LegacyScriptPath,
    [string]$FactoryPath,
    [Parameter(Mandatory=$true)][string]$NodePath,
    [string]$RuntimeFixture
)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
Set-StrictMode -Version 2
if(-not $FactoryPath) { $FactoryPath=Join-Path $PSScriptRoot 'FlClashDnsScriptChecks.ps1' }
if(-not $RuntimeFixture) { $RuntimeFixture=Join-Path $PSScriptRoot 'FlClashDnsRuntimeFixture.js' }
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\')
$temporaryRoot=Join-Path $projectRoot '.temp'
if(-not $FixtureBase) { $FixtureBase=Join-Path $temporaryRoot ('flclash-standalone-'+[guid]::NewGuid().ToString('N')) }
$FixtureBase=[IO.Path]::GetFullPath($FixtureBase).TrimEnd('\')
if(-not $FixtureBase.StartsWith($temporaryRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'FixtureBase must be inside this project .temp directory.' }
if(Test-Path -LiteralPath $FixtureBase) {
    if(@(Get-ChildItem -LiteralPath $FixtureBase -Force).Count -gt 0) { throw 'FixtureBase must be new or empty; existing evidence is never overwritten.' }
}
function Assert-BoundPath([string]$Path,[string]$Owner) {
    $full=[IO.Path]::GetFullPath($Path).TrimEnd('\')
    $ownerFull=[IO.Path]::GetFullPath($Owner).TrimEnd('\')
    if($full -ine $ownerFull -and -not $full.StartsWith($ownerFull+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Path escapes the private fixture directory.' }
    $cursor=$full
    while($cursor -and $cursor.Length -ge $temporaryRoot.Length) {
        if(Test-Path -LiteralPath $cursor) {
            if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not allowed in fixture paths.' }
        }
        $cursor=Split-Path -Parent $cursor
    }
    return $full
}
[void](Assert-BoundPath $FixtureBase $temporaryRoot)
[IO.Directory]::CreateDirectory($FixtureBase) | Out-Null
$script:invocations=New-Object 'System.Collections.Generic.List[object]'
$script:sources=New-Object 'System.Collections.Generic.List[object]'
$script:utf8=[Text.UTF8Encoding]::new($false)
try {
    if($PSVersionTable.PSVersion.Major -ne 5 -or -not [Environment]::Is64BitProcess) { throw 'Run this fixture runner in 64-bit Windows PowerShell 5.' }
    foreach($file in @($ScriptPath,$LegacyScriptPath,$FactoryPath,$NodePath,$RuntimeFixture)) {
        if(-not [IO.File]::Exists($file)) { throw ('Missing fixture input: '+$file) }
    }
    # Reuse only the published fixture definitions, never the integrated suite body.
    $factorySource=[IO.File]::ReadAllText($FactoryPath)
    $body=[regex]::Match($factorySource,'(?m)^try \{\r?$')
    if(-not $body.Success) { throw 'Fixture factory top-level boundary missing.' }
    . ([ScriptBlock]::Create($factorySource.Substring(0,$body.Index))) -FixtureBase $FixtureBase -ScriptPath $ScriptPath -NodePath $NodePath -RuntimeFixture $RuntimeFixture
    function Stage-Standalone([string]$Source,[string]$Label) {
        $assetRoot=Assert-BoundPath (Join-Path $FixtureBase ('assets/'+$Label)) $FixtureBase
        [IO.Directory]::CreateDirectory((Join-Path $assetRoot 'lib')) | Out-Null
        $copy=Join-Path $assetRoot 'FlClash-DNS.ps1'
        $sourceText=[IO.File]::ReadAllText($Source)
        [IO.File]::WriteAllText($copy,$sourceText,[Text.UTF8Encoding]::new($true))
        Check ([IO.File]::ReadAllText($copy) -ceq $sourceText) ($Label+' staged source body exactly equals the actual standalone script')
        $sourceHash=(Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
        $copyHash=(Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash
        Check ($sourceHash -ceq $copyHash) ($Label+' staged script bytes and UTF8 BOM exactly match delivered source hash')
        $sha=[Security.Cryptography.SHA256]::Create()
        try { $bodyHash=([BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($sourceText)))).Replace('-','') } finally { $sha.Dispose() }
        $script:sources.Add([ordered]@{label=$Label;source=$Source;staged=$copy;sourceHash=$sourceHash;stagedHash=$copyHash;bodyHash=$bodyHash;bodyEqual=$true}) | Out-Null
        foreach($name in @('YamlDotNet.dll','FlDns.cs')) {
            $original=Join-Path (Split-Path -Parent $Source) ('lib/'+$name)
            $destination=Join-Path $assetRoot ('lib/'+$name)
            [IO.File]::WriteAllBytes($destination,[IO.File]::ReadAllBytes($original))
            Check ((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ceq (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) ($Label+' stages exact standalone dependency '+$name)
        }
        return $copy
    }
    $current=Stage-Standalone $ScriptPath 'current'
    $historical=Stage-Standalone $LegacyScriptPath 'legacy-v2'
    $yaml=Join-Path (Split-Path -Parent $current) 'lib/YamlDotNet.dll'
    [Reflection.Assembly]::LoadFrom($yaml) | Out-Null
    Add-Type -Path (Join-Path (Split-Path -Parent $current) 'lib/FlDns.cs') -ReferencedAssemblies $yaml
    # Standalone helper imports sqlite3.dll, unlike the integrated helper.
    $sqlite=Join-Path $FixtureBase 'sqlite3.dll'
    [IO.File]::WriteAllBytes($sqlite,[IO.File]::ReadAllBytes((Join-Path $env:SystemRoot 'System32/winsqlite3.dll')))
    [FlDns.SqliteDb]::Load($sqlite)
    function Assert-Fixture([string]$Root) {
        $safe=Assert-BoundPath $Root $FixtureBase
        if(-not (Split-Path -Leaf $safe).StartsWith('flclash-dns-fixture-')) { throw 'Unexpected fixture directory name.' }
        $marker=[IO.File]::ReadAllText((Join-Path $safe '.chunson-flclash-fixture.json')) | ConvertFrom-Json
        if($marker.purpose -cne 'ChunsonSwitch.FlClashDns.Checks' -or $marker.root -ine $safe) { throw 'Invalid fixture ownership marker.' }
        $live=[IO.Path]::GetFullPath((Join-Path $env:APPDATA 'com.follow/clash')).TrimEnd('\')
        if($safe -ieq $live) { throw 'Real FlClash data is forbidden.' }
        $fakeApp=Join-Path $safe 'install/FlClash.exe'
        if([IO.File]::ReadAllText($fakeApp) -cne 'inert fixture; never execute') { throw 'Fixture executable is not inert.' }
        return $safe
    }
    function Ready-Fixture([bool]$Modern=$false) {
        if($Modern) { $root=NewModernFixture } else { $root=NewFixture }
        [void](Assert-Fixture $root)
        [IO.File]::WriteAllBytes((Join-Path $root 'install/sqlite3.dll'),[IO.File]::ReadAllBytes($sqlite))
        return $root
    }
    function Invoke-Standalone([string]$Source,[string]$Root,[string]$Action,[string]$Backup='',[bool]$PreserveCustom=$false) {
        $root=Assert-Fixture $Root
        [void](Assert-BoundPath $Source (Join-Path $FixtureBase 'assets'))
        if($Backup) { [void](Assert-BoundPath $Backup (Join-Path $root 'dns-backups')) }
        $command='[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); & '+(Quote $Source)+' -Offline -NoRestart -Action '+(Quote $Action)+' -DataDir '+(Quote $root)+' -AppPath '+(Quote (Join-Path $root 'install/FlClash.exe'))+' -ProxyGroup '+(Quote "Proxy's `$fixture")
        if($Backup) { $command+=' -BackupDir '+(Quote $Backup) }
        if($PreserveCustom) { $command+=' -PreserveCustomScript' }
        $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $output=& (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') -NoLogo -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand $encoded 2>&1
        $code=$LASTEXITCODE
        $text=$output | Out-String
        Check (-not $text.Contains('fixture-node-secret')) 'standalone output does not expose fixture credentials'
        $label=('{0:D3}-{1}-{2}' -f $script:invocations.Count,[IO.Path]::GetFileName((Split-Path -Parent $Source)),$Action)
        $log=Join-Path $FixtureBase ($label+'.txt'); [IO.File]::WriteAllText($log,$text,$utf8)
        $result=[pscustomobject]@{source=$Source;root=$root;action=$Action;backup=$Backup;preserveCustomScript=$PreserveCustom;exitCode=$code;log=$log;output=$text;offline=$true;noRestart=$true}
        $script:invocations.Add($result) | Out-Null
        return $result
    }
    function Backup-Path([string]$Root) {
        $path=[IO.File]::ReadAllText((Join-Path $Root 'dns-backups/last-backup.txt')).Trim()
        return Assert-BoundPath $path (Join-Path $Root 'dns-backups')
    }
    function Generate([string]$Root,[bool]$GlobalOnly=$false) {
        [void](Assert-Fixture $Root)
        if($GlobalOnly) { & $NodePath $RuntimeFixture $Root '--global-only' } else { & $NodePath $RuntimeFixture $Root }
        Check ($LASTEXITCODE -eq 0) 'isolated Node fixture generates configuration without FlClash or network'
    }
    function Profile-Binding([string]$Root,[bool]$Modern=$false) {
        $db=[FlDns.SqliteDb]::new((Join-Path $Root 'database.sqlite'),$false)
        try {
            $column='overwrite_type'; if($Modern) { $column='extend_type' }
            return @($db.Query("SELECT id,$column AS binding,script_id FROM profiles WHERE id<>2"))[0]
        } finally { $db.Dispose() }
    }
    function Equal-JsonValue($First,$Second) {
        if($null -eq $First -or $null -eq $Second) { return $null -eq $First -and $null -eq $Second }
        if($First -is [array] -or $Second -is [array]) {
            if($First -isnot [array] -or $Second -isnot [array] -or $First.Count -ne $Second.Count) { return $false }
            for($index=0;$index -lt $First.Count;$index++) { if(-not (Equal-JsonValue $First[$index] $Second[$index])) { return $false } }
            return $true
        }
        if($First -is [Management.Automation.PSCustomObject] -or $Second -is [Management.Automation.PSCustomObject]) {
            if($First -isnot [Management.Automation.PSCustomObject] -or $Second -isnot [Management.Automation.PSCustomObject]) { return $false }
            $firstKeys=@($First.PSObject.Properties | ForEach-Object { $_.Name } | Sort-Object -CaseSensitive)
            $secondKeys=@($Second.PSObject.Properties | ForEach-Object { $_.Name } | Sort-Object -CaseSensitive)
            if((Json $firstKeys) -cne (Json $secondKeys)) { return $false }
            foreach($key in $firstKeys) { if(-not (Equal-JsonValue $First.PSObject.Properties[$key].Value $Second.PSObject.Properties[$key].Value)) { return $false } }
            return $true
        }
        return (Json $First) -ceq (Json $Second)
    }
    function Check-RestoredDns($Before,$After,[string]$Name) {
        Check ((Equal-JsonValue $Before.patchClashConfig.dns $After.patchClashConfig.dns) -and (Equal-JsonValue $Before.patchClashConfig.tun $After.patchClashConfig.tun) -and $Before.patchClashConfig.mode -ceq $After.patchClashConfig.mode -and $Before.networkProps.appendSystemDns -eq $After.networkProps.appendSystemDns) ($Name+' restores original DNS TUN mode and system DNS settings')
    }
    foreach($source in @($current,$historical)) {
        $tokens=$null; $errors=$null
        [Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors) | Out-Null
        Check ($errors.Count -eq 0) ('PS5 parses actual standalone '+(Split-Path -Leaf (Split-Path -Parent $source)))
    }
    CheckUnknownAppProcess $current 'standalone process guard'
    $old=Ready-Fixture; $oldBefore=ReadConfig $old; $before=Tree $old
    Check ((Invoke-Standalone $current $old 'Preview').exitCode -eq 0 -and (Tree $old) -ceq $before) 'current standalone Preview preserves old-schema bytes and timestamps'
    Check ((Invoke-Standalone $current $old 'Apply').exitCode -eq 0) 'current standalone Apply accepts old overwrite_type schema'
    Check ((ReadConfig $old).patchClashConfig.tun.'strict-route' -eq $true) 'old-schema standalone Apply persists strict-route true when the field was originally absent'
    $oldBackup=Backup-Path $old; $oldMeta=[IO.File]::ReadAllText((Join-Path $oldBackup 'manifest.json')) | ConvertFrom-Json
    Check ($oldMeta.status -ceq 'applied' -and $oldMeta.profileId -eq 1 -and [IO.File]::Exists((Join-Path $oldBackup 'database.sqlite'))) 'old-schema Apply creates completed transactional database backup'
    $saved=Tree $old
    Check ((Invoke-Standalone $current $old 'Apply').exitCode -eq 0 -and (Tree $old) -ceq $saved) 'old-schema repeated Apply is byte and timestamp preserving including backups'
    Generate $old; $generated=Tree $old
    Check ((Invoke-Standalone $current $old 'Verify').exitCode -eq 0 -and (Tree $old) -ceq $generated) 'old-schema generated configuration verifies read-only without live acceptance'
    $later=ReadConfig $old; $later.appSettingProps | Add-Member userTheme 'after-current-old-apply'; WriteConfig $old $later
    Check ((Invoke-Standalone $current $old 'Restore' $oldBackup).exitCode -eq 0) 'current standalone Restore accepts its old-schema backup'
    $restored=ReadConfig $old; Check-RestoredDns $oldBefore $restored 'old-schema Restore'
    Check ($null -eq $restored.patchClashConfig.tun.PSObject.Properties['strict-route']) 'old-schema standalone Restore removes strict-route when the original global field was absent'
    $binding=Profile-Binding $old
    Check ($binding['binding'] -ceq 'standard' -and $null -eq $binding['script_id'] -and @(Get-ChildItem -LiteralPath (Join-Path $old 'scripts') -File).Count -eq 0 -and $restored.appSettingProps.userTheme -ceq 'after-current-old-apply') 'old-schema Restore removes created script and preserves later theme'
    $modern=Ready-Fixture $true; $modernBefore=ReadConfig $modern; $modernOther=ReadModernUnrelated $modern; $before=Tree $modern
    $priorError=Invoke-Standalone $historical $modern 'Preview'
    Check ($priorError.exitCode -eq 1 -and $priorError.output.Contains('数据库结构不兼容') -and (Tree $modern) -ceq $before) 'historical standalone reproduces modern schema error without writes'
    Check ((Invoke-Standalone $current $modern 'Preview').exitCode -eq 0 -and (Tree $modern) -ceq $before) 'current standalone Preview accepts modern schema and exact 64-bit profile ID read-only'
    Check ((Invoke-Standalone $current $modern 'Apply').exitCode -eq 0) 'current standalone Apply accepts modern extend_type schema'
    $modernBackup=Backup-Path $modern; $modernMeta=[IO.File]::ReadAllText((Join-Path $modernBackup 'manifest.json')) | ConvertFrom-Json
    $effective=ReadConfig $modern
    Check ($modernBefore.patchClashConfig.tun.'strict-route' -eq $false -and $effective.patchClashConfig.tun.'strict-route' -eq $true) 'modern standalone Apply replaces explicit global strict-route false with true'
    Check ($modernMeta.profileId -eq 366451883767762944L -and $modernMeta.status -ceq 'applied' -and $null -eq $effective.PSObject.Properties['overrideDns']) 'modern Apply binds exact 64-bit ID without resurrecting removed DNS boolean'
    $keys=@($effective.patchClashConfig.'dns-override-keys')
    Check (@(@('nameserver','default-nameserver','proxy-server-nameserver') | Where-Object { $keys -cnotcontains $_ }).Count -eq 0 -and @($keys | Where-Object { $_ -cin @('enable','respect-rules','prefer-h3','fallback','nameserver-policy') }).Count -eq 0 -and $effective.patchClashConfig.'mixed-port' -eq 7890) 'modern mask uses supported fields and preserves existing proxy port'
    Check ((ReadModernUnrelated $modern) -ceq $modernOther) 'modern Apply preserves unrelated profile custom rules groups and overrides'
    $saved=Tree $modern
    Check ((Invoke-Standalone $current $modern 'Apply').exitCode -eq 0 -and (Tree $modern) -ceq $saved) 'modern repeated Apply preserves every byte timestamp and backup count'
    Generate $modern $true; $generated=Tree $modern
    Check ((Invoke-Standalone $current $modern 'Verify').exitCode -eq 1 -and (Tree $modern) -ceq $generated) 'modern global-only DNS patch cannot falsely verify an incomplete generated configuration'
    Generate $modern; $runtime=[IO.File]::ReadAllText((Join-Path $modern 'config.yaml')) | ConvertFrom-Json
    Check ($runtime.tun.'strict-route' -eq $true) 'modern standalone Apply generates strict-route true after script-first global TUN patch'
    Check ($runtime.dns.enable -and $runtime.dns.'respect-rules' -and -not $runtime.dns.'prefer-h3' -and @($runtime.dns.fallback).Count -eq 0 -and $null -eq $runtime.dns.'nameserver-policy'.PSObject.Properties['www.baidu.com'] -and $runtime.dns.'nameserver-policy'.'local.lan' -ceq '192.168.1.1') 'modern actual script supplies DNS fields excluded by normalProfileKeys and preserves private DNS'
    $generated=Tree $modern
    Check ((Invoke-Standalone $current $modern 'Verify').exitCode -eq 0 -and (Tree $modern) -ceq $generated) 'modern generated DNS verifies read-only after actual managed JavaScript'
    $later=ReadConfig $modern; $later.appSettingProps | Add-Member userTheme 'after-modern-apply'; WriteConfig $modern $later
    Check ((Invoke-Standalone $current $modern 'Restore' $modernBackup).exitCode -eq 0) 'modern standalone Restore accepts its completed backup'
    $restored=ReadConfig $modern; Check-RestoredDns $modernBefore $restored 'modern Restore'
    Check ($restored.patchClashConfig.tun.'strict-route' -eq $false) 'modern standalone Restore reinstates the original explicit global strict-route false'
    $binding=Profile-Binding $modern $true
    Check ($binding['binding'] -ceq 'standard' -and $null -eq $binding['script_id'] -and (ReadModernUnrelated $modern) -ceq $modernOther -and $restored.appSettingProps.userTheme -ceq 'after-modern-apply' -and $null -eq $restored.PSObject.Properties['overrideDns'] -and (Json $restored.patchClashConfig.'dns-override-keys') -ceq (Json $modernBefore.patchClashConfig.'dns-override-keys')) 'modern Restore restores original mask binding and unrelated data without retired boolean'
    Generate $modern $true; $runtime=[IO.File]::ReadAllText((Join-Path $modern 'config.yaml')) | ConvertFrom-Json
    Check ($runtime.tun.'strict-route' -eq $false) 'modern standalone Restore regenerates original strict-route false without stale managed effects'
    Check ($runtime.dns.enable -and $runtime.dns.'enhanced-mode' -ceq 'fake-ip' -and -not $runtime.dns.'respect-rules' -and $runtime.dns.'prefer-h3' -and
        $runtime.dns.fallback[0] -ceq '1.0.0.1' -and (Json @($runtime.dns.nameserver)) -ceq (Json @($modernBefore.patchClashConfig.dns.nameserver)) -and
        $runtime.dns.'nameserver-policy'.'www.baidu.com' -ceq '223.5.5.5' -and $runtime.dns.'nameserver-policy'.'local.lan' -ceq '192.168.1.1') 'modern Restore regenerates upstream auto-enabled DNS defaults and original mask values without stale managed effects'
    $legacy=Ready-Fixture; $legacyBefore=ReadConfig $legacy; $before=Tree $legacy
    Check ((Invoke-Standalone $historical $legacy 'Preview').exitCode -eq 0 -and (Tree $legacy) -ceq $before) 'historical V2 standalone Preview accepts its original schema read-only'
    Check ((Invoke-Standalone $historical $legacy 'Apply').exitCode -eq 0) 'historical actual standalone creates genuine V2 script and backup'
    $legacyBackup=Backup-Path $legacy; $legacyMeta=[IO.File]::ReadAllText((Join-Path $legacyBackup 'manifest.json')) | ConvertFrom-Json
    $legacyCode=[IO.File]::ReadAllText((Join-Path $legacy ('scripts/'+$legacyMeta.newScriptId+'.js')))
    Check ($legacyCode.StartsWith('// FLCLASH_DNS_SETUP_V2') -and $legacyMeta.toolVersion -eq 2) 'legacy upgrade fixture comes from exact actual V2 source rather than a marker imitation'
    $saved=Tree $legacy
    Check ((Invoke-Standalone $historical $legacy 'Apply').exitCode -eq 0 -and (Tree $legacy) -ceq $saved) 'historical V2 repeated Apply remains byte and timestamp preserving'
    Check ((Invoke-Standalone $historical $legacy 'Restore' $legacyBackup).exitCode -eq 0) 'historical standalone restores its genuine V2 backup'
    Check-RestoredDns $legacyBefore (ReadConfig $legacy) 'historical V2 Restore'
    Check ((Invoke-Standalone $historical $legacy 'Apply').exitCode -eq 0) 'historical V2 source creates a second genuine backup for schema migration'
    $legacyBackup=Backup-Path $legacy
    $upgrade=Ready-Fixture $true; $upgradeOther=ReadModernUnrelated $upgrade
    $upgradeConfig=ReadConfig $upgrade; $upgradeConfig.patchClashConfig=(Json $effective.patchClashConfig) | ConvertFrom-Json; WriteConfig $upgrade $upgradeConfig
    $oldCodePath=Join-Path $upgrade 'scripts/42.js'; [IO.File]::WriteAllText($oldCodePath,$legacyCode,$utf8)
    $db=[FlDns.SqliteDb]::new((Join-Path $upgrade 'database.sqlite'),$true)
    try { $db.Execute("INSERT INTO scripts(id,label,last_update_time) VALUES(42,'exact historical V2',1)"); $db.Execute("UPDATE profiles SET extend_type='script',script_id=42 WHERE id=366451883767762944") } finally { $db.Dispose() }
    Generate $upgrade; $generated=Tree $upgrade
    Check ((Invoke-Standalone $current $upgrade 'Verify').exitCode -eq 1 -and (Tree $upgrade) -ceq $generated) 'exact historical V2 plus global modern DNS cannot pass generated verification'
    Check ((Invoke-Standalone $current $upgrade 'Apply').exitCode -eq 0) 'modern standalone upgrades exact verified V2 body'
    $upgradeBackup=Backup-Path $upgrade; $upgradeMeta=[IO.File]::ReadAllText((Join-Path $upgradeBackup 'manifest.json')) | ConvertFrom-Json
    Check ($upgradeMeta.scriptReplaced -and -not $upgradeMeta.scriptChanged -and $upgradeMeta.newScriptId -eq 42 -and [IO.File]::ReadAllText((Join-Path $upgradeBackup 'original-script.js')) -ceq $legacyCode -and [IO.File]::ReadAllText($oldCodePath).Contains('FLCLASH_DNS_SETUP_V3')) 'V2 upgrade preserves script ID and stores exact original body for Restore'
    $saved=Tree $upgrade
    Check ((Invoke-Standalone $current $upgrade 'Apply').exitCode -eq 0 -and (Tree $upgrade) -ceq $saved) 'V2 upgrade repeated Apply does not replace script or create redundant backups'
    Generate $upgrade
    Check ((Invoke-Standalone $current $upgrade 'Verify').exitCode -eq 0) 'V2 to V3 upgrade generates effective modern DNS in actual filter order'
    Check ((Invoke-Standalone $current $upgrade 'Restore' $upgradeBackup).exitCode -eq 0 -and [IO.File]::ReadAllText($oldCodePath) -ceq $legacyCode -and (ReadModernUnrelated $upgrade) -ceq $upgradeOther) 'V2 upgrade Restore preserves exact previous script and unrelated modern data'
    [IO.File]::AppendAllText($oldCodePath,'// edited V2 body must not be trusted',$utf8); $before=Tree $upgrade
    Check ((Invoke-Standalone $current $upgrade 'Preview').exitCode -eq 1 -and (Tree $upgrade) -ceq $before) 'V2 marker with edited body is rejected read-only'
    Check ((Invoke-Standalone $current $upgrade 'Apply').exitCode -eq 1 -and (Tree $upgrade) -ceq $before) 'V2 marker with edited body cannot write settings or create backups'
    $db=[FlDns.SqliteDb]::new((Join-Path $legacy 'database.sqlite'),$true)
    try {
        foreach($sql in @('ALTER TABLE profiles RENAME COLUMN overwrite_type TO extend_type','ALTER TABLE profiles ADD COLUMN type TEXT NOT NULL DEFAULT ''url''','ALTER TABLE profiles ADD COLUMN overrides TEXT','DROP TABLE profile_rule_mapping','CREATE TABLE rules(id INTEGER PRIMARY KEY,profile_id INTEGER,rule_action TEXT NOT NULL)')) { $db.Execute($sql) }
    } finally { $db.Dispose() }
    $migrated=ReadConfig $legacy; $migrated.PSObject.Properties.Remove('overrideDns')
    $normal=@('listen','ipv6','enhanced-mode','fake-ip-filter','fake-ip-filter-mode','default-nameserver','nameserver','proxy-server-nameserver','direct-nameserver')
    $migrated.patchClashConfig.'dns-override-keys'=@($migrated.patchClashConfig.'dns-override-keys' | Where-Object { $_ -cin $normal })
    $migrated.appSettingProps | Add-Member userTheme 'after-version-migration'; WriteConfig $legacy $migrated
    Check ((Invoke-Standalone $current $legacy 'Restore' $legacyBackup).exitCode -eq 0) 'current standalone restores genuine pre-upgrade V2 backup after actual schema and DNS-mask migration'
    $restored=ReadConfig $legacy; Check-RestoredDns $legacyBefore $restored 'cross-version V2 Restore'
    $binding=Profile-Binding $legacy $true
    Check ($binding['binding'] -ceq 'standard' -and $null -eq $binding['script_id'] -and $null -eq $restored.PSObject.Properties['overrideDns'] -and @($restored.patchClashConfig.'dns-override-keys').Count -eq 0 -and $restored.appSettingProps.userTheme -ceq 'after-version-migration' -and @(Get-ChildItem -LiteralPath (Join-Path $legacy 'scripts') -File).Count -eq 0) 'genuine old backup restores retired false DNS boolean as empty modern mask and preserves later theme'
    $manifest=[IO.File]::ReadAllText((Join-Path $legacyBackup 'manifest.json')) | ConvertFrom-Json
    Check ($manifest.status -ceq 'restored') 'cross-version Restore marks original V2 manifest restored'
    # First-use preserving repair must work without an active script, including
    # a standard profile with an intentionally inactive script binding.
    function Generate-BoundScript([string]$Root,[bool]$Modern) {
        $bound=Profile-Binding $Root $Modern
        $activeName=([string][long]$bound['script_id'])+'.js'
        $inactiveDir=Join-Path $Root '.fixture-inactive-scripts'
        [IO.Directory]::CreateDirectory($inactiveDir) | Out-Null
        $moved=@()
        try {
            foreach($file in @(Get-ChildItem -LiteralPath (Join-Path $Root 'scripts') -File -Filter '*.js')) {
                if($file.Name -cne $activeName) {
                    $target=Join-Path $inactiveDir $file.Name
                    [IO.File]::Move($file.FullName,$target)
                    $moved+=@{Original=$file.FullName;Temporary=$target}
                }
            }
            # The shared JS fixture requires one script. Only the bound script is
            # exposed here, as FlClash executes the active profile's binding.
            Generate $Root
        } finally { foreach($file in $moved) { [IO.File]::Move($file.Temporary,$file.Original) } }
    }
    foreach($case in @('legacy-standard','modern-standard','modern-script-null','modern-inactive')) {
        $isModern=$case -cne 'legacy-standard'
        $first=Ready-Fixture $isModern; $firstBefore=ReadConfig $first
        $profileId=[long]$firstBefore.currentProfileId
        $rawFile=Join-Path $first "profiles/$profileId.yaml"
        $raw=[IO.File]::ReadAllText($rawFile) | ConvertFrom-Json
        $raw.rules=@('DOMAIN,first-use-fixture.invalid,DIRECT',"DOMAIN-SUFFIX,preserve-fixture.invalid,Proxy's `$fixture","MATCH,Proxy's `$fixture")
        $raw | Add-Member listeners @(@{name='raw-subscription-listener';type='http';listen='127.0.0.1';port=19080;proxy='fixture-node'})
        WriteJson $rawFile $raw; WriteJson (Join-Path $first 'config.yaml') $raw
        $rawHash=(Get-FileHash -LiteralPath $rawFile -Algorithm SHA256).Hash
        $column='overwrite_type'; if($isModern) { $column='extend_type' }
        $db=[FlDns.SqliteDb]::new((Join-Path $first 'database.sqlite'),$true)
        try {
            if($case -ceq 'modern-script-null') { $db.Execute("UPDATE profiles SET extend_type='script',script_id=NULL WHERE id=$profileId") }
            if($case -ceq 'modern-inactive') {
                [IO.File]::WriteAllText((Join-Path $first 'scripts/44.js'),'const main = config => { throw new Error("INACTIVE SCRIPT MUST NOT RUN"); };',$utf8)
                $db.Execute("INSERT INTO scripts(id,label,last_update_time) VALUES(44,'inactive fixture',123456)")
                $db.Execute("UPDATE profiles SET extend_type='standard',script_id=44 WHERE id=$profileId")
            }
            $originalRecords=Json @($db.Query('SELECT * FROM scripts ORDER BY id'))
        } finally { $db.Dispose() }
        $originalBinding=Profile-Binding $first $isModern
        $inactiveHash=$null
        if($case -ceq 'modern-inactive') { $inactiveHash=(Get-FileHash -LiteralPath (Join-Path $first 'scripts/44.js') -Algorithm SHA256).Hash }
        $before=Tree $first
        $preview=Invoke-Standalone $current $first 'Preview' '' $true
        Check ($preview.exitCode -eq 0 -and $preview.output.Contains('当前没有活动脚本') -and (Tree $first) -ceq $before) ($case+' preserving Preview accepts first use without executing inactive JavaScript or writing files')
        Check ((Invoke-Standalone $current $first 'Apply' '' $true).exitCode -eq 0) ($case+' preserving Apply creates only a DNS/TUN wrapper around identity main')
        $firstBackup=Backup-Path $first
        $meta=[IO.File]::ReadAllText((Join-Path $firstBackup 'manifest.json')) | ConvertFrom-Json
        $binding=Profile-Binding $first $isModern
        $activeFile=Join-Path $first "scripts/$($binding['script_id']).js"
        $code=[IO.File]::ReadAllText($activeFile)
        $payload=[regex]::Match($code,'(?m)^// original-script-base64: ([A-Za-z0-9+/=]+)$')
        Check ($meta.scriptChanged -and -not $meta.scriptReplaced -and $binding['binding'] -ceq 'script' -and
            $payload.Success -and [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload.Groups[1].Value.Trim())) -ceq 'const main = (config) => config;') ($case+' creates canonical identity preservation script without adopting inactive source')
        $saved=Tree $first
        Check ((Invoke-Standalone $current $first 'Apply' '' $true).exitCode -eq 0 -and (Tree $first) -ceq $saved) ($case+' second preserving Apply is byte/time/backup idempotent with no nested wrapper')
        Generate-BoundScript $first $isModern
        $runtime=[IO.File]::ReadAllText((Join-Path $first 'config.yaml')) | ConvertFrom-Json
        Check ((Json @($runtime.rules)) -ceq (Json @($raw.rules)) -and (Json @($runtime.proxies)) -ceq (Json @($raw.proxies)) -and
            (Json @($runtime.'proxy-groups')) -ceq (Json @($raw.'proxy-groups')) -and (Json @($runtime.listeners)) -ceq (Json @($raw.listeners)) -and
            (Get-FileHash -LiteralPath $rawFile -Algorithm SHA256).Hash -ceq $rawHash) ($case+' executed identity wrapper preserves raw subscription rule order nodes groups listeners and profile bytes')
        Check ($runtime.dns.enable -and $runtime.dns.'respect-rules' -and -not $runtime.dns.'prefer-h3' -and @($runtime.dns.fallback).Count -eq 0 -and $runtime.tun.'strict-route') ($case+' identity wrapper emits effective DNS and TUN fields')
        $generated=Tree $first
        Check ((Invoke-Standalone $current $first 'Verify' '' $true).exitCode -eq 0 -and (Tree $first) -ceq $generated) ($case+' preserving generated Verify checks DNS/TUN read-only without requiring injected baseline rules')
        if($case -ceq 'modern-inactive') { Check ((Get-FileHash -LiteralPath (Join-Path $first 'scripts/44.js') -Algorithm SHA256).Hash -ceq $inactiveHash) 'first-use inactive script exact bytes survive Apply and generation without execution' }
        Check ((Invoke-Standalone $current $first 'Restore' $firstBackup $true).exitCode -eq 0) ($case+' Restore accepts created identity wrapper backup')
        $restored=ReadConfig $first; Check-RestoredDns $firstBefore $restored ($case+' Restore')
        $restoredBinding=Profile-Binding $first $isModern
        $db=[FlDns.SqliteDb]::new((Join-Path $first 'database.sqlite'),$false)
        try { $restoredRecords=Json @($db.Query('SELECT * FROM scripts ORDER BY id')) } finally { $db.Dispose() }
        Check ($restoredBinding['binding'] -ceq $originalBinding['binding'] -and $restoredBinding['script_id'] -eq $originalBinding['script_id'] -and
            $restoredRecords -ceq $originalRecords -and -not (Test-Path -LiteralPath $activeFile) -and
            (Get-FileHash -LiteralPath $rawFile -Algorithm SHA256).Hash -ceq $rawHash) ($case+' Restore removes created JS/DB row and returns exact standard/script-null/inactive binding and records')
        if($case -ceq 'modern-inactive') { Check ((Get-FileHash -LiteralPath (Join-Path $first 'scripts/44.js') -Algorithm SHA256).Hash -ceq $inactiveHash) 'first-use Restore preserves inactive source bytes and metadata without reactivating it' }
    }
    $custom=Ready-Fixture $true; $customBefore=ReadConfig $custom; $customOther=ReadModernUnrelated $custom
    $customCode=@'
// Synthetic custom script: no personal nodes, network, or application access.
const main = (config) => {
  config["fixture-main-calls"] = (config["fixture-main-calls"] || 0) + 1;
  config.proxies = [...config.proxies, { name: "custom-fixture-node", type: "socks5", server: "fixture.invalid", port: 19, password: "fixture-node-secret" }];
  config.listeners = [{ name: "custom-fixture-listener", type: "http", listen: "127.0.0.1", port: 19081, proxy: "custom-fixture-node" }];
  config.rules = ["DOMAIN,custom-fixture.invalid,DIRECT", "DOMAIN-SUFFIX,custom-second.invalid,custom-fixture-node", ...config.rules];
  config.dns["nameserver-policy"]["custom.lan"] = "192.168.10.1";
  return config;
};
'@
    $customCode=$customCode.Replace("`r`n","`n")
    $firstNewline=$customCode.IndexOf("`n")
    $customCode=$customCode.Substring(0,$firstNewline)+"`r`r`n"+$customCode.Substring($firstNewline+1).Replace("`n","`r`n")+"`r`n// 离线假配置：混合换行与原始字节保留验收。`r`r`n"
    $customPath=Join-Path $custom 'scripts/44.js'; [IO.File]::WriteAllText($customPath,$customCode,$utf8)
    $customHash=(Get-FileHash -LiteralPath $customPath -Algorithm SHA256).Hash
    $db=[FlDns.SqliteDb]::new((Join-Path $custom 'database.sqlite'),$true)
    try {
        $db.Execute("INSERT INTO scripts(id,label,last_update_time) VALUES(44,'synthetic custom route',123456)")
        $db.Execute("UPDATE profiles SET extend_type='script',script_id=44 WHERE id=366451883767762944")
        $customRecord=Json @($db.Query('SELECT * FROM scripts WHERE id=44'))
    } finally { $db.Dispose() }
    Generate $custom; $customOriginalRuntime=[IO.File]::ReadAllText((Join-Path $custom 'config.yaml')) | ConvertFrom-Json
    $expectedRoutes=Json @($customOriginalRuntime.rules)
    $expectedNodes=Json @($customOriginalRuntime.proxies)
    $expectedGroups=Json @($customOriginalRuntime.'proxy-groups')
    $expectedListeners=Json @($customOriginalRuntime.listeners)
    $before=Tree $custom
    Check ((Invoke-Standalone $current $custom 'Preview').exitCode -eq 1 -and (Tree $custom) -ceq $before) 'default standalone Preview preserves and rejects unrecognized custom script'
    Check ((Invoke-Standalone $current $custom 'Apply').exitCode -eq 1 -and (Tree $custom) -ceq $before) 'default standalone Apply cannot overwrite custom routes nodes or listeners'
    Check ((Invoke-Standalone $current $custom 'Preview' '' $true).exitCode -eq 0 -and (Tree $custom) -ceq $before) 'explicit PreserveCustomScript Preview accepts custom body without execution or mutation'
    Check ((Invoke-Standalone $current $custom 'Apply' '' $true).exitCode -eq 0) 'explicit PreserveCustomScript Apply saves opaque-main wrapper and DNS settings'
    Check ((ReadConfig $custom).patchClashConfig.tun.'strict-route' -eq $true) 'PreserveCustomScript Apply persists global strict-route true instead of relying only on its wrapper'
    $customBackup=Backup-Path $custom; $wrapped=[IO.File]::ReadAllText($customPath)
    $payload=[regex]::Match($wrapped,'(?m)^// original-script-base64: (.+)\r?$')
    Check ($wrapped.StartsWith('// FLCLASH_DNS_PRESERVE_V1') -and $payload.Success -and [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload.Groups[1].Value.Trim())) -ceq $customCode -and (Get-FileHash -LiteralPath (Join-Path $customBackup 'original-script.js') -Algorithm SHA256).Hash -ceq $customHash) 'custom wrapper preserves exact Unicode mixed CRCRLF CRLF original payload and backup bytes'
    $saved=Tree $custom
    Check ((Invoke-Standalone $current $custom 'Preview' '' $true).exitCode -eq 0 -and (Tree $custom) -ceq $saved) 'mixed-newline preservation wrapper has a stable canonical body on repeated Preview'
    Check ((Invoke-Standalone $current $custom 'Apply' '' $true).exitCode -eq 0 -and (Tree $custom) -ceq $saved) 'mixed-newline custom Preserve repeated Apply is byte preserving and never nests wrappers or backups'
    Check ((Invoke-Standalone $current $custom 'Preview').exitCode -eq 1 -and (Tree $custom) -ceq $saved) 'legitimate Preserve wrapper still requires explicit Preserve mode instead of silently replacing custom routes'
    Generate $custom $true; $generated=Tree $custom
    Check ((Invoke-Standalone $current $custom 'Verify' '' $true).exitCode -eq 1 -and (Tree $custom) -ceq $generated) 'Preserve mode Verify rejects modern global-only incomplete DNS settings'
    Generate $custom; $customRuntime=[IO.File]::ReadAllText((Join-Path $custom 'config.yaml')) | ConvertFrom-Json
    Check ($customRuntime.tun.'strict-route' -eq $true) 'PreserveCustomScript generates effective strict-route true after the global TUN patch'
    Check ((Json @($customRuntime.rules)) -ceq $expectedRoutes -and (Json @($customRuntime.proxies)) -ceq $expectedNodes -and (Json @($customRuntime.'proxy-groups')) -ceq $expectedGroups -and (Json @($customRuntime.listeners)) -ceq $expectedListeners) 'executed opaque-main wrapper preserves original rule order nodes groups and listener ports'
    Check ($customRuntime.'fixture-main-calls' -eq 1) 'opaque-main preservation wrapper evaluates original main exactly once'
    Check ($customRuntime.dns.enable -and $customRuntime.dns.'respect-rules' -and -not $customRuntime.dns.'prefer-h3' -and @($customRuntime.dns.fallback).Count -eq 0 -and $customRuntime.dns.'nameserver-policy'.'custom.lan' -ceq '192.168.10.1' -and $customRuntime.dns.'nameserver-policy'.'local.lan' -ceq '192.168.1.1' -and $customRuntime.dns.nameserver[0] -ceq "https://1.1.1.1/dns-query#Proxy's `$fixture") 'Preserve mode provides effective modern encrypted DNS while retaining custom private DNS policy'
    $generated=Tree $custom
    Check ((Invoke-Standalone $current $custom 'Verify' '' $true).exitCode -eq 0 -and (Tree $custom) -ceq $generated) 'Preserve mode generated DNS Verify accepts unmodified custom routes without live acceptance'
    $later=ReadConfig $custom; $later.appSettingProps | Add-Member userTheme 'after-preserved-custom-apply'; WriteConfig $custom $later
    Check ((Invoke-Standalone $current $custom 'Restore' $customBackup $true).exitCode -eq 0) 'Preserve mode Restore accepts its transactional original-script backup'
    $restored=ReadConfig $custom; Check-RestoredDns $customBefore $restored 'Preserve custom Restore'
    Check ($restored.patchClashConfig.tun.'strict-route' -eq $false) 'PreserveCustomScript Restore reinstates original global strict-route false'
    $db=[FlDns.SqliteDb]::new((Join-Path $custom 'database.sqlite'),$false)
    try { $restoredRecord=Json @($db.Query('SELECT * FROM scripts WHERE id=44')) } finally { $db.Dispose() }
    Check ((Get-FileHash -LiteralPath $customPath -Algorithm SHA256).Hash -ceq $customHash -and $restoredRecord -ceq $customRecord -and (ReadModernUnrelated $custom) -ceq $customOther -and $restored.appSettingProps.userTheme -ceq 'after-preserved-custom-apply') 'custom Restore returns exact script bytes original metadata and unrelated later settings'
    Check ((Invoke-Standalone $current $custom 'Apply' '' $true).exitCode -eq 0) 'restored custom script remains reusable in explicit Preserve mode'
    [IO.File]::AppendAllText($customPath,'// modified preservation wrapper must be refused',$utf8); $before=Tree $custom
    Check ((Invoke-Standalone $current $custom 'Preview' '' $true).exitCode -eq 1 -and (Tree $custom) -ceq $before) 'modified valid preservation marker cannot authenticate an altered wrapper'
    Check ((Invoke-Standalone $current $custom 'Apply' '' $true).exitCode -eq 1 -and (Tree $custom) -ceq $before) 'modified wrapper Apply is rejected before script preference or backup writes'
    [IO.File]::WriteAllText($customPath,"// preceding comment`n// FLCLASH_DNS_PRESERVE_V1`nconst main = config => config;`n",$utf8); $before=Tree $custom
    Check ((Invoke-Standalone $current $custom 'Preview' '' $true).exitCode -eq 1 -and (Tree $custom) -ceq $before) 'forged preservation marker behind a comment cannot bypass full-body ownership checks'
    $unsafe=Ready-Fixture $true
    $unsafeCode='const main = config => { const fs = require("fs"); return config; };'
    [IO.File]::WriteAllText((Join-Path $unsafe 'scripts/44.js'),$unsafeCode,$utf8)
    $db=[FlDns.SqliteDb]::new((Join-Path $unsafe 'database.sqlite'),$true)
    try { $db.Execute("INSERT INTO scripts(id,label,last_update_time) VALUES(44,'unsupported external dependency',1)"); $db.Execute("UPDATE profiles SET extend_type='script',script_id=44 WHERE id=366451883767762944") } finally { $db.Dispose() }
    $before=Tree $unsafe
    Check ((Invoke-Standalone $current $unsafe 'Preview' '' $true).exitCode -eq 1 -and (Tree $unsafe) -ceq $before) 'Preserve Preview conservatively rejects external require without evaluating custom code'
    Check ((Invoke-Standalone $current $unsafe 'Apply' '' $true).exitCode -eq 1 -and (Tree $unsafe) -ceq $before) 'Preserve Apply cannot write an unsupported dependency wrapper'
    WriteJson (Join-Path $FixtureBase 'standalone-verification.json') @{passed=$true;mode='actual_standalone_source_private_fixtures';checks=$checks;sources=@($script:sources.ToArray());invocations=@($script:invocations.ToArray());actualNetwork=$false;actualAppLaunch=$false;actualLiveDataWrites=$false;actualAccountOperations=$false}
    [Console]::WriteLine("$checks standalone PS5 checks passed. No live data, FlClash process, accounts, or network actions.")
} catch {
    $errorText=$_.Exception.ToString()+[Environment]::NewLine+$_.ScriptStackTrace
    [IO.File]::WriteAllText((Join-Path $FixtureBase 'standalone-failure.txt'),$errorText,$utf8)
    [Console]::WriteLine('FAIL '+$_.Exception.Message)
    exit 1
}
