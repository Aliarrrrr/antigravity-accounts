param(
  [Parameter(Mandatory=$true)][string]$InstallRoot,
  [Parameter(Mandatory=$true)][string]$BackupRoot,
  [string]$ShortcutPath
)
$ErrorActionPreference='Stop'
$executable=Join-Path ([IO.Path]::GetFullPath($InstallRoot)) 'Antigravity.exe'
if(-not(Test-Path -LiteralPath $executable -PathType Leaf)){throw 'wrong_application'}
$legacy=Join-Path $env:LOCALAPPDATA 'AntigravityZhCN'
$shortcutPath=if($ShortcutPath){[IO.Path]::GetFullPath($ShortcutPath)}else{Join-Path ([Environment]::GetFolderPath('Desktop')) 'Antigravity（简体中文）.lnk'}
if(-not(Test-Path -LiteralPath $shortcutPath -PathType Leaf)){throw 'chinese_shortcut_not_found'}
$shell=New-Object -ComObject WScript.Shell
$shortcut=$shell.CreateShortcut($shortcutPath)
if($shortcut.TargetPath -eq $executable -and -not $shortcut.Arguments){
  @{alreadyDirect=$true;shortcut=$shortcutPath} | ConvertTo-Json -Compress
  exit 0
}
if([IO.Path]::GetFileName($shortcut.TargetPath) -ne 'wscript.exe' -or $shortcut.Arguments -ne ('"'+(Join-Path $legacy 'hidden_launch.vbs')+'"')){throw 'unexpected_shortcut_target'}
$backup=Join-Path ([IO.Path]::GetFullPath($BackupRoot)) ('legacy-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
if(Test-Path -LiteralPath $backup){throw 'backup_exists'}
New-Item -ItemType Directory -Path $backup | Out-Null
Copy-Item -LiteralPath $shortcutPath -Destination (Join-Path $backup 'Antigravity（简体中文）.lnk')
if(Test-Path -LiteralPath $legacy){Copy-Item -LiteralPath $legacy -Destination (Join-Path $backup 'AntigravityZhCN') -Recurse}
@{schemaVersion=1;createdUtc=[DateTime]::UtcNow.ToString('o');shortcut=$shortcutPath;target=$shortcut.TargetPath;arguments=$shortcut.Arguments;workingDirectory=$shortcut.WorkingDirectory;legacyDirectory=$legacy} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'shortcut.json') -Encoding UTF8
$shortcut.TargetPath=$executable
$shortcut.Arguments=''
$shortcut.WorkingDirectory=[IO.Path]::GetDirectoryName($executable)
$shortcut.IconLocation=$executable+',0'
$shortcut.Description='Antigravity 简体中文（本地界面汉化）'
$shortcut.Save()
$verified=$shell.CreateShortcut($shortcutPath)
if($verified.TargetPath -ne $executable -or $verified.Arguments){
  Copy-Item -LiteralPath (Join-Path $backup 'Antigravity（简体中文）.lnk') -Destination $shortcutPath -Force
  throw 'shortcut_verification_failed_original_restored'
}
@{prepared=$true;backup=$backup;shortcut=$shortcutPath;target=$verified.TargetPath} | ConvertTo-Json -Compress
