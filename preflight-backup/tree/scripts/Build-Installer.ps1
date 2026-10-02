#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageRoot,
    [string]$OutputDirectory,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$package = [IO.Path]::GetFullPath($PackageRoot)
if (-not (Test-Path -LiteralPath $package -PathType Container)) { throw "Package root '$package' was not found." }

$manifestPath = Join-Path $package 'package-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'package-manifest.json was not found.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.hubVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'The package manifest contains an invalid Hub version.' }
if (-not $manifest.selfContained) { throw 'The daily-use installer must be built from a self-contained package.' }
foreach ($relative in @('hub\AGProfileHub.Gui.exe', 'cli\AGProfileHub.Cli.exe', 'scripts\New-IsolationBox.ps1', 'scripts\Set-IsolationWriteThrough.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $relative) -PathType Leaf)) { throw "Package is missing '$relative'." }
}

if (-not $OutputDirectory) { $OutputDirectory = Split-Path $package -Parent }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null

if (-not $IsccPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $candidates = @(
        $command.Source,
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    $IsccPath = $candidates | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup 6 compiler (ISCC.exe) was not found. Install JRSoftware.InnoSetup or pass -IsccPath.'
}

$definition = Join-Path $repo 'installer\AGProfileHub.iss'
& $IsccPath "/DSourceRoot=$package" "/DMyAppVersion=$($manifest.hubVersion)" "/O$output" $definition
if ($LASTEXITCODE -ne 0) { throw "Inno Setup exited with $LASTEXITCODE." }

$installer = Join-Path $output "AGProfileHub-Setup-win-x64-$($manifest.hubVersion).exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Expected installer '$installer' was not created." }
$hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
@{ schemaVersion = 1; file = [IO.Path]::GetFileName($installer); algorithm = $hash.Algorithm; hash = $hash.Hash } |
    ConvertTo-Json | Set-Content -LiteralPath ($installer + '.sha256.json') -Encoding utf8
Write-Output "Installer: $installer"
Write-Output "SHA-256: $($hash.Hash)"
