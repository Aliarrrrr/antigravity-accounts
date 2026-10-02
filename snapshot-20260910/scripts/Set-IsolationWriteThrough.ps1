#Requires -Version 5.1
<#
  Configures bare, repeated OpenFilePath rules for one Sandboxie box.

  Sandboxie 1.18.2/core 5.73.2 was observed to ignore indexed rules such as
  OpenFilePath.1 while accepting repeated bare keys. The service/driver also
  needs a restart before it observes a direct configuration-file edit.

  All Sandboxie boxes must therefore be stopped first. The script validates
  every input, creates an adjacent backup, replaces only the selected box's
  OpenFilePath rules, verifies the saved file, and restores the backup if the
  Sandboxie service cannot be restarted.
#>

[CmdletBinding(DefaultParameterSetName = 'Paths')]
param(
    [Parameter(Mandatory = $true)]
    [string]$BoxName,

    [Parameter(Mandatory = $true, ParameterSetName = 'Paths')]
    [string[]]$Path,

    [Parameter(Mandatory = $true, ParameterSetName = 'Profile')]
    [string]$ProfileRoot,

    [Parameter(Mandatory = $true, ParameterSetName = 'Profile')]
    [guid]$ProfileId,

    [Parameter(Mandatory = $true)]
    [switch]$ConfirmAllBoxesStopped,

    [string]$SandboxieHome = 'C:\Program Files\Sandboxie-Plus',
    [string]$ConfigurationPath = "$env:WINDIR\Sandboxie.ini"
)

$ErrorActionPreference = 'Stop'

function Read-SandboxieConfiguration([string]$LiteralPath) {
    $raw = [IO.File]::ReadAllBytes($LiteralPath)
    if ($raw.Length -ge 2 -and $raw[0] -eq 0xFF -and $raw[1] -eq 0xFE) {
        $encoding = [Text.UnicodeEncoding]::new($false, $false, $true)
        $offset = 2
        [byte[]]$preamble = @(0xFF, 0xFE)
    }
    elseif ($raw.Length -ge 3 -and $raw[0] -eq 0xEF -and $raw[1] -eq 0xBB -and $raw[2] -eq 0xBF) {
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        $offset = 3
        [byte[]]$preamble = @(0xEF, 0xBB, 0xBF)
    }
    else {
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        $offset = 0
        [byte[]]$preamble = @()
    }
    [pscustomobject]@{
        Text = $encoding.GetString($raw, $offset, $raw.Length - $offset)
        Encoding = $encoding
        Preamble = $preamble
    }
}

function ConvertTo-ConfigurationBytes($Document, [string]$Text) {
    [byte[]]$payload = $Document.Encoding.GetBytes($Text)
    [byte[]]$result = [byte[]]::new($Document.Preamble.Length + $payload.Length)
    if ($Document.Preamble.Length -gt 0) {
        [Array]::Copy($Document.Preamble, 0, $result, 0, $Document.Preamble.Length)
    }
    [Array]::Copy($payload, 0, $result, $Document.Preamble.Length, $payload.Length)
    return $result
}

function Get-BoxOpenFilePaths([string]$Text, [string]$ExpectedBox) {
    $inside = $false
    $values = [Collections.Generic.List[string]]::new()
    foreach ($line in [regex]::Split($Text, '\r?\n')) {
        if ($line -match '^\s*\[([^\]]+)\]\s*$') {
            $inside = $Matches[1].Equals($ExpectedBox, [StringComparison]::OrdinalIgnoreCase)
            continue
        }
        if ($inside -and $line -match '^\s*OpenFilePath(?:\.\d+)?\s*=\s*(.*)$') {
            $values.Add($Matches[1].Trim())
        }
    }
    return @($values)
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must run from an elevated PowerShell prompt.'
}
if ($BoxName -notmatch '^[A-Za-z0-9_]{1,64}$') {
    throw "Box name '$BoxName' contains unsupported characters."
}
if (-not $ConfirmAllBoxesStopped) {
    throw 'Stop every Sandboxie box, then rerun with -ConfirmAllBoxesStopped.'
}
if ($PSCmdlet.ParameterSetName -eq 'Profile') {
    $base = Join-Path ([IO.Path]::GetFullPath($ProfileRoot)) $ProfileId.ToString('D')
    $Path = @('home', 'roaming', 'local', 'temp', 'user-data') | ForEach-Object { Join-Path $base $_ }
}
if (-not $Path -or $Path.Count -eq 0 -or $Path.Count -gt 32) {
    throw 'Provide between 1 and 32 direct-write paths.'
}

$validated = [Collections.Generic.List[string]]::new()
foreach ($candidate in $Path) {
    if ([string]::IsNullOrWhiteSpace($candidate) -or $candidate.IndexOfAny(@([char]0, [char]10, [char]13)) -ge 0) {
        throw 'Direct-write paths must be non-empty single-line filesystem paths.'
    }
    $full = [IO.Path]::GetFullPath($candidate)
    if (-not [IO.Path]::IsPathFullyQualified($full)) { throw "Path '$candidate' is not fully qualified." }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "Directory '$full' does not exist." }
    if ($validated -notcontains $full) { $validated.Add($full) }
}

$startPath = Join-Path $SandboxieHome 'Start.exe'
if (-not (Test-Path -LiteralPath $startPath -PathType Leaf)) { throw 'Sandboxie Plus Start.exe was not found.' }
if (-not (Test-Path -LiteralPath $ConfigurationPath -PathType Leaf)) { throw "Sandboxie configuration '$ConfigurationPath' was not found." }

$running = @(& $startPath "/box:$BoxName" /listpids 2>$null | Where-Object { $_ -match '^\s*\d+\s*$' })
if ($running.Count -gt 0) { throw "Isolation box '$BoxName' is still running. Stop every Sandboxie box before changing write-through rules." }

$document = Read-SandboxieConfiguration $ConfigurationPath
$newline = if ($document.Text.Contains("`r`n")) { "`r`n" } else { "`n" }
$hadFinalNewline = $document.Text.EndsWith("`n", [StringComparison]::Ordinal)
$lines = @([regex]::Split($document.Text, '\r?\n'))
if ($hadFinalNewline -and $lines.Count -gt 0 -and $lines[-1] -eq '') {
    if ($lines.Count -eq 1) { $lines = @() } else { $lines = @($lines[0..($lines.Count - 2)]) }
}

$output = [Collections.Generic.List[string]]::new()
$insideTarget = $false
$boxSeen = $false
foreach ($line in $lines) {
    if ($line -match '^\s*\[([^\]]+)\]\s*$') {
        $insideTarget = $Matches[1].Equals($BoxName, [StringComparison]::OrdinalIgnoreCase)
        $output.Add($line)
        if ($insideTarget) {
            $boxSeen = $true
            foreach ($item in $validated) { $output.Add("OpenFilePath=$item") }
        }
        continue
    }
    if ($insideTarget -and $line -match '^\s*OpenFilePath(?:\.\d+)?\s*=') { continue }
    $output.Add($line)
}
if (-not $boxSeen) { throw "Isolation box section '$BoxName' was not found in '$ConfigurationPath'." }

$updatedText = [string]::Join($newline, $output)
if ($hadFinalNewline) { $updatedText += $newline }
$tempPath = "$ConfigurationPath.agph-$([guid]::NewGuid().ToString('N')).tmp"
$backupPath = "$ConfigurationPath.agph-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$([guid]::NewGuid().ToString('N').Substring(0, 8)).bak"
[IO.File]::WriteAllBytes($tempPath, (ConvertTo-ConfigurationBytes $document $updatedText))
try {
    [IO.File]::Replace($tempPath, $ConfigurationPath, $backupPath, $true)
}
finally {
    if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force }
}

try {
    $saved = Read-SandboxieConfiguration $ConfigurationPath
    $actual = @(Get-BoxOpenFilePaths $saved.Text $BoxName)
    if ($actual.Count -ne $validated.Count) { throw 'Saved OpenFilePath rule count does not match the requested set.' }
    foreach ($expected in $validated) {
        if ($actual -notcontains $expected) {
            throw "Saved configuration is missing '$expected'."
        }
    }

    $service = Get-Service -Name 'SbieSvc' -ErrorAction Stop
    if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name 'SbieSvc' -Force -ErrorAction Stop
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
    }
    Start-Service -Name 'SbieSvc' -ErrorAction Stop
    (Get-Service -Name 'SbieSvc').WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
}
catch {
    Copy-Item -LiteralPath $backupPath -Destination $ConfigurationPath -Force
    try { Start-Service -Name 'SbieSvc' -ErrorAction SilentlyContinue } catch { }
    throw "Write-through setup failed and '$backupPath' was restored: $($_.Exception.Message)"
}

Write-Output "Configured $($validated.Count) direct-write path(s) for '$BoxName'."
Write-Output "Backup: $backupPath"
Write-Output 'Start the Profile and run agprofile iso-status; then complete the live workspace write-through check.'
