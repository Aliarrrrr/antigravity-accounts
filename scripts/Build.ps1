#requires -Version 7.0
[CmdletBinding()]
param([string]$DotNet, [switch]$Test, [switch]$LockedRestore = $true)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Accounts.Common.ps1')
$root = Split-Path -Parent $PSScriptRoot
Invoke-AccountsEnvironment -Root $root -DotNet $DotNet -Action {
    param($context)
    $component = Join-Path $root 'src/Antigravity.Accounts/Antigravity.Accounts.csproj'
    $tests = Join-Path $root 'tests/Antigravity.Accounts.Tests/Antigravity.Accounts.Tests.csproj'
    $locks = if ($LockedRestore) { Get-AccountsLockState $root } else { $null }
    try {
        foreach ($project in @($component,$tests)) {
            $arguments = @('restore',$project)
            if ($LockedRestore) { $arguments += '--locked-mode' }
            Invoke-AccountsCommand $context.DotNet $arguments
        }
        Invoke-AccountsCommand $context.DotNet @('build',$tests,'-c','Release','--no-restore')
        if ($Test) { Invoke-AccountsCommand $context.DotNet @('run','--project',$tests,'-c','Release','--no-build','--no-restore') }
    } finally { if ($locks) { Assert-AccountsLockState $locks } }
}
