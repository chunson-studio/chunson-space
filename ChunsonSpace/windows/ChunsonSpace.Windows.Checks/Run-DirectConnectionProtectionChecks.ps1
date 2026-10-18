param(
    [Parameter(Mandatory=$true)][string]$FixtureRoot,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [string]$ProtectionScript = ''
)
$ErrorActionPreference = 'Stop'
if (-not $ProtectionScript) { $ProtectionScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'ChunsonSpace.Windows/DirectConnectionProtection.ps1' }
$fixture = [IO.Path]::GetFullPath($FixtureRoot)
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $fixture) { throw 'FixtureRoot must be new' }
if (Test-Path -LiteralPath $evidence) { throw 'EvidenceDirectory must be new' }
[void][IO.Directory]::CreateDirectory($fixture)
[void][IO.Directory]::CreateDirectory($evidence)
$script:rules = [Collections.Generic.List[object]]::new()
$script:activeOnlyRules = @()
$script:mutations = 0
$script:checks = 0
function Check([bool]$condition, [string]$name) { if (-not $condition) { throw $name }; $script:checks++; Write-Output ('PASS ' + $name) }
# All commands used by the production script are intercepted in this scope.
function Get-NetFirewallRule {
    [CmdletBinding()]param([string]$PolicyStore, [string]$Name)
    $items = @($script:rules.ToArray())
    if ($PolicyStore -eq 'ActiveStore') { $items += $script:activeOnlyRules }
    return @($items | Where-Object { -not $Name -or $_.Name -eq $Name })
}
function Get-NetFirewallProfile {
    [CmdletBinding()]param([string]$PolicyStore)
    return @(1..3 | ForEach-Object { [pscustomobject]@{Enabled=$true; AllowLocalFirewallRules='NotConfigured'} })
}
function Get-NetFirewallApplicationFilter { [CmdletBinding()]param([Parameter(ValueFromPipeline=$true)]$InputObject) process { throw 'Unexpected rule matching in retirement fixture' } }
function Get-NetFirewallAddressFilter { [CmdletBinding()]param([Parameter(ValueFromPipeline=$true)]$InputObject) process { throw 'Unexpected rule matching in retirement fixture' } }
function Get-NetFirewallPortFilter { [CmdletBinding()]param([Parameter(ValueFromPipeline=$true)]$InputObject) process { throw 'Unexpected rule matching in retirement fixture' } }
function New-NetFirewallRule { [CmdletBinding()]param() throw 'Retired feature must never create a rule' }
function Set-NetFirewallProfile { [CmdletBinding()]param() throw 'Global firewall changes forbidden' }
function Remove-NetFirewallRule {
    [CmdletBinding()]param([Parameter(ValueFromPipeline=$true)]$InputObject)
    process { $script:mutations++; $null = $script:rules.Remove($InputObject) }
}
$program = Join-Path $fixture 'ChunsonSwitch.exe'
[IO.File]::WriteAllText($program, 'fixture-never-executed')
$request = @{ProgramPaths=@($program); LocalProxy='http://127.0.0.1:7890'}
$source = [IO.File]::ReadAllText([IO.Path]::GetFullPath($ProtectionScript)) -replace '(?m)^exit \$code\s*$', ''
$block = [ScriptBlock]::Create($source)
function Invoke-Fixture([string]$action, [string]$name) {
    $path = Join-Path $evidence ($name + '.json')
    & $block -Action $action -RequestJson ($request | ConvertTo-Json -Depth 5) -ReportPath $path
    return ([IO.File]::ReadAllText($path) | ConvertFrom-Json)
}
$r = Invoke-Fixture Apply 'apply-retired'
Check ($r.status -eq 'Failed' -and $r.errorCode -eq 'feature-retired' -and $script:mutations -eq 0) 'retired apply rejects valid request without rule mutation'
$r = Invoke-Fixture Inspect 'inspect-disabled'
Check ($r.status -eq 'Disabled' -and -not $r.rulesVerified -and -not $r.networkVerified) 'empty stores report disabled without network claims'
$r = Invoke-Fixture Preview 'preview'
Check ($r.status -eq 'PreviewReady' -and $script:mutations -eq 0) 'preview remains read-only'
$owned = [pscustomobject]@{Name='ChunsonSwitch.NoDirect.v1.fixture'; Group='ChunsonSwitch.DirectConnectionProtection.v1'}
$foreign = [pscustomobject]@{Name='OtherProduct.rule'; Group='OtherProduct'}
$samePrefix = [pscustomobject]@{Name='ChunsonSwitch.NoDirect.v1.foreign'; Group='OtherProduct'}
$sameGroup = [pscustomobject]@{Name='OtherProduct.same-group'; Group='ChunsonSwitch.DirectConnectionProtection.v1'}
foreach ($item in @($owned,$foreign,$samePrefix,$sameGroup)) { $script:rules.Add($item) }
$r = Invoke-Fixture Preview 'preview-stale'
Check ($r.status -eq 'ProtectionStale' -and $script:mutations -eq 0) 'legacy rule preview reports stale without repair'
$r = Invoke-Fixture Apply 'apply-stale-retired'
Check ($r.errorCode -eq 'feature-retired' -and $script:rules.Count -eq 4 -and $script:mutations -eq 0) 'retired apply never rewrites existing rules'
$request.ProgramPaths=@()
$r = Invoke-Fixture Inspect 'unknown-targets'
Check ($r.status -eq 'Uncertain' -and -not $r.rulesVerified) 'unknown targets do not imply protection'
$r = Invoke-Fixture Restore 'restore-owned'
Check ($r.status -eq 'Restored' -and $script:rules.Count -eq 3 -and $script:mutations -eq 1) 'restore removes exact owned group and prefix only'
Check ($script:rules.Contains($foreign) -and $script:rules.Contains($samePrefix) -and $script:rules.Contains($sameGroup)) 'foreign prefix-only and group-only rules survive'
$script:activeOnlyRules=@($owned)
$r = Invoke-Fixture Inspect 'active-only'
Check ($r.status -eq 'Uncertain') 'active-store residue prevents disabled claim'
$r = Invoke-Fixture Restore 'restore-active-only'
Check ($r.status -eq 'Uncertain' -and $r.errorCode -eq 'restore-not-confirmed' -and $script:mutations -eq 1) 'restore requires active-store removal confirmation'
$script:activeOnlyRules=@()
$r = Invoke-Fixture Restore 'restore-idempotent'
Check ($r.status -eq 'Restored' -and $script:mutations -eq 1) 'repeated cleanup preserves unrelated rules'
$r = Invoke-Fixture Inspect 'foreign-only'
Check ($r.status -eq 'Disabled' -and $script:rules.Count -eq 3) 'foreign rules do not count as owned protection'
[IO.File]::WriteAllText((Join-Path $evidence 'summary.json'), (@{passed=$true; checks=$script:checks; actualFirewallChanges=$false; actualNetworkVerification=$false} | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Output ($script:checks.ToString() + ' retired direct protection fake-command checks passed.')
