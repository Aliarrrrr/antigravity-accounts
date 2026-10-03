#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('agph-build-contract-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$checks = 0
function Assert-Rejected([scriptblock]$Action, [string]$Reason) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "Expected rejection: $Reason" }
    $script:checks++
}
function Write-Lock([string]$Path, [hashtable]$Packages) {
    @{version=1; dependencies=@{'net10.0'=$Packages}} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Path -Encoding utf8
}
try {
    $tested = Join-Path $scratch 'tested.json'
    $published = Join-Path $scratch 'published.json'
    Write-Lock $tested @{App=@{type='Direct';resolved='1.0.0';contentHash='hash-a'}}
    Write-Lock $published @{App=@{type='Direct';resolved='1.0.0';contentHash='hash-a'}}
    $graph = Assert-HubApplicationGraph -TestedLock $tested -PublishedLock $published
    if ($graph.application.Count -ne 1) { throw 'Unchanged application graph was not recorded.' }
    $checks++
    Write-Lock $published @{App=@{type='Direct';resolved='1.0.1';contentHash='hash-a'}}
    Assert-Rejected { Assert-HubApplicationGraph -TestedLock $tested -PublishedLock $published } 'version drift'
    Write-Lock $published @{App=@{type='Direct';resolved='1.0.0';contentHash='altered'}}
    Assert-Rejected { Assert-HubApplicationGraph -TestedLock $tested -PublishedLock $published } 'content hash drift'
    Write-Lock $published @{}
    Assert-Rejected { Assert-HubApplicationGraph -TestedLock $tested -PublishedLock $published } 'missing application package'
    Write-Lock $published @{App=@{type='Direct';resolved='1.0.0';contentHash='hash-a'};Unexpected=@{type='Transitive';resolved='1.0.0';contentHash='extra'}}
    Assert-Rejected { Assert-HubApplicationGraph -TestedLock $tested -PublishedLock $published } 'new application package'
    Write-Lock $published @{App=@{type='Direct';resolved='1.0.0';contentHash='hash-a'};'Microsoft.NETCore.App.Runtime.win-x64'=@{type='Direct';resolved='10.0.0';contentHash='runtime'}}
    $graph = Assert-HubApplicationGraph -TestedLock $tested -PublishedLock $published
    if ($graph.runtime.Count -ne 1) { throw 'Runtime pack was not explicitly classified.' }
    $checks++
    Write-Lock $published @{App=@{type='Direct';resolved='1.0.0';contentHash='hash-a'};'Microsoft.Arbitrary'=@{type='Transitive';resolved='1.0.0';contentHash='extra'}}
    Assert-Rejected { Assert-HubApplicationGraph -TestedLock $tested -PublishedLock $published } 'broad Microsoft exclusion'
    $package = Join-Path $scratch 'package'; New-Item -ItemType Directory -Path $package | Out-Null
    $payload = Join-Path $package 'payload.txt'; 'original' | Set-Content -LiteralPath $payload
    $locks = @(); $dependencies = @()
    foreach ($name in @('Core','Cli','Gui')) {
        foreach ($kind in @('tested','published')) {
            $lockName = "$name.$kind.lock.json"
            Copy-Item -LiteralPath $tested -Destination (Join-Path $package $lockName)
            $locks += @{project=$name;graph=$kind;path=$lockName;sha256=(Get-FileHash (Join-Path $package $lockName) -Algorithm SHA256).Hash}
        }
        $depsPath = switch ($name) { 'Cli' { 'cli/agprofile-next.deps.json' } 'Gui' { 'hub/AGProfileHubNext.Gui.deps.json' } default { $null } }
        if ($depsPath) {
            New-Item -ItemType Directory -Path (Split-Path (Join-Path $package $depsPath) -Parent) -Force | Out-Null
            @{libraries=@{'App/1.0.0'=@{type='package';sha512='sha512-hash-a'}}} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package $depsPath)
        }
        $dependencies += @{project=$name;application=@([ordered]@{id='app';version='1.0.0';contentHash='hash-a'});runtime=@();platform=@();depsPath=$depsPath}
    }
    $manifest = @{schemaVersion=1; application='AntigravityProfileHubNext';runtime='win-x64';selfContained=$true;experimental=$true;manualAcceptance=$false;provenance=@{commit=('a'*40);dirty=$false;sdk='10.0.400';locks=$locks;dependencies=$dependencies};files=@(Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object { @{path=[IO.Path]::GetRelativePath($package,$_.FullName);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash} })}
    $manifestPath = Join-Path $package 'package-manifest.json'
    $manifest | ConvertTo-Json -Depth 12 | Set-Content $manifestPath
    Assert-HubPackageManifest -PackageRoot $package
    $checks++
    'tampered' | Set-Content -LiteralPath $payload
    Assert-Rejected { Assert-HubPackageManifest -PackageRoot $package } 'tampered payload'
    'original' | Set-Content -LiteralPath $payload
    $manifest.provenance.sdk = '10.0.401'; $manifest | ConvertTo-Json -Depth 12 | Set-Content $manifestPath
    Assert-Rejected { Assert-HubPackageManifest -PackageRoot $package } 'unexpected SDK'
    $manifest.provenance.sdk = '10.0.400'; $manifest.manualAcceptance=$true; $manifest | ConvertTo-Json -Depth 12 | Set-Content $manifestPath
    Assert-Rejected { Assert-HubPackageManifest -PackageRoot $package } 'unsupported acceptance claim'
    $manifest.manualAcceptance=$false; $manifest.provenance.dependencies[1].application[0].contentHash = 'wrong-provenance'; $manifest | ConvertTo-Json -Depth 12 | Set-Content $manifestPath
    Assert-Rejected { Assert-HubPackageManifest -PackageRoot $package } 'tampered dependency provenance'
    $manifest.provenance.dependencies[1].application[0].contentHash = 'hash-a'
    $originalDependencies = $manifest.provenance.dependencies; $manifest.provenance.dependencies=@($originalDependencies[0]); $manifest | ConvertTo-Json -Depth 12 | Set-Content $manifestPath
    Assert-Rejected { Assert-HubPackageManifest -PackageRoot $package } 'missing CLI/GUI provenance'
    $manifest.provenance.dependencies=$originalDependencies
    $manifest.experimental='True'; $manifest | ConvertTo-Json -Depth 12 | Set-Content $manifestPath
    Assert-Rejected { Assert-HubPackageManifest -PackageRoot $package } 'string experimental claim'
    $manifest.experimental=$true; $manifest.manualAcceptance='False'; $manifest | ConvertTo-Json -Depth 12 | Set-Content $manifestPath
    Assert-Rejected { Assert-HubPackageManifest -PackageRoot $package } 'string manual acceptance claim'
    $manifest.manualAcceptance=$false; $manifest.selfContained='True'; $manifest | ConvertTo-Json -Depth 12 | Set-Content $manifestPath
    Assert-Rejected { Assert-HubPackageManifest -PackageRoot $package } 'string self-contained claim'
    $manifest.selfContained=$true; $manifest.provenance.dirty='False'; $manifest | ConvertTo-Json -Depth 12 | Set-Content $manifestPath
    Assert-Rejected { Assert-HubPackageManifest -PackageRoot $package } 'string dirty provenance'
    $manifest.provenance.dirty=$false
    $manifest.manualAcceptance=$false; $manifest.files[0].path='../payload.txt'; $manifest | ConvertTo-Json -Depth 12 | Set-Content $manifestPath
    Assert-Rejected { Assert-HubPackageManifest -PackageRoot $package } 'escaped file path'
    foreach ($name in @('Core','Cli','Gui')) {
        $directory = Join-Path $scratch "src/AGProfileHub.$name"
        New-Item -ItemType Directory -Path $directory | Out-Null
        Copy-Item -LiteralPath $tested -Destination (Join-Path $directory 'packages.lock.json')
        Copy-Item -LiteralPath $tested -Destination (Join-Path $directory 'packages.win-x64.lock.json')
        New-Item -ItemType Directory -Path (Join-Path $directory 'obj') | Out-Null
        @{libraries=@{'App/1.0.0'=@{type='package';sha512='hash-a'}}} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $directory 'obj/project.assets.json')
    }
    $script:commands = @()
    function Invoke-HubCommand { param($FilePath, $Arguments) $script:commands += ,$Arguments }
    Invoke-HubRidRestore -Context ([pscustomobject]@{Root=$scratch;DotNet='unused'})
    if ($commands.Count -ne 2) { throw 'Expected two RID project restores.' }
    foreach ($command in $commands) {
        if ($command -notcontains '--locked-mode' -or $command -contains '--force-evaluate' -or ($command -join ' ') -match 'RestoreLockedMode=false') { throw 'RID restore bypassed the lock.' }
    }
    $checks++
    function Invoke-HubCommand { param($FilePath, $Arguments) 'tampered' | Set-Content -LiteralPath (Join-Path $scratch 'src/AGProfileHub.Core/packages.lock.json') }
    Assert-Rejected { Invoke-HubRidRestore -Context ([pscustomobject]@{Root=$scratch;DotNet='unused'}) } 'restore rewrote a checked-in lock'
    if ((Get-Content -LiteralPath (Join-Path $scratch 'src/AGProfileHub.Core/packages.lock.json') -Raw).Trim() -ne 'tampered') { throw 'Gate concealed lock mutation by restoring original bytes.' }
    $checks++
    # Execute the actual installer preflight in a child with an owned fake LOCALAPPDATA.
    # A process-launch sentinel makes a regressed preflight harmless.
    $preflight = Join-Path $scratch 'installer-preflight.ps1'
    @'
param([string]$ScriptPath, [string]$LocalRoot, [string]$ResultPath, [string]$LaunchMarker)
$env:LOCALAPPDATA = $LocalRoot
$env:TEMP = Join-Path $LocalRoot 'temp'
function Start-Process { 'attempted' | Set-Content -LiteralPath $LaunchMarker; throw 'Installer launch was reached.' }
try { & $ScriptPath -Installer (Join-Path $LocalRoot 'dummy-installer.exe'); exit 0 }
catch { $_.Exception.Message | Set-Content -LiteralPath $ResultPath; exit 1 }
'@ | Set-Content -LiteralPath $preflight -Encoding utf8
    $installerScript = Join-Path $PSScriptRoot 'Test-NextInstaller.ps1'
    foreach ($case in @('record','backup','link','dangling-link','missing')) {
        $localRoot = Join-Path $scratch "installer-$case"
        $runtime = Join-Path $localRoot 'AntigravityProfileHubNext/runtime'
        New-Item -ItemType Directory -Path $runtime -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $localRoot 'temp') | Out-Null
        $record = Join-Path $runtime 'deeplink-registration.json'
        $ownedData = $record
        if ($case -eq 'backup') { $ownedData += '.bak' }
        if ($case -in @('link','dangling-link')) {
            $target = Join-Path $localRoot 'link-target'
            New-Item -ItemType Directory -Path $target | Out-Null
            $ownedData = if ($case -eq 'link') { Join-Path $target 'sentinel.txt' } else { Join-Path $localRoot 'sentinel.txt' }
            'keep recovery evidence' | Set-Content -LiteralPath $ownedData
            New-Item -ItemType Junction -Path $record -Target $target | Out-Null
            if ($case -eq 'dangling-link') { Remove-Item -LiteralPath $target -Force }
        } elseif ($case -eq 'missing') {
            $config = Join-Path $localRoot 'AntigravityProfileHubNext/config'
            New-Item -ItemType Directory -Path $config | Out-Null
            $ownedData = Join-Path $config 'profiles.json'
            '{"profiles":[{"id":"existing-profile"}]}' | Set-Content -LiteralPath $ownedData
        } else { 'keep recovery evidence' | Set-Content -LiteralPath $ownedData }
        $originalHash = (Get-FileHash -LiteralPath $ownedData -Algorithm SHA256).Hash
        $resultPath = Join-Path $localRoot 'result.txt'
        $launchMarker = Join-Path $localRoot 'launched.txt'
        & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $preflight -ScriptPath $installerScript -LocalRoot $localRoot -ResultPath $resultPath -LaunchMarker $launchMarker
        if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $launchMarker)) { throw 'Installer preflight did not safely refuse existing state.' }
        $expected = if ($case -eq 'missing') { 'Next already has real profiles; uninstall test refused.' } else { 'Next has existing callback recovery state; isolated installer test refused.' }
        if ((Get-Content -LiteralPath $resultPath -Raw).Trim() -cne $expected -or
            (Get-FileHash -LiteralPath $ownedData -Algorithm SHA256).Hash -cne $originalHash) { throw "Installer preflight changed state or returned the wrong guard: $case" }
        if ($case -in @('link','dangling-link')) { Remove-Item -LiteralPath $record -Force }
        $checks++
    }
    "Build contract checks: $checks passed."
} finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
