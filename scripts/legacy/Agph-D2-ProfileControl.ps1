#requires -Version 5.1
<#
D2 profile instance control.
Stops or starts ONE managed profile instance by matching its unique --user-data-dir.
Pure ASCII on purpose: Windows PowerShell 5.1 reads .ps1 as ANSI, so non-ASCII
comments/strings without a BOM can swallow line breaks and break parsing.

Parameter names deliberately avoid PowerShell automatic variables
($Profile / $Home / $Pid / $Host ...), which cannot be used as ordinary variables.

Usage:
  powershell -File Agph-D2-ProfileControl.ps1 -Action Stop  -Target A
  powershell -File Agph-D2-ProfileControl.ps1 -Action Start -Target A
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('Stop', 'Start')][string]$Action,
    [Parameter(Mandatory = $true)][ValidateSet('A', 'B')][string]$Target
)

$ErrorActionPreference = 'Stop'
$logPath = Join-Path $env:TEMP "agph-d2-$Target-$Action.log"

function Write-Log([string]$message) {
    "$([DateTimeOffset]::UtcNow.ToString('o')) $message" | Out-File -LiteralPath $logPath -Append -Encoding utf8
}

try {
    Write-Log "start action=$Action target=$Target"

    $statePath = Join-Path $env:TEMP 'agph-d2-state.json'
    if (-not (Test-Path -LiteralPath $statePath)) { throw "state file not found: $statePath" }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json

    $info = $state.profiles.$Target
    if ($null -eq $info) { throw "profile $Target not present in state file" }
    $userData = [string]$info.userData
    $instanceHome = [string]$info.instanceHome
    $tokenPath = Join-Path (Join-Path $instanceHome '.gemini') 'jetski-standalone-oauth-token'
    Write-Log "userData=$userData"
    Write-Log "tokenPath=$tokenPath"

    function Get-TokenFingerprint([string]$path) {
        if (Test-Path -LiteralPath $path) {
            $item = Get-Item -LiteralPath $path
            return [ordered]@{ exists = $true; bytes = $item.Length; mtimeUtc = $item.LastWriteTimeUtc.ToString('o') }
        }
        return [ordered]@{ exists = $false }
    }

    function Get-ProfileProcesses([string]$userDataPath) {
        $pattern = '*' + $userDataPath + '*'
        return @(Get-CimInstance Win32_Process -Filter "Name='Antigravity.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -like $pattern })
    }

    $result = [ordered]@{
        action        = $Action
        target        = $Target
        userData      = $userData
        timestampUtc  = [DateTimeOffset]::UtcNow.ToString('o')
        tokenBefore   = Get-TokenFingerprint $tokenPath
        killedPids    = @()
        tokenAfter    = $null
        remainingPids = @()
        started       = $false
    }

    if ($Action -eq 'Stop') {
        $procs = Get-ProfileProcesses $userData
        $result.killedPids = @($procs | ForEach-Object { [int]$_.ProcessId })
        Write-Log "killing pids: $($result.killedPids -join ',')"
        foreach ($p in $procs) {
            & taskkill /PID $p.ProcessId /T /F 2>&1 | Out-Null
        }
        Start-Sleep -Seconds 4
        $result.tokenAfter = Get-TokenFingerprint $tokenPath
        $result.remainingPids = @(Get-ProfileProcesses $userData | ForEach-Object { [int]$_.ProcessId })
    } else {
        & explorer.exe ([string]$info.vbs)
        Start-Sleep -Seconds 22
        $result.tokenAfter = Get-TokenFingerprint $tokenPath
        $result.remainingPids = @(Get-ProfileProcesses $userData | ForEach-Object { [int]$_.ProcessId })
        $result.started = $true
    }

    $outPath = Join-Path $env:TEMP "agph-d2-$Target-Action.json"
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $outPath -Encoding UTF8
    Write-Log "wrote $outPath"
    Write-Log ($result | ConvertTo-Json -Depth 5 -Compress)
}
catch {
    Write-Log "ERROR: $($_.Exception.Message)"
    Write-Log "POSITION: $($_.InvocationInfo.PositionMessage)"
    Write-Log "STACK: $($_.ScriptStackTrace)"
    exit 1
}
exit 0
