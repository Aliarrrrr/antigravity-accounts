# Only the verified external sidecar launcher is exempt from the desktop ASAR lock.
# Unknown command lines or changed launchers remain busy.
. (Join-Path $PSScriptRoot 'Process-WorkingDirectory.ps1')
function Get-NodeSidecarProcessIds($Processes, [string]$InstallRoot,
  [scriptblock]$WorkingDirectoryReader = { param($ProcessId) Get-ProcessWorkingDirectory $ProcessId }) {
  $executable = Join-Path $InstallRoot 'Antigravity.exe'
  $cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
  $shim = Join-Path $env:APPDATA 'Antigravity\bin\agy-node.cmd'
  $loader = Join-Path $env:USERPROFILE '.gemini\antigravity\lib\node_modules\sidecar_sdk\register_loader.js'
  $argumentPrefix = '--import=' + ([Uri]::new($loader)).AbsoluteUri + ' '
  try { $shimText = [IO.File]::ReadAllText($shim).Trim() } catch { return }
  $shimLines = $shimText.Replace("`r`n", "`n").Split("`n")
  # The App rewrites this Windows path with its launch-time casing. Only the
  # executable path comparison is case-insensitive; commands stay exact.
  if ($shimLines.Count -ne 3 -or $shimLines[0] -cne '@echo off' -or
      $shimLines[1] -cne 'set ELECTRON_RUN_AS_NODE=1' -or
      $shimLines[2] -ine ('"' + $executable + '" %*')) { return }
  foreach ($entry in $Processes) {
    $arguments = $argumentPrefix + 'dist/server.cjs'
    $isSwarmBoard = $entry.CommandLine -match ('^"' + [regex]::Escape($executable) + '"\s+' + [regex]::Escape($argumentPrefix + 'main.mjs') + '\s*$')
    if ($isSwarmBoard) { $arguments = $argumentPrefix + 'main.mjs' }
    $childPattern = '^"' + [regex]::Escape($executable) + '"\s+' + [regex]::Escape($arguments) + '\s*$'
    if ($entry.ExecutablePath -ine $executable -or $entry.CommandLine -notmatch $childPattern) { continue }
    $parent = $Processes | Where-Object { $_.ProcessId -eq $entry.ParentProcessId } | Select-Object -First 1
    $invocation = '(?:"' + [regex]::Escape($shim) + '"|' + [regex]::Escape($shim) + ')\s+' + [regex]::Escape($arguments)
    # cmd /c may add one quote pair around the complete known wrapper invocation.
    $parentPattern = '^(?:"' + [regex]::Escape($cmd) + '"|' + [regex]::Escape($cmd) + ')\s+/c\s+(?:"' + $invocation + '"|' + $invocation + ')\s*$'
    if (-not $parent -or $parent.ExecutablePath -ine $cmd -or $parent.CommandLine -notmatch $parentPattern) { continue }
    if ($isSwarmBoard) {
      $boardRoot = Join-Path $env:USERPROFILE '.gemini\antigravity\builtin\plugins\swarm\sidecars\swarm_board'
      try {
        $workingDirectory = & $WorkingDirectoryReader ([int]$entry.ProcessId)
        if (-not $workingDirectory -or [IO.Path]::GetFullPath($workingDirectory).TrimEnd('\') -ine [IO.Path]::GetFullPath($boardRoot).TrimEnd('\')) { continue }
        $main = Get-Item -LiteralPath (Join-Path $boardRoot 'main.mjs') -Force -ErrorAction Stop
        if ($main.PSIsContainer -or ($main.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
      } catch { continue }
    }
    $entry.ProcessId
  }
}
