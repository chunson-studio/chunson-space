param([switch]$Check,[string]$OutputPath = (Join-Path $PSScriptRoot 'BeijingClock.exe'))
$ErrorActionPreference='Stop'
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $OutputPath))
$fx=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs=@('System.dll','System.Core.dll','System.Windows.Forms.dll','System.Drawing.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','System.Xaml.dll')
$args=@('/nologo','/target:winexe','/platform:x64','/optimize+',('/out:'+ $OutputPath))
$icon=Join-Path $PSScriptRoot 'Assets/BeijingClock.ico'
$args+=('/win32icon:'+ $icon),('/resource:'+ $icon + ',Chunson.BeijingClock.ico')
foreach($ref in $refs){$args+=('/reference:'+(Join-Path $fx $ref))}
$args+=(Join-Path $PSScriptRoot 'Native.cs'),(Join-Path $PSScriptRoot 'ClockVisibility.cs'),(Join-Path $PSScriptRoot 'ClockChecks.cs'),(Join-Path $PSScriptRoot 'TaskbarClock.cs')
& (Join-Path $fx 'csc.exe') @args
if($LASTEXITCODE -ne 0){throw 'Build failed'}
if($Check){$p=Start-Process -FilePath $OutputPath -ArgumentList '--check' -WindowStyle Hidden -PassThru -Wait;if($p.ExitCode -ne 0){throw 'Clock checks failed'};Get-Content (Join-Path (Split-Path -Parent $OutputPath) 'evidence\checks.txt')}
