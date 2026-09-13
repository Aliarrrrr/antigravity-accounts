#requires -Version 5.1
<#
.SYNOPSIS
  Run AGProfileHub.Cli from this repo's Release build using the bundled .NET 10 runtime.
.EXAMPLE
  .\Invoke-HubCli.ps1 doctor
  .\Invoke-HubCli.ps1 new "验收A"
  .\Invoke-HubCli.ps1 ls --json
#>
[CmdletBinding()]
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$CliArgs)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dll = Join-Path $root 'src\AGProfileHub.Cli\bin\Release\net10.0\AGProfileHub.Cli.dll'
if (-not (Test-Path -LiteralPath $dotnet)) { throw "Bundled SDK missing: $dotnet" }
if (-not (Test-Path -LiteralPath $dll)) { throw "Build the solution first (hub.ps1 build): $dll" }
& $dotnet $dll @CliArgs
exit $LASTEXITCODE
