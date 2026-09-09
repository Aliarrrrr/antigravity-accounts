#Requires -Version 5.1
<#
  Creates isolation boxes for Antigravity Profile Hub.

  This is the ONE step that needs elevation: Sandboxie stores its configuration
  at C:\Windows\Sandboxie.ini, which is not writable by a standard user.
  Launching programs inside an existing box does not require elevation.

  Run from an elevated prompt:
    powershell -ExecutionPolicy Bypass -File scripts\New-IsolationBox.ps1 `
      -BoxName AGPH_ISO_abc123...,AGPH_ISO_def456...

  The command to run is printed by:  AGProfileHub.Cli.exe iso-prepare <id>
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string[]]$BoxName
)

$ErrorActionPreference = 'Stop'

$home1 = 'C:\Program Files\Sandboxie-Plus'
$home2 = 'C:\Program Files\Sandboxie'
$sbieHome = @($home1, $home2) | Where-Object { Test-Path (Join-Path $_ 'SbieIni.exe') } | Select-Object -First 1
if (-not $sbieHome) { throw 'Sandboxie Plus was not found.' }
$sbieIni = Join-Path $sbieHome 'SbieIni.exe'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must run elevated: the Sandboxie configuration is not writable by a standard user.'
}

$settings = @(
    @('Enabled', 'y'),
    @('ConfigLevel', '10'),
    @('AutoRecover', 'n'),
    @('AutoDelete', 'n'),
    @('OpenCredentials', 'n')
)

$ini = 'C:\Windows\Sandboxie.ini'

foreach ($box in $BoxName) {
    if ($box -notmatch '^[A-Za-z0-9_]{1,64}$') { throw "Box name '$box' contains unsupported characters." }
}

foreach ($box in $BoxName) {
    foreach ($pair in $settings) {
        & $sbieIni set $box $pair[0] $pair[1] | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "SbieIni failed for '$box' / $($pair[0])." }
        $actual = [string](& $sbieIni query $box $pair[0] 2>$null)
        if ($actual.Trim() -ne $pair[1]) { throw "Sandboxie did not retain '$box' / $($pair[0])." }
    }
    $content = Get-Content $ini -Raw -Encoding Unicode -ErrorAction SilentlyContinue
    if (-not $content -or $content -notmatch "\[$([regex]::Escape($box))\]") {
        throw "Box '$box' was not persisted to $ini."
    }
    Write-Host "OK  $box"
}
