#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageRoot)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$package = [IO.Path]::GetFullPath($PackageRoot)
$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\package-structure'))
$testRoot = Join-Path $evidenceRoot ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

function Check([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "Package assertion failed: $Name" }
    Write-Output "PASS $Name"
}

try {
    $required = @(
        'package-manifest.json', 'README.md', 'SECURITY.md', 'CHANGELOG.md', 'VERSIONING.md',
        'hub\AGProfileHub.Gui.exe', 'cli\AGProfileHub.Cli.exe'
    )
    foreach ($relative in $required) {
        Check "contains $relative" (Test-Path -LiteralPath (Join-Path $package $relative) -PathType Leaf)
    }

    $manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
    Check 'manifest is self-contained win-x64' ($manifest.selfContained -and $manifest.runtime -eq 'win-x64')
    Check 'manifest version is semantic' ($manifest.hubVersion -match '^\d+\.\d+\.\d+$')
    # 发布清单描述的是真实机制：配置 schema 必须与 Core 的 SchemaVersions.Current 一致，
    # 隔离机制与外部依赖字段不得再出现已删除的沙盒产品。
    Check 'manifest config schema matches Core (1)' ($manifest.configSchemaVersion -eq 1)
    Check 'manifest describes no sandbox dependency' (
        -not (($manifest.authIsolation + ' ' + $manifest.externalDependency) -match 'sandboxie'))
    Check 'manifest describes process-local token-store isolation' (
        [bool]($manifest.authIsolation -match 'file-token-store'))
    $expectedFileVersion = "$($manifest.hubVersion).0"
    $cli = Join-Path $package 'cli\AGProfileHub.Cli.exe'
    $gui = Join-Path $package 'hub\AGProfileHub.Gui.exe'
    Check 'CLI version matches manifest' ((Get-Item -LiteralPath $cli).VersionInfo.FileVersion -eq $expectedFileVersion)
    Check 'GUI version matches manifest' ((Get-Item -LiteralPath $gui).VersionInfo.FileVersion -eq $expectedFileVersion)

    foreach ($script in Get-ChildItem -LiteralPath (Join-Path $package 'scripts') -Filter '*.ps1') {
        $parseErrors = $null
        [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$null, [ref]$parseErrors) | Out-Null
        Check "$($script.Name) parses" (-not $parseErrors)
    }
    $portableText = ((Get-ChildItem -LiteralPath (Join-Path $package 'scripts') -File | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n")
    Check 'packaged scripts contain no development-machine paths' ([bool]($portableText -notmatch 'C:\\Users\\ZLF|D:\\mutigravity|\.workbuddy'))

    $help = & $cli --data-root (Join-Path $testRoot 'data') --json help
    Check 'packaged CLI help runs' ([bool]($LASTEXITCODE -eq 0 -and (($help | Out-String | ConvertFrom-Json).help -match 'configure-deeplink')))
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith($evidenceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
