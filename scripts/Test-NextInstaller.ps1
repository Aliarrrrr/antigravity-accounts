#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference = 'Stop'
$identity = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/{E5F89079-8053-4E9D-BAA6-E34871D987C4}_is1'
if (Test-Path $identity) { throw 'Next is already installed; isolated installer test refused.' }
$nextRegistry = Join-Path $env:LOCALAPPDATA 'AntigravityProfileHubNext/config/profiles.json'
if (Test-Path -LiteralPath $nextRegistry) {
    if (@((Get-Content -LiteralPath $nextRegistry -Raw | ConvertFrom-Json).profiles).Count -gt 0) { throw 'Next already has real profiles; uninstall test refused.' }
}
$root = Join-Path $env:TEMP ('agprofile-next-installer-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$before = if (Test-Path 'HKCU:/Software/Classes/antigravity/shell/open/command') { (Get-Item 'HKCU:/Software/Classes/antigravity/shell/open/command').GetValue('') } else { $null }
$setup = Start-Process (Resolve-Path -LiteralPath $Installer).Path -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="'+$root+'"'),'/TASKS=') -WindowStyle Hidden -PassThru
if (-not $setup.WaitForExit(60000) -or $setup.ExitCode -ne 0) { throw 'Next installation failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $root 'cli/agprofile-next.exe'))) { throw 'Installed CLI missing.' }
$uninstall = Start-Process (Join-Path $root 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru
if (-not $uninstall.WaitForExit(60000) -or $uninstall.ExitCode -ne 0) { throw 'Next uninstall failed.' }
Start-Sleep -Seconds 2
$after = if (Test-Path 'HKCU:/Software/Classes/antigravity/shell/open/command') { (Get-Item 'HKCU:/Software/Classes/antigravity/shell/open/command').GetValue('') } else { $null }
if ($before -ne $after -or (Test-Path $identity) -or (Test-Path -LiteralPath (Join-Path $root 'cli/agprofile-next.exe'))) { throw 'Uninstall or protocol preservation failed.' }
[ordered]@{schemaVersion=1;installed=$true;uninstalled=$true;protocolPreserved=$true;legacyUntouched=$true;testDirectory=$root} | ConvertTo-Json
