# Shared functions for FlClash-DNS.ps1 -AllSubscriptions. No standalone entry point.
function New-SubscriptionDnsCode([string]$Original) {
    $body=$Original; if(-not $body.Trim()) { $body='const main = config => config;' }
    $template=@'
// CHUNSON_ALL_SUBSCRIPTIONS_DNS_V1
// original-base64: __ORIGINAL_BASE64__
const chunsonOriginalMain = (() => {
__ORIGINAL__
return typeof main === "function" ? main : (config => config);
})();
const main = (input) => {
  const config = chunsonOriginalMain(input);
  if (!config || typeof config !== "object" || typeof config.then === "function")
    throw new Error("Subscription script must return a configuration synchronously");
  const groups = Array.isArray(config["proxy-groups"]) ? config["proxy-groups"] : [];
  const rules = Array.isArray(config.rules) ? config.rules : [];
  const terminal = rules.find(rule => typeof rule === "string" && /^(MATCH|FINAL),/i.test(rule));
  const target = terminal && terminal.split(",")[1];
  const candidates = groups.filter(group => group && typeof group.name === "string" &&
    !/[#,\r\n]/.test(group.name) && !/^(DIRECT|REJECT|PASS)$/i.test(group.name));
  const route = candidates.find(group => group.name === target) || candidates[0];
  if (!route) throw new Error("No proxy group available for encrypted DNS");
  config.ipv6 = false;
  config.tun = { ...(config.tun || {}), enable: true, "auto-route": true,
    "strict-route": true, "dns-hijack": __HIJACK__ };
  const policy = { ...((config.dns || {})["nameserver-policy"] || {}) };
  delete policy["www.baidu.com"];
  for (const key of __POLICY_KEYS__) policy[key] = __DOMESTIC__;
  config.dns = { ...(config.dns || {}), enable: true, ipv6: false,
    "respect-rules": true, "prefer-h3": false, fallback: [],
    nameserver: __PROXY_DNS__.map(server => server + "#" + route.name),
    "default-nameserver": [__DOMESTIC__], "proxy-server-nameserver": [__DOMESTIC__], "direct-nameserver": [__DOMESTIC__],
    "nameserver-policy": policy };
  return config;
};
'@
    return $template.Replace('__ORIGINAL_BASE64__',[Convert]::ToBase64String($script:Utf8.GetBytes($Original))).Replace('__HIJACK__',(Json $script:TunDnsHijack)).Replace('__POLICY_KEYS__',(Json (@($script:DomesticPolicyKey)+@($script:DirectDomains | ForEach-Object { '+.'+$_ })))).Replace('__DOMESTIC__',(Json $script:DomesticDns)).Replace('__PROXY_DNS__',(Json $script:ProxyDns)).Replace('__ORIGINAL__',$body).Replace("`r`n","`n").Trim()+"`n"
}
function Get-SubscriptionOriginal([string]$Code) {
    if($Code.StartsWith('// CHUNSON_ALL_SUBSCRIPTIONS_DNS_V1')) {
        $match=[regex]::Match($Code,'(?m)^// original-base64: ([A-Za-z0-9+/=]*)\r?$')
        if(-not $match.Success) { Fail-Plan '已有统一 DNS 脚本格式损坏，未修改配置。' }
        $original=$script:Utf8.GetString([Convert]::FromBase64String($match.Groups[1].Value))
        if($Code.Replace("`r`n","`n") -cne (New-SubscriptionDnsCode $original)) { Fail-Plan '统一 DNS 脚本被手动修改，请先保留修改后再配置。' }
        return $original
    }
    return $Code
}
function Get-AllSubscriptionsPlan([string]$Root) {
    $prefs=Join-Path $Root 'shared_preferences.json'
    Assert-NoLinks $prefs
    Assert-NoLinks (Join-Path $Root 'database.sqlite')
    Assert-NoLinks (Join-Path $Root 'scripts')
    Assert-NoLinks (Join-Path $Root 'profiles')
    Require-ConfigurationFile $prefs 'FlClash 偏好设置'
    $outer=Get-Json $prefs; $config=(Get-Value $outer 'flutter.config') | ConvertFrom-Json
    foreach($path in @('patchClashConfig.dns','patchClashConfig.tun','networkProps','appSettingProps')) {
        if(-not (Get-Slot $config $path).Exists) { Fail-Plan 'FlClash 配置结构不兼容，未修改。' }
    }
    $changes=New-Object 'System.Collections.Generic.List[object]'
    # DNS is supplied after each subscription's own script. A global nameserver
    # referring to the previously selected group must not overwrite that result.
    if($null -ne $config.PSObject.Properties['overrideDns']) { Put-Change $config $changes 'overrideDns' $false }
    $mask=Get-Slot $config 'patchClashConfig.dns-override-keys'
    if($mask.Exists) {
        $managed=@('enable','ipv6','respect-rules','prefer-h3','fallback','nameserver','default-nameserver','proxy-server-nameserver','direct-nameserver','nameserver-policy')
        Put-Change $config $changes 'patchClashConfig.dns-override-keys' @($mask.Value | Where-Object { $_ -notin $managed })
    }
    Put-Change $config $changes 'patchClashConfig.ipv6' $false
    Set-ManagedTunChanges $config $changes
    Put-Change $config $changes 'networkProps.appendSystemDns' $false
    Put-Change $config $changes 'appSettingProps.autoRun' $true
    Set-Value $outer 'flutter.config' (Json $config)
    # Read only the local listener metadata; never include authentication values.
    $runtimePath=Join-Path $Root 'config.yaml'
    if(Test-Path -LiteralPath $runtimePath) {
        Require-ConfigurationFile $runtimePath 'FlClash 运行配置'
        $script:Report.proxyPorts=@(Get-RuntimeProxyPorts (Read-Yaml $runtimePath))
    }
    Require-ConfigurationFile (Join-Path $Root 'database.sqlite') 'FlClash 数据库'
    $db=[FlDns.SqliteDb]::new((Join-Path $Root 'database.sqlite'),$false)
    $profiles=New-Object 'System.Collections.Generic.List[object]'
    try {
        $schema=Assert-Schema $db
        $ids=@($db.Query('SELECT id FROM profiles ORDER BY id'))
        if($ids.Count -eq 0) { Fail-Plan '请先在 FlClash 导入订阅。' }
        $newId=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        foreach($entry in $ids) {
            $id=[long]$entry['id']; $row=Get-ProfileBinding $db $schema $id
            if($row['overwrite_type'] -notin @('standard','script')) { Fail-Plan "订阅 $id 的扩展方式暂不支持，未修改任何订阅。" }
            if($schema.Modern) {
                if($row['profile_type'] -notin @('file','url')) { Fail-Plan "订阅 $id 的类型暂不支持，未修改任何订阅。" }
                if($row['overrides'] -and $row['overrides'] -notin @('{}','null')) { Fail-Plan "订阅 $id 有单独覆写设置，需先合并；未修改任何订阅。" }
            }
            $raw=Join-Path $Root "profiles\$id.yaml"; Assert-NoLinks $raw; Require-ConfigurationFile $raw "订阅 $id"
            $oldCode=''; $oldHash=$null
            if($row['overwrite_type'] -eq 'script') {
                if($null -eq $row['script_id']) { Fail-Plan "订阅 $id 脚本绑定缺失。" }
                $path=Join-Path $Root "scripts\$($row['script_id']).js"; Require-ConfigurationFile $path "订阅 $id 脚本"
                Assert-NoLinks $path
                $oldCode=[IO.File]::ReadAllText($path); $oldHash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            } else {
                $custom=@($db.Query("SELECT COUNT(*) AS n FROM proxy_groups WHERE profile_id=$id"))[0]['n'] + @($db.Query("SELECT COUNT(*) AS n FROM $($schema.RuleTable) WHERE profile_id=$id"))[0]['n']
                if($custom -gt 0) { Fail-Plan "订阅 $id 有标准模式自定义分流，请先转为脚本；未修改任何订阅。" }
            }
            $code=New-SubscriptionDnsCode (Get-SubscriptionOriginal $oldCode)
            do { $newId++ } while(@($db.Query("SELECT id FROM scripts WHERE id=$newId")).Count -gt 0 -or (Test-Path -LiteralPath (Join-Path $Root "scripts\$newId.js")))
            $profiles.Add([pscustomobject]@{Id=$id;OldType=$row['overwrite_type'];OldScriptId=$row['script_id'];OldHash=$oldHash;NewScriptId=$newId;Code=$code;Changed=($oldCode.Replace("`r`n","`n") -cne $code)}) | Out-Null
        }
    } finally { $db.Dispose() }
    $script:Report.configurationStatus='Ready'; $script:Report.scope='all-subscriptions'; $script:Report.subscriptionCount=$profiles.Count
    $script:Report.changedFields=@($changes | ForEach-Object { $_.Path }) + @($profiles | Where-Object Changed | ForEach-Object { 'profiles/'+$_.Id+'/dns-script' })
    return @{Outer=$outer;Config=$config;Profiles=$profiles;Changes=$changes;Schema=$schema;PreferenceHash=(Get-FileHash -LiteralPath $prefs -Algorithm SHA256).Hash}
}
function Apply-AllSubscriptions([string]$Root,$Plan) {
    $changed=@($Plan.Profiles | Where-Object Changed)
    if($changed.Count -eq 0 -and $Plan.Changes.Count -eq 0) { return $null }
    $base=Join-Path $env:LOCALAPPDATA 'FlClash-DNS-Backups'; if($Offline) { $base=Join-Path $Root 'dns-backups' }
    Assert-NoLinks $base
    $backup=Join-Path $base ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
    [IO.Directory]::CreateDirectory($backup) | Out-Null
    $script:Report.backupDirectory=$backup
    $prefs=Join-Path $Root 'shared_preferences.json'; $before=[IO.File]::ReadAllBytes($prefs)
    $meta=[pscustomobject]@{toolVersion=3;scope='all-subscriptions';status='pending';dataDir=$Root;changes=@($Plan.Changes.ToArray());profiles=@()}
    $created=New-Object 'System.Collections.Generic.List[string]'; $transaction=$false; $committed=$false; $wrote=$false
    $db=[FlDns.SqliteDb]::new((Join-Path $Root 'database.sqlite'),$true)
    try {
        $db.Backup((Join-Path $backup 'database.sqlite')); [IO.File]::WriteAllBytes((Join-Path $backup 'shared_preferences.json'),$before)
        $db.Execute('BEGIN IMMEDIATE'); $transaction=$true
        if((Get-FileHash -LiteralPath $prefs -Algorithm SHA256).Hash -cne $Plan.PreferenceHash) { throw '偏好设置已变化。' }
        foreach($profile in $Plan.Profiles) {
            $fresh=Get-ProfileBinding $db $Plan.Schema $profile.Id
            if($fresh['overwrite_type'] -cne $profile.OldType -or $fresh['script_id'] -ne $profile.OldScriptId) { throw '订阅绑定已变化。' }
            if($profile.OldHash -and (Get-FileHash -LiteralPath (Join-Path $Root "scripts\$($profile.OldScriptId).js") -Algorithm SHA256).Hash -cne $profile.OldHash) { throw '原脚本已变化。' }
        }
        foreach($profile in $changed) {
            $path=Join-Path $Root "scripts\$($profile.NewScriptId).js"; Assert-NoLinks $path
            if(Test-Path -LiteralPath $path) { throw '新脚本路径已存在。' }
            Write-Atomic $path $profile.Code; $created.Add($path) | Out-Null
            $stamp=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            $db.Execute("INSERT INTO scripts(id,label,last_update_time) VALUES($($profile.NewScriptId),'Chunson DNS',$stamp)")
            $db.Execute("UPDATE profiles SET $($Plan.Schema.TypeColumn)='script',script_id=$($profile.NewScriptId) WHERE id=$($profile.Id)")
            $meta.profiles+=@([pscustomobject]@{id=$profile.Id;oldType=$profile.OldType;oldScriptId=$profile.OldScriptId;oldHash=$profile.OldHash;newScriptId=$profile.NewScriptId;newHash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash})
        }
        Write-Atomic (Join-Path $backup 'manifest.json') (Json $meta)
        Write-Atomic $prefs (Json $Plan.Outer); $wrote=$true
        if((Get-Json $prefs).'flutter.config' -cne (Json $Plan.Config)) { throw '偏好设置回读不一致。' }
        if(@($db.Query('PRAGMA quick_check'))[0]['quick_check'] -ne 'ok') { throw '数据库检查失败。' }
        $db.Execute('COMMIT'); $transaction=$false; $committed=$true; $script:Report.settingsSaved=$true
        $meta.status='applied'; Write-Atomic (Join-Path $backup 'manifest.json') (Json $meta)
        Write-Atomic (Join-Path $base 'last-backup.txt') $backup
    } catch {
        if($transaction) { $db.Execute('ROLLBACK') }
        if(-not $committed) {
            if($wrote) { Write-AtomicBytes $prefs $before }
            foreach($file in $created) { [IO.File]::Delete($file) }
            $meta.status='failed'; Write-Atomic (Join-Path $backup 'manifest.json') (Json $meta)
        }
        throw
    } finally { $db.Dispose() }
    return $backup
}
function Restore-AllSubscriptions([string]$Root,[string]$Backup) {
    $meta=Get-Json (Join-Path $Backup 'manifest.json')
    if($meta.dataDir -ine $Root -or $meta.status -cne 'applied') { throw '备份不属于本次已完成配置。' }
    $prefs=Join-Path $Root 'shared_preferences.json'; $before=[IO.File]::ReadAllBytes($prefs)
    $outer=Get-Json $prefs; $config=$outer.'flutter.config' | ConvertFrom-Json
    $reverse=@($meta.changes); [array]::Reverse($reverse)
    foreach($change in $reverse) {
        $slot=Get-ChangeSlot $config $change
        if($slot.Exists -ne $change.AfterExists -or ($slot.Exists -and (Json $slot.Value) -cne (Json $change.After))) { throw '设置后来已修改，未覆盖后续设置。' }
        if($change.BeforeExists) { Set-Value $slot.Parent $slot.Key $change.Before } else { $slot.Parent.PSObject.Properties.Remove($slot.Key) }
    }
    Set-Value $outer 'flutter.config' (Json $config)
    $db=[FlDns.SqliteDb]::new((Join-Path $Root 'database.sqlite'),$true); $transaction=$false; $wrote=$false
    try {
        $schema=Assert-Schema $db; $db.Execute('BEGIN IMMEDIATE'); $transaction=$true
        foreach($profile in $meta.profiles) {
            $id=[long]$profile.id; $newId=[long]$profile.newScriptId
            $row=Get-ProfileBinding $db $schema $id
            $path=Join-Path $Root "scripts\$newId.js"; Assert-NoLinks $path
            if($row['overwrite_type'] -cne 'script' -or $row['script_id'] -ne $newId -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $profile.newHash -or @($db.Query("SELECT COUNT(*) AS n FROM profiles WHERE script_id=$newId"))[0]['n'] -ne 1) { throw '脚本绑定或内容后来已修改，未覆盖。' }
            if($profile.oldHash -and (Get-FileHash -LiteralPath (Join-Path $Root "scripts\$($profile.oldScriptId).js") -Algorithm SHA256).Hash -cne $profile.oldHash) { throw '原脚本后来已修改，恢复停止。' }
            $oldId='NULL'; if($null -ne $profile.oldScriptId) { $oldId=[string][long]$profile.oldScriptId }
            if($profile.oldType -notin @('standard','script')) { throw '备份类型不兼容。' }
            $db.Execute("UPDATE profiles SET $($schema.TypeColumn)='$($profile.oldType)',script_id=$oldId WHERE id=$id")
            $db.Execute("DELETE FROM scripts WHERE id=$newId")
        }
        Write-Atomic $prefs (Json $outer); $wrote=$true
        $db.Execute('COMMIT'); $transaction=$false
    } catch { if($transaction) { $db.Execute('ROLLBACK') }; if($wrote) { Write-AtomicBytes $prefs $before }; throw }
    finally { $db.Dispose() }
    foreach($profile in $meta.profiles) { [IO.File]::Delete((Join-Path $Root "scripts\$([long]$profile.newScriptId).js")) }
    $meta.status='restored'; Write-Atomic (Join-Path $Backup 'manifest.json') (Json $meta)
}
function Test-AllSubscriptionsRuntime([string]$Root) {
    $runtime=Read-Yaml (Join-Path $Root 'config.yaml'); $dns=Get-Value $runtime 'dns'; $tun=Get-Value $runtime 'tun'
    if(-not $dns -or -not $tun -or $dns.enable -ne $true -or $dns.ipv6 -ne $false -or $runtime.ipv6 -ne $false -or $dns.'respect-rules' -ne $true -or @($dns.fallback).Count -ne 0 -or $tun.enable -ne $true -or $tun.'strict-route' -ne $true -or (Json $tun.'dns-hijack') -cne (Json $script:TunDnsHijack)) { return $false }
    $resolvers=@($dns.nameserver); if($resolvers.Count -ne $script:ProxyDns.Count) { return $false }
    $groupNames=@($runtime.'proxy-groups' | ForEach-Object { $_.name })
    for($i=0;$i -lt $resolvers.Count;$i++) {
        $prefix=$script:ProxyDns[$i]+'#'
        if(-not $resolvers[$i].StartsWith($prefix) -or $groupNames -cnotcontains $resolvers[$i].Substring($prefix.Length)) { return $false }
    }
    return (Json $dns.'default-nameserver') -ceq (Json @($script:DomesticDns)) -and (Json $dns.'proxy-server-nameserver') -ceq (Json @($script:DomesticDns))
}
function Invoke-AllSubscriptions {
    $plan=Get-AllSubscriptionsPlan $DataDir
    if($Action -eq 'Preview') { Complete-Report 'PreviewReady' "已读取 $($plan.Profiles.Count) 个订阅；统一 DNS 将保留各订阅原脚本与分流。尚未修改。" 0 }
    if($Action -eq 'Verify') {
        if(-not (Test-AllSubscriptionsRuntime $DataDir)) { Fail-Plan '当前生成配置未通过统一 DNS 核对。' }
        $script:Report.generatedConfigVerified=$true
        Complete-Report 'GeneratedConfigVerified' '当前订阅生成配置已核对；其他订阅与实际 DNS 网络效果未实测。' 0
    }
    if(-not $AppPath) { Complete-Report 'SelectionRequired' '请在高级设置确认 FlClash 程序位置，再配置全部订阅。' 2 }
    if(-not $Offline) { [FlDns.SqliteDb]::Load((Join-Path (Split-Path -Parent $AppPath) 'sqlite3.dll')); Stop-App $AppPath }
    try { $plan=Get-AllSubscriptionsPlan $DataDir; $backup=Apply-AllSubscriptions $DataDir $plan; $script:Report.settingsSaved=$true; if($backup) { $script:Report.backupDirectory=$backup } }
    finally { if(-not $Offline -and -not $NoRestart) { $started=[DateTime]::UtcNow; Start-App $AppPath } }
    $message="已为 $($plan.Profiles.Count) 个已保存订阅配置统一 DNS；保留原节点、分流和当前选择。新增订阅后再次点击即可补齐。"
    if($Offline -or $NoRestart) { Complete-Report 'SettingsSaved' ($message+' 尚未确认重载和实际网络效果。') 0 }
    for($attempt=0;$attempt -lt 15;$attempt++) {
        Start-Sleep -Seconds 1
        try {
            $fresh=(Get-Item -LiteralPath (Join-Path $DataDir 'config.yaml')).LastWriteTimeUtc -ge $started
            if($fresh -and @(Get-AppProcesses $AppPath | Where-Object Name -eq 'FlClashCore').Count -gt 0 -and (Test-AllSubscriptionsRuntime $DataDir)) {
                $script:Report.reloadConfirmed=$true; $script:Report.generatedConfigVerified=$true
                Complete-Report 'GeneratedConfigVerified' ($message+' 当前运行配置已核对；实际 DNS 网络效果未验证。') 0
            }
        } catch { }
    }
    Complete-Report 'ReloadUnconfirmed' ($message+' 重载未确认，可从高级设置恢复备份。') 0
}
