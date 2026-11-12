param(
  [Parameter(Mandatory=$true)][string]$RequestFile,
  [ValidateRange(15,3600)][int]$TimeoutSeconds = 900,
  [ValidateRange(1,30)][int]$SettleSeconds = 3
)
$ErrorActionPreference = 'Stop'
$script:resultFile = $null
$script:stage = 'prepare'
function Fail([string]$code) { throw $code }
function Same-Path([string]$left, [string]$right) {
  if (-not [IO.Path]::IsPathRooted($left) -or -not [IO.Path]::IsPathRooted($right)) { return $false }
  return [IO.Path]::GetFullPath($left).TrimEnd('\') -ieq [IO.Path]::GetFullPath($right).TrimEnd('\')
}
function Inside-Path([string]$file, [string]$directory) {
  $prefix = [IO.Path]::GetFullPath($directory).TrimEnd('\') + '\'
  return [IO.Path]::GetFullPath($file).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)
}
function Regular-File([string]$filename) {
  if (-not [IO.Path]::IsPathRooted($filename)) { Fail 'update_handoff_path_invalid' }
  $item = Get-Item -LiteralPath $filename -Force
  if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { Fail 'update_handoff_file_invalid' }
  return $item
}
function Read-PublicJson([string]$filename) {
  $item = Regular-File $filename
  if ($item.Length -gt 1048576) { Fail 'update_handoff_metadata_invalid' }
  return ([IO.File]::ReadAllText($filename) | ConvertFrom-Json)
}
function Digest([string]$filename) {
  $algorithm=[Security.Cryptography.SHA256]::Create()
  $stream=[IO.File]::OpenRead($filename)
  try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
  finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Result([string]$status, [string]$code = '') {
  if (-not $script:resultFile) { return }
  $data = @{schemaVersion=1;status=$status;stage=$script:stage;checkedUtc=[DateTime]::UtcNow.ToString('o');modelCalls=0;accountCalls=0}
  if ($code) { $data.errorCode=$code }
  if ($script:failureLine) { $data.failureLine=[int]$script:failureLine }
  [IO.File]::WriteAllText($script:resultFile,($data | ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
}
function Native-Quote([string]$value) {
  if ($value.IndexOf([char]0) -ge 0 -or $value.IndexOf("`r") -ge 0 -or $value.IndexOf("`n") -ge 0) { Fail 'update_handoff_argument_invalid' }
  $result='"'; $slashes=0
  foreach ($character in $value.ToCharArray()) {
    if ($character -eq '\') { $slashes++; continue }
    if ($character -eq '"') { $result+=('\' * (2*$slashes+1)) + '"' }
    else { $result+=('\' * $slashes) + $character }
    $slashes=0
  }
  return $result + ('\' * (2*$slashes)) + '"'
}
function Invoke-Hidden([string]$executable, [string[]]$arguments, [bool]$nodeMode = $false) {
  [void](Regular-File $executable)
  $info=[Diagnostics.ProcessStartInfo]::new()
  $info.FileName=$executable; $info.Arguments=(($arguments | ForEach-Object {Native-Quote $_}) -join ' ')
  $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
  if ($nodeMode) { $info.EnvironmentVariables['ELECTRON_RUN_AS_NODE']='1' }
  else { $info.EnvironmentVariables.Remove('ELECTRON_RUN_AS_NODE') }
  $child=[Diagnostics.Process]::Start($info)
  try {
    if (-not $child.WaitForExit(120000)) { Fail 'update_handoff_launch_timeout' }
    if ($child.ExitCode -ne 0) { Fail 'update_handoff_launch_refused' }
  } finally { $child.Dispose() }
}
function Sidecar-Ids($processes, [string]$installRoot) {
  Get-AccountsNodeSidecarIds -Processes $processes -InstallRoot $installRoot
}
function Updaters($processes, $request) {
  foreach ($entry in $processes) {
    if ($entry.ExecutablePath -and (Same-Path $entry.ExecutablePath $request.installerPath)) { $entry; continue }
    if ($entry.ExecutablePath -and (Inside-Path $entry.ExecutablePath $request.updaterRoot)) { $entry; continue }
    if ($entry.Name -match '^(Antigravity.*(Setup|Installer)|(Setup|Installer).*Antigravity).*\.exe$') { $entry; continue }
    if ($entry.Name -match '^ns[0-9A-F]+\.tmp$' -and $entry.CommandLine -and $entry.CommandLine.IndexOf('antigravity',[StringComparison]::OrdinalIgnoreCase) -ge 0) { $entry; continue }
    if ($entry.ExecutablePath -and (Inside-Path $entry.ExecutablePath $request.installRoot) -and $entry.Name -match '^(update|updater|setup|installer|elevate)\.exe$') { $entry }
  }
}
function Program-Stamp($request) {
  $parts=@()
  foreach ($relative in @('Antigravity.exe','resources/app.asar','resources/bin/language_server.exe')) {
    try {
      $item=Regular-File (Join-Path $request.installRoot $relative)
      $parts+=@($relative,[string]$item.Length,[string]$item.LastWriteTimeUtc.Ticks)
    } catch { return $null }
  }
  return $parts -join '|'
}
try {
  $requestItem=Regular-File $RequestFile
  if ($requestItem.Length -gt 65536) { Fail 'update_handoff_request_invalid' }
  $script:resultFile=$RequestFile+'.result.json'
  $request=Read-PublicJson $RequestFile
  if ($request.schemaVersion -ne 1 -or $request.appPid -lt 1 -or $request.appPid -gt [int]::MaxValue -or
      -not [IO.Path]::IsPathRooted($request.stateRoot) -or -not [IO.Path]::IsPathRooted($request.packageRoot) -or
      -not (Same-Path $request.resourcesRoot (Join-Path $request.installRoot 'resources')) -or
      (Inside-Path $request.packageRoot $request.installRoot) -or
      -not (Inside-Path $request.installerPath (Join-Path $request.updaterRoot 'pending')) -or
      [IO.Path]::GetExtension($request.installerPath) -ine '.exe' -or
      $request.installerSha256 -notmatch '^[a-f0-9]{64}$' -or $request.archiveSha256 -notmatch '^[a-f0-9]{64}$') { Fail 'update_handoff_request_invalid' }
  $receipt=Read-PublicJson $request.receiptFile
  if ($receipt.schemaVersion -ne 1 -or $receipt.phase -ne 'applied' -or
      -not (Same-Path $receipt.resourcesRoot $request.resourcesRoot) -or
      -not (Same-Path $receipt.packageRoot $request.packageRootLogical)) { Fail 'update_handoff_receipt_invalid' }
  if (-not (Same-Path $PSScriptRoot (Join-Path $request.packageRoot 'account-plugin'))) { Fail 'update_handoff_cache_invalid' }
  $cache=Read-PublicJson (Join-Path $request.packageRoot 'package-manifest.json')
  if ($cache.role -ne 'accounts-recovery-package') { Fail 'update_handoff_cache_invalid' }
  foreach ($relative in @('account-plugin/Update-Handoff.ps1','account-plugin/recovery-and-launch.mjs','account-plugin/Process-WorkingDirectory.ps1')) {
    $listed=@($cache.files | Where-Object path -CEQ $relative)
    $filename=Join-Path $request.packageRoot $relative
    [void](Regular-File $filename)
    if ($listed.Count -ne 1 -or $listed[0].sha256 -cne (Digest $filename)) { Fail 'update_handoff_cache_invalid' }
  }
  . (Join-Path $PSScriptRoot 'Process-WorkingDirectory.ps1')
  [void](Regular-File $request.installerPath)
  if ((Digest $request.installerPath) -cne $request.installerSha256) { Fail 'update_handoff_installer_changed' }
  $processes=@(Get-CimInstance Win32_Process)
  $original=@($processes | Where-Object ProcessId -eq $request.appPid)
  if ($original.Count -ne 1 -or -not (Same-Path $original[0].ExecutablePath (Join-Path $request.installRoot 'Antigravity.exe'))) { Fail 'update_handoff_application_invalid' }
  $originalCreation=$original[0].CreationDate
  if (@(Updaters $processes $request).Count) { Fail 'update_handoff_update_already_running' }
  $beforeStamp=Program-Stamp $request
  if (-not $beforeStamp -or (Digest (Join-Path $request.resourcesRoot 'app.asar')) -cne $request.archiveSha256) { Fail 'update_handoff_program_changed' }
  $script:stage='waiting-for-update'
  Result 'ready'
  [Console]::Out.WriteLine('ready'); [Console]::Out.Flush()
  $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
  $observedInstaller=$false; $replacement=$false; $settledSince=$null; $previousStamp=$null
  while ([DateTime]::UtcNow -lt $deadline) {
    $processes=@(Get-CimInstance Win32_Process)
    $sameApp=@($processes | Where-Object { $_.ProcessId -eq $request.appPid -and $_.CreationDate -eq $originalCreation })
    $updaters=@(Updaters $processes $request)
    if (@($processes | Where-Object { $_.ExecutablePath -and (Same-Path $_.ExecutablePath $request.installerPath) }).Count) { $observedInstaller=$true }
    $stamp=Program-Stamp $request
    if ($stamp -and $stamp -cne $beforeStamp) { $replacement=$true }
    if ($sameApp.Count -or $updaters.Count -or -not $stamp -or -not ($observedInstaller -or $replacement)) {
      $settledSince=$null; $previousStamp=$stamp; Start-Sleep -Milliseconds 500; continue
    }
    $sidecars=@(Sidecar-Ids $processes $request.installRoot)
    $busy=@($processes | Where-Object { $_.ExecutablePath -and (Inside-Path $_.ExecutablePath $request.installRoot) -and $sidecars -notcontains [int]$_.ProcessId })
    $newApp=@($busy | Where-Object { $_.ExecutablePath -ieq (Join-Path $request.installRoot 'Antigravity.exe') -and $_.CommandLine -notmatch '(?:^|\s)--type=' })
    if ($newApp.Count) { Fail 'update_handoff_unexpected_application' }
    if ($busy.Count) { $settledSince=$null; $previousStamp=$stamp; Start-Sleep -Milliseconds 500; continue }
    if ($stamp -cne $previousStamp -or -not $settledSince) { $settledSince=[DateTime]::UtcNow; $previousStamp=$stamp }
    if (([DateTime]::UtcNow-$settledSince).TotalSeconds -lt $SettleSeconds) { Start-Sleep -Milliseconds 500; continue }
    if ((Digest (Join-Path $request.resourcesRoot 'app.asar')) -ceq $request.archiveSha256) { Fail 'update_handoff_no_replacement' }
    $script:stage='repair-and-launch'
    if ($request.guardedLauncherFile) {
      $launcher=Read-PublicJson $request.guardedLauncherFile
      if ($launcher.schemaVersion -ne 1 -or $launcher.kind -ne 'localization' -or -not (Same-Path $launcher.installRoot $request.installRoot) -or
          -not [IO.Path]::IsPathRooted($launcher.toolkitRoot) -or -not [IO.Path]::IsPathRooted($launcher.stateRoot) -or -not [IO.Path]::IsPathRooted($launcher.nodePath)) { Fail 'update_handoff_launcher_invalid' }
      $integration=Read-PublicJson (Join-Path $launcher.stateRoot 'windows-persistence/install.json')
      if ($integration.schemaVersion -ne 1 -or $integration.installed -ne $true -or
          -not (Same-Path $integration.installRoot $request.installRoot) -or -not (Same-Path $integration.toolkitRoot $launcher.toolkitRoot) -or
          -not (Same-Path $integration.stateRoot $launcher.stateRoot) -or -not (Same-Path $integration.nodePath $launcher.nodePath) -or
          (Test-Path -LiteralPath (Join-Path $launcher.stateRoot 'windows-persistence/disabled'))) { Fail 'update_handoff_launcher_invalid' }
      $vbs=Join-Path $launcher.toolkitRoot 'scripts/Launch-Localized.vbs'
      [void](Regular-File $vbs); [void](Regular-File $launcher.nodePath)
      Invoke-Hidden (Join-Path $env:SystemRoot 'System32/wscript.exe') @($vbs,$launcher.nodePath,$launcher.toolkitRoot,$request.installRoot,$launcher.stateRoot)
    } else {
      Invoke-Hidden (Join-Path $request.installRoot 'Antigravity.exe') @((Join-Path $request.packageRoot 'account-plugin/recovery-and-launch.mjs'),
        '--install-root',$request.installRoot,'--package-root',$request.packageRoot,'--state-root',$request.stateRoot) $true
    }
    $script:stage='complete'; Result 'launched'; exit 0
  }
  Fail 'update_handoff_wait_timeout'
} catch {
  $script:failureLine=$_.InvocationInfo.ScriptLineNumber
  $code=$_.Exception.Message
  if ($code -notmatch '^update_handoff_[a-z_]+$') { $code='update_handoff_failed' }
  Result 'error' $code
  exit 1
}
