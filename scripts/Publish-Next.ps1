#requires -Version 7.0
[CmdletBinding()]
param([switch]$Installer, [switch]$Release)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$root = Split-Path $PSScriptRoot -Parent
Invoke-HubEnvironment -Root $root -Action {
    param($context)
    if ($Release) { Assert-HubRelease -Root $root -Version $context.Version -State (Get-HubGitState -Root $root) }
    Invoke-HubBuild -Context $context -Test -LockedRestore
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss')
    $destination = Join-Path $root "artifacts/next/$($context.Version)/$stamp"
    if (Test-Path -LiteralPath $destination) { throw 'Output already exists.' }
    $package = Join-Path $destination 'AGProfileHubNext'
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    $savedLocks = @{}
    foreach ($name in @('Core','Cli','Gui')) {
        $lockPath = Join-Path $root "src/AGProfileHub.$name/packages.lock.json"
        $savedLocks[$lockPath] = [IO.File]::ReadAllBytes($lockPath)
    }
    try { foreach ($spec in @(@{Name='Cli';Directory='cli'},@{Name='Gui';Directory='hub'})) {
        $project = Join-Path $root "src/AGProfileHub.$($spec.Name)/AGProfileHub.$($spec.Name).csproj"
        Invoke-HubCommand $context.DotNet @('restore',$project,'-r','win-x64','-p:SelfContained=true','-p:RestoreLockedMode=false','--force-evaluate')
        Invoke-HubCommand $context.DotNet @('publish',$project,'-c','Release','-r','win-x64','--no-restore','--self-contained','true','-o',(Join-Path $package $spec.Directory))
    } } finally { foreach ($lockPath in $savedLocks.Keys) { [IO.File]::WriteAllBytes($lockPath, $savedLocks[$lockPath]) } }
    foreach ($name in @('README.md','NEXT_MANUAL_TEST_PLAN.md','SECURITY.md','ARCHITECTURE.md','LICENSE')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $package }
    New-Item -ItemType Directory -Path (Join-Path $package 'docs') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'docs/VALIDATION.md') -Destination (Join-Path $package 'docs')
    "@echo off`r`n`"%~dp0cli\agprofile-next.exe`" %*`r`nexit /b %errorlevel%`r`n" | Set-Content -LiteralPath (Join-Path $package 'agprofile-next.cmd') -Encoding ascii
    "@echo off`r`nstart `"`" `"%~dp0hub\AGProfileHubNext.Gui.exe`" %*`r`n" | Set-Content -LiteralPath (Join-Path $package 'Start Next.cmd') -Encoding ascii
    $manifest = [ordered]@{schemaVersion=1;application='AntigravityProfileHubNext';version=$context.Version;runtime='win-x64';selfContained=$true;experimental=$true;manualAcceptance=$false;compatibility='requires local fingerprint-bound probe';workspace='unsupported';defaultDataRoot='%LOCALAPPDATA%/AntigravityProfileHubNext';files=@()}
    $manifest.files = @(Get-ChildItem -LiteralPath $package -Recurse -File | ForEach-Object { @{path=[IO.Path]::GetRelativePath($package,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding utf8
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
        Invoke-HubCommand $compiler @("/DSourceRoot=$package","/DMyAppVersion=$($context.Version)","/O$destination",(Join-Path $root 'installer/AGProfileHubNext.iss'))
        $setup = Join-Path $destination "AGProfileHubNext-Setup-win-x64-$($context.Version).exe"
        @{file=[IO.Path]::GetFileName($setup);algorithm='SHA256';hash=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash} | ConvertTo-Json | Set-Content -LiteralPath ($setup+'.sha256.json') -Encoding utf8
    }
    Write-Output "Next package: $destination"
}
