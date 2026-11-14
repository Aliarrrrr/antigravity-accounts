param(
  [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\antigravity'),
  [string]$ToolkitRoot = (Split-Path $PSScriptRoot -Parent),
  [string]$NodePath = 'D:\Node.js\node.exe',
  [string]$StateRoot,
  [string]$DesktopDirectory = ([Environment]::GetFolderPath('Desktop')),
  [string]$StartMenuDirectory = (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
  [string]$StartupDirectory = (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'),
  [switch]$StartWatcher,
  [switch]$Reconcile,
  [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
if (-not $StateRoot) { $StateRoot = Join-Path $ToolkitRoot 'state' }
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$ToolkitRoot = [IO.Path]::GetFullPath($ToolkitRoot)
$NodePath = [IO.Path]::GetFullPath($NodePath)
$StateRoot = [IO.Path]::GetFullPath($StateRoot)
$integrationRoot = Join-Path $StateRoot 'windows-persistence'
$manifestPath = Join-Path $integrationRoot 'install.json'
$disabledPath = Join-Path $integrationRoot 'disabled'
$executable = Join-Path $InstallRoot 'Antigravity.exe'
$wscript = Join-Path $env:SystemRoot 'System32\wscript.exe'
$launchVbs = Join-Path $ToolkitRoot 'scripts\Launch-Localized.vbs'
$watchVbs = Join-Path $ToolkitRoot 'scripts\Watch-Localized.vbs'
$startupPath = Join-Path ([IO.Path]::GetFullPath($StartupDirectory)) 'AntigravityZhCN-Watcher.vbs'
$chineseName = 'Antigravity（简体中文）.lnk'
$shortcutPaths = @((Join-Path ([IO.Path]::GetFullPath($DesktopDirectory)) 'Antigravity.lnk'), (Join-Path ([IO.Path]::GetFullPath($StartMenuDirectory)) 'Antigravity.lnk'), (Join-Path ([IO.Path]::GetFullPath($StartMenuDirectory)) $chineseName))
$utf8 = New-Object Text.UTF8Encoding($false)
$shell = New-Object -ComObject WScript.Shell

function Quote-Argument([string]$value) {
  if ($value.Contains('"') -or $value.Contains("`r") -or $value.Contains("`n")) { throw 'invalid_launch_path' }
  return '"' + $value + '"'
}
function File-Hash([string]$file) {
  $algorithm = [Security.Cryptography.SHA256]::Create()
  $stream = [IO.File]::OpenRead($file)
  try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
  finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Write-Manifest($value) {
  $temporary = $manifestPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
  [IO.File]::WriteAllText($temporary, ($value | ConvertTo-Json -Depth 6), $utf8)
  Move-Item -LiteralPath $temporary -Destination $manifestPath -Force
}
function Matches-Shortcut([string]$file, [string]$target, [string]$arguments) {
  if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $false }
  $link = $shell.CreateShortcut($file)
  return ($link.TargetPath -eq $target -and $link.Arguments -eq $arguments)
}
$mutexIdentity = $StateRoot.TrimEnd('\').ToLowerInvariant()
$mutexHashAlgorithm = [Security.Cryptography.SHA256]::Create()
try { $mutexHash = ([BitConverter]::ToString($mutexHashAlgorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($mutexIdentity)))).Replace('-', '') }
finally { $mutexHashAlgorithm.Dispose() }
$integrationMutex = [Threading.Mutex]::new($false, ('Local\AntigravityZhCN-Persistence-' + $mutexHash))
$integrationMutexHeld = $false
try {
  try { $integrationMutexHeld = $integrationMutex.WaitOne(60000) }
  catch [Threading.AbandonedMutexException] { $integrationMutexHeld = $true }
  if (-not $integrationMutexHeld) { throw 'persistence_operation_busy' }
  if ($Reconcile -and ((Test-Path -LiteralPath $disabledPath) -or -not (Test-Path -LiteralPath $manifestPath))) {
    @{installed=$false;disabled=$true} | ConvertTo-Json -Compress
    exit 0
  }
$launchArguments = ((@($launchVbs, $NodePath, $ToolkitRoot, $InstallRoot, $StateRoot) | ForEach-Object { Quote-Argument $_ }) -join ' ')
$watchArguments = ((@($watchVbs, $NodePath, $ToolkitRoot, $InstallRoot, $StateRoot) | ForEach-Object { Quote-Argument $_ }) -join ' ')
$startupCommand = (Quote-Argument $wscript) + ' ' + $watchArguments
$startupText = "Option Explicit`r`nDim sh`r`nSet sh = CreateObject(""WScript.Shell"")`r`nsh.Run """ + $startupCommand.Replace('"', '""') + """, 0, False`r`n"
$startupBytes = [Text.Encoding]::Unicode.GetPreamble() + [Text.Encoding]::Unicode.GetBytes($startupText)
$hashAlgorithm = [Security.Cryptography.SHA256]::Create()
$startupHash = ([BitConverter]::ToString($hashAlgorithm.ComputeHash($startupBytes))).Replace('-', '')
$manifest = $null
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
  $manifest = [IO.File]::ReadAllText($manifestPath, $utf8) | ConvertFrom-Json
  if ($Reconcile -and -not $manifest.installed) {
    @{installed=$false;disabled=$true} | ConvertTo-Json -Compress
    exit 0
  }
  if ($manifest.schemaVersion -ne 1 -or $manifest.installRoot -ne $InstallRoot -or $manifest.toolkitRoot -ne $ToolkitRoot -or $manifest.nodePath -ne $NodePath -or $manifest.stateRoot -ne $StateRoot -or $manifest.startup.path -ne $startupPath -or $manifest.shortcuts.Count -ne $shortcutPaths.Count) { throw 'persistence_identity_mismatch' }
  for ($i = 0; $i -lt $shortcutPaths.Count; $i++) {
    $entry = $manifest.shortcuts[$i]
    if ($entry.path -ne $shortcutPaths[$i] -or $entry.target -ne $wscript -or $entry.arguments -ne $launchArguments -or $entry.backupPath -ne (Join-Path $integrationRoot ('shortcut-' + $i + '.original.lnk'))) { throw 'persistence_identity_mismatch' }
    if ($entry.existed -and ((-not (Test-Path -LiteralPath $entry.backupPath -PathType Leaf)) -or (File-Hash $entry.backupPath) -ne $entry.originalSha256)) { throw 'original_shortcut_backup_changed' }
  }
}

if ($Uninstall) {
  if (-not $manifest) { @{uninstalled=$false;alreadyAbsent=$true} | ConvertTo-Json -Compress; exit 0 }
  if ((Test-Path -LiteralPath $startupPath) -and (File-Hash $startupPath) -ne $manifest.startup.sha256) { throw 'startup_hook_not_owned' }
  foreach ($entry in $manifest.shortcuts) {
    if (Test-Path -LiteralPath $entry.path) {
      $original = $entry.existed -and (File-Hash $entry.path) -eq $entry.originalSha256
      if (-not $original -and -not (Matches-Shortcut $entry.path $entry.target $entry.arguments) -and -not (Matches-Shortcut $entry.path $executable '')) { throw 'shortcut_not_owned' }
    }
  }
  [IO.File]::WriteAllText($disabledPath, [DateTime]::UtcNow.ToString('o'), $utf8)
  foreach ($entry in $manifest.shortcuts) {
    if ($entry.existed) { [IO.File]::WriteAllBytes($entry.path, [IO.File]::ReadAllBytes($entry.backupPath)) }
    elseif (Test-Path -LiteralPath $entry.path) { Remove-Item -LiteralPath $entry.path }
  }
  if (Test-Path -LiteralPath $startupPath) { Remove-Item -LiteralPath $startupPath }
  $manifest.installed = $false
  Write-Manifest $manifest
  @{uninstalled=$true;watcherStopsOnNextCheck=$true;manifest=$manifestPath} | ConvertTo-Json -Compress
  exit 0
}

foreach ($required in @($executable, $NodePath, (Join-Path $ToolkitRoot 'durable.mjs'), $launchVbs, (Join-Path $ToolkitRoot 'scripts\Launch-Localized.ps1'), $watchVbs)) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw 'persistence_runtime_missing' }
}
if (Test-Path -LiteralPath $startupPath) {
  if (-not $manifest -or (File-Hash $startupPath) -ne $manifest.startup.sha256) { throw 'startup_hook_not_owned' }
}
foreach ($file in $shortcutPaths) {
  if ((Test-Path -LiteralPath $file) -and -not (Matches-Shortcut $file $executable '') -and -not ($manifest -and (Matches-Shortcut $file $wscript $launchArguments))) { throw 'unrelated_shortcut_target' }
}
if (-not $manifest) {
  New-Item -ItemType Directory -Path $integrationRoot -Force | Out-Null
  $entries = @()
  for ($i = 0; $i -lt $shortcutPaths.Count; $i++) {
    $file = $shortcutPaths[$i]
    $backupPath = Join-Path $integrationRoot ('shortcut-' + $i + '.original.lnk')
    if (Test-Path -LiteralPath $backupPath) { throw 'unindexed_shortcut_backup_exists' }
    $existed = Test-Path -LiteralPath $file -PathType Leaf
    if ($existed) { Copy-Item -LiteralPath $file -Destination $backupPath }
    $entries += [pscustomobject]@{path=$file;existed=$existed;backupPath=$backupPath;originalSha256=$(if ($existed) { File-Hash $backupPath } else { $null });target=$wscript;arguments=$launchArguments}
  }
  $manifest = [pscustomobject]@{schemaVersion=1;createdUtc=[DateTime]::UtcNow.ToString('o');installRoot=$InstallRoot;toolkitRoot=$ToolkitRoot;nodePath=$NodePath;stateRoot=$StateRoot;installed=$false;startup=[pscustomobject]@{path=$startupPath;sha256=$startupHash};shortcuts=$entries}
  Write-Manifest $manifest
}
$snapshots = @($shortcutPaths + $startupPath | ForEach-Object { [pscustomobject]@{path=$_;bytes=$(if (Test-Path -LiteralPath $_) { [IO.File]::ReadAllBytes($_) } else { $null })} })
try {
  foreach ($file in @($shortcutPaths + $startupPath)) { New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force | Out-Null }
  foreach ($entry in $manifest.shortcuts) {
    $link = $shell.CreateShortcut($entry.path)
    $link.TargetPath = $wscript
    $link.Arguments = $launchArguments
    $link.WorkingDirectory = $ToolkitRoot
    $link.IconLocation = $executable + ',0'
    $link.Description = 'Antigravity 简体中文（更新后自动检查汉化）'
    $link.Save()
    if (-not (Matches-Shortcut $entry.path $wscript $launchArguments)) { throw 'shortcut_verification_failed' }
  }
  [IO.File]::WriteAllBytes($startupPath, $startupBytes)
  if ((File-Hash $startupPath) -ne $startupHash) { throw 'startup_verification_failed' }
  $manifest.startup.sha256 = $startupHash
  $manifest.installed = $true
  Write-Manifest $manifest
  if (Test-Path -LiteralPath $disabledPath) { Remove-Item -LiteralPath $disabledPath }
} catch {
  foreach ($snapshot in $snapshots) {
    if ($null -ne $snapshot.bytes) { [IO.File]::WriteAllBytes($snapshot.path, [byte[]]$snapshot.bytes) }
    elseif (Test-Path -LiteralPath $snapshot.path) { Remove-Item -LiteralPath $snapshot.path }
  }
  throw
}
if ($StartWatcher) { Start-Process -FilePath $wscript -ArgumentList $watchArguments -WindowStyle Hidden | Out-Null }
@{installed=$true;startup=$startupPath;shortcuts=$shortcutPaths;manifest=$manifestPath;watcherStarted=[bool]$StartWatcher} | ConvertTo-Json -Depth 3 -Compress
} finally {
  if ($integrationMutexHeld) { $integrationMutex.ReleaseMutex() }
  $integrationMutex.Dispose()
}
