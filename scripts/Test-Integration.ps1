#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$sdk=Join-Path $repo '.tools\dotnet\dotnet.exe'
if(-not(Test-Path -LiteralPath $sdk)) {$sdk='dotnet'}
$cli=Join-Path $repo 'src\AGProfileHub.Cli\bin\Release\net10.0\AGProfileHub.Cli.dll'
$testRoot=Join-Path $repo ('artifacts\integration\'+[guid]::NewGuid().ToString('N'))
$otherRoot=Join-Path $testRoot 'second-data-root'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$results=[Collections.Generic.List[object]]::new()
function Invoke-CliAt([string]$DataRoot,[string[]]$Arguments,[int]$ExpectedExit=0) {
    $output = & $sdk $cli --data-root $DataRoot --json @Arguments
    if($LASTEXITCODE -ne $ExpectedExit) {throw "CLI exit $LASTEXITCODE for $($Arguments[0]); expected $ExpectedExit"}
    if($output) {return ($output | Out-String | ConvertFrom-Json)}
}
function Invoke-Cli([string[]]$Arguments,[int]$ExpectedExit=0) {
    return Invoke-CliAt -DataRoot $testRoot -Arguments $Arguments -ExpectedExit $ExpectedExit
}
function Start-CliLaunch([string]$DataRoot,[string]$ProfileId,[string]$Label) {
    $stdout=Join-Path $testRoot ($Label+'.stdout.json')
    $stderr=Join-Path $testRoot ($Label+'.stderr.json')
    $process=Start-Process -FilePath $sdk -ArgumentList @($cli,'--data-root',$DataRoot,'--json','launch',$ProfileId) `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -NoNewWindow -PassThru
    return @{process=$process;stdout=$stdout;stderr=$stderr;dataRoot=$DataRoot;profileId=$ProfileId}
}
function Complete-CliLaunch([hashtable]$Job) {
    if(-not $Job.process.WaitForExit(45000)) {
        $Job.process.Kill($true)
        throw "Concurrent CLI launch timed out for $($Job.profileId)"
    }
    $Job.process.WaitForExit()
    $errorCode=$null
    if((Test-Path -LiteralPath $Job.stderr) -and (Get-Item -LiteralPath $Job.stderr).Length -gt 0) {
        try {$errorCode=(Get-Content -Raw -LiteralPath $Job.stderr | ConvertFrom-Json).error.code} catch {}
    }
    return @{exitCode=$Job.process.ExitCode;errorCode=$errorCode;dataRoot=$Job.dataRoot;profileId=$Job.profileId}
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

    $sameA=Start-CliLaunch $testRoot ([string]$a.id) 'same-root-a'
    $sameB=Start-CliLaunch $testRoot ([string]$b.id) 'same-root-b'
    $sameResults=@(Complete-CliLaunch $sameA; Complete-CliLaunch $sameB)
    Check 'independent CLIs serialize same-data-root launches' (($sameResults.exitCode | Where-Object {$_ -eq 0}).Count -eq 1 -and ($sameResults.exitCode | Where-Object {$_ -eq 4}).Count -eq 1)
    Check 'blocked same-root CLI reports unsupported capability' (($sameResults | Where-Object exitCode -eq 4).errorCode -eq 'unsupported_capability')
    $sameStatuses=@(Invoke-Cli @('status',[string]$a.id); Invoke-Cli @('status',[string]$b.id))
    Check 'same-root concurrent launch creates exactly one root' (($sameStatuses | Where-Object state -eq 'running').Count -eq 1 -and ($sameStatuses | Where-Object state -eq 'stopped').Count -eq 1)
    foreach($status in $sameStatuses | Where-Object state -eq 'running') {$null=Invoke-Cli @('stop',[string]$status.profileId,'--force')}

    $c=Invoke-CliAt -DataRoot $otherRoot -Arguments @('create','Second data root')
    $crossA=Start-CliLaunch $testRoot ([string]$a.id) 'cross-root-a'
    $crossB=Start-CliLaunch $otherRoot ([string]$c.id) 'cross-root-b'
    $crossResults=@(Complete-CliLaunch $crossA; Complete-CliLaunch $crossB)
    Check 'independent CLIs serialize different-data-root launches' (($crossResults.exitCode | Where-Object {$_ -eq 0}).Count -eq 1 -and ($crossResults.exitCode | Where-Object {$_ -eq 4}).Count -eq 1)
    Check 'blocked cross-root CLI reports unsupported capability' (($crossResults | Where-Object exitCode -eq 4).errorCode -eq 'unsupported_capability')
    $crossStatuses=@(
        Invoke-Cli @('status',[string]$a.id)
        Invoke-CliAt -DataRoot $otherRoot -Arguments @('status',[string]$c.id)
    )
    Check 'cross-root concurrent launch creates exactly one root' (($crossStatuses | Where-Object state -eq 'running').Count -eq 1 -and ($crossStatuses | Where-Object state -eq 'stopped').Count -eq 1)
    foreach($status in $crossStatuses | Where-Object state -eq 'running') {
        $owner=($crossResults | Where-Object profileId -eq ([string]$status.profileId)).dataRoot
        $null=Invoke-CliAt -DataRoot $owner -Arguments @('stop',[string]$status.profileId,'--force')
    }

    $null=Invoke-Cli @('launch',[string]$a.id)
    # Vendor update/logging begins after about 10 seconds; verify survival after CLI exits.
    Start-Sleep -Seconds 15
    $firstA=Invoke-Cli @('status',[string]$a.id)
    $null=Invoke-Cli @('launch',[string]$a.id)
    $secondA=Invoke-Cli @('status',[string]$a.id)
    Check 'duplicate launch retains root' ($firstA.root.processId -eq $secondA.root.processId -and $null -ne $firstA.root.processId)
    $null=Invoke-Cli -Arguments @('launch',[string]$b.id) -ExpectedExit 4
    $blockedB=Invoke-Cli @('status',[string]$b.id)
    Check 'concurrent profile launch is blocked' ($blockedB.state -eq 'stopped')
    $null=Invoke-Cli @('stop',[string]$a.id,'--force')
    $null=Invoke-Cli @('launch',[string]$b.id)
    Start-Sleep -Seconds 15
    $firstB=Invoke-Cli @('status',[string]$b.id)
    Check 'B starts after A stops' ($firstB.state -eq 'running' -and $null -ne $firstB.root.processId)
    $null=Invoke-Cli @('restart',[string]$b.id,'--force')
    $restartedB=Invoke-Cli @('status',[string]$b.id)
    Check 'restart replaces the only running root' ($restartedB.root.processId -ne $firstB.root.processId -and $null -ne $restartedB.root.processId)
    $null=Invoke-Cli @('stop-all','--force')
    $profileDir=Join-Path $testRoot ('profiles\'+$a.id)
    $marker=Join-Path $profileDir 'integration-marker.txt'
    Set-Content -LiteralPath $marker -Value 'non-secret integration fixture'
    $null=Invoke-Cli @('remove',[string]$a.id)
    Check 'remove preserves profile data' (Test-Path -LiteralPath $marker)
} finally {
    # Keep this owned test directory for diagnosis; never delete user profile data.
    try {$null=Invoke-Cli @('stop-all','--force')} catch {Write-Warning 'Test profiles may still be running; inspect this test data root before cleanup.'}
    try {$null=Invoke-CliAt -DataRoot $otherRoot -Arguments @('stop-all','--force')} catch {Write-Warning 'Second test data root may still have an owned profile running; inspect before cleanup.'}
    @{schemaVersion=1;timestamp=[DateTime]::UtcNow.ToString('o');checks=@($results.ToArray());authIsolation='incompatible-on-current-desktop'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testRoot 'integration-report.json') -Encoding utf8
    Write-Output "Evidence: $testRoot"
}
