param(
    [Parameter(Mandatory=$true)][string]$FixtureRoot,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [string]$ProtectionScript = ''
)
$ErrorActionPreference = 'Stop'
if (-not $ProtectionScript) { $ProtectionScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'FAswitch.Windows/DirectConnectionProtection.ps1' }
$fixture = [IO.Path]::GetFullPath($FixtureRoot)
if (Test-Path -LiteralPath $fixture) { throw 'FixtureRoot must be a new directory' }
[IO.Directory]::CreateDirectory($fixture) | Out-Null
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($EvidenceDirectory)) | Out-Null
$script:rules = [Collections.Generic.List[object]]::new()
$script:activeOnlyRules = @()
$script:profilesEnabled = $true
$script:hideActiveRules = $false
$script:mutations = 0
$script:checks = 0
function Check([bool]$condition,[string]$name) { if (-not $condition) { throw $name }; $script:checks++; Write-Output ('PASS '+$name) }
function Get-NetFirewallRule {
    [CmdletBinding()]param([string]$PolicyStore,[string]$Name)
    if ($PolicyStore -eq 'ActiveStore' -and $script:hideActiveRules) { return @() }
    $items = @($script:rules.ToArray())
    if ($PolicyStore -eq 'ActiveStore') { $items += $script:activeOnlyRules }
    return @($items | Where-Object { -not $Name -or $_.Name -eq $Name })
}
function Get-NetFirewallProfile {
    [CmdletBinding()]param([string]$PolicyStore)
    return @(1..3 | ForEach-Object { [pscustomobject]@{Enabled=$script:profilesEnabled;AllowLocalFirewallRules='NotConfigured'} })
}
function Get-NetFirewallApplicationFilter {
    [CmdletBinding()]param([Parameter(ValueFromPipeline=$true)]$InputObject)
    process { [pscustomobject]@{Program=$InputObject.Program} }
}
function Get-NetFirewallAddressFilter {
    [CmdletBinding()]param([Parameter(ValueFromPipeline=$true)]$InputObject)
    process { [pscustomobject]@{RemoteAddress=$InputObject.RemoteAddress} }
}
function Get-NetFirewallPortFilter {
    [CmdletBinding()]param([Parameter(ValueFromPipeline=$true)]$InputObject)
    process { [pscustomobject]@{Protocol=$InputObject.Protocol} }
}
function New-NetFirewallRule {
    [CmdletBinding()]param([string]$PolicyStore,[string]$Name,[string]$DisplayName,[string]$Group,[string]$Direction,[string]$Action,[string]$Enabled,[string]$Profile,[string]$Program,[string]$Protocol,[string[]]$RemoteAddress)
    if ($Direction -ne 'Outbound' -or $Action -ne 'Block' -or $Program -notlike ($fixture+'*') -or $Profile -ne 'Any' -or $Protocol -ne 'Any') { throw 'unsafe-fixture-rule' }
    $script:mutations++
    $rule = [pscustomobject]@{Name=$Name;DisplayName=$DisplayName;Group=$Group;Direction=$Direction;Action=$Action;Enabled=$Enabled;Profile=$Profile;Program=$Program;Protocol=$Protocol;RemoteAddress=$RemoteAddress}
    $script:rules.Add($rule); return $rule
}
function Remove-NetFirewallRule {
    [CmdletBinding()]param([Parameter(ValueFromPipeline=$true)]$InputObject)
    process { $script:mutations++; $null=$script:rules.Remove($InputObject) }
}
$program1 = Join-Path $fixture 'Codex.exe'
$program2 = Join-Path $fixture 'ChunsonSwitch.exe'
[IO.File]::WriteAllText($program1,'fake-executable')
[IO.File]::WriteAllText($program2,'fake-executable')
$request = @{ProgramPaths=@($program1,$program2);LocalProxy='http://127.0.0.1:7890'}
$source = [IO.File]::ReadAllText([IO.Path]::GetFullPath($ProtectionScript)) -replace '(?m)^exit \$code\s*$',''
$block = [ScriptBlock]::Create($source)
function Invoke-Fixture([string]$action,[string]$name) {
    $path = Join-Path $EvidenceDirectory ($name+'.json')
    & $block -Action $action -RequestJson ($request | ConvertTo-Json -Depth 5) -ReportPath $path
    return ([IO.File]::ReadAllText($path) | ConvertFrom-Json)
}
try {
    $request.ProgramPaths=@(); $request.LocalProxy=''
    $r = Invoke-Fixture Inspect 'inspect-disabled'
    Check ($r.status -eq 'Disabled' -and -not $r.rulesVerified -and $script:mutations -eq 0) 'inspect confirms disabled without proxy or installed targets'
    $request.ProgramPaths=@($program1,$program2); $request.LocalProxy='http://127.0.0.1:7890'
    $r = Invoke-Fixture Preview 'preview'
    Check ($r.status -eq 'PreviewReady' -and $script:mutations -eq 0) 'preview reads only without creating firewall rules'
    $r = Invoke-Fixture Apply 'apply'
    Check ($r.status -eq 'Configured' -and $r.rulesVerified -and -not $r.networkVerified -and $script:rules.Count -eq 2) 'apply creates and rereads one scoped block per program'
    $addresses = @($script:rules[0].RemoteAddress)
    Check ($addresses -contains '0.0.0.0-126.255.255.255' -and $addresses -contains '128.0.0.0-255.255.255.255') 'IPv4 ranges include LAN and public destinations while preserving 127/8'
    Check ($addresses -contains '::2-::ffff:7eff:ffff' -and $addresses -contains '::ffff:8000:0-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff') 'IPv6 ranges preserve native and mapped loopback'
    $before = $script:mutations
    $request.LocalProxy=''
    $r = Invoke-Fixture Inspect 'inspect-enabled'
    Check ($r.status -eq 'Configured' -and $r.rulesVerified -and $script:mutations -eq $before) 'inspect verifies effective coverage without probing a proxy or writing rules'
    $request.ProgramPaths=@()
    $r = Invoke-Fixture Inspect 'inspect-targets-unknown'
    Check ($r.status -eq 'Uncertain' -and -not $r.rulesVerified -and $script:mutations -eq $before) 'existing rules with unknown targets cannot claim enabled'
    $request.ProgramPaths=@($program1,$program2); $request.LocalProxy='http://127.0.0.1:7890'
    $r = Invoke-Fixture Apply 'idempotent'
    Check ($r.status -eq 'Configured' -and $script:mutations -eq $before) 'identical apply avoids duplicate or unrelated rules'
    $script:rules[0].RemoteAddress = @('0.0.0.0-126.255.255.255','128.0.0.0-255.255.255.255','::2-::ffff:126.255.255.255','::ffff:128.0.0.0-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff')
    $r = Invoke-Fixture Apply 'mapped-ipv6-normalized'
    Check ($r.status -eq 'Configured' -and $script:mutations -eq $before) 'Windows mapped IPv6 canonical rendering does not recreate valid rules'
    $script:rules[0].RemoteAddress[2] = '::3-::ffff:126.255.255.255'
    $r = Invoke-Fixture Preview 'mapped-ipv6-gap'
    Check ($r.status -eq 'ProtectionStale' -and -not $r.rulesVerified) 'one address gap remains unprotected after normalization'
    $script:rules[0].RemoteAddress[2] = '::2-::ffff:126.255.255.255'
    $script:rules[0].Protocol='TCP'
    $r = Invoke-Fixture Preview 'narrow-rule'
    Check ($r.status -eq 'ProtectionStale' -and -not $r.rulesVerified) 'narrow TCP-only rule cannot claim all protocol protection'
    $script:rules[0].Protocol='Any'
    $script:hideActiveRules = $true
    $r = Invoke-Fixture Preview 'active-missing'
    Check ($r.status -eq 'Failed' -and -not $r.rulesVerified) 'persistent rules alone cannot prove effective protection'
    $script:hideActiveRules = $false
    $program3 = Join-Path $fixture 'updated-codex.exe'; [IO.File]::WriteAllText($program3,'updated-fake')
    $request.ProgramPaths = @($program1,$program2,$program3)
    $r = Invoke-Fixture Preview 'version-change'
    Check ($r.status -eq 'ProtectionStale' -and -not $r.rulesVerified) 'new executable version path exposes stale coverage'
    $before = $script:mutations
    $r = Invoke-Fixture Inspect 'inspect-version-change'
    Check ($r.status -eq 'ProtectionStale' -and -not $r.rulesVerified -and $script:mutations -eq $before) 'inspect reports stale coverage without repairing automatically'
    $request.LocalProxy='http://192.168.1.1:7890'
    $before = $script:mutations
    $r = Invoke-Fixture Apply 'remote-proxy'
    Check ($r.status -eq 'Uncertain' -and $script:mutations -eq $before) 'remote proxy cannot cause firewall mutation'
    $request.LocalProxy='http://[::1]:7890'
    $r = Invoke-Fixture Preview 'ipv6-proxy'
    Check ($r.status -eq 'ProtectionStale') 'IPv6 local HTTP proxy is accepted'
    $script:profilesEnabled = $false
    $before = $script:mutations
    $r = Invoke-Fixture Apply 'firewall-disabled'
    Check ($r.status -eq 'Uncertain' -and $script:mutations -eq $before) 'disabled firewall fails without changing global profiles'
    $r = Invoke-Fixture Inspect 'inspect-firewall-disabled'
    Check ($r.status -eq 'Uncertain' -and -not $r.rulesVerified -and $script:mutations -eq $before) 'inspect does not claim protection with disabled firewall'
    $script:profilesEnabled = $true
    $foreign = [pscustomobject]@{Name='OtherProduct.rule';Group='OtherProduct';Program=$program1;Direction='Outbound';Action='Allow';Enabled='True';Profile='Any';RemoteAddress=@('Any')}
    $script:rules.Add($foreign)
    $activeCopy = $script:rules[0].PSObject.Copy()
    $r = Invoke-Fixture Restore 'restore'
    Check ($r.status -eq 'Restored' -and $script:rules.Count -eq 1 -and $script:rules[0].Name -eq 'OtherProduct.rule') 'restore removes only own exact group and prefix'
    $request.ProgramPaths=@(); $request.LocalProxy=''
    $before = $script:mutations
    $script:activeOnlyRules=@($activeCopy)
    $r = Invoke-Fixture Inspect 'inspect-active-only'
    Check ($r.status -eq 'Uncertain' -and $script:mutations -eq $before) 'active-only owned rule prevents a false disabled state'
    $r = Invoke-Fixture Restore 'restore-active-only'
    Check ($r.status -eq 'Uncertain' -and $script:rules.Count -eq 1) 'restore requires active store removal confirmation'
    $script:activeOnlyRules=@()
    $r = Invoke-Fixture Inspect 'inspect-foreign-only'
    Check ($r.status -eq 'Disabled' -and $script:rules[0].Name -eq 'OtherProduct.rule') 'disabled status ignores other products rules'
    Check (-not $source.Contains('Set-NetFirewallProfile') -and -not $source.Contains('chrome.exe')) 'script never modifies default firewall policy or regular Chrome'
    [IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'summary.json'), (@{passed=$true;checks=$script:checks;actualFirewallChanges=$false;actualNetworkVerification=$false} | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Write-Output ($script:checks.ToString()+' direct connection protection fake-command checks passed.')
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ($resolved -ne $fixture -or $resolved -eq [IO.Path]::GetPathRoot($resolved)) { throw 'unsafe fixture cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
