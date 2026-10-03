#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference = 'Stop'
$record = Join-Path $env:LOCALAPPDATA 'AntigravityProfileHubNext/runtime/deeplink-registration.json'
foreach ($path in @($record, ($record + '.bak'))) {
    try { $existing = Get-Item -LiteralPath $path -Force -ErrorAction Stop }
    catch [System.Management.Automation.ItemNotFoundException] { continue }
    catch { throw 'Next callback recovery state cannot be checked; isolated installer test refused.' }
    # Get-Item also detects links and directory entries, including dangling links.
    if ($null -ne $existing) { throw 'Next has existing callback recovery state; isolated installer test refused.' }
}
$nextRegistry = Join-Path $env:LOCALAPPDATA 'AntigravityProfileHubNext/config/profiles.json'
if (Test-Path -LiteralPath $nextRegistry) {
    if (@((Get-Content -LiteralPath $nextRegistry -Raw | ConvertFrom-Json).profiles).Count -gt 0) { throw 'Next already has real profiles; uninstall test refused.' }
}
$identity = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/{E5F89079-8053-4E9D-BAA6-E34871D987C4}_is1'
if (Test-Path $identity) { throw 'Next is already installed; isolated installer test refused.' }
$root = Join-Path $env:TEMP ('agprofile-next-installer-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$owner = [guid]::NewGuid().ToString('N')
$marker = Join-Path $root '.agph-installer-smoke-owner'
$owner | Set-Content -LiteralPath $marker -Encoding ascii
$before = if (Test-Path 'HKCU:/Software/Classes/antigravity/shell/open/command') { (Get-Item 'HKCU:/Software/Classes/antigravity/shell/open/command').GetValue('') } else { $null }
$setup = Start-Process (Resolve-Path -LiteralPath $Installer).Path -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="'+$root+'"'),'/TASKS=') -WindowStyle Hidden -PassThru
if (-not $setup.WaitForExit(60000) -or $setup.ExitCode -ne 0) { throw 'Next installation failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $root 'cli/agprofile-next.exe'))) { throw 'Installed CLI missing.' }
$uninstall = Start-Process (Join-Path $root 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru
if (-not $uninstall.WaitForExit(60000) -or $uninstall.ExitCode -ne 0) { throw 'Next uninstall failed.' }
Start-Sleep -Seconds 2
$after = if (Test-Path 'HKCU:/Software/Classes/antigravity/shell/open/command') { (Get-Item 'HKCU:/Software/Classes/antigravity/shell/open/command').GetValue('') } else { $null }
if ($before -ne $after -or (Test-Path $identity) -or (Test-Path -LiteralPath (Join-Path $root 'cli/agprofile-next.exe'))) { throw 'Uninstall or protocol preservation failed.' }
# Clean only this successful test's installation directory, after checking its absolute boundary,
# ownership marker and every link boundary. Failed tests preserve their files for diagnosis.
$absoluteRoot = [IO.Path]::GetFullPath($root)
$absoluteTemp = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
if (-not $absoluteRoot.StartsWith($absoluteTemp, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($absoluteRoot) -cnotmatch '^agprofile-next-installer-test-[0-9a-f]{32}$' -or
    -not (Test-Path -LiteralPath $marker -PathType Leaf) -or (Get-Content -LiteralPath $marker -Raw).Trim() -cne $owner) { throw 'Installer test cleanup ownership check failed; leftovers preserved.' }
if ((Get-Item -LiteralPath $absoluteRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint -or
    @(Get-ChildItem -LiteralPath $absoluteRoot -Recurse -Force -Attributes ReparsePoint).Count -gt 0) { throw 'Installer test cleanup found a link; leftovers preserved.' }
Remove-Item -LiteralPath $absoluteRoot -Recurse -Force
[ordered]@{schemaVersion=1;installed=$true;uninstalled=$true;protocolPreserved=$true;legacyUntouched=$true;temporaryFilesCleaned=$true;testDirectory=$root} | ConvertTo-Json
