param([Parameter(Mandatory=$true)][ValidatePattern('^Local\\AntigravityAccounts-Install-[a-f0-9]{64}$')][string]$MutexName)
$ErrorActionPreference = 'Stop'
$mutex = [Threading.Mutex]::new($false, $MutexName)
$held = $false
try {
  try { $held = $mutex.WaitOne(0) }
  catch [Threading.AbandonedMutexException] { $held = $true }
  if ($held) {
    [Console]::Out.WriteLine('acquired')
    [Console]::Out.Flush()
    # EOF also releases ownership when the parent is interrupted or crashes.
    [void][Console]::In.ReadLine()
  } else {
    [Console]::Out.WriteLine('busy')
    [Console]::Out.Flush()
  }
} finally {
  if ($held) { $mutex.ReleaseMutex() }
  $mutex.Dispose()
}
