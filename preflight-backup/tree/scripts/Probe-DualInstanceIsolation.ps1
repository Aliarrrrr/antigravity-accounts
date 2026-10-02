#Requires -Version 5.1
<#
  Dual-instance isolation probe (read-only, no login, no credential access).

  Purpose
    Determine whether two concurrently launched, unmodified Antigravity Desktop
    instances actually get distinct language-server data directories and ports
    when launched with per-profile USERPROFILE/HOME/APPDATA/LOCALAPPDATA and
    --user-data-dir. This is evidence collection only. It never signs in, never
    opens a browser OAuth flow, and never reads credential, cookie or token data.

  Redaction
    --csrf_token / --host_bridge_token / --host_bridge_url values are replaced by
    <redacted:len=N>. Only field names and lengths are recorded.
#>

[CmdletBinding()]
param(
    [int]$WarmupSeconds = 25,
    [int]$DrainSeconds = 8,
    [string]$OutRoot = 'D:\mutigravity\artifacts',
    [string]$Executable = 'C:\Users\ZLF\AppData\Local\Programs\antigravity\Antigravity.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:RedactNames = @('csrf_token', 'host_bridge_token', 'host_bridge_url', 'agy_token')

function Write-DualLog([string]$Message) {
    Write-Host "[dual-probe] $Message"
}

function Protect-CommandLine([string]$CommandLine) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $null }
    $result = $CommandLine
    foreach ($name in $script:RedactNames) {
        $pattern = '--' + $name + '\s+("[^"]*"|\S+)'
        $match = [regex]::Match($result, $pattern)
        if ($match.Success) {
            $raw = $match.Groups[1].Value
            $len = $raw.Trim('"').Length
            $result = $result.Replace($raw, '<redacted:len=' + $len + '>')
        }
    }
    return $result
}

function Get-Snapshot {
    $rows = @()
    $procs = Get-CimInstance Win32_Process -Filter "Name='Antigravity.exe' OR Name='language_server.exe'" -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        $rows += [pscustomobject]@{
            processId        = $p.ProcessId
            parentProcessId  = $p.ParentProcessId
            name             = $p.Name
            creationDate     = ($p.CreationDate | Out-String).Trim()
            executablePath   = $p.ExecutablePath
            commandLine      = (Protect-CommandLine $p.CommandLine)
        }
    }
    return $rows
}

function Get-Listeners {
    $rows = @()
    $conns = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue
    foreach ($c in $conns) {
        $rows += [pscustomobject]@{
            localAddress = $c.LocalAddress
            localPort    = $c.LocalPort
            owningPid    = $c.OwningProcess
        }
    }
    return $rows
}

function Get-Established {
    $rows = @()
    $conns = Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalAddress -eq '127.0.0.1' -or $_.RemoteAddress -eq '127.0.0.1' }
    foreach ($c in $conns) {
        $rows += [pscustomobject]@{
            localAddress  = $c.LocalAddress
            localPort     = $c.LocalPort
            remoteAddress = $c.RemoteAddress
            remotePort    = $c.RemotePort
            owningPid     = $c.OwningProcess
        }
    }
    return $rows
}

function Get-DirFacts([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ path = $Path; exists = $false; entries = @() }
    }
    $entries = @()
    Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue |
        ForEach-Object { $entries += $_.Name }
    return [pscustomobject]@{ path = $Path; exists = $true; entries = $entries }
}

function Get-TreeFacts([string]$Path, [int]$Depth) {
    $facts = @()
    if (-not (Test-Path -LiteralPath $Path)) { return $facts }
    $stack = New-Object System.Collections.Stack
    $stack.Push([pscustomobject]@{ Path = $Path; Depth = 0 })
    while ($stack.Count -gt 0) {
        $node = $stack.Pop()
        $facts += [pscustomobject]@{
            path      = $node.Path
            depth     = $node.Depth
            container = $true
        }
        if ($node.Depth -ge $Depth) { continue }
        Get-ChildItem -LiteralPath $node.Path -Directory -Force -ErrorAction SilentlyContinue |
            ForEach-Object { $stack.Push([pscustomobject]@{ Path = $_.FullName; Depth = ($node.Depth + 1) }) }
    }
    return $facts
}

# ---------------------------------------------------------------- preconditions

if (-not (Test-Path -LiteralPath $Executable)) {
    throw "Antigravity executable not found: $Executable"
}

$running = @(Get-Process -Name 'Antigravity', 'language_server' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw "Antigravity or language_server is already running ($($running.Count) process(es)). Close it before probing."
}

$nonce = [guid]::NewGuid().ToString('N')
$root = Join-Path $OutRoot ("dual-instance-isolation\$nonce")
$null = New-Item -ItemType Directory -Path $root -Force
Write-DualLog "artifacts: $root"

$homeA = Join-Path $root 'home-a'
$homeB = Join-Path $root 'home-b'
$udA = Join-Path $root 'user-data-a'
$udB = Join-Path $root 'user-data-b'
foreach ($d in @($homeA, $homeB, $udA, $udB)) { $null = New-Item -ItemType Directory -Path $d -Force }

$overrides = @{
    A = @{
        USERPROFILE  = $homeA
        HOME         = $homeA
        APPDATA      = (Join-Path $homeA 'AppData\Roaming')
        LOCALAPPDATA = (Join-Path $homeA 'AppData\Local')
        TEMP         = (Join-Path $homeA 'AppData\Local\Temp')
        TMP          = (Join-Path $homeA 'AppData\Local\Temp')
    }
    B = @{
        USERPROFILE  = $homeB
        HOME         = $homeB
        APPDATA      = (Join-Path $homeB 'AppData\Roaming')
        LOCALAPPDATA = (Join-Path $homeB 'AppData\Local')
        TEMP         = (Join-Path $homeB 'AppData\Local\Temp')
        TMP          = (Join-Path $homeB 'AppData\Local\Temp')
    }
}

function Start-Instance([string]$HomeDir, [string]$UserDataDir, [hashtable]$Override) {
    # Inherit the current process environment, temporarily overlay per-profile
    # variables, then restore. The child process captures the environment at
    # creation time.
    $saved = @{}
    foreach ($key in $Override.Keys) {
        $saved[$key] = [System.Environment]::GetEnvironmentVariable($key)
    }
    try {
        foreach ($key in $Override.Keys) {
            [System.Environment]::SetEnvironmentVariable($key, $Override[$key])
        }
        $proc = Start-Process -FilePath $Executable `
            -ArgumentList @('--user-data-dir="' + $UserDataDir + '"') `
            -WorkingDirectory $HomeDir -PassThru
        return $proc
    }
    finally {
        foreach ($key in $Override.Keys) {
            [System.Environment]::SetEnvironmentVariable($key, $saved[$key])
        }
    }
}

# ---------------------------------------------------------------- run

$result = [ordered]@{
    schema      = 'dual-instance-isolation/1'
    nonce       = $nonce
    startedUtc  = (Get-Date).ToUniversalTime().ToString('o')
    executable  = $Executable
    warmupSec   = $WarmupSeconds
    instances   = @()
    samples     = @()
    directories = @()
    notes       = @()
    losses      = @()
}

try {
    Write-DualLog 'starting instance A'
    $procA = Start-Instance $homeA $udA $overrides.A
    $result.instances += [pscustomobject]@{ tag = 'A'; pid = $procA.Id; home = $homeA; userData = $udA }
    Start-Sleep -Seconds $WarmupSeconds

    Write-DualLog 'snapshot after A'
    $result.samples += [pscustomobject]@{
        stage          = 'only-A'
        processes      = (Get-Snapshot)
        listeners      = (Get-Listeners)
        loopbackConns  = (Get-Established)
    }

    Write-DualLog 'starting instance B'
    $procB = Start-Instance $homeB $udB $overrides.B
    $result.instances += [pscustomobject]@{ tag = 'B'; pid = $procB.Id; home = $homeB; userData = $udB }
    Start-Sleep -Seconds $WarmupSeconds

    Write-DualLog 'snapshot after A+B'
    $result.samples += [pscustomobject]@{
        stage          = 'A-and-B'
        processes      = (Get-Snapshot)
        listeners      = (Get-Listeners)
        loopbackConns  = (Get-Established)
    }
}
catch {
    $result.notes += ('error: ' + $_.Exception.Message)
    Write-DualLog ('ERROR ' + $_.Exception.Message)
}
finally {
    Write-DualLog 'collecting directory facts'
    foreach ($p in @(
        (Join-Path $homeA '.gemini'),
        (Join-Path $homeB '.gemini'),
        (Join-Path $homeA 'AppData\Roaming\Antigravity'),
        (Join-Path $homeB 'AppData\Roaming\Antigravity'),
        (Join-Path $homeA 'AppData\Local\Antigravity'),
        (Join-Path $homeB 'AppData\Local\Antigravity')
    )) {
        $result.directories += (Get-TreeFacts $p 3)
    }
    # Host locations: existence only, never contents.
    $hostGemini = Join-Path $env:USERPROFILE '.gemini'
    $result.directories += [pscustomobject]@{
        path      = $hostGemini
        depth     = 0
        container = (Test-Path -LiteralPath $hostGemini)
        note      = 'host-user-gemini-existence-only'
    }

    Write-DualLog 'terminating probe processes'
    foreach ($name in @('Antigravity', 'language_server')) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            try { $_.Kill($true) } catch { $result.losses += ('kill-failed:' + $name + ':' + $_.Id) }
        }
    }
    Start-Sleep -Seconds $DrainSeconds
    $leftover = @(Get-Process -Name 'Antigravity', 'language_server' -ErrorAction SilentlyContinue)
    $result.cleanup = [pscustomobject]@{
        leftoverCount = $leftover.Count
        leftoverPids  = @($leftover | ForEach-Object { $_.Id })
        verifiedUtc   = (Get-Date).ToUniversalTime().ToString('o')
    }
}

$result.finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
$outFile = Join-Path $root 'report.json'
($result | ConvertTo-Json -Depth 8) | Out-File -FilePath $outFile -Encoding utf8
Write-DualLog "report: $outFile"
