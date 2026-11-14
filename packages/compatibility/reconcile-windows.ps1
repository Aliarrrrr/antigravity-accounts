param([Parameter(Mandatory)][string]$StateRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($StateRoot)
if (Test-Path -LiteralPath (Join-Path $root 'disabled')) { exit 0 }
$registrationFile = Join-Path $root 'windows-registration.json'
if (-not (Test-Path -LiteralPath $registrationFile -PathType Leaf)) { exit 0 }
$registration = Get-Content -LiteralPath $registrationFile -Raw | ConvertFrom-Json
$config = Get-Content -LiteralPath (Join-Path $root 'bootstrap.json') -Raw | ConvertFrom-Json
if ($registration.schemaVersion -ne 1 -or $registration.kind -cne 'compatibility' -or $registration.stateRoot -ine $root -or $config.stateRoot -ine $root) { throw 'compatibility_registration_invalid' }
function Quote([string]$value) {
  if (-not [IO.Path]::IsPathRooted($value) -or $value.Contains('"') -or $value.Contains("`r") -or $value.Contains("`n")) { throw 'compatibility_registration_invalid' }
  return '"' + $value + '"'
}
$wscript = Join-Path $env:SystemRoot 'System32\wscript.exe'
$vbs = Join-Path $root 'Launch-Compatibility.vbs'
$bootstrap = Join-Path $root 'bootstrap.mjs'
# VBS receives node, bootstrap, action and state, then supplies --state-root.
$arguments = (Quote $vbs) + ' ' + (Quote $config.nodePath) + ' ' + (Quote $bootstrap) + ' "launch" ' + (Quote $root)
$shell = New-Object -ComObject WScript.Shell
foreach ($filename in @($registration.shortcuts)) {
  if (-not [IO.Path]::IsPathRooted($filename) -or [IO.Path]::GetExtension($filename) -ine '.lnk') { throw 'compatibility_registration_invalid' }
  $link = $shell.CreateShortcut($filename)
  if ($link.TargetPath -ieq $wscript -and $link.Arguments -ceq $arguments) { continue }
  $official = $link.TargetPath -ieq (Join-Path $config.installRoot 'Antigravity.exe')
  $ours = $link.TargetPath -ieq $wscript -and ($link.Arguments.Contains($vbs) -or $link.Arguments.Contains((Join-Path $config.toolkitRoot 'scripts\Launch-Localized.vbs')))
  if ((Test-Path -LiteralPath $filename) -and -not $official -and -not $ours) { throw 'compatibility_shortcut_not_owned' }
  $link.TargetPath = $wscript; $link.Arguments = $arguments; $link.WorkingDirectory = $config.installRoot
  $link.IconLocation = (Join-Path $config.installRoot 'Antigravity.exe') + ',0'; $link.Save()
}
