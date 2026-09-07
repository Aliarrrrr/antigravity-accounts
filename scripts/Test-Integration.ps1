#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$sdk=Join-Path $repo '.tools\dotnet\dotnet.exe'
if(-not(Test-Path -LiteralPath $sdk)) {$sdk='dotnet'}
$cli=Join-Path $repo 'src\AGProfileHub.Cli\bin\Release\net10.0\AGProfileHub.Cli.dll'
$testRoot=Join-Path $repo ('artifacts\integration\'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$results=[Collections.Generic.List[object]]::new()
function Invoke-Cli([string[]]$Arguments,[int]$ExpectedExit=0) {
    $output = & $sdk $cli --data-root $testRoot --json @Arguments
    if($LASTEXITCODE -ne $ExpectedExit) {throw "CLI exit $LASTEXITCODE for $($Arguments[0]); expected $ExpectedExit"}
    if($output) {return ($output | Out-String | ConvertFrom-Json)}
}
function Check([string]$Name,[bool]$Pass) {
    $results.Add(@{name=$Name;passed=$Pass})
    if(-not $Pass) {throw "Integration assertion failed: $Name"}
    Write-Output "PASS $Name"
}
try {
    $a=Invoke-Cli @('create','测试 A with spaces')
    $b=Invoke-Cli @('create','测试 B')
    Check 'distinct stable IDs' ($a.id -ne $b.id)
    $null=Invoke-Cli @('launch',[string]$a.id)
    # Vendor update/logging begins after about 10 seconds; verify survival after CLI exits.
    Start-Sleep -Seconds 15
    $firstA=Invoke-Cli @('status',[string]$a.id)
    $null=Invoke-Cli @('launch',[string]$a.id)
    $secondA=Invoke-Cli @('status',[string]$a.id)
    Check 'duplicate launch retains root' ($firstA.root.processId -eq $secondA.root.processId -and $null -ne $firstA.root.processId)
    $null=Invoke-Cli @('launch',[string]$b.id)
    Start-Sleep -Seconds 15
    $firstB=Invoke-Cli @('status',[string]$b.id)
    Check 'two profiles have different roots' ($firstA.root.processId -ne $firstB.root.processId -and $null -ne $firstB.root.processId)
    $null=Invoke-Cli @('restart',[string]$a.id,'--force')
    $restartedA=Invoke-Cli @('status',[string]$a.id)
    $retainedB=Invoke-Cli @('status',[string]$b.id)
    Check 'restart replaces only A root' ($restartedA.root.processId -ne $firstA.root.processId -and $retainedB.root.processId -eq $firstB.root.processId)
    $null=Invoke-Cli @('stop',[string]$a.id,'--force')
    $retainedB=Invoke-Cli @('status',[string]$b.id)
    Check 'stop A preserves B' ($retainedB.root.processId -eq $firstB.root.processId)
    $null=Invoke-Cli @('stop-all','--force')
    $profileDir=Join-Path $testRoot ('profiles\'+$a.id)
    $marker=Join-Path $profileDir 'integration-marker.txt'
    Set-Content -LiteralPath $marker -Value 'non-secret integration fixture'
    $null=Invoke-Cli @('remove',[string]$a.id)
    Check 'remove preserves profile data' (Test-Path -LiteralPath $marker)
} finally {
    # Keep this owned test directory for diagnosis; never delete user profile data.
    try {$null=Invoke-Cli @('stop-all','--force')} catch {Write-Warning 'Test profiles may still be running; inspect this test data root before cleanup.'}
    @{schemaVersion=1;timestamp=[DateTime]::UtcNow.ToString('o');checks=@($results.ToArray());authIsolation='manual-verification-required'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot 'integration-report.json') -Encoding utf8
    Write-Output "Evidence: $testRoot"
}
