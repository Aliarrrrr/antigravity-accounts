#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstallerPath)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$installer = [IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer '$installer' was not found." }
$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\installer-smoke'))
$testRoot = Join-Path $evidenceRoot ([guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $testRoot 'app'
$dataRoot = Join-Path $testRoot 'data'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

function Check([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "Installer assertion failed: $Name" }
    Write-Output "PASS $Name"
}

$uninstaller = $null
try {
    $process = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$installRoot", '/MERGETASKS=!desktopicon') -Wait -PassThru
    Check 'silent install exits successfully' ($process.ExitCode -eq 0)
    $cli = Join-Path $installRoot 'cli\AGProfileHub.Cli.exe'
    Check 'installed CLI exists' (Test-Path -LiteralPath $cli -PathType Leaf)
    $profile = & $cli --data-root $dataRoot --json create 'Installer preservation test' | Out-String | ConvertFrom-Json
    Check 'installed CLI creates local state' ($LASTEXITCODE -eq 0 -and $profile.id)
    $marker = Join-Path $dataRoot 'preserve-after-uninstall.txt'
    Set-Content -LiteralPath $marker -Value 'preserve' -Encoding ascii

    $uninstaller = Join-Path $installRoot 'unins000.exe'
    Check 'uninstaller exists' (Test-Path -LiteralPath $uninstaller -PathType Leaf)
    $process = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
    Check 'silent uninstall exits successfully' ($process.ExitCode -eq 0)
    Check 'Profile data survives uninstall' (Test-Path -LiteralPath $marker -PathType Leaf)
    Check 'installed CLI is removed' (-not (Test-Path -LiteralPath $cli))
}
finally {
    if ($uninstaller -and (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait | Out-Null
    }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith($evidenceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
