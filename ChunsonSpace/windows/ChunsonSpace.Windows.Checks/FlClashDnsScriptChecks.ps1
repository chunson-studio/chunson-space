param([string]$FixtureBase,[string]$ScriptPath,[string]$PolicyPath,[string]$LibraryRoot,[string]$HelperPath,[string]$NodePath,[string]$RuntimeFixture,[switch]$AllSubscriptions)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
Import-Module -Name (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1') -Force
Import-Module -Name (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Management/Microsoft.PowerShell.Management.psd1') -Force
Set-StrictMode -Version 2
$script:utf8=[Text.UTF8Encoding]::new($false)
$script:checks=0
function Check([bool]$passed,[string]$name) { if(-not $passed) { throw $name }; $script:checks++; [Console]::WriteLine('PASS '+$name) }
function Json($value) { ConvertTo-Json -InputObject $value -Depth 100 -Compress }
function WriteJson([string]$path,$value) { [IO.File]::WriteAllText($path,(Json $value),$script:utf8) }
function Quote([string]$value) { return "'"+$value.Replace("'","''")+"'" }
function Tree([string]$root) {
    return Json @(Get-ChildItem -LiteralPath $root -Recurse -Force -File | Sort-Object FullName | ForEach-Object { [ordered]@{path=$_.FullName;hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;time=$_.LastWriteTimeUtc.Ticks} })
}
function InvokeRepair([string]$root,[string]$action,[string]$group,[string]$backup,[bool]$omitAction=$false,[bool]$omitApp=$false,[string]$explicitApp=$null) {
    $report=Join-Path $FixtureBase ('report-'+[guid]::NewGuid().ToString('N')+'.json')
    $command='& ([ScriptBlock]::Create([IO.File]::ReadAllText('+(Quote $ScriptPath)+'))) -AssetRoot '+(Quote (Split-Path -Parent $ScriptPath))+' -Offline -NoRestart -NonInteractive -DataDir '+(Quote $root)+' -ReportPath '+(Quote $report)+' -PolicyPath '+(Quote $PolicyPath)+' -LibraryRoot '+(Quote $LibraryRoot)+' -CsHelperPath '+(Quote $HelperPath)
    if(-not $omitAction) { $command+=' -Action '+(Quote $action) }
    if($explicitApp) { $command+=' -AppPath '+(Quote $explicitApp) }
    elseif(-not $omitApp) { $command+=' -AppPath '+(Quote (Join-Path $root 'install/FlClash.exe')) }
    if($group) { $command+=' -ProxyGroup '+(Quote $group) }
    if($AllSubscriptions) { $command+=' -AllSubscriptions' }
    if($backup) { $command+=' -BackupDir '+(Quote $backup) }
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $output=& (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') -NoLogo -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand $encoded 2>&1
    $code=$LASTEXITCODE
    if(-not [IO.File]::Exists($report)) { throw ('Fixture invocation has no report: '+($output | Out-String)) }
    $text=[IO.File]::ReadAllText($report)
    Check (-not $text.Contains('fixture-node-secret')) 'report excludes node credentials'
    $result=$text | ConvertFrom-Json
    Check ($code -eq $result.exitCode) 'script exit code agrees with structured report'
    return $result
}
function NewFixture([string[]]$groups=@("Proxy's `$fixture")) {
    $root=Join-Path $FixtureBase ('flclash-dns-fixture-'+[guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory((Join-Path $root 'install')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $root 'profiles')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $root 'scripts')) | Out-Null
    WriteJson (Join-Path $root '.chunson-flclash-fixture.json') @{schemaVersion=1;root=$root;purpose='ChunsonSwitch.FlClashDns.Checks'}
    [IO.File]::WriteAllText((Join-Path $root 'install/FlClash.exe'),'inert fixture; never execute',$script:utf8)
    $config=[ordered]@{currentProfileId=1;overrideDns=$false;patchClashConfig=[ordered]@{mode='global';'mixed-port'=7890;port=7891;'socks-port'=7892;'dns-override-keys'=@();dns=[ordered]@{enable=$false;'respect-rules'=$false;'prefer-h3'=$true;nameserver=@('8.8.8.8');fallback=@('1.0.0.1');'default-nameserver'=@('223.5.5.5');'proxy-server-nameserver'=@('223.5.5.5');'nameserver-policy'=[ordered]@{'www.baidu.com'='223.5.5.5';'local.lan'='192.168.1.1'}};tun=[ordered]@{enable=$false;'auto-route'=$false;'dns-hijack'=@()}};networkProps=@{appendSystemDns=$true};appSettingProps=@{autoRun=$false}}
    WriteJson (Join-Path $root 'shared_preferences.json') @{'flutter.config'=(Json $config);'flutter.unrelated'='keep'}
    $selected=@{}; $groupObjects=@()
    foreach($name in $groups) { $selected[$name]='fixture-node'; $groupObjects+=@(@{name=$name;type='select';proxies=@('fixture-node')}) }
    $runtime=@{mode='global';proxies=@(@{name='fixture-node';type='socks5';server='fixture.invalid';port=9;password='fixture-node-secret'});'proxy-groups'=$groupObjects;rules=@('DOMAIN,fixture.invalid,DIRECT')}
    if($groups.Count -eq 1) { $runtime.rules=@('MATCH,'+$groups[0]) }
    WriteJson (Join-Path $root 'config.yaml') $runtime
    WriteJson (Join-Path $root 'profiles/1.yaml') $runtime
    $db=[FlDns.SqliteDb]::new((Join-Path $root 'database.sqlite'),$true)
    try {
        foreach($sql in @('CREATE TABLE profiles(id INTEGER PRIMARY KEY,overwrite_type TEXT,script_id INTEGER,selected_map TEXT)','CREATE TABLE scripts(id INTEGER PRIMARY KEY,label TEXT,last_update_time INTEGER)','CREATE TABLE profile_rule_mapping(profile_id INTEGER)','CREATE TABLE proxy_groups(profile_id INTEGER)')) { $db.Execute($sql) }
        $selection=(Json $selected).Replace("'","''")
        $db.Execute("INSERT INTO profiles VALUES(1,'standard',NULL,'$selection')")
    } finally { $db.Dispose() }
    return $root
}
function ReadConfig([string]$root) { $outer=[IO.File]::ReadAllText((Join-Path $root 'shared_preferences.json')) | ConvertFrom-Json; return ($outer.'flutter.config' | ConvertFrom-Json) }
function WriteConfig([string]$root,$config) { $outer=[IO.File]::ReadAllText((Join-Path $root 'shared_preferences.json')) | ConvertFrom-Json; $outer.'flutter.config'=Json $config; WriteJson (Join-Path $root 'shared_preferences.json') $outer }
function NewModernFixture {
    $root=NewFixture
    $id=366451883767762944L
    $config=ReadConfig $root; $config.currentProfileId=$id; $config.PSObject.Properties.Remove('overrideDns')
    $config.patchClashConfig.tun | Add-Member 'strict-route' $false
    $config.patchClashConfig.'dns-override-keys'=@('listen','ipv6','enhanced-mode','fake-ip-filter','default-nameserver','nameserver','proxy-server-nameserver'); WriteConfig $root $config
    [IO.File]::Move((Join-Path $root 'profiles/1.yaml'),(Join-Path $root "profiles/$id.yaml"))
    $raw=[IO.File]::ReadAllText((Join-Path $root "profiles/$id.yaml")) | ConvertFrom-Json
    $raw | Add-Member dns @{enable=$false;'respect-rules'=$false;'prefer-h3'=$true;fallback=@('1.0.0.1');nameserver=@('8.8.8.8');'nameserver-policy'=@{'www.baidu.com'='223.5.5.5';'local.lan'='192.168.1.1'}}
    WriteJson (Join-Path $root "profiles/$id.yaml") $raw
    $db=[FlDns.SqliteDb]::new((Join-Path $root 'database.sqlite'),$true)
    try {
        # Relevant table definitions copied from the read-only 0.9.0+2026101401 schema capture.
        foreach($sql in @(
            'DROP TABLE profiles','DROP TABLE scripts','DROP TABLE profile_rule_mapping','DROP TABLE proxy_groups',
            'CREATE TABLE profiles(id INTEGER NOT NULL PRIMARY KEY,type TEXT NOT NULL,label TEXT NOT NULL,current_group_name TEXT,url TEXT NOT NULL,age_secret_key TEXT,last_update_date INTEGER,extend_type TEXT NOT NULL,script_id INTEGER,match_target TEXT,auto_update_duration_millis INTEGER NOT NULL,subscription_info TEXT,auto_update INTEGER NOT NULL,selected_map TEXT NOT NULL,unfold_set TEXT NOT NULL,"order" INTEGER,overrides TEXT)',
            'CREATE TABLE scripts(id INTEGER NOT NULL PRIMARY KEY,label TEXT NOT NULL,last_update_time INTEGER NOT NULL,url TEXT,"order" INTEGER)',
            'CREATE TABLE rules(id INTEGER NOT NULL PRIMARY KEY,profile_id INTEGER,rule_action TEXT NOT NULL,content TEXT,rule_target TEXT,rule_provider TEXT,sub_rule TEXT,no_resolve INTEGER NOT NULL DEFAULT 0,src INTEGER NOT NULL DEFAULT 0,"order" TEXT)',
            'CREATE TABLE proxy_groups(id INTEGER NOT NULL PRIMARY KEY,profile_id INTEGER NOT NULL,name TEXT NOT NULL,type TEXT NOT NULL,proxies TEXT,"use" TEXT,definition TEXT NOT NULL DEFAULT ''{}'',"order" TEXT)'
        )) { $db.Execute($sql) }
        $selection=(Json @{"Proxy's `$fixture"='fixture-node'}).Replace("'","''")
        $db.Execute("INSERT INTO profiles(id,type,label,url,extend_type,auto_update_duration_millis,auto_update,selected_map,unfold_set) VALUES($id,'url','fixture','','standard',0,0,'$selection','[]')")
        $db.Execute('INSERT INTO profiles(id,type,label,url,extend_type,auto_update_duration_millis,auto_update,selected_map,unfold_set,overrides) VALUES(2,''url'',''other fixture'','''',''standard'',0,0,''{}'',''[]'',''{"dns":{"enable":false}}'')')
        $db.Execute("INSERT INTO rules(id,profile_id,rule_action,content,rule_target) VALUES(21,2,'DOMAIN','other-fixture.invalid','DIRECT')")
        $db.Execute('INSERT INTO proxy_groups(id,profile_id,name,type,proxies,definition) VALUES(22,2,''other custom group'',''select'',''["DIRECT"]'',''{"name":"other custom group"}'')')
    } finally { $db.Dispose() }
    return $root
}
function ReadModernUnrelated([string]$root) {
    $db=[FlDns.SqliteDb]::new((Join-Path $root 'database.sqlite'),$false)
    try { return Json @(@($db.Query('SELECT * FROM profiles WHERE id=2'));@($db.Query('SELECT * FROM rules WHERE profile_id=2'));@($db.Query('SELECT * FROM proxy_groups WHERE profile_id=2'))) } finally { $db.Dispose() }
}
function NewLegacyV4Code([string]$group,$policy) {
    # Frozen verified pre-0.9.0 body: compare full contents, never just a marker.
    $legacyV4=@'
// CHUNSON_SWITCH_FLCLASH_DNS_V4
// proxy-group-json: __PROXY_JSON__
const main = (config) => {
  const proxy = __PROXY_JSON__;
  config.tun = config.tun || {};
  config.tun["strict-route"] = true;
  config["find-process-mode"] = "always";
  const protectedProcesses = __PROTECTED_PROCESSES__;
  const directProcesses = __PROCESSES__;
  const directDomains = __DIRECT__;
  const proxyDomains = __FOREIGN__;
  const original = Array.isArray(config.rules) ? config.rules : [];
  const priority = [
    ...protectedProcesses.flatMap(name => [`PROCESS-NAME,${name},${proxy}`, `PROCESS-NAME,${name},REJECT`]),
    ...proxyDomains.flatMap(domain => [`DOMAIN-SUFFIX,${domain},${proxy}`, `DOMAIN-SUFFIX,${domain},REJECT`]),
    ...directProcesses.map(name => `PROCESS-NAME,${name},DIRECT`),
    ...directDomains.map(domain => `DOMAIN-SUFFIX,${domain},DIRECT`),
    ...__GEOSITES__.map(site => `GEOSITE,${site},${proxy}`),
  ];
  const domestic = __DOMESTIC_RULES__;
  const key = rule => rule.split(",").slice(0, 2).join(",").toLowerCase();
  const managed = new Set([...priority, ...domestic].map(key));
  const retained = original.filter(rule => typeof rule === "string" &&
    !/^(MATCH|FINAL),/i.test(rule) && !managed.has(key(rule)));
  config.rules = [...priority, ...retained, ...domestic, `MATCH,${proxy}`];
  return config;
};
'@
    return $legacyV4.Replace('__PROXY_JSON__',(Json $group)).Replace('__PROTECTED_PROCESSES__',(Json $policy.proxyProcesses)).Replace('__PROCESSES__',(Json $policy.directProcesses)).Replace('__DIRECT__',(Json $policy.directDomains)).Replace('__FOREIGN__',(Json $policy.proxyDomains)).Replace('__GEOSITES__',(Json $policy.proxyGeosites)).Replace('__DOMESTIC_RULES__',(Json @("GEOSITE,$($policy.domesticGeosite),DIRECT","GEOIP,CN,DIRECT"))).Replace("`r`n","`n").Trim()+"`n"
}
function CheckUnknownAppProcess([string]$Source,[string]$Label) {
    foreach($name in @('FlClash','FlClashCore')) {
        foreach($pathCase in @('missing','null','throwing')) {
            $result=& {
                param($source,$processName,$unknownPath)
                $tokens=$null; $errors=$null
                $ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
                if($errors.Count -ne 0) { throw 'Cannot extract product functions with syntax errors.' }
                foreach($definition in @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] })) {
                    . ([ScriptBlock]::Create($definition.Extent.Text))
                }
                $state=@{kills=0;starts=0;writes=0}
                $visible=[pscustomobject]@{Name='FlClash';Id=41001;Path='D:\fixture-only\FlClash.exe';StartTime=[DateTime]'2020-01-01';HasExited=$false;State=$state}
                $visible | Add-Member ScriptMethod Kill { $this.State.kills++ }
                $visible | Add-Member ScriptMethod WaitForExit { param($milliseconds) return $true }
                $unknown=[pscustomobject]@{Name=$processName;Id=41002;StartTime=[DateTime]'2020-01-01';HasExited=$false;State=$state}
                if($unknownPath -eq 'null') { $unknown | Add-Member Path $null }
                if($unknownPath -eq 'throwing') { $unknown | Add-Member ScriptProperty Path { throw 'synthetic process path access denied' } }
                $unknown | Add-Member ScriptMethod Kill { $this.State.kills++ }
                $unknown | Add-Member ScriptMethod WaitForExit { param($milliseconds) return $true }
                $fakeProcesses=@($visible,$unknown)
                function Get-Process { param($Name,$Id,$ErrorAction); if($null -ne $Id) { return @($fakeProcesses | Where-Object { $_.Id -eq $Id }) }; return $fakeProcesses }
                function Start-Process { param($FilePath,$WindowStyle); $state.starts++ }
                $applyBlocked=$false; $restoreBlocked=$false; $startBlocked=$false
                try { Stop-App 'D:\fixture-only\FlClash.exe'; $state.writes++ } catch { $applyBlocked=$true }
                try { Stop-App 'D:\fixture-only\FlClash.exe'; $state.writes++ } catch { $restoreBlocked=$true }
                try { Start-App 'D:\fixture-only\FlClash.exe' } catch { $startBlocked=$true }
                return @{blocked=($applyBlocked -and $restoreBlocked -and $startBlocked);kills=$state.kills;starts=$state.starts;writes=$state.writes}
            } $Source $name $pathCase
            Check ($result.blocked -and $result.kills -eq 0 -and $result.starts -eq 0 -and $result.writes -eq 0) ($Label+' visible target plus '+$name+' '+$pathCase+' Path blocks Apply/Restore/start model before any kill write or launch')
        }
    }
}
function ExtendedFixture($group,$providers=$null,$extraNodes=@()) {
    $root=NewFixture @('Extended')
    $runtime=Get-Content -LiteralPath (Join-Path $root 'config.yaml') -Raw | ConvertFrom-Json
    $runtime.'proxy-groups'=@($group); $runtime.proxies=@($runtime.proxies)+@($extraNodes)
    if($null -ne $providers) { $runtime | Add-Member 'proxy-providers' $providers }
    WriteJson (Join-Path $root 'config.yaml') $runtime; WriteJson (Join-Path $root 'profiles/1.yaml') $runtime
    $db=[FlDns.SqliteDb]::new((Join-Path $root 'database.sqlite'),$true)
    try { $db.Execute('UPDATE profiles SET selected_map=''{}''') } finally { $db.Dispose() }
    return $root
}
try {
    foreach($file in @($ScriptPath,(Join-Path (Split-Path -Parent $ScriptPath) '../../windows/ChunsonSpace.Windows.Checks/FlClashDnsScriptChecks.ps1'))) {
        $tokens=$null; $errors=$null
        [Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$errors) | Out-Null
        Check ($errors.Count -eq 0) ('PS5 syntax-only '+[IO.Path]::GetFileName($file))
    }
    $source=[IO.File]::ReadAllText($ScriptPath)
    Check ($source -match "\`$Action = 'Preview'") 'default action is read-only Preview'
    Check (-not $source.Contains('Unblock-File')) 'preview does not unblock a library or modify its zone data'
    Check (-not $source.Contains('Read-Host')) 'all ambiguous selections return candidates without an interactive prompt'
    CheckUnknownAppProcess $ScriptPath 'integrated process guard'
    $yaml=Join-Path $LibraryRoot 'YamlDotNet.dll'
    [Reflection.Assembly]::LoadFrom($yaml) | Out-Null
    Add-Type -Path $HelperPath -ReferencedAssemblies $yaml
    [FlDns.SqliteDb]::Load((Join-Path $env:SystemRoot 'System32/winsqlite3.dll'))
    $root=NewFixture
    $before=Tree $root
    $preview=InvokeRepair $root '' '' '' $true
    Check ($preview.status -eq 'PreviewReady' -and (Tree $root) -ceq $before) 'default Preview preserves every fixture file byte and timestamp'
    Check ($preview.ports.'mixed-port' -eq 7890 -and $preview.selectedGroup -ceq "Proxy's `$fixture") 'preview resolves group with quote and dollar data and preserves port'
    $portRoot=NewFixture; $portRuntime=[IO.File]::ReadAllText((Join-Path $portRoot 'config.yaml')) | ConvertFrom-Json
    $portRuntime | Add-Member 'mixed-port' 7890; WriteJson (Join-Path $portRoot 'config.yaml') $portRuntime
    $unknownPort=InvokeRepair $portRoot 'Preview' '' ''
    Check ($null -eq $unknownPort.proxyPorts[0].bindAddress -and $unknownPort.proxyPorts[0].requiresAuthentication -eq $false) 'missing binding remains unknown and omitted authentication uses Mihomo default'
    $portRuntime | Add-Member 'bind-address' '127.0.0.1'; $portRuntime | Add-Member authentication @(); WriteJson (Join-Path $portRoot 'config.yaml') $portRuntime
    $safePort=InvokeRepair $portRoot 'Preview' '' ''
    Check ($safePort.proxyPorts[0].bindAddress -eq '127.0.0.1' -and $safePort.proxyPorts[0].requiresAuthentication -eq $false) 'explicit runtime loopback and empty authentication yield safe metadata'
    $portRuntime.authentication=@('fixture-user:fixture-node-secret'); WriteJson (Join-Path $portRoot 'config.yaml') $portRuntime
    Check ((InvokeRepair $portRoot 'Preview' '' '').proxyPorts[0].requiresAuthentication -eq $true) 'authenticated port exposes requirement but not username or password'
    $automatic=InvokeRepair $root 'Preview' '' '' $false $true
    Check ($automatic.status -eq 'PreviewReady' -and $automatic.applicationDiscoveryStatus -eq 'Found' -and $automatic.configurationStatus -eq 'Ready' -and $automatic.applicationPath -eq (Join-Path $root 'install/FlClash.exe') -and $automatic.profileId -eq 1 -and (Tree $root) -ceq $before) 'unique known installation and configured group are discovered automatically without data writes'
    $missingRoot=NewFixture; [IO.File]::Delete((Join-Path $missingRoot 'install/FlClash.exe')); $missingBefore=Tree $missingRoot
    $missing=InvokeRepair $missingRoot 'Preview' '' '' $false $true
    Check ($missing.status -eq 'PreviewReady' -and $missing.applicationDiscoveryStatus -eq 'NotFound' -and $missing.configurationStatus -eq 'Ready' -and @($missing.groups).Count -eq 1 -and $missing.selectedGroup -ceq "Proxy's `$fixture" -and $null -eq $missing.applicationPath -and (Tree $missingRoot) -ceq $missingBefore) 'missing application never blocks read-only preferences database and group discovery'
    $invalidApp=InvokeRepair $missingRoot 'Preview' '' '' $false $true ('invalid|path/FlClash.exe')
    Check ($invalidApp.status -eq 'PreviewReady' -and $invalidApp.applicationDiscoveryStatus -eq 'InvalidPath' -and $invalidApp.configurationStatus -eq 'Ready' -and @($invalidApp.groups).Count -eq 1 -and (Tree $missingRoot) -ceq $missingBefore) 'invalid executable path exceptions never block read-only configuration discovery'
    $refuseApply=InvokeRepair $missingRoot 'Apply' '' '' $false $true
    Check ($refuseApply.status -eq 'SelectionRequired' -and $refuseApply.configurationStatus -eq 'Ready' -and @($refuseApply.groups).Count -eq 1 -and (Tree $missingRoot) -ceq $missingBefore) 'apply without application path retains group report and cannot write configuration'
    $refuseRestore=InvokeRepair $missingRoot 'Restore' '' '' $false $true
    Check ($refuseRestore.status -eq 'SelectionRequired' -and @($refuseRestore.groups).Count -eq 1 -and (Tree $missingRoot) -ceq $missingBefore) 'restore without application path retains readable groups without touching backups'
    $ambiguousRoot=NewFixture; [IO.Directory]::CreateDirectory((Join-Path $ambiguousRoot 'alternate-install')) | Out-Null; [IO.File]::WriteAllText((Join-Path $ambiguousRoot 'alternate-install/FlClash.exe'),'inert fixture',$utf8); $ambiguousBefore=Tree $ambiguousRoot
    $ambiguousApp=InvokeRepair $ambiguousRoot 'Preview' '' '' $false $true
    Check ($ambiguousApp.status -eq 'PreviewReady' -and $ambiguousApp.applicationDiscoveryStatus -eq 'Ambiguous' -and @($ambiguousApp.appCandidates).Count -eq 2 -and $ambiguousApp.configurationStatus -eq 'Ready' -and $ambiguousApp.selectedGroup -ceq "Proxy's `$fixture" -and (Tree $ambiguousRoot) -ceq $ambiguousBefore) 'multiple installations preserve configured groups and selected group without choosing an executable'
    $missingPrefs=NewFixture; [IO.File]::Delete((Join-Path $missingPrefs 'shared_preferences.json')); $missingPrefsBefore=Tree $missingPrefs
    $notConfigured=InvokeRepair $missingPrefs 'Preview' '' '' $false $true
    Check ($notConfigured.status -eq 'Failed' -and $notConfigured.configurationStatus -eq 'Missing' -and $notConfigured.message.Contains('偏好设置未找到') -and (Tree $missingPrefs) -ceq $missingPrefsBefore) 'missing preferences are reported separately from missing application'
    $invalidPrefs=NewFixture; [IO.File]::WriteAllText((Join-Path $invalidPrefs 'shared_preferences.json'),'{fixture-node-secret',$utf8); $invalidPrefsBefore=Tree $invalidPrefs
    $invalidConfig=InvokeRepair $invalidPrefs 'Preview' '' '' $false $true
    Check ($invalidConfig.status -eq 'Failed' -and $invalidConfig.configurationStatus -eq 'Invalid' -and (Tree $invalidPrefs) -ceq $invalidPrefsBefore) 'corrupt preferences report a safe configuration error without credential fragments'
    $applied=InvokeRepair $root 'Apply' '' ''
    Check ($applied.status -eq 'SettingsSaved' -and $applied.settingsSaved -and -not $applied.reloadConfirmed -and -not $applied.networkVerified) 'offline apply saves settings without claiming reload or network success'
    Check ([IO.File]::Exists((Join-Path $applied.backupDirectory 'manifest.json')) -and [IO.File]::Exists((Join-Path $applied.backupDirectory 'database.sqlite'))) 'apply retains transactional manifest and SQLite backup'
    $config=ReadConfig $root
    Check ($config.patchClashConfig.tun.'strict-route' -eq $true) 'old-schema Apply persists strict-route true when the global field was originally absent'
    Check ($config.patchClashConfig.dns.nameserver[0] -ceq "https://1.1.1.1/dns-query#Proxy's `$fixture" -and $config.patchClashConfig.'mixed-port' -eq 7890) 'apply consumes shared policy and keeps existing ports'
    $saved=Tree $root
    $again=InvokeRepair $root 'Apply' '' ''
    Check ($again.status -eq 'SettingsSaved' -and $null -eq $again.backupDirectory -and @($again.changedFields).Count -eq 0 -and (Tree $root) -ceq $saved) 'second apply is byte-preserving and creates no duplicate backup'
    & $NodePath $RuntimeFixture $root
    Check ($LASTEXITCODE -eq 0) 'actual managed JavaScript creates generated fixture configuration'
    $beforeVerify=Tree $root; $verify=InvokeRepair $root 'Verify' '' ''
    Check ($verify.status -eq 'GeneratedConfigVerified' -and $verify.generatedConfigVerified -and -not $verify.networkVerified -and (Tree $root) -ceq $beforeVerify) 'generated configuration verification is read-only and separate from network proof'
    $config=ReadConfig $root; $config.appSettingProps | Add-Member userTheme 'after-apply'; $config.patchClashConfig.dns.'nameserver-policy' | Add-Member 'extra.lan' '10.0.0.1'; WriteConfig $root $config
    $restored=InvokeRepair $root 'Restore' '' $applied.backupDirectory
    $config=ReadConfig $root
    Check ($restored.status -eq 'Restored' -and -not $config.patchClashConfig.dns.enable -and $config.appSettingProps.userTheme -eq 'after-apply' -and $config.patchClashConfig.dns.'nameserver-policy'.'extra.lan' -eq '10.0.0.1') 'restore reverses managed fields and preserves unrelated later settings'
    Check ($null -eq $config.patchClashConfig.tun.PSObject.Properties['strict-route']) 'old-schema Restore removes managed global strict-route when the field was originally absent'
    $db=[FlDns.SqliteDb]::new((Join-Path $root 'database.sqlite'),$false)
    try { Check (@($db.Query('SELECT overwrite_type,script_id FROM profiles'))[0]['overwrite_type'] -eq 'standard' -and @($db.Query('SELECT COUNT(*) AS n FROM scripts'))[0]['n'] -eq 0) 'restore reverses script binding and managed row' } finally { $db.Dispose() }
    $guard=NewFixture; $applyGuard=InvokeRepair $guard 'Apply' '' ''; $config=ReadConfig $guard; $config.patchClashConfig.dns.enable=$false; WriteConfig $guard $config; $beforeGuard=Tree $guard
    $refused=InvokeRepair $guard 'Restore' '' $applyGuard.backupDirectory
    Check ($refused.status -eq 'Failed' -and (Tree $guard) -ceq $beforeGuard) 'restore refuses later changes to a managed field without partial writes'
    $scriptGuard=NewFixture; $applyScript=InvokeRepair $scriptGuard 'Apply' '' ''; $managed=@(Get-ChildItem -LiteralPath (Join-Path $scriptGuard 'scripts') -File)[0].FullName; [IO.File]::AppendAllText($managed,"`n// later change",$utf8); $beforeScript=Tree $scriptGuard
    $refused=InvokeRepair $scriptGuard 'Restore' '' $applyScript.backupDirectory
    Check ($refused.status -eq 'Failed' -and (Tree $scriptGuard) -ceq $beforeScript) 'restore refuses changed script hash and preserves later edits'
    $multi=NewFixture @('Alpha','Beta'); $beforeMulti=Tree $multi; $select=InvokeRepair $multi 'Preview' '' ''
    Check ($select.status -eq 'SelectionRequired' -and @($select.groups).Count -eq 2 -and (Tree $multi) -ceq $beforeMulti) 'ambiguous groups return a structured choice without prompt'
    Check ((InvokeRepair $multi 'Preview' 'Beta' '').selectedGroup -eq 'Beta') 'explicit group resolves ambiguity'
    [IO.File]::Delete((Join-Path $multi 'install/FlClash.exe')); $noAppMultiBefore=Tree $multi
    $noAppGroups=InvokeRepair $multi 'Preview' '' '' $false $true
    Check ($noAppGroups.status -eq 'SelectionRequired' -and $noAppGroups.configurationStatus -eq 'Ready' -and $noAppGroups.applicationDiscoveryStatus -eq 'NotFound' -and @($noAppGroups.groups).Count -eq 2 -and $null -eq $noAppGroups.selectedGroup -and (Tree $multi) -ceq $noAppMultiBefore) 'ambiguous groups are returned even when no installation is located'
    $config=ReadConfig $multi; $config.patchClashConfig.dns.nameserver=@('https://1.1.1.1/dns-query#Beta'); WriteConfig $multi $config
    Check ((InvokeRepair $multi 'Preview' '' '' $false $true).selectedGroup -eq 'Beta') 'existing explicit DNS group is selected automatically without an executable'
    $config.patchClashConfig.dns.nameserver=@('8.8.8.8'); WriteConfig $multi $config
    $conflictingRuntime=[IO.File]::ReadAllText((Join-Path $multi 'config.yaml')) | ConvertFrom-Json; $conflictingRuntime.rules=@('MATCH,Alpha','MATCH,Beta'); WriteJson (Join-Path $multi 'config.yaml') $conflictingRuntime
    Check ((InvokeRepair $multi 'Preview' '' '' $false $true).status -eq 'SelectionRequired') 'conflicting MATCH groups cannot silently select the last rule'
    $custom=NewFixture; [IO.File]::WriteAllText((Join-Path $custom 'scripts/44.js'),'const main = config => config; // user custom script',$utf8)
    $db=[FlDns.SqliteDb]::new((Join-Path $custom 'database.sqlite'),$true)
    try { $db.Execute("INSERT INTO scripts VALUES(44,'custom',1)"); $db.Execute("UPDATE profiles SET overwrite_type='script',script_id=44") } finally { $db.Dispose() }
    $customBefore=Tree $custom; $customReport=InvokeRepair $custom 'Preview' '' '' $false $true
    Check ($customReport.status -eq 'Failed' -and $customReport.configurationStatus -eq 'Ready' -and @($customReport.groups).Count -eq 1 -and $customReport.selectedGroup -ceq "Proxy's `$fixture" -and $customReport.message.Contains('自定义脚本') -and (Tree $custom) -ceq $customBefore) 'unsupported custom overwrite preserves already-read groups and selected identity'
    $direct=NewFixture; $db=[FlDns.SqliteDb]::new((Join-Path $direct 'database.sqlite'),$true)
    try { $db.Execute('UPDATE profiles SET selected_map=''{}'''); } finally { $db.Dispose() }
    $raw=[IO.File]::ReadAllText((Join-Path $direct 'config.yaml')) | ConvertFrom-Json; $raw.'proxy-groups'[0].proxies=@('DIRECT'); WriteJson (Join-Path $direct 'config.yaml') $raw
    Check ((InvokeRepair $direct 'Preview' '' '').status -eq 'Failed') 'DIRECT selection cannot become ordinary DNS proxy group'
    $mixed=NewFixture
    $raw=[IO.File]::ReadAllText((Join-Path $mixed 'config.yaml')) | ConvertFrom-Json; $raw.'proxy-groups'[0].proxies=@('fixture-node','DIRECT'); WriteJson (Join-Path $mixed 'config.yaml') $raw
    Check ((InvokeRepair $mixed 'Preview' '' '').status -eq 'Failed') 'selector with unused DIRECT candidate rejected'
    $mixedBefore=Tree $mixed; $nodeChoice=InvokeRepair $mixed 'Preview' '' ''
    Check ($nodeChoice.status -eq 'Failed' -and @($nodeChoice.groups).Count -eq 1 -and $nodeChoice.groups[0] -ceq 'fixture-node' -and $nodeChoice.nodeTargets[0] -ceq 'fixture-node' -and $nodeChoice.selectionNote -and (Tree $mixed) -ceq $mixedBefore) 'unsafe selector exposes concrete subscription node and reason without guessing or writes'
    Check ((InvokeRepair $mixed 'Preview' "Proxy's `$fixture" '').status -eq 'Failed') 'explicit unsafe selector remains rejected after node fallback'
    $nodePreview=InvokeRepair $mixed 'Preview' 'fixture-node' ''
    Check ($nodePreview.status -eq 'PreviewReady' -and $nodePreview.selectedGroup -ceq 'fixture-node' -and (Tree $mixed) -ceq $mixedBefore) 'explicit concrete node previews without deleting DIRECT selector members'
    $nodeApply=InvokeRepair $mixed 'Apply' 'fixture-node' ''
    Check ($nodeApply.status -eq 'SettingsSaved' -and (ReadConfig $mixed).patchClashConfig.dns.nameserver[0].EndsWith('#fixture-node') -and ([IO.File]::ReadAllText((Join-Path $mixed 'config.yaml')) | ConvertFrom-Json).'proxy-groups'[0].proxies -contains 'DIRECT') 'node apply uses chosen outbound and leaves original runtime selector untouched'

    $safeNode=@{name='provider-safe';type='socks5';server='fixture.invalid';port=9;password='fixture-node-secret'}
    $directNode=@{name='provider-direct';type='direct'}
    foreach($flag in @('include-all','include-all-proxies')) {
        $extendedGroup=@{name='Extended';type='select';proxies=@('fixture-node');$flag=$true}
        $expandedDirect=ExtendedFixture $extendedGroup $null @($directNode); $unchanged=Tree $expandedDirect
        Check ((InvokeRepair $expandedDirect 'Preview' '' '').status -eq 'Failed' -and (Tree $expandedDirect) -ceq $unchanged) ($flag+' expands all top-level nodes and refuses hidden direct without writes')
        if($flag -eq 'include-all-proxies') { Check ((InvokeRepair $expandedDirect 'Apply' '' '').status -eq 'Failed' -and (Tree $expandedDirect) -ceq $unchanged) 'apply independently refuses included direct before backup or settings writes' }
        $expandedSafe=ExtendedFixture $extendedGroup $null @($safeNode)
        Check ((InvokeRepair $expandedSafe 'Preview' '' '').status -eq 'PreviewReady') ($flag+' keeps pure proxy group usable')
    }
    foreach($flag in @('use','include-all-providers','include-all')) {
        $extendedGroup=@{name='Extended';type='select';proxies=@('fixture-node')}
        if($flag -eq 'use') { $extendedGroup[$flag]=@('local') } else { $extendedGroup[$flag]=$true }
        $inlineDirect=ExtendedFixture $extendedGroup @{local=@{type='inline';payload=@($safeNode,$directNode)}}
        Check ((InvokeRepair $inlineDirect 'Preview' '' '').status -eq 'Failed') ($flag+' expands inline provider and refuses unused direct candidate')
        $inlineSafe=ExtendedFixture $extendedGroup @{local=@{type='inline';payload=@($safeNode)}}
        Check ((InvokeRepair $inlineSafe 'Preview' '' '').status -eq 'PreviewReady') ($flag+' keeps complete pure inline provider usable')
        $unknownProvider=ExtendedFixture $extendedGroup @{local=@{type='http';path='missing.yaml';interval=3600}}
        Check ((InvokeRepair $unknownProvider 'Preview' '' '').status -eq 'Failed') ($flag+' cannot mark unknown provider safe from explicit proxy members')
    }
    $onlyProvider=ExtendedFixture @{name='Extended';type='select';use=@('local')} @{local=@{type='inline';payload=@($safeNode)}}
    Check ((InvokeRepair $onlyProvider 'Preview' '' '').status -eq 'PreviewReady') 'provider-only pure proxy group remains usable'
    $prefixProvider=ExtendedFixture @{name='Extended';type='select';use=@('local')} @{local=@{type='inline';payload=@($safeNode);override=@{'additional-prefix'='pre-';'additional-suffix'='-post'}}}
    Check ((InvokeRepair $prefixProvider 'Preview' '' '').status -eq 'PreviewReady') 'static provider name prefix and suffix are resolved'
    $cachedProvider=ExtendedFixture @{name='Extended';type='select';use=@('local')} @{local=@{type='http';path='cache.yaml';interval=3600}}
    WriteJson (Join-Path $cachedProvider 'cache.yaml') @{proxies=@($safeNode)}
    Check ((InvokeRepair $cachedProvider 'Preview' '' '').status -eq 'PreviewReady') 'fresh local HTTP provider cache resolves pure proxy group'
    [IO.File]::SetLastWriteTimeUtc((Join-Path $cachedProvider 'cache.yaml'),[DateTime]::UtcNow.AddHours(-2))
    Check ((InvokeRepair $cachedProvider 'Preview' '' '').status -eq 'Failed') 'expired HTTP provider cache cannot establish safe group'
    $dynamicProvider=ExtendedFixture @{name='Extended';type='select';proxies=@('fixture-node');use=@('local')} @{local=@{type='inline';payload=@($safeNode);filter='safe'}}
    Check ((InvokeRepair $dynamicProvider 'Preview' '' '').status -eq 'Failed') 'dynamic provider filtering remains unknown rather than safe'
    $unknownUse=ExtendedFixture @{name='Extended';type='select';proxies=@('fixture-node');use=@('undeclared')}
    Check ((InvokeRepair $unknownUse 'Preview' '' '').status -eq 'Failed') 'undeclared use cannot be hidden by explicit safe member'
    $cyclicExtended=ExtendedFixture @{name='Extended';type='select';proxies=@('Extended');'include-all-proxies'=$true} $null @($safeNode)
    Check ((InvokeRepair $cyclicExtended 'Preview' '' '').status -eq 'Failed') 'expanded group retains recursive cycle rejection'
    $legacy=NewFixture
    $policy=[IO.File]::ReadAllText($PolicyPath) | ConvertFrom-Json
    $template=[IO.File]::ReadAllText((Join-Path (Split-Path -Parent $RuntimeFixture) 'FlClashDnsLegacyV3.txt')).Trim()
    $template=$template.Replace('// CHUNSON_SWITCH_FLCLASH_DNS_V3','// FLCLASH_DNS_SETUP_V2').Replace('__PROCESSES__',(Json $policy.directProcesses)).Replace('__DIRECT__',(Json $policy.directDomains)).Replace('__FOREIGN__',(Json $policy.proxyDomains))
    $oldSites='['+(@($policy.proxyGeosites | ForEach-Object { Json $_ }) -join ', ')+']'
    $oldDomestic='['+(@(("GEOSITE,$($policy.domesticGeosite),DIRECT"),'GEOIP,CN,DIRECT' | ForEach-Object { Json $_ }) -join ', ')+']'
    $template=$template.Replace('__GEOSITES__',$oldSites).Replace('__DOMESTIC_RULES__',$oldDomestic)
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $legacyHash=([BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($template.Replace("`r`n","`n").Trim())))).Replace('-','') } finally { $sha.Dispose() }
    Check ($legacyHash -ceq '1A08C0D87F2B79E94DCDCF797562C78C810DED81BD4E030E955DAC7B4DF73420') 'legacy V2 fixture exactly matches verified canonical template hash'
    $legacyCode=$template.Replace('__PROXY_JSON__',(Json "Proxy's `$fixture"))+"`n"
    [IO.File]::WriteAllText((Join-Path $legacy 'scripts/42.js'),$legacyCode,$utf8)
    $db=[FlDns.SqliteDb]::new((Join-Path $legacy 'database.sqlite'),$true)
    try { $db.Execute("INSERT INTO scripts VALUES(42,'old verified body',1)"); $db.Execute("UPDATE profiles SET overwrite_type='script',script_id=42") } finally { $db.Dispose() }
    $upgrade=InvokeRepair $legacy 'Apply' '' ''
    Check ($upgrade.status -eq 'SettingsSaved' -and [IO.File]::ReadAllText((Join-Path $legacy 'scripts/42.js')).Contains('CHUNSON_SWITCH_FLCLASH_DNS_V4')) 'verified V2 routing body upgrades without trusting only marker'
    $restoredLegacy=InvokeRepair $legacy 'Restore' '' $upgrade.backupDirectory
    Check ($restoredLegacy.status -eq 'Restored' -and [IO.File]::ReadAllText((Join-Path $legacy 'scripts/42.js')) -ceq $legacyCode) 'V2 upgrade restoration preserves exact old script bytes'
    [IO.File]::AppendAllText((Join-Path $legacy 'scripts/42.js'),'// marker cannot authenticate this edit',$utf8)
    $legacyBefore=Tree $legacy
    Check ((InvokeRepair $legacy 'Preview' '' '').status -eq 'Failed' -and (Tree $legacy) -ceq $legacyBefore) 'marker with altered V2 body is rejected without writes'
    $transaction=NewFixture
    $transactionDb=Join-Path $transaction 'database.sqlite'; $prefsBefore=(Get-FileHash -LiteralPath (Join-Path $transaction 'shared_preferences.json')).Hash
    $db=[FlDns.SqliteDb]::new($transactionDb,$true)
    try { $db.Execute("CREATE TRIGGER refuse_managed_script BEFORE INSERT ON scripts BEGIN SELECT RAISE(ABORT,'fixture-write-failure'); END") } finally { $db.Dispose() }
    $failure=InvokeRepair $transaction 'Apply' '' ''
    $db=[FlDns.SqliteDb]::new($transactionDb,$false)
    try { Check ($failure.status -eq 'Failed' -and (Get-FileHash -LiteralPath (Join-Path $transaction 'shared_preferences.json')).Hash -ceq $prefsBefore -and @($db.Query('SELECT COUNT(*) AS n FROM scripts'))[0]['n'] -eq 0 -and @($db.Query('SELECT overwrite_type FROM profiles'))[0]['overwrite_type'] -eq 'standard' -and @(Get-ChildItem -LiteralPath (Join-Path $transaction 'scripts') -File).Count -eq 0) 'SQLite write failure rolls back binding and removes uncommitted script without changing preferences' } finally { $db.Dispose() }
    Check ([IO.File]::Exists((Join-Path $failure.backupDirectory 'manifest.json')) -and ([IO.File]::ReadAllText((Join-Path $failure.backupDirectory 'manifest.json')) | ConvertFrom-Json).status -eq 'failed') 'failed transaction retains backup and explicit failed manifest'
    $strictShadow=NewModernFixture; [IO.File]::WriteAllText((Join-Path $strictShadow 'scripts/42.js'),(NewLegacyV4Code "Proxy's `$fixture" $policy),$utf8)
    & $NodePath $RuntimeFixture $strictShadow
    $strictShadowRuntime=[IO.File]::ReadAllText((Join-Path $strictShadow 'config.yaml')) | ConvertFrom-Json
    Check ($LASTEXITCODE -eq 0 -and $strictShadowRuntime.tun.'strict-route' -eq $false) 'modern script-first generation reproduces global strict-route false overriding a script that sets true'
    $modern=NewModernFixture; $modernBefore=Tree $modern; $modernOther=ReadModernUnrelated $modern
    $modernPreview=InvokeRepair $modern 'Preview' '' ''
    Check ($modernPreview.status -eq 'PreviewReady' -and $modernPreview.profileId -eq 366451883767762944L -and $modernPreview.selectedGroup -ceq "Proxy's `$fixture" -and (Tree $modern) -ceq $modernBefore) '0.9.0 extend_type schema Preview reads exact 64-bit profile identity without writes'
    $modernApply=InvokeRepair $modern 'Apply' '' ''
    Check ($modernApply.status -eq 'SettingsSaved' -and $modernApply.settingsSaved -and -not $modernApply.reloadConfirmed -and -not $modernApply.networkVerified) '0.9.0 Apply saves fixture settings without claiming live acceptance'
    $modernConfig=ReadConfig $modern
    $modernEffectiveGlobals=$modernConfig
    Check ($modernConfig.patchClashConfig.tun.'strict-route' -eq $true -and $modernApply.changedFields -contains 'patchClashConfig.tun.strict-route') 'modern Apply changes explicit global strict-route false to true and records it for Restore'
    Check ($null -eq $modernConfig.PSObject.Properties['overrideDns'] -and @($modernApply.changedFields | Where-Object { $_ -eq 'overrideDns' }).Count -eq 0) '0.9.0 Apply uses DNS override keys without reintroducing removed overrideDns'
    $modernRequiredKeys=@('listen','ipv6','enhanced-mode','fake-ip-filter','nameserver','default-nameserver','proxy-server-nameserver')
    Check (@($modernRequiredKeys | Where-Object { $modernConfig.patchClashConfig.'dns-override-keys' -cnotcontains $_ }).Count -eq 0 -and @($modernConfig.patchClashConfig.'dns-override-keys' | Where-Object { $_ -cin @('enable','respect-rules','prefer-h3','fallback','nameserver-policy') }).Count -eq 0) '0.9.0 Apply uses only supported DNS override keys and preserves pre-existing keys'
    Check ($modernConfig.patchClashConfig.dns.nameserver[0] -ceq "https://1.1.1.1/dns-query#Proxy's `$fixture" -and $modernConfig.patchClashConfig.'mixed-port' -eq 7890) '0.9.0 Apply writes selected encrypted DNS and preserves the existing port'
    $db=[FlDns.SqliteDb]::new((Join-Path $modern 'database.sqlite'),$false)
    try {
        $binding=@($db.Query('SELECT extend_type,script_id FROM profiles WHERE id=366451883767762944'))[0]
        Check ($binding['extend_type'] -eq 'script' -and $null -ne $binding['script_id'] -and @($db.Query('SELECT COUNT(*) AS n FROM scripts'))[0]['n'] -eq 1 -and @($db.Query('PRAGMA table_info(profiles)') | ForEach-Object { $_['name'] }) -notcontains 'overwrite_type') '0.9.0 Apply binds one managed script using extend_type without adding obsolete database columns'
    } finally { $db.Dispose() }
    Check ((ReadModernUnrelated $modern) -ceq $modernOther) '0.9.0 Apply preserves other-profile rules groups overrides and identity'
    $modernSaved=Tree $modern; $modernAgain=InvokeRepair $modern 'Apply' '' ''
    Check ($modernAgain.status -eq 'SettingsSaved' -and $null -eq $modernAgain.backupDirectory -and @($modernAgain.changedFields).Count -eq 0 -and (Tree $modern) -ceq $modernSaved) '0.9.0 second Apply is byte-preserving and produces no additional backup'
    & $NodePath $RuntimeFixture $modern '--global-only'
    Check ($LASTEXITCODE -eq 0) '0.9.0 global-only generation applies the actual supported DNS mask without running managed code'
    $globalRuntime=[IO.File]::ReadAllText((Join-Path $modern 'config.yaml')) | ConvertFrom-Json
    Check ($globalRuntime.dns.enable -and $globalRuntime.dns.'enhanced-mode' -eq 'fake-ip' -and -not $globalRuntime.dns.'respect-rules' -and $globalRuntime.dns.'prefer-h3' -and $globalRuntime.dns.fallback[0] -eq '1.0.0.1' -and $globalRuntime.dns.'nameserver-policy'.'www.baidu.com' -eq '223.5.5.5' -and $globalRuntime.dns.nameserver[0] -ceq "https://1.1.1.1/dns-query#Proxy's `$fixture") '0.9.0 baseline enables DNS and global patch changes allowed nameserver but cannot repair filtered respect-rules fallback or policy fields'
    $globalGenerated=Tree $modern; $globalVerify=InvokeRepair $modern 'Verify' '' ''
    Check ($globalVerify.status -eq 'Failed' -and -not $globalVerify.generatedConfigVerified -and (Tree $modern) -ceq $globalGenerated) '0.9.0 Verify rejects incomplete global-only DNS repair without file writes'
    & $NodePath $RuntimeFixture $modern
    Check ($LASTEXITCODE -eq 0) '0.9.0 managed JavaScript executes in isolated generated configuration fixture'
    $modernRuntime=[IO.File]::ReadAllText((Join-Path $modern 'config.yaml')) | ConvertFrom-Json
    Check ($modernRuntime.tun.'strict-route' -eq $true) 'modern Apply generates effective strict-route true after the global TUN patch'
    Check ($modernRuntime.dns.enable -and $modernRuntime.dns.'respect-rules' -and -not $modernRuntime.dns.'prefer-h3' -and @($modernRuntime.dns.fallback).Count -eq 0 -and $null -eq $modernRuntime.dns.'nameserver-policy'.PSObject.Properties['www.baidu.com'] -and $modernRuntime.dns.'nameserver-policy'.'local.lan' -eq '192.168.1.1') '0.9.0 script repairs fields filtered from the global mask and preserves unrelated private DNS policy'
    $modernGenerated=Tree $modern; $modernVerify=InvokeRepair $modern 'Verify' '' ''
    Check ($modernVerify.status -eq 'GeneratedConfigVerified' -and $modernVerify.generatedConfigVerified -and -not $modernVerify.networkVerified -and (Tree $modern) -ceq $modernGenerated) '0.9.0 generated configuration verification remains read-only and separate from network proof'
    $modernConfig=ReadConfig $modern; $modernConfig.appSettingProps | Add-Member userTheme 'modern-after-apply'; WriteConfig $modern $modernConfig
    $modernRestore=InvokeRepair $modern 'Restore' '' $modernApply.backupDirectory
    $modernConfig=ReadConfig $modern
    Check ($modernConfig.patchClashConfig.tun.'strict-route' -eq $false) 'modern Restore returns the original explicit global strict-route false'
    Check ($modernRestore.status -eq 'Restored' -and -not $modernConfig.patchClashConfig.dns.enable -and $modernConfig.appSettingProps.userTheme -eq 'modern-after-apply' -and $null -eq $modernConfig.PSObject.Properties['overrideDns']) '0.9.0 Restore reverses managed DNS while preserving later settings and absence of overrideDns'
    $db=[FlDns.SqliteDb]::new((Join-Path $modern 'database.sqlite'),$false)
    try { $binding=@($db.Query('SELECT extend_type,script_id FROM profiles WHERE id=366451883767762944'))[0]; Check ($binding['extend_type'] -eq 'standard' -and $null -eq $binding['script_id'] -and @($db.Query('SELECT COUNT(*) AS n FROM scripts'))[0]['n'] -eq 0) '0.9.0 Restore restores standard binding and removes only the created managed script row' } finally { $db.Dispose() }
    Check ((ReadModernUnrelated $modern) -ceq $modernOther -and @(Get-ChildItem -LiteralPath (Join-Path $modern 'scripts') -File).Count -eq 0) '0.9.0 Restore preserves other-profile custom data and removes the managed script file'
    Check ((Json $modernConfig.patchClashConfig.'dns-override-keys') -ceq (Json @('listen','ipv6','enhanced-mode','fake-ip-filter','default-nameserver','nameserver','proxy-server-nameserver'))) '0.9.0 Restore restores the original DNS override key array'
    & $NodePath $RuntimeFixture $modern '--global-only'
    Check ($LASTEXITCODE -eq 0) '0.9.0 restored settings regenerate without a managed script'
    $modernRestoredRuntime=[IO.File]::ReadAllText((Join-Path $modern 'config.yaml')) | ConvertFrom-Json
    Check ($modernRestoredRuntime.tun.'strict-route' -eq $false) 'modern Restore regenerates strict-route false without stale managed script effects'
    Check ($modernRestoredRuntime.dns.enable -and $modernRestoredRuntime.dns.'enhanced-mode' -eq 'fake-ip' -and -not $modernRestoredRuntime.dns.'respect-rules' -and $modernRestoredRuntime.dns.fallback[0] -eq '1.0.0.1' -and $modernRestoredRuntime.dns.'nameserver-policy'.'www.baidu.com' -eq '223.5.5.5') '0.9.0 Restore regenerates original fallback policy and respect-rules with the application baseline instead of retaining managed script effects'
    $oldV4=NewModernFixture; $oldV4Code=NewLegacyV4Code "Proxy's `$fixture" $policy; $oldV4Path=Join-Path $oldV4 'scripts/42.js'
    [IO.File]::WriteAllText($oldV4Path,$oldV4Code,$utf8); WriteConfig $oldV4 $modernEffectiveGlobals
    $db=[FlDns.SqliteDb]::new((Join-Path $oldV4 'database.sqlite'),$true)
    try { $db.Execute("INSERT INTO scripts(id,label,last_update_time) VALUES(42,'verified old V4',1)"); $db.Execute("UPDATE profiles SET extend_type='script',script_id=42 WHERE id=366451883767762944") } finally { $db.Dispose() }
    & $NodePath $RuntimeFixture $oldV4
    Check ($LASTEXITCODE -eq 0) 'verified old V4 body executes before the filtered modern global DNS patch'
    $oldV4Runtime=[IO.File]::ReadAllText((Join-Path $oldV4 'config.yaml')) | ConvertFrom-Json; $oldV4Before=Tree $oldV4; $oldV4Verify=InvokeRepair $oldV4 'Verify' '' ''
    Check ($oldV4Verify.status -eq 'Failed' -and $oldV4Runtime.dns.enable -and -not $oldV4Runtime.dns.'respect-rules' -and $oldV4Runtime.dns.nameserver[0] -ceq "https://1.1.1.1/dns-query#Proxy's `$fixture" -and (Tree $oldV4) -ceq $oldV4Before) 'verified old V4 plus saved global DNS remains incomplete on modern FlClash and Verify rejects it'
    $oldV4Upgrade=InvokeRepair $oldV4 'Apply' '' ''
    Check ($oldV4Upgrade.status -eq 'SettingsSaved' -and [IO.File]::ReadAllText($oldV4Path).Contains('CHUNSON_SWITCH_FLCLASH_DNS_V5') -and [IO.File]::ReadAllText((Join-Path $oldV4Upgrade.backupDirectory 'original-script.js')) -ceq $oldV4Code) 'modern Apply upgrades the exact verified V4 body to V5 and backs up its original bytes'
    & $NodePath $RuntimeFixture $oldV4
    Check ($LASTEXITCODE -eq 0 -and (InvokeRepair $oldV4 'Verify' '' '').status -eq 'GeneratedConfigVerified') 'upgraded V4 to V5 produces a complete modern DNS configuration in actual filter order'
    $oldV4Restore=InvokeRepair $oldV4 'Restore' '' $oldV4Upgrade.backupDirectory
    Check ($oldV4Restore.status -eq 'Restored' -and [IO.File]::ReadAllText($oldV4Path) -ceq $oldV4Code) 'modern V4 upgrade Restore preserves exact old body bytes'
    [IO.File]::AppendAllText($oldV4Path,'// marker cannot authenticate edited V4',$utf8); $oldV4Edited=Tree $oldV4
    Check ((InvokeRepair $oldV4 'Preview' '' '').status -eq 'Failed' -and (Tree $oldV4) -ceq $oldV4Edited) 'edited V4 body is rejected without writes despite retaining its verified marker'
    $modernBindingGuard=NewModernFixture; $modernBindingApply=InvokeRepair $modernBindingGuard 'Apply' '' ''
    $db=[FlDns.SqliteDb]::new((Join-Path $modernBindingGuard 'database.sqlite'),$true)
    try { $db.Execute("UPDATE profiles SET extend_type='standard' WHERE id=366451883767762944") } finally { $db.Dispose() }
    $modernBindingBefore=Tree $modernBindingGuard; $modernBindingRestore=InvokeRepair $modernBindingGuard 'Restore' '' $modernBindingApply.backupDirectory
    Check ($modernBindingRestore.status -eq 'Failed' -and (Tree $modernBindingGuard) -ceq $modernBindingBefore) '0.9.0 Restore refuses a later extend_type binding change without partial writes'
    foreach($case in @(
        @{name='custom profile with active overrides';sql='UPDATE profiles SET type=''custom'',overrides=''{"dns":{"nameserver":["8.8.8.8"]}}'' WHERE id=366451883767762944'},
        @{name='custom profile without an override payload';sql="UPDATE profiles SET type='custom' WHERE id=366451883767762944"},
        @{name='unknown profile type';sql="UPDATE profiles SET type='remote' WHERE id=366451883767762944"},
        @{name='current-profile appended rule';sql="INSERT INTO rules(id,profile_id,rule_action,content,rule_target) VALUES(31,366451883767762944,'DOMAIN','custom-fixture.invalid','DIRECT')"},
        @{name='global appended rule';sql="INSERT INTO rules(id,profile_id,rule_action,content,rule_target) VALUES(32,NULL,'DOMAIN','global-fixture.invalid','DIRECT')"},
        @{name='current-profile custom proxy group';sql="INSERT INTO proxy_groups(id,profile_id,name,type,definition) VALUES(33,366451883767762944,'custom current group','select','{}')"}
    )) {
        $modernGuard=NewModernFixture; $db=[FlDns.SqliteDb]::new((Join-Path $modernGuard 'database.sqlite'),$true)
        try { $db.Execute($case.sql) } finally { $db.Dispose() }
        $modernGuardBefore=Tree $modernGuard
        $modernGuardPreview=InvokeRepair $modernGuard 'Preview' '' ''
        Check ($modernGuardPreview.status -eq 'Failed' -and $modernGuardPreview.configurationStatus -eq 'Ready' -and @($modernGuardPreview.groups).Count -eq 1 -and (Tree $modernGuard) -ceq $modernGuardBefore) ('0.9.0 Preview rejects '+$case.name+' while preserving discovered groups and all bytes')
        $modernGuardApply=InvokeRepair $modernGuard 'Apply' '' ''
        Check ($modernGuardApply.status -eq 'Failed' -and (Tree $modernGuard) -ceq $modernGuardBefore) ('0.9.0 Apply independently rejects '+$case.name+' before backups and writes')
    }
    $modernCustom=NewModernFixture; [IO.File]::WriteAllText((Join-Path $modernCustom 'scripts/44.js'),'const main = config => config; // user modern custom script',$utf8)
    $db=[FlDns.SqliteDb]::new((Join-Path $modernCustom 'database.sqlite'),$true)
    try { $db.Execute("INSERT INTO scripts(id,label,last_update_time,url) VALUES(44,'custom',1,'https://fixture.invalid/custom')"); $db.Execute("UPDATE profiles SET extend_type='script',script_id=44 WHERE id=366451883767762944") } finally { $db.Dispose() }
    $modernCustomBefore=Tree $modernCustom
    Check ((InvokeRepair $modernCustom 'Preview' '' '').status -eq 'Failed' -and (Tree $modernCustom) -ceq $modernCustomBefore) '0.9.0 Preview rejects and preserves a bound custom script and its database metadata'
    Check ((InvokeRepair $modernCustom 'Apply' '' '').status -eq 'Failed' -and (Tree $modernCustom) -ceq $modernCustomBefore) '0.9.0 Apply cannot overwrite a bound custom script'
    foreach($inactive in @(
        @{type='url';payload='{"dns":{"nameserver":["8.8.8.8"]}}'},
        @{type='file';payload='invalid inactive override'}
    )) {
        $inactiveRoot=NewModernFixture; $inactiveCode='const main = config => config; // inactive user script'; $inactivePath=Join-Path $inactiveRoot 'scripts/44.js'; [IO.File]::WriteAllText($inactivePath,$inactiveCode,$utf8)
        $db=[FlDns.SqliteDb]::new((Join-Path $inactiveRoot 'database.sqlite'),$true)
        try {
            $db.Execute("INSERT INTO scripts(id,label,last_update_time,url) VALUES(44,'inactive user',1,'https://fixture.invalid/inactive')")
            $db.Execute("UPDATE profiles SET type='$($inactive.type)',overrides='$($inactive.payload)',script_id=44 WHERE id=366451883767762944")
            $inactiveScriptRow=Json @($db.Query('SELECT * FROM scripts WHERE id=44'))
        } finally { $db.Dispose() }
        $inactiveBefore=Tree $inactiveRoot; $inactivePreview=InvokeRepair $inactiveRoot 'Preview' '' ''
        Check ($inactivePreview.status -eq 'PreviewReady' -and (Tree $inactiveRoot) -ceq $inactiveBefore) ('0.9.0 '+$inactive.type+' Preview preserves inactive overrides and script without parsing or execution')
        $inactiveApply=InvokeRepair $inactiveRoot 'Apply' '' ''
        $db=[FlDns.SqliteDb]::new((Join-Path $inactiveRoot 'database.sqlite'),$false)
        try {
            $inactiveBinding=@($db.Query('SELECT extend_type,script_id,overrides FROM profiles WHERE id=366451883767762944'))[0]
            Check ($inactiveApply.status -eq 'SettingsSaved' -and $inactiveBinding['extend_type'] -eq 'script' -and $inactiveBinding['script_id'] -ne 44 -and $inactiveBinding['overrides'] -ceq $inactive.payload -and (Json @($db.Query('SELECT * FROM scripts WHERE id=44'))) -ceq $inactiveScriptRow -and [IO.File]::ReadAllText($inactivePath) -ceq $inactiveCode) ('0.9.0 '+$inactive.type+' Apply preserves inactive override bytes and custom script metadata while binding a separate managed script')
        } finally { $db.Dispose() }
        $inactiveRestore=InvokeRepair $inactiveRoot 'Restore' '' $inactiveApply.backupDirectory
        $db=[FlDns.SqliteDb]::new((Join-Path $inactiveRoot 'database.sqlite'),$false)
        try {
            $inactiveBinding=@($db.Query('SELECT extend_type,script_id,overrides FROM profiles WHERE id=366451883767762944'))[0]
            Check ($inactiveRestore.status -eq 'Restored' -and $inactiveBinding['extend_type'] -eq 'standard' -and $inactiveBinding['script_id'] -eq 44 -and $inactiveBinding['overrides'] -ceq $inactive.payload -and (Json @($db.Query('SELECT * FROM scripts WHERE id=44'))) -ceq $inactiveScriptRow -and @($db.Query('SELECT COUNT(*) AS n FROM scripts'))[0]['n'] -eq 1 -and [IO.File]::ReadAllText($inactivePath) -ceq $inactiveCode -and @(Get-ChildItem -LiteralPath (Join-Path $inactiveRoot 'scripts') -File).Count -eq 1) ('0.9.0 '+$inactive.type+' Restore restores the inactive binding and preserves only its original script and overrides')
        } finally { $db.Dispose() }
    }
    $migrated=NewFixture; $beforeMigration=ReadConfig $migrated; $migrationApply=InvokeRepair $migrated 'Apply' '' ''
    Check ($migrationApply.status -eq 'SettingsSaved') 'legacy schema Apply creates a transactional backup for migration fixture'
    $manifestPath=Join-Path $migrationApply.backupDirectory 'manifest.json'; $newManifest=[IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    # Preserve the exact old v2 manifest contract rather than relying on new metadata.
    $legacyManifest=[ordered]@{toolVersion=2}
    foreach($field in @('status','dataDir','profileId','proxyGroup','ports','changes','scriptChanged','scriptReplaced','oldScriptHash','oldScriptTime','newScriptUpdateTime','newScriptId','newScriptHash','oldScriptId')) { $legacyManifest[$field]=$newManifest.$field }
    $legacyManifest.oldOverwriteType='standard'; WriteJson $manifestPath $legacyManifest
    $db=[FlDns.SqliteDb]::new((Join-Path $migrated 'database.sqlite'),$true)
    try {
        foreach($sql in @('ALTER TABLE profiles RENAME COLUMN overwrite_type TO extend_type','ALTER TABLE profiles ADD COLUMN type TEXT NOT NULL DEFAULT ''url''','ALTER TABLE profiles ADD COLUMN overrides TEXT','DROP TABLE profile_rule_mapping','CREATE TABLE rules(id INTEGER PRIMARY KEY,profile_id INTEGER,rule_action TEXT NOT NULL)')) { $db.Execute($sql) }
    } finally { $db.Dispose() }
    $migratedConfig=ReadConfig $migrated; $migratedConfig.PSObject.Properties.Remove('overrideDns')
    $normalDnsKeys=@('listen','ipv6','enhanced-mode','fake-ip-filter','fake-ip-filter-mode','default-nameserver','nameserver','proxy-server-nameserver','direct-nameserver')
    $migratedConfig.patchClashConfig.'dns-override-keys'=@($migratedConfig.patchClashConfig.'dns-override-keys' | Where-Object { $_ -cin $normalDnsKeys })
    $migratedConfig.appSettingProps | Add-Member userTheme 'after-schema-migration'; WriteConfig $migrated $migratedConfig
    $migrationRestore=InvokeRepair $migrated 'Restore' '' $migrationApply.backupDirectory; $migratedConfig=ReadConfig $migrated
    $migratedDns=$migratedConfig.patchClashConfig.dns; $beforeMigrationDns=$beforeMigration.patchClashConfig.dns
    Check ($migrationRestore.status -eq 'Restored' -and -not $migratedDns.enable -and -not $migratedDns.'respect-rules' -and $migratedDns.'prefer-h3' -and (Json $migratedDns.nameserver) -ceq (Json $beforeMigrationDns.nameserver) -and (Json $migratedDns.fallback) -ceq (Json $beforeMigrationDns.fallback) -and $migratedDns.'nameserver-policy'.'www.baidu.com' -eq $beforeMigrationDns.'nameserver-policy'.'www.baidu.com' -and $migratedDns.'nameserver-policy'.'local.lan' -eq '192.168.1.1' -and @($migratedDns.'nameserver-policy'.PSObject.Properties | ForEach-Object { $_.Name }).Count -eq 2 -and $null -eq $migratedConfig.PSObject.Properties['overrideDns'] -and $migratedConfig.appSettingProps.userTheme -eq 'after-schema-migration') 'old v2 backup restores managed DNS across schema migration while preserving removed fields and later settings'
    Check (@($migratedConfig.patchClashConfig.'dns-override-keys').Count -eq 0) 'old v2 backup restores legacy overrideDns false as the empty modern DNS override mask'
    $db=[FlDns.SqliteDb]::new((Join-Path $migrated 'database.sqlite'),$false)
    try { $binding=@($db.Query('SELECT extend_type,script_id FROM profiles WHERE id=1'))[0]; Check ($binding['extend_type'] -eq 'standard' -and $null -eq $binding['script_id'] -and @($db.Query('SELECT COUNT(*) AS n FROM scripts'))[0]['n'] -eq 0) 'old v2 backup restores standard binding on extend_type and removes created script after migration' } finally { $db.Dispose() }
    Check (@(Get-ChildItem -LiteralPath (Join-Path $migrated 'scripts') -File).Count -eq 0 -and [IO.File]::ReadAllText($manifestPath).Contains('"status":"restored"')) 'old v2 restore removes created script bytes and marks its manifest restored'
    [Console]::WriteLine("$checks FlClash PS5 fixture checks passed. No live data, processes, elevation, or network actions.")
} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }
