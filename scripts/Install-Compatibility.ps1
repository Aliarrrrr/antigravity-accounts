#requires -Version 5.1
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$BundleRoot,
  [Parameter(Mandatory)][string]$StateRoot,
  [Parameter(Mandatory)][string]$LocalizationStateRoot,
  [Parameter(Mandatory)][string]$AccountsStateRoot,
  [Parameter(Mandatory)][string]$ToolkitRoot,
  [string]$NodePath = 'D:\Node.js\node.exe',
  [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\antigravity'),
  [switch]$StartWatcher
)
$ErrorActionPreference = 'Stop'
foreach ($name in @('BundleRoot','StateRoot','LocalizationStateRoot','AccountsStateRoot','ToolkitRoot','NodePath','InstallRoot')) {
  Set-Variable -Name $name -Value ([IO.Path]::GetFullPath((Get-Variable -Name $name -ValueOnly)))
}
function Save-JSON([string]$filename, $value) {
  $temporary = $filename + '.' + [Guid]::NewGuid().ToString('N') + '.new'
  [IO.File]::WriteAllText($temporary, ($value | ConvertTo-Json -Depth 7), [Text.UTF8Encoding]::new($false))
  Move-Item -LiteralPath $temporary -Destination $filename -Force
}
function Quote([string]$value) {
  if ($value.Contains('"') -or $value.Contains("`r") -or $value.Contains("`n")) { throw 'compatibility_launcher_path_invalid' }
  return '"' + $value + '"'
}
$bootstrapSource = Join-Path $BundleRoot 'compatibility\bootstrap.mjs'
if (-not (Test-Path -LiteralPath $NodePath -PathType Leaf) -or -not (Test-Path -LiteralPath $bootstrapSource -PathType Leaf)) { throw 'compatibility_bootstrap_missing' }
$arguments = @($bootstrapSource,'install','--bundle-root',$BundleRoot,'--state-root',$StateRoot,'--localization-state-root',$LocalizationStateRoot,
  '--accounts-state-root',$AccountsStateRoot,'--toolkit-root',$ToolkitRoot,'--node-path',$NodePath,'--install-root',$InstallRoot)
$output = & $NodePath @arguments
if ($LASTEXITCODE -ne 0) { throw 'compatibility_bootstrap_install_failed' }
$result = ($output -join "`n") | ConvertFrom-Json
if (-not $result.installed) { throw 'compatibility_bootstrap_install_failed' }
if (-not [IO.Path]::IsPathRooted([string]$result.accountsStateRoot)) { throw 'compatibility_accounts_state_invalid' }
$AccountsStateRoot = [IO.Path]::GetFullPath($result.accountsStateRoot)

# Stop only the old registered watcher. Keep its shortcut originals, backups,
# receipts and account locations; do not run the old uninstall operation.
$oldPersistence = Join-Path $LocalizationStateRoot 'windows-persistence'
if (Test-Path -LiteralPath (Join-Path $oldPersistence 'install.json')) {
  [IO.File]::WriteAllText((Join-Path $oldPersistence 'disabled'), [DateTime]::UtcNow.ToString('o'), [Text.UTF8Encoding]::new($false))
}
$oldLock = Join-Path $LocalizationStateRoot 'watcher.lock'
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while (Test-Path -LiteralPath $oldLock) {
  $record = Get-Content -LiteralPath $oldLock -Raw | ConvertFrom-Json
  if (-not ($record.pid -is [int] -or $record.pid -is [long]) -or -not (Get-Process -Id $record.pid -ErrorAction SilentlyContinue)) { break }
  if ([DateTime]::UtcNow -ge $deadline) { throw 'compatibility_previous_watcher_busy' }
  Start-Sleep -Milliseconds 250
}
$stableBootstrap = Join-Path $StateRoot 'bootstrap.mjs'
$vbsPath = Join-Path $StateRoot 'Launch-Compatibility.vbs'
$vbs = @'
Option Explicit
Dim sh, args, command, i
Set sh = CreateObject("WScript.Shell")
Set args = WScript.Arguments
If args.Count < 4 Then WScript.Quit 1
command = Q(args(0)) & " " & Q(args(1)) & " " & args(2) & " --state-root " & Q(args(3))
If args.Count > 4 Then
  command = command & " --"
  For i = 4 To args.Count - 1
    command = command & " " & Q(args(i))
  Next
End If
WScript.Quit sh.Run(command, 0, True)
Function Q(value)
  Q = Chr(34) & Replace(value, Chr(34), "\" & Chr(34)) & Chr(34)
End Function
'@
[IO.File]::WriteAllText($vbsPath, $vbs, [Text.Encoding]::Unicode)
$wscript = Join-Path $env:SystemRoot 'System32\wscript.exe'
$launchArguments = ((@($vbsPath,$NodePath,$stableBootstrap,'launch',$StateRoot) | ForEach-Object { Quote $_ }) -join ' ')
$watchArguments = ((@($vbsPath,$NodePath,$stableBootstrap,'watch',$StateRoot) | ForEach-Object { Quote $_ }) -join ' ')
$desktop = [Environment]::GetFolderPath('Desktop')
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$startup = Join-Path $startMenu 'Startup\AntigravityZhCN-Watcher.vbs'
$shortcuts = @((Join-Path $desktop 'Antigravity.lnk'),(Join-Path $startMenu 'Antigravity.lnk'),(Join-Path $startMenu 'Antigravity（简体中文）.lnk'))
$shell = New-Object -ComObject WScript.Shell
foreach ($filename in $shortcuts) {
  $link = $shell.CreateShortcut($filename)
  $link.TargetPath = $wscript
  $link.Arguments = $launchArguments
  $link.WorkingDirectory = $InstallRoot
  $link.IconLocation = (Join-Path $InstallRoot 'Antigravity.exe') + ',0'
  $link.Save()
}
$startupCommand = (Quote $wscript) + ' ' + $watchArguments
$startupText = 'Option Explicit' + "`r`n" + 'Dim sh' + "`r`n" + 'Set sh = CreateObject("WScript.Shell")' + "`r`n" + 'sh.Run "' + $startupCommand.Replace('"','""') + '", 0, False' + "`r`n"
[IO.File]::WriteAllText($startup,$startupText,[Text.Encoding]::Unicode)
$registration = @{schemaVersion=1;kind='compatibility';installRoot=$InstallRoot;stateRoot=$StateRoot;localizationStateRoot=$LocalizationStateRoot;
  accountsStateRoot=$AccountsStateRoot;nodePath=$NodePath;toolkitRoot=$ToolkitRoot;bootstrapPath=$stableBootstrap;shortcuts=$shortcuts;startup=$startup}
Save-JSON (Join-Path $StateRoot 'windows-registration.json') $registration
Save-JSON (Join-Path $AccountsStateRoot 'guarded-launcher.json') @{schemaVersion=1;kind='compatibility';installRoot=$InstallRoot;
  stateRoot=$StateRoot;nodePath=$NodePath;bootstrapPath=$stableBootstrap}
if ($StartWatcher) { Start-Process -FilePath $wscript -ArgumentList $watchArguments -WindowStyle Hidden | Out-Null }
@{installed=$true;stateRoot=$StateRoot;version=$result.version;shortcuts=$shortcuts;watcherStarted=[bool]$StartWatcher} | ConvertTo-Json -Depth 3 -Compress
