#requires -Version 7.0
[CmdletBinding()]
param([string]$Executable = "$env:LOCALAPPDATA/Programs/antigravity/Antigravity.exe", [int]$ObserveSeconds = 50,
      [string]$Report = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/next-desktop-probe.json'))
$ErrorActionPreference = 'Stop'
$Report = [IO.Path]::GetFullPath($Report)
New-Item -ItemType Directory -Path (Split-Path $Report -Parent) -Force | Out-Null
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$probeRoot = Join-Path $env:TEMP ('agprofile-next-desktop-' + [guid]::NewGuid().ToString('N'))
foreach ($folder in @('home','roaming','local','temp','user-data','workspace-sentinel')) {
    New-Item -ItemType Directory -Path (Join-Path $probeRoot $folder) -Force | Out-Null
}
$schemePath = 'HKCU:/Software/Classes/antigravity/shell/open/command'
$previousCommand = if (Test-Path $schemePath) { (Get-Item $schemePath).GetValue('') } else { $null }
$expectedData = Join-Path $probeRoot 'user-data'
$started = [DateTime]::UtcNow
$result = [ordered]@{schemaVersion=1;strategyRevision=2;scratchRoot=$probeRoot;fileTokenStoreMarker=$false;rootObserved=$false;homeStateObserved=$false;userDataObserved=$false;workspaceContract='unsupported';protocolRestored=$false;cleanup='pending';manualAcceptance=$false}
function Get-OwnedRoots {
    # No argv is emitted or persisted. Exact quoted switch and canonical executable are both required.
    @(Get-CimInstance Win32_Process -Filter "Name='Antigravity.exe'" | Where-Object {
        $_.ExecutablePath -eq $Executable -and $_.CreationDate.ToUniversalTime() -ge $started.AddSeconds(-1) -and
        ($_.CommandLine.Contains('"--user-data-dir=' + $expectedData + '"') -or $_.CommandLine.Contains('--user-data-dir="' + $expectedData + '"'))
    })
}
try {
    $wrapper = Join-Path $probeRoot 'probe.cmd'
    $lines = @('@echo off','@chcp 65001>nul')
    foreach ($pair in @{'HOME'=(Join-Path $probeRoot 'home');'USERPROFILE'=(Join-Path $probeRoot 'home');'APPDATA'=(Join-Path $probeRoot 'roaming');'LOCALAPPDATA'=(Join-Path $probeRoot 'local');'TEMP'=(Join-Path $probeRoot 'temp');'TMP'=(Join-Path $probeRoot 'temp');'WSL_DISTRO_NAME'='AGPHNextProbe';'ELECTRON_RUN_AS_NODE'='';'NODE_OPTIONS'='';'ANTIGRAVITY_WSL_DISTRO'='';'SSH_CONNECTION'='';'SSH_CLIENT'=''}.GetEnumerator()) {
        $lines += 'set "' + $pair.Key + '=' + $pair.Value.Replace('%','%%') + '"'
    }
    $lines += 'start "" "' + $Executable + '" "--user-data-dir=' + $expectedData + '" "' + (Join-Path $probeRoot 'workspace-sentinel') + '"'
    [IO.File]::WriteAllText($wrapper, ($lines -join "`r`n"), [Text.UTF8Encoding]::new($false))
    Start-Process -FilePath $wrapper -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds($ObserveSeconds)
    do {
        Start-Sleep -Seconds 2
        $roots = @(Get-OwnedRoots)
        $result.rootObserved = $roots.Count -gt 0
        $log = Join-Path $expectedData 'logs/language_server.log'
        if (Test-Path -LiteralPath $log) {
            # Read only the fresh probe log, retaining a fixed boolean. Never read auth state files.
            $logStream = [IO.FileStream]::new($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            $logReader = [IO.StreamReader]::new($logStream)
            try { $result.fileTokenStoreMarker = $logReader.ReadToEnd().Contains('Using file-based token storage') }
            finally { $logReader.Dispose() }
        }
    } while ([DateTime]::UtcNow -lt $deadline -and -not $result.fileTokenStoreMarker)
    $result.homeStateObserved = Test-Path -LiteralPath (Join-Path $probeRoot 'home/.gemini')
    $result.userDataObserved = Test-Path -LiteralPath (Join-Path $expectedData 'Local State')
} finally {
    foreach ($row in @(Get-OwnedRoots)) {
        try {
            $ownedProcess = [Diagnostics.Process]::GetProcessById([int]$row.ProcessId)
            if ([Math]::Abs(($ownedProcess.StartTime.ToUniversalTime() - $row.CreationDate.ToUniversalTime()).TotalSeconds) -gt 1) { continue }
            $null = $ownedProcess.CloseMainWindow()
            if (-not $ownedProcess.WaitForExit(5000)) { $ownedProcess.Kill($true); $null=$ownedProcess.WaitForExit(5000) }
        } catch [ArgumentException] { }
    }
    $currentCommand = if (Test-Path $schemePath) { (Get-Item $schemePath).GetValue('') } else { $null }
    if ($currentCommand -eq $previousCommand) { $result.protocolRestored=$true }
    elseif ($currentCommand -and $currentCommand.StartsWith('"' + $Executable + '"')) {
        if ($null -ne $previousCommand) { (Get-Item $schemePath).SetValue('', $previousCommand) }
        else { (Get-Item $schemePath).DeleteValue('', $false) }
        $result.protocolRestored=$true
    }
    $result.cleanup = if (@(Get-OwnedRoots).Count -eq 0) {'owned-processes-stopped-scratch-retained'} else {'failed-processes-remain'}
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Report -Encoding utf8
    $result | ConvertTo-Json -Depth 6
}
