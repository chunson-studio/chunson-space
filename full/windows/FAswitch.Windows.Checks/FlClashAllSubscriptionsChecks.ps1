param([Parameter(Mandatory=$true)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$FixtureBase=Join-Path $project ('.temp/all-dns-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($FixtureBase) | Out-Null
[IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'FlClashDnsScriptChecks.ps1'))
$boundary=[regex]::Match($source,'(?m)^try \{\r?$')
if(-not $boundary.Success) { throw 'Fixture factory boundary missing.' }
. ([ScriptBlock]::Create($source.Substring(0,$boundary.Index))) -FixtureBase $FixtureBase -ScriptPath (Join-Path $project 'scripts/proxy/FlClash-DNS.ps1') -PolicyPath (Join-Path $project 'windows/FAswitch.Proxy/Resources/dns-policy.json') -LibraryRoot (Join-Path $project 'windows/ThirdParty/YamlDotNet') -HelperPath (Join-Path $project 'scripts/proxy/lib/FlDns.cs') -AllSubscriptions
$yaml=Join-Path $LibraryRoot 'YamlDotNet.dll'
[Reflection.Assembly]::LoadFrom($yaml) | Out-Null
Add-Type -Path $HelperPath -ReferencedAssemblies $yaml
[FlDns.SqliteDb]::Load((Join-Path $env:SystemRoot 'System32/winsqlite3.dll'))
$results=@()
try {
    foreach($modern in @($false,$true)) {
        $root=NewFixture @('First route')
        if($modern) {
            $db=[FlDns.SqliteDb]::new((Join-Path $root 'database.sqlite'),$true)
            try {
                $db.Execute('ALTER TABLE profiles RENAME COLUMN overwrite_type TO extend_type')
                $db.Execute("ALTER TABLE profiles ADD COLUMN type TEXT DEFAULT 'url'")
                $db.Execute('ALTER TABLE profiles ADD COLUMN overrides TEXT')
                $db.Execute('ALTER TABLE profile_rule_mapping RENAME TO rules')
            } finally { $db.Dispose() }
            $config=ReadConfig $root; $config.PSObject.Properties.Remove('overrideDns')
            $config.patchClashConfig.'dns-override-keys'=@('nameserver','ipv6','default-nameserver','proxy-server-nameserver')
            WriteConfig $root $config
        }
        $raw=Get-Content -LiteralPath (Join-Path $root 'profiles/1.yaml') -Raw | ConvertFrom-Json
        $raw | Add-Member dns @{'nameserver-policy'=@{'local.lan'='192.168.1.1'}}
        WriteJson (Join-Path $root 'profiles/1.yaml') $raw
        $raw.'proxy-groups'[0].name='Second route'; $raw.rules=@('MATCH,Second route')
        WriteJson (Join-Path $root 'profiles/2.yaml') $raw
        $original='const main = config => { config.originalCustom = "preserved"; config.rules.unshift("DOMAIN,movie.fixture,DIRECT"); return config; };'
        [IO.File]::WriteAllText((Join-Path $root 'scripts/9.js'),$original,$script:utf8)
        $db=[FlDns.SqliteDb]::new((Join-Path $root 'database.sqlite'),$true)
        $column='overwrite_type'; if($modern) { $column='extend_type' }
        try {
            $db.Execute("INSERT INTO scripts VALUES(9,'user movie routing',1)")
            $db.Execute("INSERT INTO profiles(id,$column,script_id,selected_map) VALUES(2,'script',9,'{}')")
        } finally { $db.Dispose() }
        $before=Tree $root
        $preview=InvokeRepair $root 'Preview'
        Check ($preview.status -ceq 'PreviewReady' -and $preview.scope -ceq 'all-subscriptions' -and $preview.subscriptionCount -eq 2) 'preview covers both subscriptions without an IP selection'
        Check ((Tree $root) -ceq $before) 'all-subscription preview is read only'
        $runtimePath=Join-Path $root 'config.yaml'
        $runtime=Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json
        $runtime | Add-Member 'mixed-port' 7897 -Force; $runtime | Add-Member 'bind-address' '*' -Force
        $runtime.PSObject.Properties.Remove('authentication'); WriteJson $runtimePath $runtime
        $portReport=InvokeRepair $root 'Preview'
        Check ($portReport.proxyPorts[0].bindAddress -ceq '*' -and $portReport.proxyPorts[0].requiresAuthentication -eq $false) 'wildcard listener with omitted authentication is available on loopback'
        foreach($invalidAuth in @('malformed', @{unexpected='value'})) {
            $runtime | Add-Member authentication $invalidAuth -Force; WriteJson $runtimePath $runtime
            Check ($null -eq (InvokeRepair $root 'Preview').proxyPorts[0].requiresAuthentication) 'malformed authentication stays unknown'
        }
        $runtime.authentication=$null; WriteJson $runtimePath $runtime
        Check ($null -eq (InvokeRepair $root 'Preview').proxyPorts[0].requiresAuthentication) 'explicit null authentication stays unknown'
        $runtime.authentication=@('fixture:secret'); WriteJson $runtimePath $runtime
        Check ((InvokeRepair $root 'Preview').proxyPorts[0].requiresAuthentication -eq $true) 'configured authentication remains required'
        $runtime.authentication=@(); WriteJson $runtimePath $runtime
        Check ((InvokeRepair $root 'Preview').proxyPorts[0].requiresAuthentication -eq $false) 'empty authentication permits local connection'
        $applied=InvokeRepair $root 'Apply'
        Check ($applied.settingsSaved -and $applied.status -ceq 'SettingsSaved' -and -not $applied.networkVerified) 'all subscriptions saved without claiming live network success'
        Check ([IO.File]::ReadAllText((Join-Path $root 'scripts/9.js')) -ceq $original) 'custom script original bytes retained'
        $db=[FlDns.SqliteDb]::new((Join-Path $root 'database.sqlite'),$false)
        try { $rows=@($db.Query("SELECT id,script_id,$column AS type FROM profiles ORDER BY id")) } finally { $db.Dispose() }
        WriteJson (Join-Path $root 'all-fixture.json') @($rows | ForEach-Object { @{profile=[string]$_['id'];script=[string]$_['script_id'];group=$(if($_['id'] -eq 1){'First route'}else{'Second route'});custom=($_['id'] -eq 2);current=($_['id'] -eq 1)} })
        & node (Join-Path $PSScriptRoot 'FlClashAllSubscriptionsFixture.js') $root
        Check ($LASTEXITCODE -eq 0) 'two generated configurations preserve routes and use their own encrypted DNS group'
        $verify=InvokeRepair $root 'Verify'
        Check ($verify.generatedConfigVerified -and -not $verify.networkVerified) 'current generated DNS verification is separate from network acceptance'
        $after=Tree $root
        $repeat=InvokeRepair $root 'Apply'
        Check ($repeat.settingsSaved -and -not $repeat.backupDirectory -and (Tree $root) -ceq $after) 'repeat is idempotent without nested wrappers or a second backup'
        $raw.'proxy-groups'[0].name='Third route'; $raw.rules=@('MATCH,Third route')
        WriteJson (Join-Path $root 'profiles/3.yaml') $raw
        $db=[FlDns.SqliteDb]::new((Join-Path $root 'database.sqlite'),$true)
        try { $db.Execute("INSERT INTO profiles(id,$column,script_id,selected_map) VALUES(3,'script',9,'{}')") } finally { $db.Dispose() }
        $supplement=InvokeRepair $root 'Apply'
        Check ($supplement.settingsSaved -and $supplement.subscriptionCount -eq 3 -and @($supplement.changedFields).Count -eq 1) 'new subscription is covered without rewriting previous subscriptions'
        Check ([IO.File]::ReadAllText((Join-Path $root 'scripts/9.js')) -ceq $original) 'shared original script is never modified'
        $meta=Get-Content -LiteralPath (Join-Path $supplement.backupDirectory 'manifest.json') -Raw | ConvertFrom-Json
        $managed=Join-Path $root "scripts/$($meta.profiles[0].newScriptId).js"
        $savedScript=[IO.File]::ReadAllBytes($managed)
        [IO.File]::AppendAllText($managed,"`n// later user edit",$script:utf8)
        $tampered=Tree $root
        $refused=InvokeRepair $root 'Restore' '' $supplement.backupDirectory
        Check ($refused.status -ceq 'Failed' -and (Tree $root) -ceq $tampered) 'restore refuses to overwrite later user script edits'
        [IO.File]::WriteAllBytes($managed,$savedScript)
        $restoredNew=InvokeRepair $root 'Restore' '' $supplement.backupDirectory
        Check ($restoredNew.status -ceq 'Restored') 'latest subscription-only change restores separately'
        $restored=InvokeRepair $root 'Restore' '' $applied.backupDirectory
        Check ($restored.status -ceq 'Restored') 'all-subscription backup restores settings and original script bindings'
        $db=[FlDns.SqliteDb]::new((Join-Path $root 'database.sqlite'),$false)
        try { $rows=@($db.Query("SELECT id,script_id,$column AS type FROM profiles ORDER BY id")) } finally { $db.Dispose() }
        Check ($rows[0]['type'] -ceq 'standard' -and $null -eq $rows[0]['script_id'] -and $rows[1]['script_id'] -eq 9 -and $rows.Count -eq 3) 'restore preserves later added subscriptions'
        $db=[FlDns.SqliteDb]::new((Join-Path $root 'database.sqlite'),$true)
        try { $db.Execute('INSERT INTO proxy_groups(profile_id) VALUES(1)') } finally { $db.Dispose() }
        $beforeUnsupported=Tree $root
        $unsupported=InvokeRepair $root 'Apply'
        Check ($unsupported.status -ceq 'Failed' -and -not $unsupported.settingsSaved -and (Tree $root) -ceq $beforeUnsupported) 'unsupported custom standard rules leave every subscription unchanged'
        $results+=@(@{modern=$modern;root=$root;backup=$applied.backupDirectory;passed=$true})
    }
    WriteJson (Join-Path $EvidenceDirectory 'all-subscriptions.json') @{passed=$true;checks=$script:checks;actualProxyChanges=$false;fixtures=$results}
    [Console]::WriteLine("PASS $script:checks checks")
} catch {
    WriteJson (Join-Path $EvidenceDirectory 'all-subscriptions.json') @{passed=$false;checks=$script:checks;error=$_.Exception.Message;fixtureRoot=$FixtureBase;actualProxyChanges=$false}
    throw
}
