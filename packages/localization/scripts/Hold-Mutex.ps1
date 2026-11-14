param([Parameter(Mandatory=$true)][ValidatePattern('^Local\\(?:AntigravityZhCN-|AntigravityAccounts-Install-)[a-f0-9]{64}$')][string]$MutexName)
$ErrorActionPreference='Stop'
$mutex=[Threading.Mutex]::new($false,$MutexName)
$held=$false
try {
  try { $held=$mutex.WaitOne(0) }
  catch [Threading.AbandonedMutexException] { $held=$true }
  if (-not $held) {
    [Console]::Out.WriteLine('busy')
    [Console]::Out.Flush()
  } else {
    [Console]::Out.WriteLine('acquired')
    [Console]::Out.Flush()
    # The parent keeps this pipe open while holding the lock. A parent crash
    # closes the pipe, so EOF releases the mutex without stale PID recovery.
    [void][Console]::In.ReadLine()
  }
} finally {
  if ($held) { $mutex.ReleaseMutex() }
  $mutex.Dispose()
}
