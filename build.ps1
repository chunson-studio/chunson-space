param(
    [ValidateSet('All','Full','Simple')][string]$Edition='All',
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$DotnetPath='dotnet',
    [switch]$Verify,
    [switch]$Package
)
$ErrorActionPreference='Stop'
$outputPath=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $outputPath){throw 'Output must be a new directory'}
New-Item -ItemType Directory -Path $outputPath | Out-Null
if($Edition -in @('All','Full')){ & (Join-Path $PSScriptRoot 'Initialize-Dependencies.ps1') }
$editions=@(
    @{Name='Full';Project='full/windows/FAswitch.Desktop/FAswitch.Desktop.csproj';Exe='ChunsonSwitch.exe';Modes=@('--verify-ui','--verify-storage-ui')},
    @{Name='Simple';Project='simple/ChunsonLogin.csproj';Exe='ChunsonLogin.exe';Modes=@('--verify')}
)
foreach($item in $editions){
    if($Edition -ne 'All' -and $Edition -ne $item.Name){continue}
    $app=Join-Path $outputPath $item.Name
    & $DotnetPath publish (Join-Path $PSScriptRoot $item.Project) -c Release -r win-x64 --self-contained true -o $app --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false -p:DebugType=None -p:DebugSymbols=false
    if($LASTEXITCODE -ne 0){throw ('Build failed: '+$item.Name)}
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE'),(Join-Path $PSScriptRoot 'README.md') -Destination $app
    # Resolve runtime notices from the exact packages referenced by the published app.
    $deps=Get-Content (Get-ChildItem -LiteralPath $app -Filter '*.deps.json' | Select-Object -First 1).FullName -Raw | ConvertFrom-Json
    $cache=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget/packages'}
    foreach($reference in $deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'runtimepack.*/*' }){
        $identity=$reference.Substring('runtimepack.'.Length)
        $pack=Join-Path $cache $identity.ToLowerInvariant()
        $notices=@(Get-ChildItem -LiteralPath $pack -File | Where-Object { $_.Name -match '^(LICENSE|THIRD.PARTY.NOTICES)' })
        if($notices.Count -eq 0){throw ('Runtime notices missing: '+$identity)}
        $noticePath=Join-Path $app ('ThirdParty/dotnet/'+$identity.Replace('/','-'))
        New-Item -ItemType Directory -Path $noticePath -Force | Out-Null
        $notices | Copy-Item -Destination $noticePath
    }
    if($Verify){
        foreach($mode in $item.Modes){
            $evidence=Join-Path $outputPath ($item.Name+'-'+$mode.TrimStart('-'))
            $p=Start-Process -FilePath (Join-Path $app $item.Exe) -ArgumentList @($mode,('"'+$evidence+'"')) -WindowStyle Hidden -PassThru -Wait
            if($p.ExitCode -ne 0){throw ('Offline verification failed: '+$evidence)}
        }
        $clock=Start-Process -FilePath (Join-Path $app 'Widgets/BeijingClock.exe') -ArgumentList '--check' -WindowStyle Hidden -PassThru -Wait
        if($clock.ExitCode -ne 0){throw 'Clock verification failed'}
        $clockEvidence=Join-Path $app 'Widgets/evidence/checks.txt'
        Move-Item -LiteralPath $clockEvidence -Destination (Join-Path $outputPath ($item.Name+'-clock-checks.txt'))
        Remove-Item -LiteralPath (Split-Path $clockEvidence -Parent)
    }
    $manifest=@(Get-ChildItem -LiteralPath $app -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative=$_.FullName.Substring($app.Length+1).Replace('\','/')
        if($relative -match '(^|/)(auth\.json|accounts|profiles|browser-profiles|evidence|verification|position\.txt)(/|$)' -or $_.Extension -eq '.pdb'){throw ('Runtime data in release: '+$relative)}
        [pscustomobject]@{path=$relative;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputPath ($item.Name+'-files.json')) -Encoding utf8
    if($Package){
        $archive=Join-Path $outputPath ('Chunson-'+$item.Name+'-Windows-x64.zip')
        Compress-Archive -LiteralPath $app -DestinationPath $archive -CompressionLevel Optimal
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip=[IO.Compression.ZipFile]::OpenRead($archive)
        try{
            $entries=@{}
            foreach($entry in $zip.Entries){if($entry.Name){if($entries.ContainsKey($entry.FullName)){throw 'Duplicate ZIP entry'};$entries[$entry.FullName.Replace('\','/')]=$entry}}
            if($entries.Count -ne $manifest.Count){throw 'ZIP file count mismatch'}
            foreach($file in $manifest){
                $entry=$entries[$item.Name+'/'+$file.path]
                if($null -eq $entry -or $entry.Length -ne $file.bytes){throw ('ZIP entry mismatch: '+$file.path)}
                $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
                try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
                if($hash -ne $file.sha256){throw ('ZIP checksum mismatch: '+$file.path)}
            }
        }finally{$zip.Dispose()}
        [pscustomobject]@{passed=$true;files=$manifest.Count;sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath ($item.Name+'-zip-check.json')) -Encoding utf8
    }
}
Write-Output ('Build output: '+$outputPath)
