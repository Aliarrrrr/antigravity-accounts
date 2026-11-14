param([Parameter(Mandatory)][string]$ErrorCode)
$ErrorActionPreference = 'Stop'
if ($ErrorCode -cnotmatch '^[a-z][a-z0-9_]{0,90}$') { exit 1 }
$shell = New-Object -ComObject WScript.Shell
[void]$shell.Popup(('兼容包暂未准备完成，AGY 官方更新仍可继续。当前插件状态请查看兼容状态记录。原因：' + $ErrorCode), 5, 'Antigravity 兼容更新', 48)
