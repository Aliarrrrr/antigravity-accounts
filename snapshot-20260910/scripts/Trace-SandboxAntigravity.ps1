#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('A', 'B')]
    [string]$Profile,
    [Parameter(Mandatory)]
    [string]$StatePath,
    [ValidateRange(5, 60)]
    [int]$DurationSeconds = 15,
    [string]$SandboxieHome = 'C:\Program Files\Sandboxie-Plus'
)

$ErrorActionPreference = 'Stop'
$start = Join-Path $SandboxieHome 'Start.exe'
$sbieIni = Join-Path $SandboxieHome 'SbieIni.exe'
$sbieDll = Join-Path $SandboxieHome 'SbieDll.dll'
foreach ($required in @($start, $sbieIni, $sbieDll)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Sandboxie tool not found: $required"
    }
}

$resolvedStatePath = [IO.Path]::GetFullPath($StatePath)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AntigravityProfileHub\sandboxie-p2'))

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
    $prefix = $allowedRoot + '\'
    Assert-NoReparseIfPresent (Split-Path $allowedRoot -Parent)
    Assert-NoReparseIfPresent $allowedRoot
    if (-not $resolvedStatePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Probe state must be under $allowedRoot"
    }
    $relative = $resolvedStatePath.Substring($prefix.Length)
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
    if (-not (Test-Path -LiteralPath $resolvedStatePath -PathType Leaf)) {
        throw "Probe state file not found: $resolvedStatePath"
    }
    if (Test-ReparsePoint $resolvedStatePath) { throw "Probe state file must not be a reparse point: $resolvedStatePath" }
    $state = Get-Content -LiteralPath $resolvedStatePath -Raw | ConvertFrom-Json
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

$state = Read-ProbeState
$box = if ($Profile -eq 'A') { $state.boxA } else { $state.boxB }

$launcher = Join-Path $PSScriptRoot 'Test-SandboxAntigravity.ps1'
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw "Sandbox diagnostic launcher not found: $launcher"
}

function Get-BoxProcessIds {
    $raw = (& $start "/box:$box" /silent /listpids | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "Could not list processes in sandbox '$box' (exit code $LASTEXITCODE)." }
    $numbers = @([regex]::Matches($raw, '(?m)^\s*(\d+)\s*$') | ForEach-Object { [uint32]$_.Groups[1].Value })
    if ($numbers.Count -gt 0 -and $numbers[0] -eq ($numbers.Count - 1)) {
        $numbers = @($numbers | Select-Object -Skip 1)
    }
    return $numbers
}

$existingProcessIds = @(Get-BoxProcessIds)
if ($existingProcessIds.Count -ne 0) {
    throw "Sandbox '$box' must be stopped before startup tracing."
}

$dllLiteral = $sbieDll.Replace('"', '""')
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace AGProfileHub.Diagnostics
{
    public static class SandboxTraceNative
    {
        [DllImport(@"$dllLiteral", CallingConvention = CallingConvention.StdCall)]
        public static extern int SbieApi_MonitorControl(IntPtr newState, out uint oldState);

        [DllImport(@"$dllLiteral", CallingConvention = CallingConvention.StdCall)]
        public static extern int SbieApi_MonitorControl(ref uint newState, IntPtr oldState);

        [DllImport(@"$dllLiteral", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public static extern int SbieApi_MonitorGetEx(
            ref uint type,
            ref uint processId,
            ref uint threadId,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 256)] char[] data);

        [DllImport(@"$dllLiteral", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public static extern int SbieApi_QueryProcess(
            IntPtr processId,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 34)] char[] boxName,
            IntPtr imageName,
            IntPtr sidString,
            IntPtr sessionId);

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static string[] SplitCommandLine(string commandLine)
        {
            if (String.IsNullOrEmpty(commandLine)) return Array.Empty<string>();
            int argumentCount;
            IntPtr argumentVector = CommandLineToArgvW(commandLine, out argumentCount);
            if (argumentVector == IntPtr.Zero) return Array.Empty<string>();
            try
            {
                string[] arguments = new string[argumentCount];
                for (int index = 0; index < argumentCount; index++)
                {
                    IntPtr argument = Marshal.ReadIntPtr(argumentVector, index * IntPtr.Size);
                    arguments[index] = Marshal.PtrToStringUni(argument) ?? String.Empty;
                }
                return arguments;
            }
            finally
            {
                LocalFree(argumentVector);
            }
        }
    }
}
"@

function Convert-NtStatus([int]$Status) {
    return '{0:X8}' -f $Status
}

function Get-NativeString([char[]]$Buffer) {
    $end = [Array]::IndexOf($Buffer, [char]0)
    if ($end -lt 0) { $end = $Buffer.Length }
    if ($end -eq 0) { return '' }
    return -join $Buffer[0..($end - 1)]
}

function Get-TraceStrings([char[]]$Buffer) {
    $values = [Collections.Generic.List[string]]::new()
    $startIndex = 0
    for ($index = 0; $index -lt $Buffer.Length; $index++) {
        if ($Buffer[$index] -ne [char]0) { continue }
        if ($index -eq $startIndex) { break }
        $values.Add((-join $Buffer[$startIndex..($index - 1)]))
        $startIndex = $index + 1
    }
    if ($startIndex -lt $Buffer.Length -and $Buffer[$startIndex] -ne [char]0) {
        $values.Add((-join $Buffer[$startIndex..($Buffer.Length - 1)]))
    }
    return $values.ToArray()
}

function Get-ProcessBox([uint32]$ProcessId) {
    $boxBuffer = New-Object char[] 34
    $status = [AGProfileHub.Diagnostics.SandboxTraceNative]::SbieApi_QueryProcess(
        [IntPtr][int64]$ProcessId,
        $boxBuffer,
        [IntPtr]::Zero,
        [IntPtr]::Zero,
        [IntPtr]::Zero
    )
    if ($status -ne 0) { return $null }
    return Get-NativeString $boxBuffer
}

function Get-SettingValues([string]$Setting) {
    $values = @(& $sbieIni query $box $Setting | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($LASTEXITCODE -ne 0) { throw "Could not query '$Setting' from sandbox '$box' (exit code $LASTEXITCODE)." }
    return $values
}

function Set-SettingValues([string]$Setting, [string[]]$Values) {
    $current = @(Get-SettingValues $Setting)
    foreach ($value in $current) {
        & $sbieIni delete $box $Setting $value | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not remove '$Setting' from sandbox '$box'." }
    }
    if ($Values.Count -gt 0) {
        & $sbieIni set $box $Setting $Values[0] | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not restore '$Setting' in sandbox '$box'." }
        foreach ($value in @($Values | Select-Object -Skip 1)) {
            & $sbieIni append $box $Setting $value | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not restore '$Setting' in sandbox '$box'." }
        }
    }
    $actual = @(Get-SettingValues $Setting)
    if ($actual.Count -ne $Values.Count) {
        throw "Sandbox '$box' did not retain the expected '$Setting' value count."
    }
    for ($index = 0; $index -lt $Values.Count; $index++) {
        if ($actual[$index] -cne $Values[$index]) {
            throw "Sandbox '$box' did not retain the expected '$Setting' value order."
        }
    }
}

function Protect-TraceValue([string]$Value) {
    if ([string]::IsNullOrEmpty($Value)) { return $Value }
    $protected = $Value
    $replacements = [ordered]@{
        $state.profileA.userData = '%PROFILE_A_USER_DATA%'
        $state.profileA.roaming = '%PROFILE_A_ROAMING%'
        $state.profileA.local = '%PROFILE_A_LOCAL%'
        $state.profileA.temp = '%PROFILE_A_TEMP%'
        $state.profileA.home = '%PROFILE_A_HOME%'
        $state.profileB.userData = '%PROFILE_B_USER_DATA%'
        $state.profileB.roaming = '%PROFILE_B_ROAMING%'
        $state.profileB.local = '%PROFILE_B_LOCAL%'
        $state.profileB.temp = '%PROFILE_B_TEMP%'
        $state.profileB.home = '%PROFILE_B_HOME%'
        $env:USERPROFILE = '%USERPROFILE%'
    }
    foreach ($source in $replacements.Keys) {
        if (-not [string]::IsNullOrWhiteSpace($source)) {
            $protected = $protected.Replace($source, $replacements[$source], [StringComparison]::OrdinalIgnoreCase)
        }
    }
    $protected = [regex]::Replace($protected, 'S-1-5-21-(?:\d+-){2,}\d+', '%USER_SID%', 'IgnoreCase')
    $protected = [regex]::Replace($protected, '(?i)\b[A-Z]:\\Users\\[^\\\s]+', '%USERPROFILE%')
    $protected = [regex]::Replace(
        $protected,
        '(?i)(?<scheme>\b[a-z][a-z0-9+.-]*://)(?<userinfo>[^/@\s?#]+@)',
        '${scheme}%URL_USERINFO_REDACTED%@'
    )
    $protected = [regex]::Replace(
        $protected,
        '(?i)(?<origin>\b[a-z][a-z0-9+.-]*://[^/\s?#]+)(?:[/]?[\S]*)',
        '${origin}/%URL_PATH_REDACTED%'
    )
    $protected = [regex]::Replace(
        $protected,
        '(?i)(?<prefix>\bAuthorization\s*:\s*)[^\r\n,;]+',
        '${prefix}%REDACTED%'
    )
    $protected = [regex]::Replace(
        $protected,
        '(?i)(?<key>\b(?:access[-_]?token|refresh[-_]?token|id[-_]?token|client[-_]?secret|authorization[-_]?code|oauth[-_]?code|host[-_ ]?bridge[-_ ]?token|csrf(?:[-_ ]?token)?|code|state)\b\s*[:=]\s*)[^,\s;&#]+',
        '${key}%REDACTED%'
    )
    $protected = [regex]::Replace(
        $protected,
        '(?i)(?<key>\b(?:access[-_]?token|refresh[-_]?token|id[-_]?token|client[-_]?secret|authorization[-_]?code|oauth[-_]?code|host[-_ ]?bridge[-_ ]?token|csrf(?:[-_ ]?token)?|code|state)\b[/\\:=\s]+)[^,\s;&#]+',
        '${key}%REDACTED%'
    )
    $protected = [regex]::Replace(
        $protected,
        '\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}(?:\.[A-Za-z0-9_-]{10,})?\b',
        '%JWT_REDACTED%'
    )
    $protected = [regex]::Replace(
        $protected,
        '(?i)\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b|\b[0-9a-f]{24,}\b',
        '%OPAQUE_ID_REDACTED%'
    )
    return [regex]::Replace(
        $protected,
        '\b(?=[A-Za-z0-9_-]{32,}\b)(?=[A-Za-z0-9_-]*[A-Za-z])(?=[A-Za-z0-9_-]*\d)[A-Za-z0-9_-]+\b',
        '%OPAQUE_ID_REDACTED%'
    )
}

function Get-ProtectedErrorMessage([string]$Message) {
    $protected = Protect-TraceValue $Message
    if ($protected.Length -le 1024) { return $protected }
    return $protected.Substring(0, 1024) + '%TRUNCATED%'
}

function Get-SafeCommandMetadata([string]$CommandLine) {
    $metadata = [ordered]@{
        commandLineObserved = -not [string]::IsNullOrWhiteSpace($CommandLine)
        parseFailed = $false
        switchNames = @()
        safeFields = [ordered]@{}
    }
    if (-not $metadata.commandLineObserved) { return $metadata }

    try {
        $arguments = @([AGProfileHub.Diagnostics.SandboxTraceNative]::SplitCommandLine($CommandLine))
        if ($arguments.Count -eq 0) {
            $metadata.parseFailed = $true
            return $metadata
        }
        $switches = [Collections.Generic.List[string]]::new()
        $seenSwitches = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($argument in @($arguments | Select-Object -Skip 1)) {
            $match = [regex]::Match(
                [string]$argument,
                '^(?<prefix>--?|/)(?<name>[A-Za-z0-9][A-Za-z0-9._-]{0,63})(?:(?:=|:)(?<value>.*))?$'
            )
            if (-not $match.Success) { continue }
            $prefix = $match.Groups['prefix'].Value
            $name = $match.Groups['name'].Value.ToLowerInvariant()
            $safeName = [regex]::Replace($name, '[A-Fa-f0-9]{16,}|[A-Za-z0-9_-]{32,}', '%opaque%')
            $switchName = $prefix + $safeName
            if ($seenSwitches.Add($switchName)) { $switches.Add($switchName) }

            $value = $match.Groups['value'].Value
            switch ($switchName) {
                '--type' {
                    $safeValue = $value.ToLowerInvariant()
                    if ($safeValue -in @('browser', 'renderer', 'gpu-process', 'utility', 'zygote', 'broker', 'crashpad-handler')) {
                        $metadata.safeFields.processType = $safeValue
                    }
                }
                '--utility-sub-type' {
                    if ($value -cmatch '^(?:[a-z][a-z0-9_]*\.)+mojom\.[A-Z][A-Za-z0-9]{0,63}(?:Service|Factory)$') {
                        $metadata.safeFields.utilitySubtype = $value
                    }
                }
                '--lang' {
                    if ($value -cmatch '^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,2}$') {
                        $metadata.safeFields.language = $value
                    }
                }
                '--user-data-dir' {
                    try {
                        $fullPath = [IO.Path]::GetFullPath($value)
                        if ($fullPath.Equals($state.profileA.userData, [StringComparison]::OrdinalIgnoreCase)) {
                            $metadata.safeFields.userDataScope = 'profile-a'
                        }
                        elseif ($fullPath.Equals($state.profileB.userData, [StringComparison]::OrdinalIgnoreCase)) {
                            $metadata.safeFields.userDataScope = 'profile-b'
                        }
                        else {
                            $metadata.safeFields.userDataScope = 'other-redacted'
                        }
                    }
                    catch { $metadata.safeFields.userDataScope = 'invalid-redacted' }
                }
                '--extension-process' { $metadata.safeFields.extensionProcess = $true }
            }
        }
        $metadata.switchNames = $switches.ToArray()
    }
    catch {
        $metadata.parseFailed = $true
    }
    finally {
        $arguments = $null
        $CommandLine = $null
    }
    return $metadata
}

function Get-TypeName([uint32]$Type) {
    switch ($Type -band 0xFF) {
        2 { return 'Pipe' }
        3 { return 'Ipc' }
        10 { return 'File' }
        11 { return 'Key' }
        default { return $null }
    }
}

function Get-StatusName([uint32]$Type) {
    $values = [Collections.Generic.List[string]]::new()
    switch ($Type -band 0x000F0000) {
        0x00010000 { $values.Add('Open') }
        0x00020000 { $values.Add('Denied') }
    }
    if (($Type -band 0x40000000) -ne 0) { $values.Add('Trace') }
    if ($values.Count -eq 0) { $values.Add('Observed') }
    return $values -join ','
}

function New-EvidenceWriter([string]$Path) {
    return [IO.StreamWriter]::new($Path, $false, [Text.UTF8Encoding]::new($false))
}

function Write-EvidenceRecord([IO.StreamWriter]$Writer, $Record, $Capture, [string]$Counter) {
    try {
        $Writer.WriteLine(($Record | ConvertTo-Json -Compress -Depth 8))
        $Capture.coverage[$Counter]++
        if (($Capture.coverage[$Counter] % 1024) -eq 0) { $Writer.Flush() }
    }
    catch {
        $Capture.loss.evidenceWriteFailures++
        throw
    }
}

function Get-TcpStateName($State) {
    try {
        switch ([int]$State) {
            0 { return 'Unknown' }
            1 { return 'Closed' }
            2 { return 'Listen' }
            3 { return 'SynSent' }
            4 { return 'SynReceived' }
            5 { return 'Established' }
            6 { return 'FinWait1' }
            7 { return 'FinWait2' }
            8 { return 'CloseWait' }
            9 { return 'Closing' }
            10 { return 'LastAck' }
            11 { return 'TimeWait' }
            12 { return 'DeleteTcb' }
            default { return 'Other' }
        }
    }
    catch { return 'Unknown' }
}

function ConvertTo-SafeProcessMetadata($Row, $Capture) {
    $startedUtc = $null
    if ($null -ne $Row.CreationDate) {
        try { $startedUtc = ([DateTime]$Row.CreationDate).ToUniversalTime().ToString('o') } catch { }
    }

    $commandLine = if ($null -ne $Row.CommandLine) { [string]$Row.CommandLine } else { $null }
    $safeCommand = Get-SafeCommandMetadata $commandLine
    $commandLine = $null
    if ($safeCommand.commandLineObserved) { $Capture.coverage.commandLinesObserved++ }
    else { $Capture.loss.commandLinesUnavailable++ }
    if ($safeCommand.parseFailed) { $Capture.loss.commandLineParseFailures++ }

    return [ordered]@{
        processId = [uint32]$Row.ProcessId
        parentProcessId = if ($null -ne $Row.ParentProcessId) { [uint32]$Row.ParentProcessId } else { $null }
        name = Protect-TraceValue ([string]$Row.Name)
        executablePath = if ($null -ne $Row.ExecutablePath) { Protect-TraceValue ([string]$Row.ExecutablePath) } else { $null }
        startedUtc = $startedUtc
        commandLineObserved = [bool]$safeCommand.commandLineObserved
        switchNames = @($safeCommand.switchNames)
        safeFields = $safeCommand.safeFields
    }
}

function Get-TargetProcessMetadata([uint32[]]$ProcessIds, $Capture) {
    $metadataById = @{}
    if ($ProcessIds.Count -eq 0) { return $metadataById }

    $uniqueIds = @($ProcessIds | Sort-Object -Unique)
    $Capture.coverage.processMetadataPidsRequested += $uniqueIds.Count
    $filter = ($uniqueIds | ForEach-Object { "ProcessId = $_" }) -join ' OR '
    $rows = @()
    $Capture.coverage.processMetadataQueries++
    try {
        $rows = @(Get-CimInstance -Namespace 'root/cimv2' -ClassName 'Win32_Process' -Filter $filter -Property @(
            'ProcessId',
            'ParentProcessId',
            'Name',
            'ExecutablePath',
            'CreationDate',
            'CommandLine'
        ) -ErrorAction Stop)
    }
    catch {
        $Capture.loss.processMetadataQueryFailures++
        foreach ($processId in $uniqueIds) {
            $Capture.coverage.processMetadataQueries++
            try {
                $rows += @(Get-CimInstance -Namespace 'root/cimv2' -ClassName 'Win32_Process' -Filter "ProcessId = $processId" -Property @(
                    'ProcessId',
                    'ParentProcessId',
                    'Name',
                    'ExecutablePath',
                    'CreationDate',
                    'CommandLine'
                ) -ErrorAction Stop)
            }
            catch { $Capture.loss.processMetadataQueryFailures++ }
        }
    }

    foreach ($row in $rows) {
        $safeMetadata = ConvertTo-SafeProcessMetadata $row $Capture
        $metadataById[[string]$safeMetadata.processId] = $safeMetadata
        $Capture.coverage.processMetadataRecords++
    }
    return $metadataById
}

function Invoke-ProcessSnapshot([IO.StreamWriter]$Writer, $Capture) {
    $Capture.coverage.processSampleAttempts++
    $observedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    try {
        $processIds = @([uint32[]](Get-BoxProcessIds) | Sort-Object -Unique)
    }
    catch {
        $Capture.loss.processSampleFailures++
        return [pscustomobject]@{ succeeded = $false; processIds = @() }
    }

    $Capture.coverage.processSamplesCompleted++
    $Capture.processSampleSequence++
    if ($null -eq $Capture.firstProcessSampleUtc) { $Capture.firstProcessSampleUtc = $observedUtc }
    $Capture.lastProcessSampleUtc = $observedUtc
    $Capture.coverage.processObservations += $processIds.Count
    if ($processIds.Count -gt $Capture.coverage.maximumProcessesInSample) {
        $Capture.coverage.maximumProcessesInSample = $processIds.Count
    }

    $newProcessIds = @($processIds | Where-Object { -not $Capture.activeProcessNodes.ContainsKey([string]$_) })
    $metadataRetryIds = @($processIds | Where-Object {
        $existingNode = $Capture.activeProcessNodes[[string]$_]
        $null -ne $existingNode -and -not $existingNode.metadataAvailable
    })
    $metadataRequestIds = @(($newProcessIds + $metadataRetryIds) | Sort-Object -Unique)
    $metadataById = if ($metadataRequestIds.Count -gt 0) {
        Get-TargetProcessMetadata -ProcessIds $metadataRequestIds -Capture $Capture
    }
    else { @{} }
    $newNodes = [Collections.Generic.List[object]]::new()
    $updatedNodes = [Collections.Generic.List[object]]::new()
    foreach ($processId in $newProcessIds) {
        $metadata = $metadataById[[string]$processId]
        if ($null -eq $metadata) {
            $Capture.loss.processMetadataRecordsMissing++
            $metadata = [ordered]@{
                processId = [uint32]$processId
                parentProcessId = $null
                name = 'metadata-unavailable'
                executablePath = $null
                startedUtc = $null
                commandLineObserved = $false
                switchNames = @()
                safeFields = [ordered]@{}
            }
        }
        $Capture.processNodeSequence++
        $node = [ordered]@{
            nodeId = "p$($Capture.processNodeSequence)"
            processId = [uint32]$processId
            parentProcessId = $metadata.parentProcessId
            parentNodeId = $null
            name = $metadata.name
            executablePath = $metadata.executablePath
            startedUtc = $metadata.startedUtc
            firstObservedUtc = $observedUtc
            lastObservedUtc = $observedUtc
            exitObservedUtc = $null
            sampleCount = 0
            metadataAvailable = $metadataById.ContainsKey([string]$processId)
            commandLineObserved = $metadata.commandLineObserved
            switchNames = @($metadata.switchNames)
            safeFields = $metadata.safeFields
        }
        $Capture.activeProcessNodes[[string]$processId] = $node
        $Capture.processNodes.Add($node)
        $newNodes.Add($node)
    }

    foreach ($processId in $metadataRetryIds) {
        $metadata = $metadataById[[string]$processId]
        if ($null -eq $metadata) {
            $Capture.loss.processMetadataRecordsMissing++
            continue
        }
        $node = $Capture.activeProcessNodes[[string]$processId]
        $node.parentProcessId = $metadata.parentProcessId
        $node.name = $metadata.name
        $node.executablePath = $metadata.executablePath
        $node.startedUtc = $metadata.startedUtc
        $node.metadataAvailable = $true
        $node.commandLineObserved = $metadata.commandLineObserved
        $node.switchNames = @($metadata.switchNames)
        $node.safeFields = $metadata.safeFields
        $updatedNodes.Add($node)
    }

    foreach ($node in $Capture.activeProcessNodes.Values) {
        if ($null -eq $node.parentNodeId -and $null -ne $node.parentProcessId) {
            $parentNode = $Capture.activeProcessNodes[[string]$node.parentProcessId]
            if ($null -ne $parentNode -and $parentNode.nodeId -cne $node.nodeId) {
                $node.parentNodeId = $parentNode.nodeId
            }
        }
    }

    foreach ($node in $newNodes) {
        $Capture.processEventSequence++
        Write-EvidenceRecord $Writer ([ordered]@{
            sequence = $Capture.processEventSequence
            observedUtc = $observedUtc
            event = 'start-observed'
            nodeId = $node.nodeId
            processId = $node.processId
            parentProcessId = $node.parentProcessId
            parentNodeId = $node.parentNodeId
            name = $node.name
            executablePath = $node.executablePath
            startedUtc = $node.startedUtc
            commandLineObserved = $node.commandLineObserved
            switchNames = @($node.switchNames)
            safeFields = $node.safeFields
        }) $Capture 'processEventRecordsWritten'
    }
    foreach ($node in $updatedNodes) {
        $Capture.processEventSequence++
        Write-EvidenceRecord $Writer ([ordered]@{
            sequence = $Capture.processEventSequence
            observedUtc = $observedUtc
            event = 'metadata-observed'
            nodeId = $node.nodeId
            processId = $node.processId
            parentProcessId = $node.parentProcessId
            parentNodeId = $node.parentNodeId
            name = $node.name
            executablePath = $node.executablePath
            startedUtc = $node.startedUtc
            commandLineObserved = $node.commandLineObserved
            switchNames = @($node.switchNames)
            safeFields = $node.safeFields
        }) $Capture 'processEventRecordsWritten'
    }

    $currentIdSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($processId in $processIds) {
        $key = [string]$processId
        $null = $currentIdSet.Add($key)
        $node = $Capture.activeProcessNodes[$key]
        $node.lastObservedUtc = $observedUtc
        $node.sampleCount++
    }
    foreach ($key in @($Capture.activeProcessNodes.Keys)) {
        if ($currentIdSet.Contains([string]$key)) { continue }
        $node = $Capture.activeProcessNodes[$key]
        $node.exitObservedUtc = $observedUtc
        $Capture.processEventSequence++
        Write-EvidenceRecord $Writer ([ordered]@{
            sequence = $Capture.processEventSequence
            observedUtc = $observedUtc
            event = 'exit-observed'
            nodeId = $node.nodeId
            processId = $node.processId
        }) $Capture 'processEventRecordsWritten'
        $null = $Capture.activeProcessNodes.Remove($key)
    }

    $Capture.processEventSequence++
    Write-EvidenceRecord $Writer ([ordered]@{
        sequence = $Capture.processEventSequence
        observedUtc = $observedUtc
        event = 'snapshot'
        sampleSequence = $Capture.processSampleSequence
        processIds = @($processIds)
    }) $Capture 'processEventRecordsWritten'
    return [pscustomobject]@{ succeeded = $true; processIds = @($processIds) }
}

function Invoke-NetworkSnapshot([uint32[]]$ProcessIds, [IO.StreamWriter]$Writer, $Capture) {
    $Capture.coverage.networkSampleAttempts++
    $Capture.networkSampleSequence++
    $observedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    if ($null -eq $Capture.firstNetworkSampleUtc) { $Capture.firstNetworkSampleUtc = $observedUtc }
    $Capture.lastNetworkSampleUtc = $observedUtc
    $uniqueIds = @($ProcessIds | Sort-Object -Unique)
    $Capture.coverage.networkProcessPidsRequested += $uniqueIds.Count
    if ($uniqueIds.Count -eq 0) {
        $Capture.coverage.networkSamplesCompleted++
        $Capture.coverage.networkSamplesWithoutEndpoints++
        $Capture.networkEventSequence++
        Write-EvidenceRecord $Writer ([ordered]@{
            sequence = $Capture.networkEventSequence
            recordType = 'sample'
            sampleSequence = $Capture.networkSampleSequence
            observedUtc = $observedUtc
            processIds = @()
            status = 'complete-no-target-processes'
            tcpQueryStatus = 'not-requested'
            udpQueryStatus = 'not-requested'
            tcpEndpointCount = 0
            udpEndpointCount = 0
        }) $Capture 'networkEventRecordsWritten'
        $Capture.coverage.networkSampleRecordsWritten++
        return
    }

    $filter = ($uniqueIds | ForEach-Object { "OwningProcess = $_" }) -join ' OR '
    $pidSet = [Collections.Generic.HashSet[uint32]]::new()
    foreach ($processId in $uniqueIds) { $null = $pidSet.Add([uint32]$processId) }
    $tcpRows = @()
    $udpRows = @()
    $queryFailed = $false
    $tcpQueryStatus = 'completed'
    $udpQueryStatus = 'completed'
    $Capture.coverage.networkProtocolQueries++
    try {
        $tcpRows = @(Get-CimInstance -Namespace 'root/StandardCimv2' -ClassName 'MSFT_NetTCPConnection' -Filter $filter -Property @(
            'OwningProcess',
            'LocalAddress',
            'LocalPort',
            'RemoteAddress',
            'RemotePort',
            'State'
        ) -ErrorAction Stop)
    }
    catch {
        $Capture.loss.networkProtocolQueryFailures++
        $queryFailed = $true
        $tcpQueryStatus = 'failed'
    }
    $Capture.coverage.networkProtocolQueries++
    try {
        $udpRows = @(Get-CimInstance -Namespace 'root/StandardCimv2' -ClassName 'MSFT_NetUDPEndpoint' -Filter $filter -Property @(
            'OwningProcess',
            'LocalAddress',
            'LocalPort'
        ) -ErrorAction Stop)
    }
    catch {
        $Capture.loss.networkProtocolQueryFailures++
        $queryFailed = $true
        $udpQueryStatus = 'failed'
    }

    $tcpRows = @($tcpRows | Where-Object { $pidSet.Contains([uint32]$_.OwningProcess) })
    $udpRows = @($udpRows | Where-Object { $pidSet.Contains([uint32]$_.OwningProcess) })
    if ($queryFailed) { $Capture.loss.networkPartialSamples++ }
    else { $Capture.coverage.networkSamplesCompleted++ }
    if ($tcpRows.Count + $udpRows.Count -eq 0) { $Capture.coverage.networkSamplesWithoutEndpoints++ }

    $Capture.networkEventSequence++
    Write-EvidenceRecord $Writer ([ordered]@{
        sequence = $Capture.networkEventSequence
        recordType = 'sample'
        sampleSequence = $Capture.networkSampleSequence
        observedUtc = $observedUtc
        processIds = @($uniqueIds)
        status = if ($queryFailed) { 'partial' } else { 'complete' }
        tcpQueryStatus = $tcpQueryStatus
        udpQueryStatus = $udpQueryStatus
        tcpEndpointCount = $tcpRows.Count
        udpEndpointCount = $udpRows.Count
    }) $Capture 'networkEventRecordsWritten'
    $Capture.coverage.networkSampleRecordsWritten++

    foreach ($row in $tcpRows) {
        $processId = [uint32]$row.OwningProcess
        $processNode = $Capture.activeProcessNodes[[string]$processId]
        $Capture.networkEventSequence++
        Write-EvidenceRecord $Writer ([ordered]@{
            sequence = $Capture.networkEventSequence
            recordType = 'endpoint'
            sampleSequence = $Capture.networkSampleSequence
            observedUtc = $observedUtc
            transport = 'TCP'
            processId = $processId
            processNodeId = if ($null -ne $processNode) { $processNode.nodeId } else { $null }
            localAddress = [string]$row.LocalAddress
            localPort = [uint32]$row.LocalPort
            remoteAddress = [string]$row.RemoteAddress
            remotePort = [uint32]$row.RemotePort
            state = Get-TcpStateName $row.State
        }) $Capture 'networkEventRecordsWritten'
        $Capture.coverage.tcpEndpointObservations++
    }
    foreach ($row in $udpRows) {
        $processId = [uint32]$row.OwningProcess
        $processNode = $Capture.activeProcessNodes[[string]$processId]
        $Capture.networkEventSequence++
        Write-EvidenceRecord $Writer ([ordered]@{
            sequence = $Capture.networkEventSequence
            recordType = 'endpoint'
            sampleSequence = $Capture.networkSampleSequence
            observedUtc = $observedUtc
            transport = 'UDP'
            processId = $processId
            processNodeId = if ($null -ne $processNode) { $processNode.nodeId } else { $null }
            localAddress = [string]$row.LocalAddress
            localPort = [uint32]$row.LocalPort
            remoteAddress = $null
            remotePort = $null
            state = 'Bound'
        }) $Capture 'networkEventRecordsWritten'
        $Capture.coverage.udpEndpointObservations++
    }
}

function Get-Sha256Lower([string]$Path, $Capture) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $Capture.loss.evidenceFilesMissing++
        return $null
    }
    try { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
    catch {
        $Capture.loss.evidenceHashFailures++
        return $null
    }
}

$nonce = [guid]::NewGuid().ToString('N')
$reportRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\sandboxie-resource-trace\$nonce"
$reportPath = Join-Path $reportRoot 'report.json'
$resourceEventsPath = Join-Path $reportRoot 'resource-events.ndjson'
$processEventsPath = Join-Path $reportRoot 'process-events.ndjson'
$networkEventsPath = Join-Path $reportRoot 'network-events.ndjson'
$hashManifestPath = Join-Path $reportRoot 'sha256.json'
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null

$traceSettings = @('FileTrace', 'KeyTrace', 'IpcTrace', 'PipeTrace')
$savedSettings = [ordered]@{}
$aggregates = @{}
$totalEntries = 0
$targetEntries = 0
$unmappedEntries = 0
$truncatedEntries = 0
$monitorReadCalls = 0
$monitorEmptyReads = 0
$unsupportedTypeEntries = 0
$otherBoxEntries = 0
$emptyTraceEntries = 0
$operationMetadataEntries = 0
$monitorTailDrainAttempted = $false
$monitorTailDrainCompleted = $false
$monitorTailDrainLimitReached = 0
$monitorTailDrainSkipped = 0
$monitorTailDrainInterrupted = 0
$monitorTailEntriesRead = 0
$monitorReadEstablished = $false
$monitorUnsuccessfulReads = 0
$monitorDeviceNotReadyReads = 0
$monitorAmbiguousEmptyReads = 0
$monitorStartupRetryAttempts = 0
$monitorStartupReenableAttempts = 0
$monitorStartupRetryExhausted = 0
$monitorStartupRetryLimit = 100
$monitorStartupRetryWindowMilliseconds = 5000
$monitorTailDrainSeconds = 10
$terminationPollIntervalMilliseconds = 250
$terminationTimeoutMilliseconds = 15000
$terminationPollAttempts = 0
$terminationRemainingProcessCount = 0
$terminationVerified = $false
$monitorEnabled = $false
$profileStarted = $false
$launchProcess = $null
$result = 'failed'
$errorType = $null
$errorMessage = $null
$cleanupFailures = [Collections.Generic.List[object]]::new()
$resourceEventsWriter = $null
$processEventsWriter = $null
$networkEventsWriter = $null
$processSampleIntervalMilliseconds = 250
$networkSampleIntervalMilliseconds = 500
$capture = [ordered]@{
    coverage = [ordered]@{
        resourceEventRecordsWritten = 0
        processEventRecordsWritten = 0
        networkEventRecordsWritten = 0
        processSampleAttempts = 0
        processSamplesCompleted = 0
        processObservations = 0
        maximumProcessesInSample = 0
        processMetadataQueries = 0
        processMetadataPidsRequested = 0
        processMetadataRecords = 0
        commandLinesObserved = 0
        networkSampleAttempts = 0
        networkSamplesCompleted = 0
        networkSampleRecordsWritten = 0
        networkSamplesWithoutEndpoints = 0
        networkProtocolQueries = 0
        networkProcessPidsRequested = 0
        tcpEndpointObservations = 0
        udpEndpointObservations = 0
    }
    loss = [ordered]@{
        evidenceWriteFailures = 0
        evidenceFilesMissing = 0
        evidenceHashFailures = 0
        processSampleFailures = 0
        processMetadataQueryFailures = 0
        processMetadataRecordsMissing = 0
        commandLinesUnavailable = 0
        commandLineParseFailures = 0
        networkProtocolQueryFailures = 0
        networkPartialSamples = 0
        resourcePidsWithoutProcessNode = 0
        processesWithoutObservedExit = 0
        unresolvedParentProcessLinks = 0
    }
    resourceEventSequence = 0
    processEventSequence = 0
    networkEventSequence = 0
    processSampleSequence = 0
    networkSampleSequence = 0
    processNodeSequence = 0
    firstProcessSampleUtc = $null
    lastProcessSampleUtc = $null
    firstNetworkSampleUtc = $null
    lastNetworkSampleUtc = $null
    activeProcessNodes = @{}
    processNodes = [Collections.Generic.List[object]]::new()
    monitorProcessIds = [Collections.Generic.HashSet[uint32]]::new()
}

function Add-CleanupFailure([string]$Step, [string]$Message) {
    $cleanupFailures.Add([ordered]@{ step = $Step; message = (Get-ProtectedErrorMessage $Message) })
}

try {
    $resourceEventsWriter = New-EvidenceWriter $resourceEventsPath
    $processEventsWriter = New-EvidenceWriter $processEventsPath
    $networkEventsWriter = New-EvidenceWriter $networkEventsPath

    [uint32]$oldState = 0
    $queryStatus = [AGProfileHub.Diagnostics.SandboxTraceNative]::SbieApi_MonitorControl(
        [IntPtr]::Zero,
        [ref]$oldState
    )
    if ($queryStatus -ne 0) { throw "Could not query Sandboxie monitor state: $(Convert-NtStatus $queryStatus)." }
    if ($oldState -ne 0) { throw 'Sandboxie resource monitoring is already active in this Windows session.' }

    foreach ($setting in $traceSettings) {
        $savedSettings[$setting] = @(Get-SettingValues $setting)
        Set-SettingValues $setting @('*')
    }

    [uint32]$enable = 1
    $enableStatus = [AGProfileHub.Diagnostics.SandboxTraceNative]::SbieApi_MonitorControl(
        [ref]$enable,
        [IntPtr]::Zero
    )
    if ($enableStatus -ne 0) { throw "Could not enable Sandboxie monitoring: $(Convert-NtStatus $enableStatus)." }
    $monitorEnabled = $true

    $pwsh = (Get-Process -Id $PID).Path
    $launchArguments = @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-File',
        $launcher,
        '-Action',
        "Launch$Profile",
        '-StatePath',
        $resolvedStatePath,
        '-SandboxieHome',
        $SandboxieHome
    )
    $launchInfo = [Diagnostics.ProcessStartInfo]::new()
    $launchInfo.FileName = $pwsh
    $launchInfo.UseShellExecute = $false
    $launchInfo.CreateNoWindow = $true
    $launchInfo.RedirectStandardOutput = $true
    $launchInfo.RedirectStandardError = $true
    foreach ($argument in $launchArguments) {
        $launchInfo.ArgumentList.Add($argument)
    }
    $launchProcess = [Diagnostics.Process]::new()
    $launchProcess.StartInfo = $launchInfo
    if (-not $launchProcess.Start()) { throw "Could not start the Profile $Profile launcher." }
    $launchOutputTask = $launchProcess.StandardOutput.ReadToEndAsync()
    $launchErrorTask = $launchProcess.StandardError.ReadToEndAsync()
    $profileStarted = $true
    $collectionDeadline = [DateTimeOffset]::UtcNow.AddSeconds($DurationSeconds)
    $tailDrainDeadline = $collectionDeadline.AddSeconds($monitorTailDrainSeconds)
    $monitorStartupRetryDeadline = [DateTimeOffset]::UtcNow.AddMilliseconds($monitorStartupRetryWindowMilliseconds)
    $nextProcessSample = [DateTimeOffset]::UtcNow
    $nextNetworkSample = [DateTimeOffset]::UtcNow
    [uint32[]]$lastTargetProcessIds = @()

    do {
        $now = [DateTimeOffset]::UtcNow
        if ($now -ge $collectionDeadline) { $monitorTailDrainAttempted = $true }
        if ($now -lt $collectionDeadline -and $now -ge $nextProcessSample) {
            $processSnapshot = Invoke-ProcessSnapshot $processEventsWriter $capture
            if ($processSnapshot.succeeded) { $lastTargetProcessIds = @([uint32[]]$processSnapshot.processIds) }
            else { $lastTargetProcessIds = @() }
            $nextProcessSample = [DateTimeOffset]::UtcNow.AddMilliseconds($processSampleIntervalMilliseconds)
        }
        if ($now -lt $collectionDeadline -and $now -ge $nextNetworkSample) {
            Invoke-NetworkSnapshot -ProcessIds $lastTargetProcessIds -Writer $networkEventsWriter -Capture $capture
            $nextNetworkSample = [DateTimeOffset]::UtcNow.AddMilliseconds($networkSampleIntervalMilliseconds)
        }

        $readAny = $false
        for ($batchIndex = 0; $batchIndex -lt 256; $batchIndex++) {
            if ([DateTimeOffset]::UtcNow -ge $tailDrainDeadline) { break }
            [uint32]$type = 0
            [uint32]$processId = 0
            [uint32]$threadId = 0
            $data = New-Object char[] 256
            $monitorReadCalls++
            $readStatus = [AGProfileHub.Diagnostics.SandboxTraceNative]::SbieApi_MonitorGetEx(
                [ref]$type,
                [ref]$processId,
                [ref]$threadId,
                $data
            )
            $statusHex = Convert-NtStatus $readStatus
            $monitorOutputWasCleared = $type -eq 0 -and $processId -eq 0 -and $threadId -eq 0 -and $data[0] -eq [char]0
            if ($statusHex -in @('C0000001', 'C00000A3') -and $monitorOutputWasCleared) {
                if ($statusHex -eq 'C0000001') { $monitorUnsuccessfulReads++ }
                else { $monitorDeviceNotReadyReads++ }
                if (-not $monitorReadEstablished) {
                    if ($monitorStartupRetryAttempts -lt $monitorStartupRetryLimit -and
                        [DateTimeOffset]::UtcNow -lt $monitorStartupRetryDeadline) {
                        $monitorStartupRetryAttempts++
                        Start-Sleep -Milliseconds 50
                        continue
                    }
                    $monitorStartupRetryExhausted = 1
                    break
                }
                $monitorAmbiguousEmptyReads++
                if ([DateTimeOffset]::UtcNow -ge $collectionDeadline) {
                    $monitorTailDrainCompleted = $true
                }
                break
            }
            if ($statusHex -eq '8000001A') {
                $monitorReadEstablished = $true
                $monitorEmptyReads++
                if ([DateTimeOffset]::UtcNow -ge $collectionDeadline) {
                    $monitorTailDrainCompleted = $true
                }
                break
            }
            if ($readStatus -ne 0 -and $statusHex -ne 'C0000023') {
                throw "Sandboxie trace read failed: $statusHex."
            }

            $monitorReadEstablished = $true
            $readAny = $true
            $totalEntries++
            if ([DateTimeOffset]::UtcNow -ge $collectionDeadline) { $monitorTailEntriesRead++ }
            if ($statusHex -eq 'C0000023') { $truncatedEntries++ }
            $typeName = Get-TypeName $type
            if ($null -eq $typeName) {
                $unsupportedTypeEntries++
                continue
            }

            $processBox = Get-ProcessBox $processId
            if ([string]::IsNullOrWhiteSpace($processBox)) {
                $unmappedEntries++
                continue
            }
            if (-not $processBox.Equals($box, [StringComparison]::OrdinalIgnoreCase)) {
                $otherBoxEntries++
                continue
            }

            $values = @(Get-TraceStrings $data)
            if ($values.Count -eq 0) {
                $emptyTraceEntries++
                continue
            }
            $resource = Protect-TraceValue $values[0]
            $operation = if ($values.Count -gt 1 -and $values[1] -match '^\([A-Z]{2}\) +[0-9A-Fa-f.]+$') {
                $operationMetadataEntries++
                Protect-TraceValue $values[1]
            } else { '' }
            $statusName = Get-StatusName $type
            $capture.resourceEventSequence++
            $null = $capture.monitorProcessIds.Add([uint32]$processId)
            Write-EvidenceRecord $resourceEventsWriter ([ordered]@{
                sequence = $capture.resourceEventSequence
                observedUtc = [DateTimeOffset]::UtcNow.ToString('o')
                processId = [uint32]$processId
                threadId = [uint32]$threadId
                type = $typeName
                status = $statusName
                op = $operation
                resource = $resource
                truncated = [bool]($statusHex -eq 'C0000023')
            }) $capture 'resourceEventRecordsWritten'
            $key = "$typeName`u{001F}$statusName`u{001F}$operation`u{001F}$resource"
            if (-not $aggregates.ContainsKey($key)) {
                $aggregates[$key] = [ordered]@{
                    type = $typeName
                    status = $statusName
                    operation = $operation
                    resource = $resource
                    count = 0
                }
            }
            $aggregates[$key].count++
            $targetEntries++
        }
        if ($monitorTailDrainCompleted) { break }
        if (-not $readAny) { Start-Sleep -Milliseconds 20 }
    } while ([DateTimeOffset]::UtcNow -lt $tailDrainDeadline)
    if (-not $monitorTailDrainCompleted) { $monitorTailDrainLimitReached = 1 }
    if ($totalEntries -eq 0) {
        throw 'Sandboxie monitor produced no successful entries during the capture; capture coverage is incomplete.'
    }

    if (-not $launchProcess.WaitForExit(15000)) {
        throw 'Profile launcher did not exit within 15 seconds.'
    }
    $launchOutputTask.GetAwaiter().GetResult() | Out-Null
    $launchErrorTask.GetAwaiter().GetResult() | Out-Null
    if ($launchProcess.ExitCode -ne 0) {
        throw "Profile $Profile launch failed with exit code $($launchProcess.ExitCode). Launcher output was intentionally not retained."
    }
    $result = 'captured'
}
catch {
    $errorType = $_.Exception.GetType().FullName
    $errorMessage = Get-ProtectedErrorMessage $_.Exception.Message
}
finally {
    if ($monitorEnabled -and -not $monitorTailDrainAttempted) { $monitorTailDrainSkipped = 1 }
    if ($monitorEnabled -and $monitorTailDrainAttempted -and -not $monitorTailDrainCompleted -and
        $monitorTailDrainLimitReached -eq 0) {
        $monitorTailDrainInterrupted = 1
    }
    if ($monitorEnabled) {
        try {
            [uint32]$disable = 0
            $disableStatus = [AGProfileHub.Diagnostics.SandboxTraceNative]::SbieApi_MonitorControl(
                [ref]$disable,
                [IntPtr]::Zero
            )
            if ($disableStatus -ne 0) { throw "NTSTATUS $(Convert-NtStatus $disableStatus)" }
            [uint32]$remainingState = 0
            $verifyStatus = [AGProfileHub.Diagnostics.SandboxTraceNative]::SbieApi_MonitorControl(
                [IntPtr]::Zero,
                [ref]$remainingState
            )
            if ($verifyStatus -ne 0) { throw "verification NTSTATUS $(Convert-NtStatus $verifyStatus)" }
            if ($remainingState -ne 0) { throw 'Sandboxie monitor remained enabled after disable.' }
        }
        catch { Add-CleanupFailure 'monitor-disable' $_.Exception.Message }
    }
    if ($profileStarted) {
        if ($null -ne $processEventsWriter) {
            try {
                $finalRunningSnapshot = Invoke-ProcessSnapshot $processEventsWriter $capture
                if ($finalRunningSnapshot.succeeded -and $null -ne $networkEventsWriter) {
                    Invoke-NetworkSnapshot -ProcessIds @([uint32[]]$finalRunningSnapshot.processIds) -Writer $networkEventsWriter -Capture $capture
                }
            }
            catch { Add-CleanupFailure 'final-running-snapshot' $_.Exception.Message }
        }
        try {
            & $start "/box:$box" /silent /terminate | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "exit code $LASTEXITCODE" }
            $terminationDeadline = [DateTimeOffset]::UtcNow.AddMilliseconds($terminationTimeoutMilliseconds)
            do {
                $terminationPollAttempts++
                $remainingProcessIds = @(Get-BoxProcessIds)
                $terminationRemainingProcessCount = $remainingProcessIds.Count
                if ($terminationRemainingProcessCount -eq 0) {
                    $terminationVerified = $true
                    break
                }
                if ([DateTimeOffset]::UtcNow -ge $terminationDeadline) { break }
                Start-Sleep -Milliseconds $terminationPollIntervalMilliseconds
            } while ([DateTimeOffset]::UtcNow -lt $terminationDeadline)
            if (-not $terminationVerified) {
                throw "Sandbox '$box' still has $terminationRemainingProcessCount processes after the $terminationTimeoutMilliseconds ms termination timeout."
            }
        }
        catch { Add-CleanupFailure 'profile-terminate' $_.Exception.Message }
        if ($null -ne $processEventsWriter) {
            try { $null = Invoke-ProcessSnapshot $processEventsWriter $capture }
            catch { Add-CleanupFailure 'final-stopped-snapshot' $_.Exception.Message }
        }
    }
    if ($null -ne $launchProcess) {
        try {
            if (-not $launchProcess.HasExited) {
                if (-not $launchProcess.WaitForExit(15000)) {
                    $launchProcess.Kill($true)
                    if (-not $launchProcess.WaitForExit(15000)) { throw 'launcher did not exit after Kill.' }
                }
            }
            if (-not $launchProcess.HasExited) { throw 'launcher is still running.' }
        }
        catch { Add-CleanupFailure 'launcher-wait' $_.Exception.Message }
    }
    foreach ($setting in $traceSettings) {
        try {
            if (-not $savedSettings.Contains($setting)) { throw "No saved value for '$setting'." }
            Set-SettingValues $setting @($savedSettings[$setting])
        }
        catch { Add-CleanupFailure "restore-$setting" $_.Exception.Message }
    }
    foreach ($writerEntry in @(
        [ordered]@{ name = 'resource-events'; writer = $resourceEventsWriter },
        [ordered]@{ name = 'process-events'; writer = $processEventsWriter },
        [ordered]@{ name = 'network-events'; writer = $networkEventsWriter }
    )) {
        if ($null -eq $writerEntry.writer) { continue }
        try { $writerEntry.writer.Flush() }
        catch { Add-CleanupFailure "flush-$($writerEntry.name)" $_.Exception.Message }
        try { $writerEntry.writer.Dispose() }
        catch { Add-CleanupFailure "close-$($writerEntry.name)" $_.Exception.Message }
    }
    if ($cleanupFailures.Count -gt 0) {
        $result = 'failed'
        if ($null -eq $errorType) { $errorType = 'CleanupFailure' }
        $cleanupSummary = ($cleanupFailures | ForEach-Object { "$($_.step): $($_.message)" }) -join '; '
        if ([string]::IsNullOrWhiteSpace($errorMessage)) { $errorMessage = "Cleanup failed: $cleanupSummary" }
        else { $errorMessage = "$errorMessage Cleanup failed: $cleanupSummary" }
        $errorMessage = Get-ProtectedErrorMessage $errorMessage
    }
}

$resources = @($aggregates.Values | Sort-Object type, resource, operation, status)
$processNodes = @($capture.processNodes)
$processEdges = @($processNodes | Where-Object { $null -ne $_.parentNodeId } | ForEach-Object {
    [ordered]@{
        parentNodeId = $_.parentNodeId
        childNodeId = $_.nodeId
        parentProcessId = $_.parentProcessId
        childProcessId = $_.processId
    }
})
$processNodePids = [Collections.Generic.HashSet[uint32]]::new()
foreach ($node in $processNodes) { $null = $processNodePids.Add([uint32]$node.processId) }
foreach ($processId in $capture.monitorProcessIds) {
    if (-not $processNodePids.Contains([uint32]$processId)) {
        $capture.loss.resourcePidsWithoutProcessNode++
    }
}
$capture.loss.processesWithoutObservedExit = @($capture.activeProcessNodes.Keys).Count
$capture.loss.unresolvedParentProcessLinks = @($processNodes | Where-Object {
    $null -ne $_.parentProcessId -and $_.parentProcessId -ne 0 -and $null -eq $_.parentNodeId
}).Count

$resourceEventsSha256 = Get-Sha256Lower $resourceEventsPath $capture
$processEventsSha256 = Get-Sha256Lower $processEventsPath $capture
$networkEventsSha256 = Get-Sha256Lower $networkEventsPath $capture
if ($null -eq $resourceEventsSha256 -or $null -eq $processEventsSha256 -or $null -eq $networkEventsSha256) {
    $result = 'failed'
    if ($null -eq $errorType) { $errorType = 'EvidenceIntegrityFailure' }
    if ([string]::IsNullOrWhiteSpace($errorMessage)) {
        $errorMessage = 'One or more evidence event files could not be created or hashed.'
    }
}
$coverageCounters = [ordered]@{
    monitor = [ordered]@{
        readCalls = $monitorReadCalls
        emptyReads = $monitorEmptyReads
        readEstablished = $monitorReadEstablished
        unsuccessfulReads = $monitorUnsuccessfulReads
        deviceNotReadyReads = $monitorDeviceNotReadyReads
        ambiguousEmptyReadsAfterEstablishment = $monitorAmbiguousEmptyReads
        startupRetryAttempts = $monitorStartupRetryAttempts
        startupReenableAttempts = $monitorStartupReenableAttempts
        startupRetryLimit = $monitorStartupRetryLimit
        startupRetryWindowMilliseconds = $monitorStartupRetryWindowMilliseconds
        sessionEntriesRead = $totalEntries
        tailEntriesRead = $monitorTailEntriesRead
        tailDrainLimitSeconds = $monitorTailDrainSeconds
        tailDrainAttempted = $monitorTailDrainAttempted
        tailDrainCompleted = $monitorTailDrainCompleted
        targetEntriesRecorded = $targetEntries
        operationMetadataEntries = $operationMetadataEntries
        resourceEventRecordsWritten = $capture.coverage.resourceEventRecordsWritten
    }
    process = [ordered]@{
        sampleIntervalMilliseconds = $processSampleIntervalMilliseconds
        sampleAttempts = $capture.coverage.processSampleAttempts
        samplesCompleted = $capture.coverage.processSamplesCompleted
        observations = $capture.coverage.processObservations
        maximumProcessesInSample = $capture.coverage.maximumProcessesInSample
        metadataQueries = $capture.coverage.processMetadataQueries
        metadataPidsRequested = $capture.coverage.processMetadataPidsRequested
        metadataRecords = $capture.coverage.processMetadataRecords
        commandLinesObservedInMemory = $capture.coverage.commandLinesObserved
        lifecycleEventRecordsWritten = $capture.coverage.processEventRecordsWritten
        uniqueProcessNodes = $processNodes.Count
        parentChildEdges = $processEdges.Count
    }
    network = [ordered]@{
        sampleIntervalMilliseconds = $networkSampleIntervalMilliseconds
        sampleAttempts = $capture.coverage.networkSampleAttempts
        samplesCompleted = $capture.coverage.networkSamplesCompleted
        sampleRecordsWritten = $capture.coverage.networkSampleRecordsWritten
        samplesWithoutEndpoints = $capture.coverage.networkSamplesWithoutEndpoints
        protocolQueries = $capture.coverage.networkProtocolQueries
        processPidsRequested = $capture.coverage.networkProcessPidsRequested
        tcpEndpointObservations = $capture.coverage.tcpEndpointObservations
        udpEndpointObservations = $capture.coverage.udpEndpointObservations
        endpointEventRecordsWritten = $capture.coverage.tcpEndpointObservations + $capture.coverage.udpEndpointObservations
        totalEventRecordsWritten = $capture.coverage.networkEventRecordsWritten
    }
    cleanup = [ordered]@{
        terminationRequested = $profileStarted
        terminationTimeoutMilliseconds = $terminationTimeoutMilliseconds
        terminationPollIntervalMilliseconds = $terminationPollIntervalMilliseconds
        terminationPollAttempts = $terminationPollAttempts
        terminationVerified = $terminationVerified
        remainingProcesses = $terminationRemainingProcessCount
    }
}
$lossCounters = [ordered]@{
    monitorNoSuccessfulEntries = if ($totalEntries -eq 0) { 1 } else { 0 }
    monitorStartupRetryExhausted = $monitorStartupRetryExhausted
    monitorEntriesTruncated = $truncatedEntries
    monitorEntriesUnmapped = $unmappedEntries
    monitorEntriesUnsupportedType = $unsupportedTypeEntries
    monitorEntriesFromOtherBoxes = $otherBoxEntries
    monitorEntriesWithoutTraceStrings = $emptyTraceEntries
    monitorTailDrainLimitReached = $monitorTailDrainLimitReached
    monitorTailDrainSkipped = $monitorTailDrainSkipped
    monitorTailDrainInterrupted = $monitorTailDrainInterrupted
    evidenceWriteFailures = $capture.loss.evidenceWriteFailures
    evidenceFilesMissing = $capture.loss.evidenceFilesMissing
    evidenceHashFailures = $capture.loss.evidenceHashFailures
    processSampleFailures = $capture.loss.processSampleFailures
    processMetadataQueryFailures = $capture.loss.processMetadataQueryFailures
    processMetadataRecordsMissing = $capture.loss.processMetadataRecordsMissing
    commandLinesUnavailable = $capture.loss.commandLinesUnavailable
    commandLineParseFailures = $capture.loss.commandLineParseFailures
    resourcePidsWithoutProcessNode = $capture.loss.resourcePidsWithoutProcessNode
    processesWithoutObservedExit = $capture.loss.processesWithoutObservedExit
    unresolvedParentProcessLinks = $capture.loss.unresolvedParentProcessLinks
    networkProtocolQueryFailures = $capture.loss.networkProtocolQueryFailures
    networkPartialSamples = $capture.loss.networkPartialSamples
    terminationVerificationFailed = if ($profileStarted -and -not $terminationVerified) { 1 } else { 0 }
    terminationProcessesRemaining = $terminationRemainingProcessCount
}
$report = [ordered]@{
    schemaVersion = 1
    capturedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    result = $result
    sandboxieVersion = (Get-Item -LiteralPath $start).VersionInfo.ProductVersion
    profile = $Profile
    box = $box
    durationSeconds = $DurationSeconds
    collection = 'resource-type-access-flags-and-path-only'
    totalSessionEntriesRead = $totalEntries
    targetEntriesRecorded = $targetEntries
    uniqueTargetResources = $resources.Count
    unmappedEntriesDiscarded = $unmappedEntries
    truncatedEntries = $truncatedEntries
    resources = $resources
    evidenceSchemaVersion = 1
    evidenceFiles = [ordered]@{
        resourceEvents = [ordered]@{
            file = [IO.Path]::GetFileName($resourceEventsPath)
            available = $null -ne $resourceEventsSha256
            sha256 = $resourceEventsSha256
            records = $capture.coverage.resourceEventRecordsWritten
        }
        processEvents = [ordered]@{
            file = [IO.Path]::GetFileName($processEventsPath)
            available = $null -ne $processEventsSha256
            sha256 = $processEventsSha256
            records = $capture.coverage.processEventRecordsWritten
        }
        networkEvents = [ordered]@{
            file = [IO.Path]::GetFileName($networkEventsPath)
            available = $null -ne $networkEventsSha256
            sha256 = $networkEventsSha256
            records = $capture.coverage.networkEventRecordsWritten
        }
        hashManifest = [IO.Path]::GetFileName($hashManifestPath)
    }
    processCapture = [ordered]@{
        firstSampleUtc = $capture.firstProcessSampleUtc
        lastSampleUtc = $capture.lastProcessSampleUtc
        nodes = $processNodes
        edges = $processEdges
    }
    networkCapture = [ordered]@{
        firstSampleUtc = $capture.firstNetworkSampleUtc
        lastSampleUtc = $capture.lastNetworkSampleUtc
        addressResolution = 'none'
    }
    coverageCounters = $coverageCounters
    lossCounters = $lossCounters
    privacy = [ordered]@{
        credentialContentsRead = $false
        rawCommandLinesPersisted = $false
        commandLinePolicy = 'switch-names-and-strictly-allowlisted-derived-fields-only'
        urlPolicy = 'scheme-and-authority-only-path-query-and-fragment-redacted'
        sensitiveValuePolicy = 'oauth-code-state-csrf-authorization-and-host-bridge-token-redacted'
        networkAddressResolution = 'disabled'
    }
    limitations = @(
        'Cleared-output C0000001 or C00000A3 monitor statuses are idle-polled at 50 ms for at most five seconds before normal polling resumes; no monitor control state is reset, and the earliest startup events can still be omitted.',
        'Process start and exit are sampling observations; processes shorter than the sampling interval can be missed.',
        'Parent edges are created only for concurrently observed in-box nodes; unresolved parent PIDs are counted without historical PID inference.',
        'Endpoint records are point-in-time samples and can miss connections shorter than the network sampling interval.',
        'Sandboxie monitor entries that cannot be mapped to a live process are discarded and counted.',
        "A bounded $monitorTailDrainSeconds-second tail drain is attempted; a nonempty queue at that limit is reported as loss.",
        'Events arriving between the observed empty queue and monitor disable, and internal monitor queue overflow, are not observable through this API.'
    )
    cleanupFailures = @($cleanupFailures)
    error = if ($null -ne $errorType) { [ordered]@{ type = $errorType; message = $errorMessage } } else { $null }
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
$reportSha256 = Get-Sha256Lower $reportPath $capture
$hashManifest = [ordered]@{
    schemaVersion = 1
    algorithm = 'SHA-256'
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    files = @(
        [ordered]@{ file = [IO.Path]::GetFileName($resourceEventsPath); sha256 = $resourceEventsSha256 },
        [ordered]@{ file = [IO.Path]::GetFileName($processEventsPath); sha256 = $processEventsSha256 },
        [ordered]@{ file = [IO.Path]::GetFileName($networkEventsPath); sha256 = $networkEventsSha256 },
        [ordered]@{ file = [IO.Path]::GetFileName($reportPath); sha256 = $reportSha256 }
    )
}
$hashManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $hashManifestPath -Encoding utf8

if ($result -ne 'captured') {
    throw "Sandbox resource trace failed. Report: $reportPath. $errorMessage"
}
[ordered]@{
    action = 'TraceSandboxAntigravity'
    result = $result
    reportPath = $reportPath
    resourceEventsPath = $resourceEventsPath
    processEventsPath = $processEventsPath
    networkEventsPath = $networkEventsPath
    hashManifestPath = $hashManifestPath
    reportSha256 = $reportSha256
    totalSessionEntriesRead = $totalEntries
    targetEntriesRecorded = $targetEntries
    uniqueTargetResources = $resources.Count
    unmappedEntriesDiscarded = $unmappedEntries
    truncatedEntries = $truncatedEntries
} | ConvertTo-Json
