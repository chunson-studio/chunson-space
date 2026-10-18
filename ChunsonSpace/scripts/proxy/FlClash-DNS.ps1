[CmdletBinding()]
param(
    [ValidateSet('Apply','Preview','Restore','Verify')][string]$Action = 'Preview',
    [string]$DataDir = (Join-Path $env:APPDATA 'com.follow\clash'),
    [string]$AppPath,
    [string]$ProxyGroup,
    [string]$BackupDir,
    [switch]$NoRestart,
    [switch]$AllSubscriptions,
    [switch]$Offline,
    [switch]$NonInteractive,
    [string]$ReportPath,
    [string]$PolicyPath,
    [string]$LibraryRoot,
    [string]$CsHelperPath,
    [string]$AssetRoot = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module -Name (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force
Import-Module -Name (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Management\Microsoft.PowerShell.Management.psd1') -Force
Set-StrictMode -Version 2
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$script:Utf8 = [Text.UTF8Encoding]::new($false)
$script:SelectionNeeded=$false
$script:SafeFailureMessage='操作未完成，保留原配置与可用恢复材料；请检查安装路径、配置结构或后续修改。'
$script:Report=[ordered]@{schemaVersion=1;action=$Action;status='Uncertain';groups=@();appCandidates=@();applicationPath=$null;applicationDiscoveryStatus='Unknown';configurationStatus='NotRead';profileId=$null;selectedGroup=$null;ports=@{};proxyPorts=@();changedFields=@();backupDirectory=$null;settingsSaved=$false;reloadConfirmed=$false;generatedConfigVerified=$false;networkVerified=$false;exitCode=1;message=''}
$script:ManagedTemplate = @'
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
function Initialize-Policy([string]$Path) {
    $policy=Get-Json $Path
    if($policy.schemaVersion -ne 1) { throw 'DNS 政策版本不兼容。' }
    foreach($key in @('directDomains','directProcesses','proxyProcesses','proxyDomains','proxyGeosites','proxyDns','tunDnsHijack')) {
        $property=$policy.PSObject.Properties[$key]
        $values=$null; if($null -ne $property) { $values=$property.Value }
        if($values -isnot [array] -or @($values).Count -eq 0) { throw 'DNS 政策缺少必需数组。' }
        foreach($value in $values) { if($value -isnot [string] -or -not $value -or $value -match '[,\r\n]') { throw 'DNS 政策字段无效。' } }
    }
    if($policy.domesticDns -isnot [string] -or $policy.domesticDns -notmatch '^https://[^#]+#DIRECT$' -or $policy.domesticGeosite -notmatch '^[A-Za-z0-9_-]+$') { throw '国内 DNS 政策无效。' }
    foreach($resolver in $policy.proxyDns) { if($resolver -notmatch '^https://[^#]+$') { throw '代理 DNS 政策无效。' } }
    $script:DirectDomains=@($policy.directDomains); $script:DirectProcesses=@($policy.directProcesses)
    $script:ProxyProcesses=@($policy.proxyProcesses); $script:ProxyDomains=@($policy.proxyDomains); $script:ProxyGeosites=@($policy.proxyGeosites)
    $script:DomesticDns=$policy.domesticDns; $script:ProxyDns=@($policy.proxyDns)
    $script:TunDnsHijack=@($policy.tunDnsHijack); $script:DomesticGeosite=$policy.domesticGeosite
    $script:DomesticPolicyKey='geosite:'+$script:DomesticGeosite.ToLowerInvariant()
    $script:ManagedTemplate=$script:ManagedTemplate.Replace('__PROTECTED_PROCESSES__',(Json $script:ProxyProcesses)).Replace('__PROCESSES__',(Json $script:DirectProcesses)).Replace('__DIRECT__',(Json $script:DirectDomains)).Replace('__FOREIGN__',(Json $script:ProxyDomains)).Replace('__GEOSITES__',(Json $script:ProxyGeosites)).Replace('__DOMESTIC_RULES__',(Json @("GEOSITE,$($script:DomesticGeosite),DIRECT","GEOIP,CN,DIRECT")))
}
function New-ManagedCode([string]$Group,[bool]$Modern=$false) {
    $template=$script:ManagedTemplate.Replace("`r`n","`n")
    if($Modern) {
        # Modern file/url profiles filter global DNS overrides; the script runs
        # first and supplies only the fields their normalProfileKeys exclude.
        $overlay=@'
  config.dns = config.dns || {};
  config.dns.enable = true;
  config.dns["respect-rules"] = true;
  config.dns["prefer-h3"] = false;
  config.dns.fallback = [];
  const dnsPolicy = { ...(config.dns["nameserver-policy"] || {}) };
  delete dnsPolicy["www.baidu.com"];
  for (const key of __DNS_POLICY_KEYS__) dnsPolicy[key] = __DOMESTIC_DNS__;
  config.dns["nameserver-policy"] = dnsPolicy;
'@
        $overlay=$overlay.Replace('__DNS_POLICY_KEYS__',(Json (@($script:DomesticPolicyKey)+@($script:DirectDomains | ForEach-Object { '+.'+$_ })))).Replace('__DOMESTIC_DNS__',(Json $script:DomesticDns))
        $template=$template.Replace('// CHUNSON_SWITCH_FLCLASH_DNS_V4','// CHUNSON_SWITCH_FLCLASH_DNS_V5').Replace('  const proxy = __PROXY_JSON__;',"  const proxy = __PROXY_JSON__;`n"+$overlay.Replace("`r`n","`n"))
    }
    return $template.Replace('__PROXY_JSON__',(Json $Group)).Trim()+"`n"
}
function Test-OwnedCode([string]$Code) {
    $canonical=$Code.Replace("`r`n","`n").Trim()
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $hash=([BitConverter]::ToString($sha.ComputeHash($script:Utf8.GetBytes($canonical)))).Replace('-','') } finally { $sha.Dispose() }
    # Exact legacy routing body, never trust a marker comment alone.
    if($hash -ceq 'DC13A745713CF4E085B18D30878E342ACDC259B7CF7D3215E15A5179ADA36156') { return $true }
    if($canonical -match '(?m)^// proxy-group-json: (.+)$') {
        try {
            $group=ConvertFrom-Json $Matches[1]
            if($group -is [string] -and $group) {
                if($canonical -ceq (New-ManagedCode $group).Trim() -or $canonical -ceq (New-ManagedCode $group $true).Trim()) { return $true }
                # The verified V2 body may have different policy lists. Normalize only
                # its two matching group literals, then require the complete known hash.
                $encoded=Json $group
                if($canonical.Contains('  const proxy = '+$encoded+';')) {
                    $normalized=$canonical.Replace('// proxy-group-json: '+$encoded,'// proxy-group-json: __PROXY_JSON__').Replace('  const proxy = '+$encoded+';','  const proxy = __PROXY_JSON__;')
                    $sha=[Security.Cryptography.SHA256]::Create()
                    try { $v2=([BitConverter]::ToString($sha.ComputeHash($script:Utf8.GetBytes($normalized)))).Replace('-','') } finally { $sha.Dispose() }
                    if($v2 -cin @('1A08C0D87F2B79E94DCDCF797562C78C810DED81BD4E030E955DAC7B4DF73420','A3CBDC43EE4B411BB19209C437F0F33853BA6B6A3B9D1B2FD68279D04FC01A39')) { return $true }
                }
            }
        } catch { return $false }
    }
    return $false
}

function Get-Json([string]$Path) {
    return [IO.File]::ReadAllText($Path) | ConvertFrom-Json
}
function Json($Value) { return ConvertTo-Json -InputObject $Value -Depth 100 -Compress }
function Set-Value($Obj,[string]$Key,$Value) {
    if ($null -eq $Obj) { throw "缺少配置对象：$Key" }
    $Obj | Add-Member -MemberType NoteProperty -Name $Key -Value $Value -Force
}
function Get-Value($Obj,[string]$Key) {
    if ($null -eq $Obj) { return $null }
    $property=$Obj.PSObject.Properties[$Key]
    if ($null -ne $property) { return $property.Value }
    return $null
}
function Get-RuntimeProxyPorts($Runtime) {
    # Mihomo defaults to no authentication when the field is omitted.
    # Explicit malformed/null fields and unknown bindings remain unconfirmed.
    $bind=Get-Value $Runtime 'bind-address'; $ip=$null
    if($bind -isnot [string] -or ($bind -cne '*' -and -not [Net.IPAddress]::TryParse($bind,[ref]$ip))) { $bind=$null }
    $authentication=$Runtime.PSObject.Properties['authentication']; $requiresAuthentication=$null
    if($null -eq $authentication) { $requiresAuthentication=$false }
    elseif($authentication.Value -is [array]) { $requiresAuthentication=@($authentication.Value).Count -gt 0 }
    foreach($kind in @('mixed','http')) {
        $field='mixed-port'; if($kind -eq 'http') { $field='port' }
        $port=0
        if([int]::TryParse([string](Get-Value $Runtime $field),[ref]$port) -and $port -gt 0 -and $port -le 65535) {
            [ordered]@{kind=$kind;port=$port;bindAddress=$bind;requiresAuthentication=$requiresAuthentication}
        }
    }
}
function Get-Slot($Obj,[string]$Path) {
    $parts=$Path.Split('.')
    $parent=$Obj
    for($i=0;$i -lt $parts.Length-1;$i++) {
        $parent=Get-Value $parent $parts[$i]
        if ($null -eq $parent) { throw "配置结构不兼容：$Path" }
    }
    $key=$parts[-1]
    $p=$parent.PSObject.Properties[$key]
    $value=$null
    if($null -ne $p) { $value=$p.Value }
    return @{Parent=$parent;Key=$key;Exists=($null -ne $p);Value=$value}
}
function Put-Change($Config,$Changes,[string]$Path,$Value,[bool]$Remove=$false) {
    $slot=Get-Slot $Config $Path
    if ($Remove -and -not $slot.Exists) { return }
    if (-not $Remove -and $slot.Exists -and (Json $slot.Value) -ceq (Json $Value)) { return }
    $Changes.Add([pscustomobject]@{Path=$Path;BeforeExists=$slot.Exists;Before=$slot.Value;AfterExists=(-not $Remove);After=$Value}) | Out-Null
    if($Remove) { $slot.Parent.PSObject.Properties.Remove($slot.Key) } else { Set-Value $slot.Parent $slot.Key $Value }
}
function Write-Atomic([string]$Path,[string]$Content) {
    Write-AtomicBytes $Path ($script:Utf8.GetBytes($Content))
}
function Write-AtomicBytes([string]$Path,[byte[]]$Content) {
    $temp=$Path+'.dns-'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [IO.File]::WriteAllBytes($temp,$Content)
        if([IO.File]::Exists($Path)) { [IO.File]::Replace($temp,$Path,[NullString]::Value) }
        else { [IO.File]::Move($temp,$Path) }
    } finally { if([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
}
function Read-Yaml([string]$Path) {
    return [FlDns.YamlReader]::ToJson([IO.File]::ReadAllText($Path)) | ConvertFrom-Json
}
function Find-App {
    try {
    $choices=New-Object 'System.Collections.Generic.List[string]'
    if($AppPath) {
        $path=[IO.Path]::GetFullPath($AppPath)
        if((Split-Path -Leaf $path) -ine 'FlClash.exe' -or ($Offline -and -not $path.StartsWith($DataDir+'\',[StringComparison]::OrdinalIgnoreCase))) {
            $script:Report.applicationDiscoveryStatus='InvalidPath'; return $null
        }
        $choices.Add($path)
    } elseif($Offline) {
        # Fixture-only discovery; inert candidates are bounded to the marked root.
        foreach($folder in @('install','alternate-install')) { $choices.Add((Join-Path $DataDir "$folder/FlClash.exe")) }
    } else {
        foreach($proc in @(Get-Process -Name 'FlClash' -ErrorAction SilentlyContinue)) { if($proc.Path) { $choices.Add($proc.Path) } }
        foreach($base in @($env:ProgramFiles,${env:ProgramFiles(x86)},$env:LOCALAPPDATA)) { if($base) { $choices.Add((Join-Path $base 'FlClash\FlClash.exe')) } }
    }
    $found=@($choices | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Sort-Object -Unique)
    $script:Report.appCandidates=$found
    if($found.Count -eq 1) {
        try { Assert-NoLinks $found[0] } catch { $script:Report.applicationDiscoveryStatus='InvalidPath'; return $null }
        $script:Report.applicationPath=(Get-Item -LiteralPath $found[0]).FullName
        $script:Report.applicationDiscoveryStatus='Found'
        return $script:Report.applicationPath
    }
    $script:Report.applicationDiscoveryStatus='NotFound'
    if($found.Count -gt 1) { $script:Report.applicationDiscoveryStatus='Ambiguous' }
    if($AppPath) { $script:Report.applicationDiscoveryStatus='InvalidPath' }
    return $null
    } catch {
        $script:Report.applicationPath=$null
        $script:Report.applicationDiscoveryStatus='Unknown'
        if($AppPath) { $script:Report.applicationDiscoveryStatus='InvalidPath' }
        return $null
    }
}
function Require-ConfigurationFile([string]$Path,[string]$Label) {
    try { $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite); $stream.Dispose() }
    catch {
        $script:Report.configurationStatus='Invalid'; $script:SafeFailureMessage=$Label+'不可读，请检查文件权限或配置结构。'
        $cause=$_.Exception.GetBaseException()
        if($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) {
            $script:Report.configurationStatus='Missing'; $script:SafeFailureMessage=$Label+'未找到，请先在 FlClash 中导入订阅并选择配置档。'
        }
        throw
    }
}
function Fail-Plan([string]$Message) { $script:SafeFailureMessage=$Message; throw $Message }
function Assert-Schema($Db) {
    $profileColumns=@($Db.Query('PRAGMA table_info(profiles)') | ForEach-Object { $_['name'] })
    $modern=$profileColumns -contains 'extend_type'
    if($modern -eq ($profileColumns -contains 'overwrite_type')) { throw '数据库覆写字段缺失或冲突，未修改文件。' }
    $typeColumn='overwrite_type'; $ruleTable='profile_rule_mapping'
    if($modern) { $typeColumn='extend_type'; $ruleTable='rules' }
    $expected=@{
        profiles=@('id',$typeColumn,'script_id','selected_map');
        scripts=@('id','label','last_update_time');
        proxy_groups=@('profile_id')
    }
    $expected[$ruleTable]=@('profile_id')
    if($modern) { $expected.profiles+=@('type','overrides') }
    foreach($table in $expected.Keys) {
        $columns=@($Db.Query("PRAGMA table_info($table)") | ForEach-Object { $_['name'] })
        foreach($col in $expected[$table]) { if($columns -notcontains $col) { throw "数据库结构不兼容：$table/$col。未修改文件。" } }
    }
    if(@($Db.Query('PRAGMA quick_check'))[0]['quick_check'] -ne 'ok') { throw '数据库检查不通过，未修改文件。' }
    return @{TypeColumn=$typeColumn;RuleTable=$ruleTable;Modern=$modern}
}
function Get-ProfileBinding($Db,$Schema,[long]$Id) {
    # Alias the renamed column only in memory; never migrate the user's database.
    $extra=''
    if($Schema.Modern) { $extra=',type AS profile_type,overrides' }
    $rows=@($Db.Query("SELECT id,$($Schema.TypeColumn) AS overwrite_type,script_id,selected_map$extra FROM profiles WHERE id=$Id"))
    if($rows.Count -ne 1) { throw '找不到当前配置档。' }
    return $rows[0]
}
function Set-ManagedTunChanges($Config,$Changes,[bool]$MergeHijack=$false) {
    Put-Change $Config $Changes 'patchClashConfig.tun.enable' $true
    $mask=Get-Slot $Config 'patchClashConfig.tun-override-keys'
    $path='patchClashConfig.tun'
    if($mask.Exists) {
        $override=Get-Slot $Config 'patchClashConfig.tun-override'
        if($mask.Value -isnot [array] -or @($mask.Value | Where-Object { $_ -isnot [string] }).Count -gt 0 -or
            -not $override.Exists -or $override.Value -isnot [pscustomobject]) {
            Fail-Plan 'TUN 覆写结构不兼容，未修改文件。'
        }
        $path='patchClashConfig.tun-override'
        $keys=@($mask.Value)
        foreach($key in @('strict-route','dns-hijack')) { if($keys -cnotcontains $key) { $keys+=$key } }
        Put-Change $Config $Changes 'patchClashConfig.tun-override-keys' $keys
        # New clients persist advanced fields here; coreJson owns auto-route.
    } else {
        Put-Change $Config $Changes 'patchClashConfig.tun.auto-route' $true
    }
    Put-Change $Config $Changes ($path+'.strict-route') $true
    $hijack=@($script:TunDnsHijack)
    if($MergeHijack) {
        $slot=Get-Slot $Config ($path+'.dns-hijack')
        $hijack=@($slot.Value | Where-Object { $null -ne $_ })
        foreach($entry in $script:TunDnsHijack) { if($hijack -cnotcontains $entry) { $hijack+=$entry } }
    }
    Put-Change $Config $Changes ($path+'.dns-hijack') $hijack
}
function Get-MigratedDnsKeys($Config,[bool]$Enabled) {
    if(-not $Enabled) { return ,@() }
    # Official Config migration intersects legacy keys with normalProfileKeys.
    $normal=@('listen','ipv6','enhanced-mode','fake-ip-filter','fake-ip-filter-mode','default-nameserver','nameserver','proxy-server-nameserver','direct-nameserver')
    $slot=Get-Slot $Config 'patchClashConfig.dns-override-keys'
    $keys=@('listen','ipv6','enhanced-mode','fake-ip-filter','default-nameserver','nameserver','proxy-server-nameserver')
    if($slot.Exists) {
        if($slot.Value -isnot [array]) { throw '备份 DNS 覆写字段列表不兼容。' }
        $keys=@($slot.Value)
    }
    return ,@($keys | Where-Object { $_ -cin $normal } | Sort-Object -CaseSensitive -Unique)
}
function Restore-MigratedDnsFlag($Config,$Meta,[string]$Backup) {
    if($null -ne $Config.PSObject.Properties['overrideDns']) { return $false }
    $mask=Get-Slot $Config 'patchClashConfig.dns-override-keys'
    if(-not $mask.Exists) { return $false }
    $flags=@($Meta.changes | Where-Object { $_.Path -ceq 'overrideDns' })
    if($flags.Count -eq 0) { return $false }
    # A retired boolean is restored as the corresponding key set, without
    # resurrecting an ignored setting or overwriting a user's later mask edit.
    if($flags.Count -ne 1 -or $flags[0].After -isnot [bool] -or $mask.Value -isnot [array]) { throw '迁移后的 DNS 备份结构不兼容。' }
    $before=(Get-Json (Join-Path $Backup 'shared_preferences.json')).'flutter.config' | ConvertFrom-Json
    $after=(Json $before) | ConvertFrom-Json
    $maskChanges=@($Meta.changes | Where-Object { $_.Path -ceq 'patchClashConfig.dns-override-keys' })
    if($maskChanges.Count -gt 1) { throw '迁移后的 DNS 备份结构不兼容。' }
    if($maskChanges.Count -eq 1) {
        if(-not $maskChanges[0].AfterExists -or $maskChanges[0].After -isnot [array]) { throw '迁移后的 DNS 备份结构不兼容。' }
        Set-Value $after.patchClashConfig 'dns-override-keys' $maskChanges[0].After
    }
    $expected=Get-MigratedDnsKeys $after ([bool]$flags[0].After)
    $current=@($mask.Value | Sort-Object -CaseSensitive -Unique)
    if((Json $current) -cne (Json $expected)) { throw '迁移后的 DNS 覆写列表后来被修改，自动恢复已停止。' }
    $enabled=$flags[0].BeforeExists -and $flags[0].Before -eq $true
    Set-Value $mask.Parent $mask.Key (Get-MigratedDnsKeys $before $enabled)
    return $true
}
function Test-LegacyStrict([string]$Code) {
    $clean=($Code -replace '(?m)//[^\r\n]*','') -replace '\s',''
    $clean=$clean.Replace("'",'"')
    return $clean -ceq 'constmain=(config)=>{config.tun=config.tun||{};config.tun["strict-route"]=true;returnconfig;};'
}
function Resolve-Proxy([string]$Name,$Groups,$Nodes,$Selected,$Visited,$ProviderNodes,$BaseNodes) {
    if($Name -in @('DIRECT','REJECT','REJECT-DROP','PASS','PASS-RULE','COMPATIBLE','GLOBAL')) { return $false }
    if($Nodes.ContainsKey($Name)) { return ($Nodes[$Name] -and $Nodes[$Name] -notin @('direct','reject','reject-drop','pass','pass-rule','compatible')) }
    if(-not $Groups.ContainsKey($Name) -or $Visited.Contains($Name)) { return $false }
    $seen=New-Object 'System.Collections.Generic.HashSet[string]'
    foreach($item in $Visited) { [void]$seen.Add($item) }
    [void]$seen.Add($Name)
    $g=$Groups[$Name]
    foreach($key in @('filter','exclude-filter','exclude-type')) { if($null -ne (Get-Value $g $key)) { return $false } }
    $members=@(Get-Value $g 'proxies')
    $members=@($members | Where-Object { $null -ne $_ })
    if((Get-Value $g 'include-all') -eq $true -or (Get-Value $g 'include-all-proxies') -eq $true) {
        if($null -eq $BaseNodes) { return $false }
        $members+=@($BaseNodes | Sort-Object -CaseSensitive)
    }
    $use=@(Get-Value $g 'use') | Where-Object { $null -ne $_ }
    if((Get-Value $g 'include-all') -eq $true -or (Get-Value $g 'include-all-providers') -eq $true) {
        if($null -eq $ProviderNodes) { return $false }
        $use=@($ProviderNodes.Keys | Sort-Object -CaseSensitive)
    }
    foreach($provider in $use) {
        if($null -eq $ProviderNodes -or -not $ProviderNodes.ContainsKey([string]$provider) -or $null -eq $ProviderNodes[[string]$provider]) { return $false }
        $members+=@($ProviderNodes[[string]$provider])
    }
    $members=@($members | Select-Object -Unique)
    $type=Get-Value $g 'type'
    if($type -eq 'select') {
        $choice=Get-Value $Selected $Name
        if(-not $choice -and $members.Count -gt 0) { $choice=$members[0] }
        if(-not $choice -or $choice -cnotin $members) { return $false }
    }
    if($type -notin @('select','url-test','fallback','load-balance') -or $members.Count -eq 0) { return $false }
    foreach($member in $members) { if(-not (Resolve-Proxy ([string]$member) $Groups $Nodes $Selected $seen $ProviderNodes $BaseNodes)) { return $false } }
    return $true
}
function Get-RepairPlan([string]$Root,[string]$Chosen) {
    $script:Report.configurationStatus='Invalid'
    $script:SafeFailureMessage='FlClash 偏好设置结构无法读取，请检查格式或权限。'
    $prefs=Join-Path $Root 'shared_preferences.json'
    Require-ConfigurationFile $prefs 'FlClash 偏好设置'
    $outer=Get-Json $prefs
    $stored=Get-Value $outer 'flutter.config'
    if($stored -isnot [string]) { Fail-Plan '未找到兼容的 flutter.config，请先启动 FlClash 并导入订阅。' }
    $config=$stored | ConvertFrom-Json
    $patch=Get-Value $config 'patchClashConfig'
    $dns=Get-Value $patch 'dns'
    $tun=Get-Value $patch 'tun'
    if($null -eq $dns -or $null -eq $tun -or $null -eq (Get-Value $config 'networkProps') -or $null -eq (Get-Value $config 'appSettingProps')) { Fail-Plan '偏好设置结构不兼容。未修改文件。' }
    $profileId=Get-Value $config 'currentProfileId'
    if($null -eq $profileId) { Fail-Plan '请先导入订阅、选择配置并运行一次 FlClash。' }
    $profileId=[long]$profileId
    $script:Report.profileId=$profileId
    Require-ConfigurationFile (Join-Path $Root 'database.sqlite') 'FlClash 配置数据库'
    $script:SafeFailureMessage='FlClash 配置数据库结构或当前配置档无法读取。'
    $db=[FlDns.SqliteDb]::new((Join-Path $Root 'database.sqlite'),$false)
    try {
        $schema=Assert-Schema $db
        $profile=Get-ProfileBinding $db $schema $profileId
        $type=$profile['overwrite_type']
        $existingId=$profile['script_id']
        $scriptCreated=$true
        $oldCode=$null; $oldScriptHash=$null; $oldScriptTime=$null
        $newId=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $selected=$profile['selected_map'] | ConvertFrom-Json
    } finally { $db.Dispose() }
    Require-ConfigurationFile (Join-Path $Root 'config.yaml') 'FlClash 运行配置'
    $script:SafeFailureMessage='FlClash 运行配置格式无法读取，未修改数据。'
    $runtime=Read-Yaml (Join-Path $Root 'config.yaml')
    $script:Report.proxyPorts=@(Get-RuntimeProxyPorts $runtime)
    Require-ConfigurationFile (Join-Path $Root "profiles\$profileId.yaml") 'FlClash 当前订阅配置'
    $script:SafeFailureMessage='FlClash 当前订阅配置格式无法读取，未修改数据。'
    $rawProfile=Read-Yaml (Join-Path $Root "profiles\$profileId.yaml")
    $groups=[System.Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $nodes=[System.Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach($g in @(Get-Value $runtime 'proxy-groups')) {
        if(-not $g -or -not (Get-Value $g 'name') -or $groups.ContainsKey([string]$g.name)) { Fail-Plan '运行配置代理组名称缺失或重复，未修改数据。' }
        $groups[[string]$g.name]=$g
    }
    $baseNodes=New-Object 'System.Collections.Generic.List[string]'
    foreach($node in @(Get-Value $runtime 'proxies')) {
        if($null -eq $node) { continue }
        if(-not (Get-Value $node 'name') -or -not (Get-Value $node 'type') -or $nodes.ContainsKey([string]$node.name) -or $groups.ContainsKey([string]$node.name)) { Fail-Plan '运行配置节点名称或类型缺失、重复，未修改数据。' }
        $nodes[[string]$node.name]=[string]$node.type; $baseNodes.Add([string]$node.name)
    }
    $providerNodes=[System.Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $providers=Get-Value $runtime 'proxy-providers'
    if($providers) {
        foreach($provider in $providers.PSObject.Properties) {
            # Null means a declared provider whose complete members cannot be confirmed.
            $providerNodes[$provider.Name]=$null
            $details=$provider.Value
            $dynamic=$false
            foreach($key in @('filter','exclude-filter','exclude-type')) { if($null -ne (Get-Value $details $key)) { $dynamic=$true } }
            $override=Get-Value $details 'override'
            foreach($key in @('proxy-name','override-expr')) { if($null -ne (Get-Value $override $key)) { $dynamic=$true } }
            if($dynamic) { continue }
            $entries=$null; $providerType=Get-Value $details 'type'
            if($providerType -eq 'inline') {
                $payload=$details.PSObject.Properties['payload']
                if($null -ne $payload) { $entries=$payload.Value }
            } elseif($providerType -in @('file','http')) {
                $cache=Get-Value $details 'path'
                if($cache -isnot [string] -or -not $cache -or $cache.Contains('://')) { continue }
                if(-not [IO.Path]::IsPathRooted($cache)) { $cache=Join-Path $Root $cache }
                $cache=[IO.Path]::GetFullPath($cache)
                if(-not $cache.StartsWith($Root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $cache -PathType Leaf)) { continue }
                Assert-NoLinks $cache
                if($providerType -eq 'http') {
                    $interval=0L
                    if(-not [long]::TryParse([string](Get-Value $details 'interval'),[ref]$interval) -or $interval -le 0 -or $interval -gt 31536000 -or (Get-Item -LiteralPath $cache).LastWriteTimeUtc.AddSeconds($interval) -lt [DateTime]::UtcNow) { continue }
                }
                $cached=Read-Yaml $cache; $cachedEntries=$cached.PSObject.Properties['proxies']
                if($null -ne $cachedEntries) { $entries=$cachedEntries.Value }
            }
            if($entries -isnot [array] -or $entries.Count -eq 0) { continue }
            $prefix=Get-Value $override 'additional-prefix'; $suffix=Get-Value $override 'additional-suffix'
            if(($null -ne $prefix -and $prefix -isnot [string]) -or ($null -ne $suffix -and $suffix -isnot [string])) { continue }
            $candidateNodes=[System.Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
            $valid=$true
            foreach($node in $entries) {
                $name=Get-Value $node 'name'; $nodeType=Get-Value $node 'type'
                if($name -isnot [string] -or -not $name -or $nodeType -isnot [string] -or -not $nodeType) { $valid=$false; break }
                $name=[string]$prefix+$name+[string]$suffix
                if($candidateNodes.ContainsKey($name) -or $groups.ContainsKey($name) -or ($nodes.ContainsKey($name) -and $nodes[$name] -cne $nodeType)) { $valid=$false; break }
                $candidateNodes[$name]=$nodeType
            }
            if(-not $valid) { continue }
            foreach($name in $candidateNodes.Keys) { $nodes[$name]=$candidateNodes[$name] }
            $providerNodes[$provider.Name]=@($candidateNodes.Keys)
        }
    }
    $rawNames=@(foreach($g in @(Get-Value $rawProfile 'proxy-groups')) { Get-Value $g 'name' })
    $usable=@($groups.Keys | Where-Object {
        $_ -cin $rawNames -and $_ -notmatch '[,#&\r\n]' -and
        (Resolve-Proxy $_ $groups $nodes $selected (New-Object 'System.Collections.Generic.HashSet[string]') $providerNodes $baseNodes)
    } | Sort-Object)
    $script:Report.nodeTargets=@()
    $script:Report.selectionNote=''
    if($usable.Count -eq 0) {
        # A selector containing DIRECT is not a protected outbound. Offer verified
        # concrete nodes from the active subscription without changing that selector.
        $nodeTargets=@($(foreach($node in @(Get-Value $rawProfile 'proxies')) {
            $name=Get-Value $node 'name'; $nodeType=Get-Value $node 'type'
            if($name -is [string] -and $name -and $name -notmatch '[,#&\r\n]' -and
                $nodes.ContainsKey($name) -and -not $groups.ContainsKey($name) -and $nodes[$name] -ceq $nodeType -and
                (Resolve-Proxy $name $groups $nodes $selected (New-Object 'System.Collections.Generic.HashSet[string]') $providerNodes $baseNodes)) { $name }
        }) | Sort-Object -Unique)
        $script:Report.nodeTargets=$nodeTargets
        $script:Report.groups=$nodeTargets
        $script:Report.configurationStatus='Ready'
        $script:Report.selectionNote='当前分组含直连候选，或无法确认全部候选都走代理。下面提供当前订阅中的具体节点；选择后将用于 DNS 与受保护服务，原分组和国内直连规则保留。'
        if(-not $Chosen -or $Chosen -cnotin $nodeTargets) {
            Fail-Plan ("已读取当前订阅；没有可直接用于保护的代理分组。已列出 $($nodeTargets.Count) 个可用节点，请选择一个出口后再点一键配置。")
        }
        $usable=$nodeTargets
    }
    $script:Report.groups=$usable
    $script:Report.configurationStatus='Ready'
    if($usable.Count -eq 0) { Fail-Plan '没有找到已选择代理节点的可用代理组。请先运行订阅，并确保代理组没有选择 DIRECT／直连。' }
    if($Chosen) {
        if($Chosen -cnotin $usable) { Fail-Plan '指定代理组不存在、未选择代理节点或格式不兼容。未修改文件。' }
    } else {
        # Only a unique existing DNS or MATCH choice establishes a selected group.
        $dnsChoices=@($(foreach($server in @(Get-Value $dns 'nameserver')) {
            if($server -match '#([^#&]+)$' -and $Matches[1] -cin $usable) { $Matches[1] }
        }) | Sort-Object -Unique)
        if($dnsChoices.Count -eq 1) { $Chosen=$dnsChoices[0] }
        if(-not $Chosen -and $dnsChoices.Count -eq 0) {
            $matchChoices=@($(foreach($rule in @(Get-Value $runtime 'rules')) {
                if($rule -match '^(MATCH|FINAL),([^,]+)$' -and $Matches[2] -cin $usable) { $Matches[2] }
            }) | Sort-Object -Unique)
            if($matchChoices.Count -eq 1) { $Chosen=$matchChoices[0] }
        }
        if(-not $Chosen -and $usable.Count -eq 1) { $Chosen=$usable[0] }
        if(-not $Chosen) {
            $script:SelectionNeeded=$true
            if(-not $NonInteractive) { foreach($candidate in $usable) { Write-Host ('候选代理组：'+$candidate) } }
            Fail-Plan '存在多个可用代理组，请携带 ProxyGroup 明确选择后重新预览。'
        }
    }
    $script:Report.selectedGroup=$Chosen
    $script:SafeFailureMessage='配置已读取；已有覆写或脚本需处理，当前修复预览不可用，未修改数据。'
    $db=[FlDns.SqliteDb]::new((Join-Path $Root 'database.sqlite'),$false)
    try {
        if($schema.Modern -and $profile['profile_type'] -notin @('file','url')) { Fail-Plan '当前配置档类型未验证兼容，请使用订阅或文件配置；未修改文件。' }
        if($type -eq 'script' -and $null -ne $existingId) {
            $oldPath=Join-Path $Root "scripts\$existingId.js"
            if(-not (Test-Path -LiteralPath $oldPath)) { Fail-Plan '当前绑定的脚本文件不存在，未修改文件。' }
            $oldCode=[IO.File]::ReadAllText($oldPath)
            if(-not (Test-LegacyStrict $oldCode) -and -not (Test-OwnedCode $oldCode)) { Fail-Plan '检测到已有自定义脚本，需人工合并。本工具没有覆盖它。' }
            $scriptRows=@($db.Query("SELECT last_update_time FROM scripts WHERE id=$([long]$existingId)"))
            if($scriptRows.Count -ne 1) { Fail-Plan '绑定脚本的数据库记录不存在，未修改文件。' }
            if(@($db.Query("SELECT COUNT(*) AS n FROM profiles WHERE script_id=$([long]$existingId)"))[0]['n'] -ne 1) { Fail-Plan '原脚本还被其他配置使用，需人工合并；未修改文件。' }
            $oldScriptTime=$scriptRows[0]['last_update_time']
            $oldScriptHash=(Get-FileHash -LiteralPath $oldPath -Algorithm SHA256).Hash
            $newId=[long]$existingId
            $scriptCreated=$false
        } elseif($type -eq 'standard' -or ($type -eq 'script' -and $null -eq $existingId)) {
            foreach($table in @($schema.RuleTable,'proxy_groups')) {
                $n=@($db.Query("SELECT COUNT(*) AS n FROM $table WHERE profile_id=$profileId OR profile_id IS NULL"))[0]['n']
                if($n -ne 0) { Fail-Plan '检测到附加规则或代理组覆写，需人工合并。本工具没有覆盖它们。' }
            }
            while(@($db.Query("SELECT COUNT(*) AS n FROM scripts WHERE id=$newId"))[0]['n'] -gt 0 -or (Test-Path -LiteralPath (Join-Path $Root "scripts\$newId.js"))) { $newId++ }
        } else { Fail-Plan '配置已读取；已有覆写方式未验证兼容，未修改文件。' }
    } finally { $db.Dispose() }
    $managedCode=New-ManagedCode $Chosen $schema.Modern
    $scriptReplaced=(-not $scriptCreated -and $oldCode.Replace("`r`n","`n").Trim() -cne $managedCode.Trim())
    # Unknown public DNS policy overrides need a review rather than silent deletion.
    $policy=Get-Value $dns 'nameserver-policy'
    if($policy) {
        foreach($p in $policy.PSObject.Properties) {
            if($p.Name -in @('www.baidu.com',$script:DomesticPolicyKey) -or $p.Name -cin @($script:DirectDomains | ForEach-Object { '+.'+$_ })) { continue }
            foreach($resolver in @($p.Value)) {
                $ip=$null
                if(-not [Net.IPAddress]::TryParse([string]$resolver,[ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { Fail-Plan '检测到其他自定义 DNS 策略，需人工确认出口。本工具保留原设置并停止。' }
                $b=$ip.GetAddressBytes()
                if(-not ($b[0] -eq 10 -or ($b[0] -eq 192 -and $b[1] -eq 168) -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or $b[0] -eq 127)) { Fail-Plan '检测到其他公共 DNS 策略，需人工确认。本工具保留原设置并停止。' }
            }
        }
    }
    foreach($extra in @('direct-nameserver','proxy-server-nameserver-policy')) {
        $value=Get-Value $dns $extra
        if($null -ne $value -and (Json $value) -notin @('[]','{}','null')) { Fail-Plan "检测到额外 DNS 字段 $extra，需人工确认；未修改文件。" }
    }
    $changes=New-Object 'System.Collections.Generic.List[object]'
    if(-not $schema.Modern) { Put-Change $config $changes 'overrideDns' $true }
    Put-Change $config $changes 'patchClashConfig.mode' 'rule'
    Put-Change $config $changes 'patchClashConfig.find-process-mode' 'always'
    Put-Change $config $changes 'patchClashConfig.dns.enable' $true
    Put-Change $config $changes 'patchClashConfig.dns.respect-rules' $true
    Put-Change $config $changes 'patchClashConfig.dns.prefer-h3' $false
    Put-Change $config $changes 'patchClashConfig.dns.nameserver' @($script:ProxyDns | ForEach-Object { "$_#$Chosen" })
    Put-Change $config $changes 'patchClashConfig.dns.fallback' @()
    Put-Change $config $changes 'patchClashConfig.dns.default-nameserver' @($script:DomesticDns)
    Put-Change $config $changes 'patchClashConfig.dns.proxy-server-nameserver' @($script:DomesticDns)
    # Track each literal policy key separately so restore preserves unrelated later additions.
    if($null -eq $policy) {
        $slot=Get-Slot $config 'patchClashConfig.dns.nameserver-policy'
        $changes.Add([pscustomobject]@{Path='patchClashConfig.dns.nameserver-policy';BeforeExists=$slot.Exists;Before=$slot.Value;AfterExists=$true;After=[pscustomobject]@{}}) | Out-Null
        $policy=[pscustomobject]@{}
        Set-Value $dns 'nameserver-policy' $policy
    }
    foreach($key in @('www.baidu.com',$script:DomesticPolicyKey)+@($script:DirectDomains | ForEach-Object { '+.'+$_ })) {
        $p=$policy.PSObject.Properties[$key]
        $exists=$null -ne $p; $before=$null
        if($exists) { $before=$p.Value }
        $remove=$key -ceq 'www.baidu.com'
        if($remove -and -not $exists) { continue }
        if(-not $remove -and $exists -and (Json $before) -ceq (Json $script:DomesticDns)) { continue }
        $changes.Add([pscustomobject]@{Path='patchClashConfig.dns.nameserver-policy';PolicyKey=$key;BeforeExists=$exists;Before=$before;AfterExists=(-not $remove);After=$(if($remove) {$null} else {$script:DomesticDns})}) | Out-Null
        if($remove) { $policy.PSObject.Properties.Remove($key) } else { Set-Value $policy $key $script:DomesticDns }
    }
    # New FlClash versions select which DNS fields are overridden individually.
    $mask=Get-Slot $config 'patchClashConfig.dns-override-keys'
    if($schema.Modern -and (-not $mask.Exists -or $mask.Value -isnot [array])) { Fail-Plan 'DNS 覆写字段列表缺失或不兼容，未修改文件。' }
    if($mask.Exists) {
        if($null -ne $mask.Value -and $mask.Value -isnot [array]) { Fail-Plan 'DNS 覆写字段列表格式不兼容，未修改文件。' }
        $keys=@($mask.Value | Where-Object { $null -ne $_ })
        $required=@('enable','respect-rules','prefer-h3','nameserver','fallback','default-nameserver','proxy-server-nameserver','nameserver-policy')
        if($schema.Modern) { $required=@('nameserver','default-nameserver','proxy-server-nameserver') }
        foreach($key in $required) {
            if($keys -cnotcontains $key) { $keys+=$key }
        }
        Put-Change $config $changes 'patchClashConfig.dns-override-keys' $keys
    }
    Set-ManagedTunChanges $config $changes $true
    Put-Change $config $changes 'networkProps.appendSystemDns' $false
    Put-Change $config $changes 'appSettingProps.autoRun' $true
    Set-Value $outer 'flutter.config' (Json $config)
    $ports=@{}
    foreach($port in @('mixed-port','port','socks-port','redir-port','tproxy-port')) { $ports[$port]=Get-Value $patch $port }
    return @{Outer=$outer;Config=$config;Schema=$schema;Profile=$profile;ProfileId=$profileId;Group=$Chosen;NewScriptId=$newId;ScriptChange=($scriptCreated -or $scriptReplaced);ScriptCreated=$scriptCreated;ScriptReplaced=$scriptReplaced;ManagedCode=$managedCode;OldScriptHash=$oldScriptHash;OldScriptTime=$oldScriptTime;Changes=$changes;Ports=$ports;PreferenceHash=(Get-FileHash -LiteralPath $prefs -Algorithm SHA256).Hash}
}
function Get-AppProcesses([string]$Exe) {
    $appFolder=Split-Path -Parent $Exe
    $matchedProcesses=New-Object 'System.Collections.Generic.List[object]'
    foreach($proc in @(Get-Process -Name 'FlClash','FlClashCore' -ErrorAction SilentlyContinue)) {
        $processPath=$null
        try { $processPath=$proc.Path } catch { }
        if([string]::IsNullOrWhiteSpace([string]$processPath)) {
            $message='无法读取 FlClash 同名进程的程序路径，无法安全确认退出；请用当前用户的管理员权限运行修复或恢复入口。'
            $script:SafeFailureMessage=$message
            throw $message
        }
        if((Split-Path -Parent $processPath) -ieq $appFolder) { $matchedProcesses.Add($proc) }
    }
    return @($matchedProcesses.ToArray())
}
function Stop-App([string]$Exe) {
    $processes=Get-AppProcesses $Exe
    foreach($proc in @($processes | Sort-Object @{Expression={if($_.Name -eq 'FlClash') {0} else {1}}})) {
        if(-not $proc.HasExited) {
            $created=$proc.StartTime; $path=$proc.Path
            $now=Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
            if($null -ne $now -and $now.StartTime -eq $created -and $now.Path -ieq $path) { $proc.Kill(); [void]$proc.WaitForExit(5000) }
        }
    }
    if(@(Get-AppProcesses $Exe).Count -gt 0) { throw '无法停止 FlClash，未开始写入配置。' }
}
function Start-App([string]$Exe) {
    if(@(Get-AppProcesses $Exe | Where-Object { $_.Name -eq 'FlClash' }).Count -eq 0) { Start-Process -FilePath $Exe -WindowStyle Hidden | Out-Null }
}
function Get-ChangeSlot($Config,$Change) {
    $slot=Get-Slot $Config $Change.Path
    $policyKey=Get-Value $Change 'PolicyKey'
    if($policyKey) {
        $parent=$slot.Value
        if($null -eq $parent) { throw 'DNS 策略结构已改变，自动恢复已停止。' }
        $p=$parent.PSObject.Properties[$policyKey]
        $value=$null
        if($null -ne $p) { $value=$p.Value }
        $slot=@{Parent=$parent;Key=$policyKey;Exists=($null -ne $p);Value=$value}
    }
    return $slot
}
function Restore-Settings([string]$Root,[string]$Backup) {
    $meta=Get-Json (Join-Path $Backup 'manifest.json')
    if($meta.dataDir -ine $Root -or $meta.status -ne 'applied') { throw '备份不属于此数据目录，或不是一次已完成的修改。' }
    $prefs=Join-Path $Root 'shared_preferences.json'
    $hash=(Get-FileHash -LiteralPath $prefs -Algorithm SHA256).Hash
    $outer=Get-Json $prefs
    $config=$outer.'flutter.config' | ConvertFrom-Json
    $migratedDns=Restore-MigratedDnsFlag $config $meta $Backup
    $reverse=@($meta.changes); [array]::Reverse($reverse)
    foreach($change in $reverse) {
        if($migratedDns -and $change.Path -cin @('overrideDns','patchClashConfig.dns-override-keys')) { continue }
        $slot=Get-ChangeSlot $config $change
        if($slot.Exists -ne $change.AfterExists -or ($slot.Exists -and (Json $slot.Value) -cne (Json $change.After))) { throw "设置 $($change.Path) 后来被改过，自动恢复已停止，避免覆盖后续修改。" }
        if($change.BeforeExists) { Set-Value $slot.Parent $slot.Key $change.Before } else { $slot.Parent.PSObject.Properties.Remove($slot.Key) }
    }
    Set-Value $outer 'flutter.config' (Json $config)
    $db=[FlDns.SqliteDb]::new((Join-Path $Root 'database.sqlite'),$true)
    $transaction=$false
    $original=[IO.File]::ReadAllBytes($prefs)
    $replaced=(Get-Value $meta 'scriptReplaced') -eq $true
    $scriptBeforeRestore=$null; $scriptRestored=$false
    try {
        $schema=Assert-Schema $db
        $id=[long]$meta.profileId
        if($meta.scriptChanged -or $replaced) {
            $row=Get-ProfileBinding $db $schema $id
            if($row['overwrite_type'] -ne 'script' -or $row['script_id'] -ne [long]$meta.newScriptId) { throw '配置档的脚本绑定后来被修改，自动恢复已停止。' }
            $references=@($db.Query("SELECT COUNT(*) AS n FROM profiles WHERE script_id=$([long]$meta.newScriptId)"))[0]['n']
            if($references -ne 1) { throw '本次脚本已被其他配置档使用，自动恢复已停止。' }
            $created=Join-Path $Root "scripts\$($meta.newScriptId).js"
            if(-not (Test-Path -LiteralPath $created) -or (Get-FileHash -LiteralPath $created -Algorithm SHA256).Hash -cne $meta.newScriptHash) { throw '本次脚本后来被修改，自动恢复已停止。' }
        }
        if($replaced) {
            $oldFile=Join-Path $Backup 'original-script.js'
            if(-not (Test-Path -LiteralPath $oldFile) -or (Get-FileHash -LiteralPath $oldFile -Algorithm SHA256).Hash -cne $meta.oldScriptHash) { throw '原脚本备份校验失败，自动恢复已停止。' }
            $stamp=@($db.Query("SELECT last_update_time FROM scripts WHERE id=$([long]$meta.newScriptId)"))[0]['last_update_time']
            if($stamp -ne $meta.newScriptUpdateTime) { throw '脚本记录后来被修改，自动恢复已停止。' }
            $scriptBeforeRestore=[IO.File]::ReadAllBytes($created)
        }
        if((Get-FileHash -LiteralPath $prefs -Algorithm SHA256).Hash -cne $hash) { throw '偏好文件正在变化，请退出 FlClash 后重试。' }
        $db.Execute('BEGIN IMMEDIATE'); $transaction=$true
        if($meta.scriptChanged) {
            $oldId='NULL'
            if($null -ne $meta.oldScriptId) { $oldId=[string][long]$meta.oldScriptId }
            $oldType=[string]$meta.oldOverwriteType
            if($oldType -notin @('standard','script')) { throw '备份覆写方式不兼容。' }
            $db.Execute("UPDATE profiles SET $($schema.TypeColumn)='$oldType',script_id=$oldId WHERE id=$id")
            $db.Execute("DELETE FROM scripts WHERE id=$([long]$meta.newScriptId)")
        }
        if($replaced) {
            Write-AtomicBytes $created ([IO.File]::ReadAllBytes($oldFile)); $scriptRestored=$true
            $db.Execute("UPDATE scripts SET last_update_time=$([long]$meta.oldScriptTime) WHERE id=$([long]$meta.newScriptId)")
        }
        Write-Atomic $prefs (Json $outer)
        if(@($db.Query('PRAGMA quick_check'))[0]['quick_check'] -ne 'ok') { throw '恢复后的数据库检查不通过。' }
        $db.Execute('COMMIT'); $transaction=$false
    } catch {
        if($transaction) { $db.Execute('ROLLBACK') }
        if((Get-FileHash -LiteralPath $prefs -Algorithm SHA256).Hash -cne $hash) { Write-AtomicBytes $prefs $original }
        if($scriptRestored) { Write-AtomicBytes $created $scriptBeforeRestore }
        throw
    } finally { $db.Dispose() }
    if($meta.scriptChanged) { [IO.File]::Delete((Join-Path $Root "scripts\$($meta.newScriptId).js")) }
    Set-Value $meta 'status' 'restored'
    Write-Atomic (Join-Path $Backup 'manifest.json') (Json $meta)
    if(-not $NonInteractive) { Write-Host '已恢复本次改动，其他设置和订阅保留。' -ForegroundColor Green }
}
function Apply-Settings([string]$Root,$Plan) {
    $prefs=Join-Path $Root 'shared_preferences.json'
    if((Get-Item -LiteralPath $prefs).IsReadOnly) { throw '偏好文件为只读，未修改文件。' }
    if((Get-FileHash -LiteralPath $prefs -Algorithm SHA256).Hash -cne $Plan.PreferenceHash) { throw '配置正在变化，请退出 FlClash 后重试。' }
    if(-not $Plan.ScriptChange -and $Plan.Changes.Count -eq 0) { if(-not $NonInteractive) { Write-Host '所需持久设置已存在，无需再次修改。' }; return $null }
    $backupBase=Join-Path $env:LOCALAPPDATA 'FlClash-DNS-Backups'
    if($Offline) { $backupBase=Join-Path $Root 'dns-backups' }
    Assert-NoLinks $backupBase
    $backup=Join-Path $backupBase ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
    [IO.Directory]::CreateDirectory($backup) | Out-Null
    $script:Report.backupDirectory=$backup
    $db=[FlDns.SqliteDb]::new((Join-Path $Root 'database.sqlite'),$true)
    $scriptPath=Join-Path $Root "scripts\$($Plan.NewScriptId).js"
    $original=[IO.File]::ReadAllBytes($prefs)
    $transaction=$false; $created=$false; $replaced=$false; $committed=$false; $scriptOriginal=$null
    $meta=[pscustomobject]@{toolVersion=2;status='pending';dataDir=$Root;profileId=$Plan.ProfileId;proxyGroup=$Plan.Group;ports=$Plan.Ports;changes=@($Plan.Changes.ToArray());scriptChanged=$Plan.ScriptCreated;scriptReplaced=$Plan.ScriptReplaced;oldScriptHash=$Plan.OldScriptHash;oldScriptTime=$Plan.OldScriptTime;newScriptUpdateTime=$null;newScriptId=$Plan.NewScriptId;newScriptHash=$null;oldOverwriteType=$Plan.Profile['overwrite_type'];oldScriptId=$Plan.Profile['script_id']}
    try {
        $schema=Assert-Schema $db
        $db.Backup((Join-Path $backup 'database.sqlite'))
        [IO.File]::WriteAllBytes((Join-Path $backup 'shared_preferences.json'),$original)
        if($null -ne $Plan.Profile['script_id']) {
            $oldPath=Join-Path $Root "scripts\$($Plan.Profile['script_id']).js"
            if(Test-Path -LiteralPath $oldPath) { Copy-Item -LiteralPath $oldPath -Destination (Join-Path $backup 'original-script.js') }
        }
        Write-Atomic (Join-Path $backup 'manifest.json') (Json $meta)
        $db.Execute('BEGIN IMMEDIATE'); $transaction=$true
        $fresh=Get-ProfileBinding $db $schema $Plan.ProfileId
        if($schema.TypeColumn -cne $Plan.Schema.TypeColumn -or ($schema.Modern -and $fresh['profile_type'] -cne $Plan.Profile['profile_type'])) { throw '配置档结构或类型发生变化，已取消修改。' }
        if($fresh['overwrite_type'] -cne $Plan.Profile['overwrite_type'] -or $fresh['script_id'] -ne $Plan.Profile['script_id']) { throw '脚本绑定发生变化，已取消修改。' }
        if((Get-FileHash -LiteralPath $prefs -Algorithm SHA256).Hash -cne $Plan.PreferenceHash) { throw '偏好设置发生变化，已取消修改。' }
        if(-not $Plan.ScriptCreated -and (Get-FileHash -LiteralPath $scriptPath -Algorithm SHA256).Hash -cne $Plan.OldScriptHash) { throw '原脚本发生变化，已取消修改。' }
        if($Plan.ScriptCreated) {
            [IO.Directory]::CreateDirectory((Split-Path -Parent $scriptPath)) | Out-Null
            if(Test-Path -LiteralPath $scriptPath) { throw '脚本 ID 冲突，请重试。' }
            Write-Atomic $scriptPath $Plan.ManagedCode; $created=$true
            $id=[long]$Plan.NewScriptId
            $time=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            $db.Execute("INSERT INTO scripts (id,label,last_update_time) VALUES ($id,'DNS及国内外分流-一键设置',$time)")
            $db.Execute("UPDATE profiles SET $($schema.TypeColumn)='script',script_id=$id WHERE id=$($Plan.ProfileId)")
            $meta.newScriptHash=(Get-FileHash -LiteralPath $scriptPath -Algorithm SHA256).Hash
        }
        if($Plan.ScriptReplaced) {
            $scriptOriginal=[IO.File]::ReadAllBytes($scriptPath)
            if((Get-FileHash -LiteralPath (Join-Path $backup 'original-script.js') -Algorithm SHA256).Hash -cne $Plan.OldScriptHash) { throw '原脚本备份发生变化，已取消修改。' }
            Write-Atomic $scriptPath $Plan.ManagedCode; $replaced=$true
            $time=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            $db.Execute("UPDATE scripts SET last_update_time=$time WHERE id=$([long]$Plan.NewScriptId)")
            $meta.newScriptUpdateTime=$time
            $meta.newScriptHash=(Get-FileHash -LiteralPath $scriptPath -Algorithm SHA256).Hash
        }
        Write-Atomic $prefs (Json $Plan.Outer)
        $saved=(Get-Json $prefs).'flutter.config' | ConvertFrom-Json
        if((Json $saved.patchClashConfig.dns) -cne (Json $Plan.Config.patchClashConfig.dns)) { throw 'DNS 设置写入验证失败。' }
        if(@($db.Query('PRAGMA quick_check'))[0]['quick_check'] -ne 'ok') { throw '数据库验证失败。' }
        $meta.status='applied'
        Write-Atomic (Join-Path $backup 'manifest.json') (Json $meta)
        $db.Execute('COMMIT'); $transaction=$false; $committed=$true; $script:Report.settingsSaved=$true
        [IO.Directory]::CreateDirectory($backupBase) | Out-Null
        Write-Atomic (Join-Path $backupBase 'last-backup.txt') $backup
    } catch {
        if($transaction) { $db.Execute('ROLLBACK') }
        if(-not $committed) {
            if((Get-FileHash -LiteralPath $prefs -Algorithm SHA256).Hash -cne $Plan.PreferenceHash) { [IO.File]::WriteAllBytes($prefs,$original) }
            if($created) { [IO.File]::Delete($scriptPath) }
            if($replaced) { Write-AtomicBytes $scriptPath $scriptOriginal }
            $meta.status='failed'; Write-Atomic (Join-Path $backup 'manifest.json') (Json $meta)
        }
        throw
    } finally { $db.Dispose() }
    if(-not $NonInteractive) { Write-Host "设置已写入。原配置备份：$backup" -ForegroundColor Green }
    return $backup
}
function Verify-Runtime([string]$Root,[string]$Group,$Ports) {
    $r=Read-Yaml (Join-Path $Root 'config.yaml')
    $checks=@{
        '规则模式'=($r.mode -eq 'rule'); 'TUN 开启'=($r.tun.enable -eq $true);
        '严格路由'=((Get-Value $r.tun 'strict-route') -eq $true);
        '自动路由'=((Get-Value $r.tun 'auto-route') -eq $true);
        'DNS 接管'=(@($script:TunDnsHijack | Where-Object { @($r.tun.'dns-hijack') -cnotcontains $_ }).Count -eq 0);
        'DNS 开启'=($r.dns.enable -eq $true); '遵守规则'=($r.dns.'respect-rules' -eq $true);
        '普通 DNS'=((Json $r.dns.nameserver) -ceq (Json @($script:ProxyDns | ForEach-Object { "$_#$Group" })));
        '默认 DNS'=((Json $r.dns.'default-nameserver') -ceq (Json @($script:DomesticDns)));
        '进程分流'=((Get-Value $r 'find-process-mode') -eq 'always');
        '节点 DNS'=((Json $r.dns.'proxy-server-nameserver') -ceq (Json @($script:DomesticDns)));
        '清空 Fallback'=(@($r.dns.fallback).Count -eq 0)
    }
    $policy=Get-Value $r.dns 'nameserver-policy'
    foreach($key in @($script:DomesticPolicyKey)+@($script:DirectDomains | ForEach-Object { '+.'+$_ })) {
        $checks["国内 DNS $key"]=(Json (Get-Value $policy $key)) -ceq (Json $script:DomesticDns)
    }
    if($policy -and $null -ne $policy.PSObject.Properties['www.baidu.com']) { return $false }
    $rules=@(Get-Value $r 'rules')
    $priority=@($script:ProxyProcesses | ForEach-Object { "PROCESS-NAME,$_,$Group"; "PROCESS-NAME,$_,REJECT" })+@($script:ProxyDomains | ForEach-Object { "DOMAIN-SUFFIX,$_,$Group"; "DOMAIN-SUFFIX,$_,REJECT" })+@($script:DirectProcesses | ForEach-Object { "PROCESS-NAME,$_,DIRECT" })+@($script:DirectDomains | ForEach-Object { "DOMAIN-SUFFIX,$_,DIRECT" })+@($script:ProxyGeosites | ForEach-Object { "GEOSITE,$_,$Group" })
    if($rules.Count -lt $priority.Count+3) { return $false }
    for($i=0;$i -lt $priority.Count;$i++) { if($rules[$i] -cne $priority[$i]) { return $false } }
    if((Json @($rules[-3],$rules[-2],$rules[-1])) -cne (Json @("GEOSITE,$($script:DomesticGeosite),DIRECT",'GEOIP,CN,DIRECT',"MATCH,$Group"))) { return $false }
    if($Ports) { foreach($key in $Ports.Keys) { if($null -ne $Ports[$key]) { $checks["保留 $key"]=(Get-Value $r $key) -eq $Ports[$key] } } }
    foreach($key in $checks.Keys) { if(-not $checks[$key]) { return $false } }
    return $true
}

function Assert-NoLinks([string]$Path) {
    $part=[IO.Path]::GetFullPath($Path)
    while($part) {
        if(Test-Path -LiteralPath $part) {
            if(((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '目录包含链接，未执行操作。' }
        }
        $parent=[IO.Path]::GetDirectoryName($part)
        if($parent -eq $part) { break }; $part=$parent
    }
}
function Complete-Report([string]$Status,[string]$Message,[int]$Code) {
    $script:Report.status=$Status; $script:Report.message=$Message; $script:Report.exitCode=$Code
    if($ReportPath) { Write-Atomic $ReportPath (Json $script:Report) }
    if(-not $NonInteractive) { Write-Host $Message }
    exit $Code
}
function Set-ReportPlan($Plan) {
    $script:Report.selectedGroup=$Plan.Group; $script:Report.ports=$Plan.Ports
    $script:Report.changedFields=@($Plan.Changes | ForEach-Object { if(Get-Value $_ 'PolicyKey') { $_.Path+'/'+$_.PolicyKey } else { $_.Path } })
    if($Plan.ScriptChange) { $script:Report.changedFields+=@('profiles.script_id','scripts.managed-code') }
}
try {
    $DataDir=[IO.Path]::GetFullPath($DataDir).TrimEnd('\')
    $liveRoot=[IO.Path]::GetFullPath((Join-Path $env:APPDATA 'com.follow\clash')).TrimEnd('\')
    Assert-NoLinks $DataDir
    if($ReportPath) {
        $ReportPath=[IO.Path]::GetFullPath($ReportPath); Assert-NoLinks $ReportPath
        if($ReportPath.StartsWith($DataDir+'\',[StringComparison]::OrdinalIgnoreCase) -or $ReportPath -ieq $DataDir) { throw '报告文件必须位于数据目录外，避免预览改动数据。' }
    } elseif($NonInteractive) { throw '非交互调用必须提供 ReportPath。' }
    if($Offline) {
        if($DataDir -ieq $liveRoot -or (Split-Path -Leaf $DataDir) -notlike 'flclash-dns-fixture-*') { throw 'Offline 仅支持明确生成的离线夹具根。' }
        $marker=Get-Json (Join-Path $DataDir '.chunson-flclash-fixture.json')
        if($marker.schemaVersion -ne 1 -or $marker.root -ine $DataDir -or $marker.purpose -cne 'ChunsonSwitch.FlClashDns.Checks') { throw '离线夹具标记无效。' }
    } elseif($DataDir -ine $liveRoot) { $script:SafeFailureMessage='本机模式仅支持 FlClash 标准数据目录；自定义便携数据目录暂不支持。'; throw $script:SafeFailureMessage }
    $applicationRoot=[IO.Path]::GetFullPath((Join-Path $AssetRoot '../..'))
    if(-not $PolicyPath) {
        $PolicyPath=Join-Path $applicationRoot 'Resources/dns-policy.json'
        if(-not (Test-Path -LiteralPath $PolicyPath)) { $PolicyPath=Join-Path $applicationRoot 'windows/ChunsonSpace.Proxy/Resources/dns-policy.json' }
    }
    Initialize-Policy $PolicyPath
    $AppPath=Find-App
    if(-not $LibraryRoot) {
        $LibraryRoot=$applicationRoot
        if(-not (Test-Path -LiteralPath (Join-Path $LibraryRoot 'YamlDotNet.dll'))) { $LibraryRoot=Join-Path $applicationRoot 'windows/ThirdParty/YamlDotNet' }
    }
    $yaml=Join-Path $LibraryRoot 'YamlDotNet.dll'
    if((Get-FileHash -LiteralPath $yaml -Algorithm SHA256).Hash -ine '9172f0758ed76963e0ede3b5ca9a1fb5b82f64c7e7a92cb3a6de39efa933a668') { throw 'YAML 解析库校验失败。' }
    [Reflection.Assembly]::LoadFrom($yaml) | Out-Null
    if(-not $CsHelperPath) { $CsHelperPath=Join-Path $AssetRoot 'lib/FlDns.cs' }
    Add-Type -Path $CsHelperPath -ReferencedAssemblies $yaml
    . ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $AssetRoot 'FlClash-AllSubscriptions.ps1'))))
    $sqlite=Join-Path $env:SystemRoot 'System32/winsqlite3.dll'
    [FlDns.SqliteDb]::Load($sqlite)
    if($Action -in @('Apply','Restore') -and -not $Offline) {
        $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
        if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '应用或恢复需要明确的管理员执行入口，未修改配置。' }
    }
    if($Action -eq 'Restore') {
        if(-not $AppPath) {
            try { $readOnlyPlan=Get-RepairPlan $DataDir $ProxyGroup; Set-ReportPlan $readOnlyPlan } catch { }
            Complete-Report 'SelectionRequired' '请先确认 FlClash 程序位置，再恢复备份；当前未修改配置。' 2
        }
        if(-not $Offline) { [FlDns.SqliteDb]::Load((Join-Path (Split-Path -Parent $AppPath) 'sqlite3.dll')) }
        $base=Join-Path $env:LOCALAPPDATA 'FlClash-DNS-Backups'
        if($Offline) { $base=Join-Path $DataDir 'dns-backups' }
        if(-not $BackupDir) { $BackupDir=[IO.File]::ReadAllText((Join-Path $base 'last-backup.txt')).Trim() }
        $BackupDir=[IO.Path]::GetFullPath($BackupDir).TrimEnd('\'); Assert-NoLinks $BackupDir
        if(-not $BackupDir.StartsWith([IO.Path]::GetFullPath($base).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '恢复备份必须处于本工具专用备份目录。' }
        $script:Report.backupDirectory=$BackupDir
        if(-not $Offline) { Stop-App $AppPath }
        try {
            $manifest=Get-Json (Join-Path $BackupDir 'manifest.json')
            if((Get-Value $manifest 'scope') -ceq 'all-subscriptions') { Restore-AllSubscriptions $DataDir $BackupDir }
            else { Restore-Settings $DataDir $BackupDir }
            $script:Report.settingsSaved=$true
        }
        finally { if(-not $Offline -and -not $NoRestart) { Start-App $AppPath } }
        Complete-Report 'Restored' '本次改动已恢复；订阅与其他设置保留，网络效果未验证。' 0
    }
    if($AllSubscriptions) { Invoke-AllSubscriptions; exit }
    $plan=Get-RepairPlan $DataDir $ProxyGroup; Set-ReportPlan $plan
    if($Action -eq 'Preview') {
        $message='配置与代理分组已读取；未修改配置、未提权或重启软件。'
        if(-not $AppPath) { $message+='应用修复前需确认 FlClash 程序位置。' }
        Complete-Report 'PreviewReady' $message 0
    }
    if($Action -eq 'Verify') {
        if(Verify-Runtime $DataDir $plan.Group $plan.Ports) {
            $script:Report.generatedConfigVerified=$true
            Complete-Report 'GeneratedConfigVerified' '生成配置核对通过；未验证 DNS 网络效果。' 0
        }
        throw '当前生成配置未匹配方案。'
    }
    if(-not $AppPath) { Complete-Report 'SelectionRequired' '配置与代理分组已读取；请确认 FlClash 程序位置后再应用，当前未修改配置。' 2 }
    if(-not $Offline) { [FlDns.SqliteDb]::Load((Join-Path (Split-Path -Parent $AppPath) 'sqlite3.dll')); Stop-App $AppPath }
    try {
        $plan=Get-RepairPlan $DataDir $plan.Group; Set-ReportPlan $plan
        $backup=Apply-Settings $DataDir $plan
        $script:Report.settingsSaved=$true
        if($backup) { $script:Report.backupDirectory=$backup }
    } finally {
        if(-not $Offline -and -not $NoRestart) { $reloadStarted=[DateTime]::UtcNow; Start-App $AppPath }
    }
    if($Offline -or $NoRestart) { Complete-Report 'SettingsSaved' '持久设置已保存；尚未确认软件重载或 DNS 网络效果。' 0 }
    $verified=$false
    for($attempt=0;$attempt -lt 15;$attempt++) {
        Start-Sleep -Seconds 1
        try {
            $fresh=(Get-Item -LiteralPath (Join-Path $DataDir 'config.yaml')).LastWriteTimeUtc -ge $reloadStarted
            $coreRunning=@(Get-AppProcesses $AppPath | Where-Object { $_.Name -eq 'FlClashCore' }).Count -gt 0
            $verified=$fresh -and $coreRunning -and (Verify-Runtime $DataDir $plan.Group $plan.Ports)
        } catch { $verified=$false }
        if($verified) { break }
    }
    if($verified) {
        $script:Report.reloadConfirmed=$true; $script:Report.generatedConfigVerified=$true
        Complete-Report 'GeneratedConfigVerified' '软件重载与生成配置核对通过；未验证 DNS 网络效果。' 0
    }
    Complete-Report 'ReloadUnconfirmed' '设置已保存，软件重载结果待确认；可使用备份撤回本次改动。' 0
} catch {
    $message=$script:SafeFailureMessage
    $status='Failed'; $code=1
    if($script:SelectionNeeded) { $status='SelectionRequired'; $code=2; $message='请从候选中明确选择安装路径或代理组后重新预览。' }
    if(-not $NonInteractive) { $message=$_.Exception.Message }
    try { Complete-Report $status $message $code } catch { [Console]::Error.WriteLine('结构化报告无法保存，结果未确认。'); exit 1 }
}
