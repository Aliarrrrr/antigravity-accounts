#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Prepare', 'LaunchA', 'LaunchB', 'StopA', 'StopB', 'Status')]
    [string]$Action,
    [string]$StatePath,
    [string]$Executable = "$env:LOCALAPPDATA\Programs\antigravity\Antigravity.exe",
    [string]$SandboxieHome = 'C:\Program Files\Sandboxie-Plus'
)

$ErrorActionPreference = 'Stop'
$start = Join-Path $SandboxieHome 'Start.exe'
$sbieIni = Join-Path $SandboxieHome 'SbieIni.exe'
foreach ($required in @($start, $sbieIni)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Sandboxie tool not found: $required"
    }
}

function Set-BoxValue([string]$Box, [string]$Setting, [string]$SettingValue) {
    & $sbieIni set $Box $Setting $SettingValue | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not configure sandbox '$Box' setting '$Setting'." }
    $actual = (& $sbieIni query $Box $Setting | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Could not verify sandbox '$Box' setting '$Setting'." }
    if ($actual -cne $SettingValue) { throw "Sandbox '$Box' did not retain setting '$Setting'." }
}

function Set-ProbeBoxConfiguration([string]$Box) {
    Set-BoxValue $Box 'Enabled' 'y'
    Set-BoxValue $Box 'ConfigLevel' '10'
    Set-BoxValue $Box 'AutoRecover' 'n'
    Set-BoxValue $Box 'AutoDelete' 'n'
    Set-BoxValue $Box 'BoxNameTitle' 'y'
    Set-BoxValue $Box 'OpenCredentials' 'n'
}

function New-ProbeBox([string]$Box, [Collections.Generic.List[string]]$CreatedBoxes) {
    & $sbieIni set $Box 'Enabled' 'y' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create sandbox '$Box'." }
    $CreatedBoxes.Add($Box)
    $actual = (& $sbieIni query $Box 'Enabled' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Could not verify sandbox '$Box' Enabled setting." }
    if ($actual -cne 'y') { throw "Sandbox '$Box' did not retain setting 'Enabled'." }
}

function Test-ReparsePoint([string]$Path) {
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    return (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Assert-NoReparseIfPresent([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        if (Test-ReparsePoint $Path) { throw "Probe path must not be a reparse point: $Path" }
    }
}

function Assert-ProfilePathsSafe($State) {
    foreach ($profileLabel in @('A', 'B')) {
        $profile = $State.('profile' + $profileLabel)
        Assert-NoReparseIfPresent (Join-Path (Split-Path $profile.home -Parent) '')
        foreach ($field in @('home', 'roaming', 'local', 'temp', 'userData')) {
            Assert-NoReparseIfPresent $profile.$field
        }
    }
}

function Read-ProbeState {
    if ([string]::IsNullOrWhiteSpace($StatePath)) { throw '-StatePath is required for this action.' }
    $resolved = [IO.Path]::GetFullPath($StatePath)
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AntigravityProfileHub\sandboxie-p2'))
    Assert-NoReparseIfPresent (Split-Path $allowedRoot -Parent)
    Assert-NoReparseIfPresent $allowedRoot
    $prefix = $allowedRoot + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Probe state must be under $allowedRoot"
    }
    $relative = $resolved.Substring($prefix.Length)
    $pathMatch = [regex]::Match($relative, '^(?<nonce>[0-9a-f]{32})\\state\.json$')
    if (-not $pathMatch.Success) {
        throw 'Probe state must be sandboxie-p2\<32 lowercase hex nonce>\state.json.'
    }
    $nonce = $pathMatch.Groups['nonce'].Value
    $nonceRoot = Join-Path $allowedRoot $nonce
    if (-not (Test-Path -LiteralPath $nonceRoot -PathType Container)) {
        throw "Probe nonce directory not found: $nonceRoot"
    }
    if (Test-ReparsePoint $nonceRoot) { throw "Probe nonce directory must not be a reparse point: $nonceRoot" }
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Probe state file not found: $resolved"
    }
    if (Test-ReparsePoint $resolved) { throw "Probe state file must not be a reparse point: $resolved" }
    $state = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
    $integerSchemaTypes = @([byte], [sbyte], [int16], [uint16], [int], [uint], [int64], [uint64])
    if ($null -eq $state.schemaVersion -or
        $integerSchemaTypes -notcontains $state.schemaVersion.GetType() -or
        [int64]$state.schemaVersion -ne 1) {
        throw 'Probe state schemaVersion must be the JSON integer 1.'
    }
    if ($state.boxA -isnot [string] -or $state.boxB -isnot [string]) {
        throw 'Probe sandbox names must be strings.'
    }
    $boxMatch = [regex]::Match($state.boxA, '^AGPH_P2_(?<prefix>[0-9a-f]{20}|[0-9a-f]{10})_A$')
    if (-not $boxMatch.Success) { throw 'Probe sandbox name A has an invalid format.' }
    $boxPrefix = $boxMatch.Groups['prefix'].Value
    if ($boxPrefix -cne $nonce.Substring(0, $boxPrefix.Length) -or
        $state.boxB -cne "AGPH_P2_${boxPrefix}_B") {
        throw 'Probe sandbox names do not match the state nonce.'
    }
    if ($state.boxA -ceq $state.boxB) { throw 'Probe sandbox names must be distinct.' }
    foreach ($profileLabel in @('A', 'B')) {
        $profile = $state.('profile' + $profileLabel)
        if ($null -eq $profile) { throw "Probe profile$profileLabel is missing." }
        foreach ($field in @('home', 'roaming', 'local', 'temp', 'userData')) {
            if ($profile.$field -isnot [string]) { throw "Probe profile$profileLabel.$field must be a string." }
            $directoryName = if ($field -eq 'userData') { 'user-data' } else { $field }
            $expected = [IO.Path]::GetFullPath((Join-Path $nonceRoot "$($profileLabel.ToLowerInvariant())\$directoryName"))
            if ($profile.$field -cne $expected) {
                throw "Probe profile$profileLabel.$field does not match the nonce directory."
            }
        }
    }
    Assert-NoReparseIfPresent (Join-Path $nonceRoot 'a')
    Assert-NoReparseIfPresent (Join-Path $nonceRoot 'b')
    Assert-ProfilePathsSafe $state
    if ($state.executable -isnot [string] -or -not [IO.Path]::IsPathFullyQualified($state.executable)) {
        throw 'Probe executable must be an absolute path.'
    }
    if ([IO.Path]::GetFileName($state.executable) -cne 'Antigravity.exe' -or
        -not (Test-Path -LiteralPath $state.executable -PathType Leaf)) {
        throw 'Probe executable must be an existing Antigravity.exe.'
    }
    if (Test-ReparsePoint $state.executable) { throw "Probe executable must not be a reparse point: $($state.executable)" }
    return $state
}

function Get-ExistingProbeBoxes {
    $listing = @(& $sbieIni queryex /boxes '*' 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "Could not query the Sandboxie box list (exit code $LASTEXITCODE)." }
    return @($listing | ForEach-Object {
        $line = ([string]$_).Trim()
        if ($line -match '^\[(?<name>[^\]]+)\]$') { $Matches.name } elseif ($line -notmatch '[=:\s]' -and $line) { $line }
    })
}

function Remove-ProbeBox([string]$Box) {
    $errors = [Collections.Generic.List[string]]::new()
    try { & $start "/box:$Box" /silent /terminate | Out-Null; if ($LASTEXITCODE -ne 0) { throw "terminate exit code $LASTEXITCODE" } } catch { $errors.Add("$Box terminate: $($_.Exception.Message)") }
    try { & $start "/box:$Box" /silent delete_sandbox_silent | Out-Null; if ($LASTEXITCODE -ne 0) { throw "delete exit code $LASTEXITCODE" } } catch { $errors.Add("$Box delete: $($_.Exception.Message)") }
    try { & $sbieIni set $Box '*' '' | Out-Null; if ($LASTEXITCODE -ne 0) { throw "clear configuration exit code $LASTEXITCODE" } } catch { $errors.Add("$Box configuration: $($_.Exception.Message)") }
    try {
        if (@(Get-ExistingProbeBoxes) -contains $Box) { throw 'sandbox remains in the box list' }
    }
    catch { $errors.Add("$Box verification: $($_.Exception.Message)") }
    return $errors.ToArray()
}

function Get-BoxProcesses([string]$Box) {
    $raw = (& $start "/box:$Box" /silent /listpids | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Could not list processes in sandbox '$Box' (exit code $LASTEXITCODE)." }
    $numbers = @([regex]::Matches($raw, '(?m)^\s*(\d+)\s*$') | ForEach-Object { [int]$_.Groups[1].Value })
    if ($numbers.Count -gt 0 -and $numbers[0] -eq ($numbers.Count - 1)) { $numbers = @($numbers | Select-Object -Skip 1) }
    return @($numbers | ForEach-Object {
        try {
            $process = Get-Process -Id $_ -ErrorAction Stop
            $processPath = $null
            $startedUtc = $null
            $hasWindow = $false
            try { $processPath = $process.Path } catch { }
            try { $startedUtc = $process.StartTime.ToUniversalTime().ToString('o') } catch { }
            try { $hasWindow = $process.MainWindowHandle -ne 0 } catch { }
            [ordered]@{
                processId = $_
                name = $process.ProcessName
                executablePath = $processPath
                startedUtc = $startedUtc
                hasWindow = $hasWindow
            }
        }
        catch {
            [ordered]@{ processId = $_; name = 'exited'; executablePath = $null; startedUtc = $null; hasWindow = $false }
        }
    })
}

function Launch-Profile($State, [string]$Label) {
    $profile = if ($Label -eq 'A') { $State.profileA } else { $State.profileB }
    $box = if ($Label -eq 'A') { $State.boxA } else { $State.boxB }
    Set-ProbeBoxConfiguration $box
    $existing = @(Get-BoxProcesses $box | Where-Object { $_.name -ieq 'Antigravity' })
    if ($existing.Count -gt 0) {
        return [ordered]@{ action = "Launch$Label"; started = $false; detail = 'already-running'; box = $box; processes = @(Get-BoxProcesses $box) }
    }

    foreach ($path in @($profile.home, $profile.roaming, $profile.local, $profile.temp, $profile.userData)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
    Assert-ProfilePathsSafe $State
    $drive = [IO.Path]::GetPathRoot($profile.home).TrimEnd('\')
    $homePath = if ($profile.home.Length -ge 3 -and $profile.home[1] -eq ':') { $profile.home.Substring(2) } else { $profile.home }
    $arguments = @(
        "/box:$box",
        '/silent',
        "/env:USERPROFILE=$($profile.home)",
        "/env:HOME=$($profile.home)",
        "/env:HOMEDRIVE=$drive",
        "/env:HOMEPATH=$homePath",
        "/env:APPDATA=$($profile.roaming)",
        "/env:LOCALAPPDATA=$($profile.local)",
        "/env:TEMP=$($profile.temp)",
        "/env:TMP=$($profile.temp)",
        $State.executable,
        "--user-data-dir=$($profile.userData)"
    )
    & $start @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Sandboxie refused to launch Profile $Label (exit code $LASTEXITCODE)." }

    $processes = @()
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 500
        $processes = @(Get-BoxProcesses $box)
        if ($processes | Where-Object { $_.name -ieq 'Antigravity' }) { break }
    }
    if (-not ($processes | Where-Object { $_.name -ieq 'Antigravity' })) {
        throw "Profile $Label launch returned success but no Antigravity process appeared in sandbox '$box'."
    }
    return [ordered]@{ action = "Launch$Label"; started = $true; detail = 'sandbox-membership-observed'; box = $box; processes = $processes }
}

function Stop-Profile($State, [string]$Label) {
    $box = if ($Label -eq 'A') { $State.boxA } else { $State.boxB }
    Set-ProbeBoxConfiguration $box
    $before = @(Get-BoxProcesses $box)
    if ($before.Count -eq 0) {
        return [ordered]@{ action = "Stop$Label"; wasRunning = $false; box = $box; remainingProcesses = @() }
    }

    & $start "/box:$box" /silent /terminate | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Sandboxie refused to stop Profile $Label (exit code $LASTEXITCODE)." }
    $remaining = @()
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 250
        $remaining = @(Get-BoxProcesses $box)
        if ($remaining.Count -eq 0) { break }
    }
    if ($remaining.Count -ne 0) { throw "Profile $Label still has processes in sandbox '$box' after termination." }
    return [ordered]@{ action = "Stop$Label"; wasRunning = $true; box = $box; stoppedProcessCount = $before.Count; remainingProcesses = @() }
}

if ($Action -eq 'Prepare') {
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw "Antigravity executable not found: $Executable" }
    $Executable = [IO.Path]::GetFullPath($Executable)
    if ([IO.Path]::GetFileName($Executable) -cne 'Antigravity.exe') { throw 'Executable must be Antigravity.exe.' }
    if (Test-ReparsePoint $Executable) { throw "Probe executable must not be a reparse point: $Executable" }
    $profileHubRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AntigravityProfileHub'))
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $profileHubRoot 'sandboxie-p2'))
    Assert-NoReparseIfPresent $profileHubRoot
    Assert-NoReparseIfPresent $allowedRoot
    $nonce = [guid]::NewGuid().ToString('N')
    $root = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "AntigravityProfileHub\sandboxie-p2\$nonce"))
    $StatePath = Join-Path $root 'state.json'
    $boxA = "AGPH_P2_$($nonce.Substring(0, 20))_A"
    $boxB = "AGPH_P2_$($nonce.Substring(0, 20))_B"
    $state = [ordered]@{
        schemaVersion = 1
        createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
        sandboxieVersion = (Get-Item -LiteralPath $start).VersionInfo.ProductVersion
        antigravityVersion = (Get-Item -LiteralPath $Executable).VersionInfo.ProductVersion
        executable = $Executable
        boxA = $boxA
        boxB = $boxB
        profileA = [ordered]@{
            home = Join-Path $root 'a\home'
            roaming = Join-Path $root 'a\roaming'
            local = Join-Path $root 'a\local'
            temp = Join-Path $root 'a\temp'
            userData = Join-Path $root 'a\user-data'
        }
        profileB = [ordered]@{
            home = Join-Path $root 'b\home'
            roaming = Join-Path $root 'b\roaming'
            local = Join-Path $root 'b\local'
            temp = Join-Path $root 'b\temp'
            userData = Join-Path $root 'b\user-data'
        }
        authentication = 'user-manual-only'
        cleanup = 'preserve-until-manual-acceptance-finishes'
    }
    $createdBoxes = [Collections.Generic.List[string]]::new()
    try {
        $existingBoxes = @(Get-ExistingProbeBoxes)
        foreach ($box in @($boxA, $boxB)) {
            if ($existingBoxes -contains $box) { throw "Sandboxie box already exists: $box" }
        }
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        Assert-NoReparseIfPresent $profileHubRoot
        Assert-NoReparseIfPresent $allowedRoot
        if (Test-ReparsePoint $root) { throw "Probe nonce directory must not be a reparse point: $root" }
        foreach ($box in @($boxA, $boxB)) {
            New-ProbeBox $box $createdBoxes
            Set-BoxValue $box 'ConfigLevel' '10'
            Set-BoxValue $box 'AutoRecover' 'n'
            Set-BoxValue $box 'AutoDelete' 'n'
            Set-BoxValue $box 'BoxNameTitle' 'y'
            Set-BoxValue $box 'OpenCredentials' 'n'
        }
        $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $StatePath -Encoding utf8
    }
    catch {
        $originalError = $_.Exception.Message
        $rollbackErrors = [Collections.Generic.List[string]]::new()
        foreach ($box in @($createdBoxes | Select-Object -Reverse)) {
            foreach ($rollbackError in @(Remove-ProbeBox $box)) { $rollbackErrors.Add($rollbackError) }
        }
        if ($rollbackErrors.Count -gt 0) {
            throw "$originalError Rollback failed: $($rollbackErrors -join '; ')"
        }
        throw
    }
    [ordered]@{ action = 'Prepare'; statePath = $StatePath; state = $state } | ConvertTo-Json -Depth 10
    exit 0
}

$state = Read-ProbeState
switch ($Action) {
    'LaunchA' { Launch-Profile $state 'A' | ConvertTo-Json -Depth 8 }
    'LaunchB' { Launch-Profile $state 'B' | ConvertTo-Json -Depth 8 }
    'StopA' { Stop-Profile $state 'A' | ConvertTo-Json -Depth 8 }
    'StopB' { Stop-Profile $state 'B' | ConvertTo-Json -Depth 8 }
    'Status' {
        Set-ProbeBoxConfiguration $state.boxA
        Set-ProbeBoxConfiguration $state.boxB
        [ordered]@{
            action = 'Status'
            statePath = [IO.Path]::GetFullPath($StatePath)
            a = [ordered]@{ box = $state.boxA; processes = @(Get-BoxProcesses $state.boxA) }
            b = [ordered]@{ box = $state.boxB; processes = @(Get-BoxProcesses $state.boxB) }
        } | ConvertTo-Json -Depth 8
    }
}
