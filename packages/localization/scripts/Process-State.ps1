param([Parameter(Mandatory=$true)][string]$InstallRoot)
$ErrorActionPreference='Stop'
$resolvedInstall=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
. (Join-Path $PSScriptRoot 'Node-Sidecar-State.ps1')
$processes=@(Get-CimInstance Win32_Process)
$nodeSidecars=@(Get-NodeSidecarProcessIds -Processes $processes -InstallRoot $resolvedInstall)
$owned=@($processes | Where-Object {
    $_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($resolvedInstall+'\',[StringComparison]::OrdinalIgnoreCase)) -and $nodeSidecars -notcontains $_.ProcessId
} | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath)
ConvertTo-Json -InputObject $owned -Depth 3 -Compress
