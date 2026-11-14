param([Parameter(Mandatory=$true)][string]$InstallRoot)
$ErrorActionPreference='Stop'
$resolvedInstall=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$updateRoot=Join-Path $env:LOCALAPPDATA 'antigravity-updater'
. (Join-Path $PSScriptRoot 'Node-Sidecar-State.ps1')
$processes=@(Get-CimInstance Win32_Process)
$nodeSidecars=@(Get-NodeSidecarProcessIds -Processes $processes -InstallRoot $resolvedInstall)
function Test-AntigravityUpdater($Entry) {
  ($Entry.ExecutablePath -and $Entry.ExecutablePath.StartsWith($updateRoot+'\',[StringComparison]::OrdinalIgnoreCase)) -or
  ($Entry.Name -match '^(Antigravity.*(Setup|Installer)|(Setup|Installer).*Antigravity).*\.exe$') -or
  ($Entry.Name -match '^ns[0-9A-F]+\.tmp$' -and $Entry.CommandLine -and $Entry.CommandLine.IndexOf('antigravity',[StringComparison]::OrdinalIgnoreCase) -ge 0) -or
  ($Entry.ExecutablePath -and $Entry.ExecutablePath.StartsWith($resolvedInstall+'\',[StringComparison]::OrdinalIgnoreCase) -and $Entry.Name -match '^(update|updater|setup|installer|elevate)\.exe$')
}
$applicationIds=@{}
$desktopExecutable=Join-Path $resolvedInstall 'Antigravity.exe'
$runtimeExecutables=@('language_server.exe','language_server_windows_x64.exe','webm_encoder.exe') | ForEach-Object {Join-Path $resolvedInstall ('resources\bin\'+$_)}
foreach($entry in $processes) {
  if ($nodeSidecars -contains $entry.ProcessId -or (Test-AntigravityUpdater $entry)) { continue }
  if ($entry.ExecutablePath -and ($entry.ExecutablePath -ieq $desktopExecutable -or $runtimeExecutables -icontains $entry.ExecutablePath)) {
    $applicationIds[[int]$entry.ProcessId]=$true
  }
}
# Native helpers may have new names after an update. A child of a recognized
# application process remains application work; installer processes never do.
do {
  $added=$false
  foreach($entry in $processes) {
    if ($applicationIds.ContainsKey([int]$entry.ProcessId) -or $nodeSidecars -contains $entry.ProcessId -or (Test-AntigravityUpdater $entry)) { continue }
    if ($entry.ExecutablePath -and $entry.ExecutablePath.StartsWith($resolvedInstall+'\',[StringComparison]::OrdinalIgnoreCase) -and $applicationIds.ContainsKey([int]$entry.ParentProcessId)) {
      $applicationIds[[int]$entry.ProcessId]=$true
      $added=$true
    }
  }
} while($added)
$busy=@($processes | Where-Object {
  ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($resolvedInstall+'\',[StringComparison]::OrdinalIgnoreCase) -and $nodeSidecars -notcontains $_.ProcessId) -or
  (Test-AntigravityUpdater $_)
} | Select-Object ProcessId,Name,@{Name='kind';Expression={
  if ($applicationIds.ContainsKey([int]$_.ProcessId)) { 'application' } else { 'updater' }
}})
ConvertTo-Json -InputObject $busy -Depth 3 -Compress
