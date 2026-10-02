#requires -Version 5.1
<#
.SYNOPSIS
  Run agprofile-next from this repo's Release build using the configured .NET 10 SDK.
.EXAMPLE
  .\Invoke-HubCli.ps1 doctor
  .\Invoke-HubCli.ps1 new "验收A"
  .\Invoke-HubCli.ps1 ls --json
#>
[CmdletBinding()]
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$CliArgs)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $root 'src\AGProfileHub.Cli\bin\Release\net10.0\agprofile-next.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "Build the solution first (hub.ps1 build): $dll" }
. (Join-Path $PSScriptRoot 'Common.ps1')
Invoke-HubEnvironment -Root $root -Action {
    param($context)
    Invoke-HubCommand -FilePath $context.DotNet -Arguments (@($dll) + $CliArgs)
}
