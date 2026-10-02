#requires -Version 5.1
[CmdletBinding()]
param([switch]$Test, [switch]$LockedRestore, [switch]$IncludeSystemTests)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$root = Split-Path -Parent $PSScriptRoot
$code = 0
try {
    Invoke-HubEnvironment -Root $root -Action {
        param($context)
        Invoke-HubBuild -Context $context -Test:$Test -LockedRestore:$LockedRestore -IncludeSystemTests:$IncludeSystemTests
    }
}
catch {
    $code = Get-HubExitCode -Failure $_
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
}
exit $code
