param(
    [ValidateSet('Preview','Apply','Restore','Inspect')][string]$Action = 'Preview',
    [Parameter(Mandatory=$true)][string]$RequestJson,
    [Parameter(Mandatory=$true)][string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$group = 'ChunsonSwitch.DirectConnectionProtection.v1'
$prefix = 'ChunsonSwitch.NoDirect.v1.'
# Block all IPv4/IPv6 destinations except native and mapped loopback.
$remote = @('0.0.0.0-126.255.255.255','128.0.0.0-255.255.255.255','::2-::ffff:7eff:ffff','::ffff:8000:0-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff')
$report = [ordered]@{schema=1;action=$Action;status='Failed';message='';programPaths=@();rulesVerified=$false;networkVerified=$false}
$code = 1
function Rule-Name([string]$path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($path.ToLowerInvariant())) }
    finally { $sha.Dispose() }
    return $prefix + ([BitConverter]::ToString($hash).Replace('-','').Substring(0,24))
}
function Own-Rules([string]$store='PersistentStore') {
    return @(Get-NetFirewallRule -PolicyStore $store -ErrorAction Stop | Where-Object { $_.Group -eq $group -and $_.Name.StartsWith($prefix,[StringComparison]::Ordinal) })
}
function Address-Key([string]$range) {
    # Windows may render mapped IPv6 bounds as dotted IPv4. Compare address bytes.
    return (($range -split '-' | ForEach-Object {
        $address = $null
        if (-not [Net.IPAddress]::TryParse($_,[ref]$address)) { throw 'invalid-firewall-address' }
        [BitConverter]::ToString($address.GetAddressBytes())
    }) -join '/')
}
function Rule-Matches($rule, [string]$path) {
    if ([string]$rule.Direction -ne 'Outbound' -or [string]$rule.Action -ne 'Block' -or [string]$rule.Enabled -ne 'True' -or [string]$rule.Profile -ne 'Any') { return $false }
    $app = @($rule | Get-NetFirewallApplicationFilter -ErrorAction Stop)
    $address = @($rule | Get-NetFirewallAddressFilter -ErrorAction Stop)
    $port = @($rule | Get-NetFirewallPortFilter -ErrorAction Stop)
    if ($app.Count -ne 1 -or $address.Count -ne 1 -or $port.Count -ne 1 -or [string]$port[0].Protocol -ne 'Any' -or -not ([string]$app[0].Program).Equals($path,[StringComparison]::OrdinalIgnoreCase)) { return $false }
    return (@($address[0].RemoteAddress | ForEach-Object { Address-Key $_ } | Sort-Object) -join '|') -eq (@($remote | ForEach-Object { Address-Key $_ } | Sort-Object) -join '|')
}
try {
    if ($Action -eq 'Apply') { throw 'feature-retired' }
    $request = $RequestJson | ConvertFrom-Json
    if ($Action -in @('Preview','Apply')) {
        $endpoint = $null
        if (-not [Uri]::TryCreate([string]$request.LocalProxy,[UriKind]::Absolute,[ref]$endpoint) -or $endpoint.Scheme -notin @('http','https') -or $endpoint.UserInfo.Length -gt 0 -or $endpoint.AbsolutePath -ne '/' -or $endpoint.Query.Length -gt 0 -or $endpoint.Fragment.Length -gt 0) { throw 'invalid-local-proxy' }
        $ip = $null
        if ($endpoint.Host -ne 'localhost' -and (-not [Net.IPAddress]::TryParse($endpoint.Host.Trim('[',']'),[ref]$ip) -or -not [Net.IPAddress]::IsLoopback($ip))) { throw 'proxy-is-not-loopback' }
    }
    if ($Action -ne 'Restore') {
        $paths = @($request.ProgramPaths | ForEach-Object {
            $p = [string]$_
            if (-not [IO.Path]::IsPathRooted($p) -or [IO.Path]::GetExtension($p) -ne '.exe' -or -not [IO.File]::Exists($p)) { throw 'invalid-program-path' }
            [IO.Path]::GetFullPath($p)
        } | Sort-Object -Unique)
        if (($paths.Count -eq 0 -and $Action -ne 'Inspect') -or $paths.Count -gt 512) { throw 'invalid-program-count' }
        $report.programPaths = @($paths)
    }
    $own = Own-Rules
    $active = Own-Rules 'ActiveStore'
    if ($Action -eq 'Restore') {
        foreach ($rule in $own) { $rule | Remove-NetFirewallRule -ErrorAction Stop }
        if ((Own-Rules).Count -ne 0 -or (Own-Rules 'ActiveStore').Count -ne 0) { throw 'restore-not-confirmed' }
        $report.status = 'Restored'; $report.message = '已移除 Chunson Switch 自身防直连规则；其他防火墙设置保持原状。'; $code = 0
    } elseif ($Action -eq 'Inspect' -and $own.Count -eq 0 -and $active.Count -eq 0) {
        $report.status = 'Disabled'; $report.message = '系统防护已关闭：未发现本项目的禁止直连规则。Windows 防火墙和 DNS 配置未修改。'; $code = 0
    } else {
        if ($paths.Count -eq 0) { throw 'protection-targets-unknown' }
        $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop)
        if ($profiles.Count -ne 3 -or @($profiles | Where-Object { -not $_.Enabled -or [string]$_.AllowLocalFirewallRules -eq 'False' }).Count -gt 0) { throw 'firewall-disabled-or-local-rules-disallowed' }
        $missing = @($paths | Where-Object { $p=$_; @($own | Where-Object { $_.Name -eq (Rule-Name $p) -and (Rule-Matches $_ $p) }).Count -ne 1 })
        if ($missing.Count -eq 0) {
            # Read ActiveStore as well: persisted rules alone do not prove policy is effective.
            $active = @(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction Stop | Where-Object { $_.Group -eq $group })
            foreach ($p in $paths) { if (@($active | Where-Object { $_.Name -eq (Rule-Name $p) -and (Rule-Matches $_ $p) }).Count -ne 1) { throw 'effective-rules-not-confirmed' } }
            $report.status = 'Configured'; $report.rulesVerified = $true; $report.message = '所选程序的出站规则已保存并回读，只保留本机回环连接。实际网络阻断尚未验证。'
        } elseif ($own.Count -gt 0 -or $active.Count -gt 0) {
            $report.status = 'ProtectionStale'; $report.message = '程序路径或规则已变化，发现旧规则，功能已移除，请使用清理命令移除。'
        } else {
            $report.status = 'PreviewReady'; $report.message = '未发现旧规则；系统防护功能已移除。'
        }
        $code = 0
    }
} catch {
    $report.status = if ($Action -in @('Preview','Apply')) { 'Failed' } else { 'Uncertain' }
    $report.message = '应用防直连配置未确认，请检查管理员授权、防火墙策略与程序路径后重新预览。实际网络验证未执行。'
    $report.errorCode = if ($_.Exception.Message -match '^[a-z]+(-[a-z]+)+$') { $_.Exception.Message } else { $_.Exception.GetType().Name }
} finally {
    $folder = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReportPath))
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    [IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
exit $code
