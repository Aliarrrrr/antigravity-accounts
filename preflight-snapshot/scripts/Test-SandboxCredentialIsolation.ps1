#requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Probe')]
param(
    [Parameter(ParameterSetName = 'Worker', Mandatory)]
    [ValidateSet('Write', 'Expect', 'ExpectMissing', 'Delete')]
    [string]$WorkerAction,
    [Parameter(ParameterSetName = 'Worker', Mandatory)]
    [string]$Target,
    [Parameter(ParameterSetName = 'Worker')]
    [string]$Value,
    [Parameter(ParameterSetName = 'Probe')]
    [string]$SandboxieHome = 'C:\Program Files\Sandboxie-Plus'
)

$ErrorActionPreference = 'Stop'

function Initialize-CredentialApi {
    if ('AGProfileHub.Probe.CredentialApi' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AGProfileHub.Probe
{
    public static class CredentialApi
    {
        private const uint Generic = 1;
        private const uint PersistLocalMachine = 2;
        public const int ErrorNotFound = 1168;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr credential);

        public static void Write(string target, string value)
        {
            if (CredRead(target, Generic, 0, out IntPtr existing))
            {
                CredFree(existing);
                throw new InvalidOperationException("The synthetic probe target already exists.");
            }
            int readError = Marshal.GetLastWin32Error();
            if (readError != ErrorNotFound)
                throw new Win32Exception(readError, "CredReadW failed while reserving the synthetic probe target.");

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            IntPtr blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new Credential
                {
                    Type = Generic,
                    TargetName = target,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = PersistLocalMachine,
                    UserName = "AGProfileHub synthetic probe"
                };
                if (!CredWrite(ref credential, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CredWriteW failed for the synthetic probe target.");
            }
            finally
            {
                Marshal.FreeHGlobal(blob);
            }
        }

        public static string Read(string target)
        {
            if (!CredRead(target, Generic, 0, out IntPtr pointer))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CredReadW failed for the synthetic probe target.");
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                byte[] bytes = new byte[credential.CredentialBlobSize];
                if (bytes.Length > 0) Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public static int TryReadError(string target)
        {
            if (CredRead(target, Generic, 0, out IntPtr pointer))
            {
                CredFree(pointer);
                return 0;
            }
            return Marshal.GetLastWin32Error();
        }

        public static void DeleteOwned(string target, string expectedValue)
        {
            if (!CredRead(target, Generic, 0, out IntPtr pointer))
            {
                int readError = Marshal.GetLastWin32Error();
                if (readError == ErrorNotFound) return;
                throw new Win32Exception(readError, "CredReadW failed while checking the synthetic probe target.");
            }

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                byte[] expected = Encoding.UTF8.GetBytes(expectedValue);
                if (!string.Equals(credential.UserName, "AGProfileHub synthetic probe", StringComparison.Ordinal) ||
                    credential.CredentialBlobSize != expected.Length)
                    throw new InvalidOperationException("The existing credential is not owned by the synthetic probe.");

                byte[] actual = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, actual, 0, actual.Length);
                for (int index = 0; index < actual.Length; index++)
                {
                    if (actual[index] != expected[index])
                        throw new InvalidOperationException("The existing credential is not owned by the synthetic probe.");
                }

                if (!CredDelete(target, Generic, 0))
                {
                    int deleteError = Marshal.GetLastWin32Error();
                    if (deleteError != ErrorNotFound)
                        throw new Win32Exception(deleteError, "CredDeleteW failed for the synthetic probe target.");
                }
            }
            finally
            {
                CredFree(pointer);
            }
        }
    }
}
'@
}

function Invoke-Worker {
    if ($Target -cnotmatch '\AAGProfileHub\.Probe\.(?<nonce>[0-9a-f]{32})\z') {
        throw 'Worker target must be the synthetic probe target AGProfileHub.Probe.<32 lowercase hex>.'
    }
    $workerNonce = $Matches['nonce']
    if ($WorkerAction -in @('Write', 'Expect', 'Delete')) {
        $allowedValues = @("host-$workerNonce", "box-a-$workerNonce", "box-b-$workerNonce")
        if ($Value -cnotin $allowedValues) {
            throw 'Worker value must be the synthetic value derived from the probe target.'
        }
    }
    elseif (-not [string]::IsNullOrEmpty($Value)) {
        throw 'ExpectMissing workers do not accept a value.'
    }

    Initialize-CredentialApi
    try {
        switch ($WorkerAction) {
            'Write' {
                [AGProfileHub.Probe.CredentialApi]::Write($Target, $Value)
            }
            'Expect' {
                $actual = [AGProfileHub.Probe.CredentialApi]::Read($Target)
                if ($actual -cne $Value) { exit 12 }
            }
            'ExpectMissing' {
                $errorCode = [AGProfileHub.Probe.CredentialApi]::TryReadError($Target)
                if ($errorCode -eq 0) { exit 13 }
                if ($errorCode -ne [AGProfileHub.Probe.CredentialApi]::ErrorNotFound) { exit 14 }
            }
            'Delete' {
                [AGProfileHub.Probe.CredentialApi]::DeleteOwned($Target, $Value)
            }
        }
        exit 0
    }
    catch {
        [Console]::Error.WriteLine($_.Exception.GetType().FullName)
        exit 15
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Worker') {
    Invoke-Worker
}

$start = Join-Path $SandboxieHome 'Start.exe'
$sbieIni = Join-Path $SandboxieHome 'SbieIni.exe'
foreach ($required in @($start, $sbieIni)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Sandboxie 1.18.2 tool not found: $required"
    }
}

$version = (Get-Item -LiteralPath $start).VersionInfo.ProductVersion
$nonce = [guid]::NewGuid().ToString('N')
$targetName = "AGProfileHub.Probe.$nonce"
$boxA = "AGPH_$($nonce.Substring(0, 20))_A"
$boxB = "AGPH_$($nonce.Substring(0, 20))_B"
$hostValue = "host-$nonce"
$aValue = "box-a-$nonce"
$bValue = "box-b-$nonce"
$pwsh = (Get-Process -Id $PID).Path
$reportRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\sandboxie-credential-probe\$nonce"
$reportPath = Join-Path $reportRoot 'report.json'
$report = [ordered]@{
    schemaVersion = 1
    timestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
    sandboxieVersion = $version
    target = $targetName
    boxes = @($boxA, $boxB)
    result = 'running'
    stages = @()
    cleanup = 'pending'
}
$createdBoxes = [Collections.Generic.List[string]]::new()
$hostCredentialCleanupRequired = $false

function Add-Stage([string]$Name, [string]$Result, [int]$ExitCode = 0) {
    $report.stages += [ordered]@{ name = $Name; result = $Result; exitCode = $ExitCode }
}

function Invoke-HostWorker([string]$Action, [string]$ExpectedValue = '') {
    & $pwsh -NoLogo -NoProfile -NonInteractive -File $PSCommandPath -WorkerAction $Action -Target $targetName -Value $ExpectedValue
    return $LASTEXITCODE
}

function Invoke-BoxWorker([string]$Box, [string]$Action, [string]$ExpectedValue = '') {
    & $start "/box:$Box" /silent /wait $pwsh -NoLogo -NoProfile -NonInteractive -File $PSCommandPath -WorkerAction $Action -Target $targetName -Value $ExpectedValue
    return $LASTEXITCODE
}

function Set-BoxValue([string]$Box, [string]$Setting, [string]$SettingValue) {
    & $sbieIni set $Box $Setting $SettingValue | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not configure sandbox '$Box' setting '$Setting'." }
    $actual = (& $sbieIni query $Box $Setting | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Could not verify sandbox '$Box' setting '$Setting'." }
    if ($actual -cne $SettingValue) { throw "Sandbox '$Box' did not retain setting '$Setting'." }
}

function Get-ExistingBoxList {
    $lines = @(& $sbieIni queryex /boxes '*' 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "Could not list Sandboxie boxes (exit code $exitCode)." }
    return $lines
}

function Test-BoxListed([object[]]$Lines, [string]$Box) {
    $escaped = [regex]::Escape($Box)
    return [bool]($Lines | Where-Object {
        $line = ([string]$_).Trim()
        $line -ieq $Box -or $line -ieq "[$Box]" -or $line -imatch "(?<![A-Za-z0-9_])$escaped(?![A-Za-z0-9_])"
    } | Select-Object -First 1)
}

function New-ProbeBox([string]$Box) {
    & $sbieIni set $Box Enabled y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create sandbox '$Box'." }
    $createdBoxes.Add($Box)
    $actual = (& $sbieIni query $Box Enabled | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -cne 'y') { throw "Sandbox '$Box' did not retain setting 'Enabled'." }
}

try {
    New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
    $existingBoxes = @(Get-ExistingBoxList)
    foreach ($box in @($boxA, $boxB)) {
        if (Test-BoxListed $existingBoxes $box) {
            throw "Random probe sandbox '$box' already exists; refusing to use an existing sandbox."
        }
    }
    foreach ($box in @($boxA, $boxB)) {
        New-ProbeBox $box
        Set-BoxValue $box 'ConfigLevel' '10'
        Set-BoxValue $box 'AutoRecover' 'n'
        Set-BoxValue $box 'AutoDelete' 'n'
        Set-BoxValue $box 'OpenCredentials' 'n'
    }

    $exitCode = Invoke-HostWorker 'ExpectMissing'
    if ($exitCode -ne 0) {
        Add-Stage 'host-target-missing-before-write' 'failed' $exitCode
        throw "Synthetic host target was not missing before the probe write (worker exit code $exitCode)."
    }
    Add-Stage 'host-target-missing-before-write' 'passed'
    $exitCode = Invoke-HostWorker 'Write' $hostValue
    if ($exitCode -ne 0) { throw "Host synthetic credential write failed with exit code $exitCode." }
    $hostCredentialCleanupRequired = $true
    Add-Stage 'host-write' 'passed'

    foreach ($box in @($boxA, $boxB)) {
        $exitCode = Invoke-BoxWorker $box 'ExpectMissing'
        if ($exitCode -ne 0) {
            Add-Stage "$box-empty-cannot-read-host" 'failed' $exitCode
            throw "Empty sandbox '$box' could access the host synthetic credential (worker exit code $exitCode)."
        }
        Add-Stage "$box-empty-cannot-read-host" 'passed'
    }

    $exitCode = Invoke-BoxWorker $boxA 'Write' $aValue
    if ($exitCode -ne 0) { throw "Sandbox A write failed with exit code $exitCode." }
    Add-Stage 'box-a-write' 'passed'
    $exitCode = Invoke-BoxWorker $boxB 'Write' $bValue
    if ($exitCode -ne 0) { throw "Sandbox B write failed with exit code $exitCode." }
    Add-Stage 'box-b-write' 'passed'

    foreach ($check in @(
        @($boxA, $aValue, 'box-a-reads-a'),
        @($boxB, $bValue, 'box-b-reads-b')
    )) {
        $exitCode = Invoke-BoxWorker $check[0] 'Expect' $check[1]
        if ($exitCode -ne 0) { throw "$($check[2]) failed with exit code $exitCode." }
        Add-Stage $check[2] 'passed'
    }

    $exitCode = Invoke-HostWorker 'Expect' $hostValue
    if ($exitCode -ne 0) { throw "Host credential changed with exit code $exitCode." }
    Add-Stage 'host-remains-host' 'passed'

    $exitCode = Invoke-BoxWorker $boxA 'Delete' $aValue
    if ($exitCode -ne 0) { throw "Sandbox A delete failed with exit code $exitCode." }
    $exitCode = Invoke-BoxWorker $boxA 'ExpectMissing'
    if ($exitCode -ne 0) {
        Add-Stage 'box-a-delete-does-not-reveal-host' 'failed' $exitCode
        throw "Deleting Sandbox A's value revealed the host value (worker exit code $exitCode)."
    }
    Add-Stage 'box-a-delete-does-not-reveal-host' 'passed'

    $exitCode = Invoke-BoxWorker $boxB 'Expect' $bValue
    if ($exitCode -ne 0) { throw "Sandbox B changed after Sandbox A delete (exit code $exitCode)." }
    Add-Stage 'box-b-survives-box-a-delete' 'passed'
    $report.result = 'passed'
}
catch {
    $report.result = 'failed'
    $report.error = $_.Exception.Message
}
finally {
    $cleanup = [ordered]@{
        result = 'pending'
        hostCredential = [ordered]@{ action = 'Delete'; result = 'pending'; exitCode = $null }
        boxes = [ordered]@{}
        listExitCode = $null
    }
    if ($hostCredentialCleanupRequired) {
        try {
            $hostExitCode = Invoke-HostWorker 'Delete' $hostValue
            $cleanup.hostCredential.exitCode = $hostExitCode
            $cleanup.hostCredential.result = if ($hostExitCode -eq 0) { 'completed' } else { 'failed' }
        }
        catch {
            $cleanup.hostCredential.result = 'failed'
            $cleanup.hostCredential.error = $_.Exception.Message
        }
    }
    else {
        $cleanup.hostCredential.action = 'Skipped'
        $cleanup.hostCredential.result = 'not-required'
    }
    foreach ($box in $createdBoxes) {
        $boxCleanup = [ordered]@{
            terminate = [ordered]@{ result = 'pending'; exitCode = $null }
            delete = [ordered]@{ result = 'pending'; exitCode = $null }
            clearConfiguration = [ordered]@{ result = 'pending'; exitCode = $null }
            result = 'pending'
        }
        $cleanup.boxes[$box] = $boxCleanup
        try {
            & $start "/box:$box" /silent /terminate | Out-Null
            $boxCleanup.terminate.exitCode = $LASTEXITCODE
            $boxCleanup.terminate.result = if ($LASTEXITCODE -eq 0) { 'completed' } else { 'failed' }
        }
        catch {
            $boxCleanup.terminate.result = 'failed'
            $boxCleanup.terminate.error = $_.Exception.Message
        }
        try {
            & $start "/box:$box" /silent delete_sandbox_silent | Out-Null
            $boxCleanup.delete.exitCode = $LASTEXITCODE
            $boxCleanup.delete.result = if ($LASTEXITCODE -eq 0) { 'completed' } else { 'failed' }
        }
        catch {
            $boxCleanup.delete.result = 'failed'
            $boxCleanup.delete.error = $_.Exception.Message
        }
        try {
            & $sbieIni set $box '*' '' | Out-Null
            $boxCleanup.clearConfiguration.exitCode = $LASTEXITCODE
            $boxCleanup.clearConfiguration.result = if ($LASTEXITCODE -eq 0) { 'completed' } else { 'failed' }
        }
        catch {
            $boxCleanup.clearConfiguration.result = 'failed'
            $boxCleanup.clearConfiguration.error = $_.Exception.Message
        }
        $boxCleanup.result = if (@($boxCleanup.terminate.result, $boxCleanup.delete.result, $boxCleanup.clearConfiguration.result) -contains 'failed') { 'failed' } else { 'completed' }
    }
    try {
        $listedBoxes = @(Get-ExistingBoxList)
        $cleanup.listExitCode = 0
        $remaining = @($createdBoxes | Where-Object { Test-BoxListed $listedBoxes $_ })
        if ($remaining.Count -gt 0) {
            $cleanup.result = 'box-config-retained'
            $cleanup.remainingBoxes = $remaining
        }
        elseif (($cleanup.hostCredential.result -eq 'completed' -or $cleanup.hostCredential.result -eq 'not-required') -and
            -not ($cleanup.boxes.Values | Where-Object { $_.result -eq 'failed' })) {
            $cleanup.result = 'completed'
        }
        else {
            $cleanup.result = 'failed'
        }
    }
    catch {
        $cleanup.result = 'verification-failed'
        $cleanup.listError = $_.Exception.Message
    }
    $report.cleanup = $cleanup.result
    $report.cleanupDetails = $cleanup
    if ($report.cleanup -ne 'completed') {
        $report.result = 'failed'
        $report.cleanupError = "Cleanup did not complete: $($report.cleanup)."
    }
    New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
}

$report | ConvertTo-Json -Depth 8
if ($report.result -ne 'passed') { exit 1 }
