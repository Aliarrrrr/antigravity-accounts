#requires -Version 7.0
param([switch]$Test)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$sdk=Join-Path $root '.tools\dotnet\dotnet.exe'
if(-not(Test-Path -LiteralPath $sdk)) {$sdk='dotnet'}
& $sdk build (Join-Path $root 'AGProfileHub.slnx') -c Release
if($LASTEXITCODE -ne 0) {exit $LASTEXITCODE}
if($Test) {
    & $sdk run --project (Join-Path $root 'tests\AGProfileHub.Tests') -c Release --no-build
    exit $LASTEXITCODE
}
