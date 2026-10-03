#requires -Version 7.0
[CmdletBinding()]
param([switch]$Installer, [switch]$Release)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$root = Split-Path $PSScriptRoot -Parent
Invoke-HubEnvironment -Root $root -Action {
    param($context)
    if ($Release) { Assert-HubRelease -Root $root -Version $context.Version -State (Get-HubGitState -Root $root) }
    $lockEvidence = Get-HubLockEvidence -Root $root
    Invoke-HubBuild -Context $context -Test -LockedRestore
    Assert-HubLockEvidence -Evidence $lockEvidence
    foreach ($name in @('Core','Cli','Gui')) {
        $directory = Join-Path $root "src/AGProfileHub.$name"
        $null = Assert-HubResolvedGraph -Lock (Join-Path $directory 'packages.lock.json') -Resolved (Join-Path $directory 'obj/project.assets.json')
    }
    Invoke-HubRidRestore -Context $context
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss')
    $destination = Join-Path $root "artifacts/next/$($context.Version)/$stamp"
    if (Test-Path -LiteralPath $destination) { throw 'Output already exists.' }
    $package = Join-Path $destination 'AGProfileHubNext'
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    foreach ($spec in @(@{Name='Cli';Directory='cli'},@{Name='Gui';Directory='hub'})) {
        $project = Join-Path $root "src/AGProfileHub.$($spec.Name)/AGProfileHub.$($spec.Name).csproj"
        Invoke-HubCommand $context.DotNet @('publish',$project,'-c','Release','-r','win-x64','--no-restore','--self-contained','true','-o',(Join-Path $package $spec.Directory))
    }
    Assert-HubLockEvidence -Evidence $lockEvidence
    foreach ($name in @('README.md','NEXT_MANUAL_TEST_PLAN.md','SECURITY.md','ARCHITECTURE.md','LICENSE')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $package }
    New-Item -ItemType Directory -Path (Join-Path $package 'docs') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'docs/VALIDATION.md') -Destination (Join-Path $package 'docs')
    "@echo off`r`n`"%~dp0cli\agprofile-next.exe`" %*`r`nexit /b %errorlevel%`r`n" | Set-Content -LiteralPath (Join-Path $package 'agprofile-next.cmd') -Encoding ascii
    "@echo off`r`nstart `"`" `"%~dp0hub\AGProfileHubNext.Gui.exe`" %*`r`n" | Set-Content -LiteralPath (Join-Path $package 'Start Next.cmd') -Encoding ascii
    $gitState = Get-HubGitState -Root $root
    $provenance = [ordered]@{commit=$gitState.Commit;dirty=$gitState.Dirty;sdk=$context.SdkVersion;locks=@();dependencies=@()}
    foreach ($name in @('Core','Cli','Gui')) {
        $directory = Join-Path $root "src/AGProfileHub.$name"
        $proofDirectory = Join-Path $package "provenance/$name"
        New-Item -ItemType Directory -Path $proofDirectory -Force | Out-Null
        foreach ($spec in @(@{file='packages.lock.json';graph='tested'},@{file='packages.win-x64.lock.json';graph='published'})) {
            $path = Join-Path $directory $spec.file
            Copy-Item -LiteralPath $path -Destination $proofDirectory
            $provenance.locks += [ordered]@{project=$name;graph=$spec.graph;path="provenance/$name/$($spec.file)";sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
        }
        $graph = Assert-HubApplicationGraph -TestedLock (Join-Path $directory 'packages.lock.json') -PublishedLock (Join-Path $directory 'packages.win-x64.lock.json')
        $depsPath = switch ($name) { 'Cli' { 'cli/agprofile-next.deps.json' } 'Gui' { 'hub/AGProfileHubNext.Gui.deps.json' } default { $null } }
        if ($depsPath) { $graph = Assert-HubResolvedGraph -Lock (Join-Path $directory 'packages.win-x64.lock.json') -Resolved (Join-Path $package $depsPath) -Format Deps }
        $provenance.dependencies += [ordered]@{project=$name;application=$graph.application;runtime=$graph.runtime;platform=$graph.platform;depsPath=$depsPath}
    }
    $manifest = [ordered]@{schemaVersion=1;application='AntigravityProfileHubNext';version=$context.Version;runtime='win-x64';selfContained=$true;experimental=$true;manualAcceptance=$false;compatibility='requires local fingerprint-bound probe';workspace='unsupported';defaultDataRoot='%LOCALAPPDATA%/AntigravityProfileHubNext';provenance=$provenance;files=@()}
    $manifest.files = @(Get-ChildItem -LiteralPath $package -Recurse -File | ForEach-Object { @{path=[IO.Path]::GetRelativePath($package,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding utf8
    Assert-HubPackageManifest -PackageRoot $package
    $zip = Join-Path $destination "AGProfileHubNext-$($context.Version)-win-x64.zip"
    Compress-Archive -LiteralPath $package -DestinationPath $zip
    @{file=[IO.Path]::GetFileName($zip);algorithm='SHA256';hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash} | ConvertTo-Json | Set-Content -LiteralPath ($zip+'.sha256.json') -Encoding utf8
    if ($Installer) {
        $candidates = @()
        if ($env:AGPH_ISCC) { $candidates += $env:AGPH_ISCC }
        $candidates += Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'
        if (${env:ProgramFiles(x86)}) { $candidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe' }
        if ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe' }
        $onPath = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($onPath) { $candidates += $onPath.Source }
        $compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if (-not $compiler) { throw 'Inno Setup compiler unavailable; set AGPH_ISCC or install Inno Setup 6. Portable package preserved.' }
        # The .iss verifies the compiler's own PREPROCVER; ISCC.exe may have 0.0.0.0 PE resources.
        $guiVersion = (Get-Item -LiteralPath (Join-Path $package 'hub/AGProfileHubNext.Gui.exe')).VersionInfo.FileVersion
        if ($guiVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Published GUI has no numeric file version.' }
        Invoke-HubCommand $compiler @("/DSourceRoot=$package","/DMyAppVersion=$($context.Version)","/DMyFileVersion=$guiVersion","/O$destination",(Join-Path $root 'installer/AGProfileHubNext.iss'))
        $setup = Join-Path $destination "AGProfileHubNext-Setup-win-x64-$($context.Version).exe"
        @{file=[IO.Path]::GetFileName($setup);algorithm='SHA256';hash=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash} | ConvertTo-Json | Set-Content -LiteralPath ($setup+'.sha256.json') -Encoding utf8
    }
    Write-Output "Next package: $destination"
}
