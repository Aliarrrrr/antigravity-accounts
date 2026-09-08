#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageRoot)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$package=[IO.Path]::GetFullPath($PackageRoot)
$cli=Join-Path $package 'cli\AGProfileHub.Cli.exe'
$testRoot=Join-Path $repo ('artifacts\package-smoke\'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$checks=[Collections.Generic.List[object]]::new()
function Invoke-Cli([string[]]$Arguments,[int]$ExpectedExit=0) {
    $output=& $cli --data-root $testRoot --json @Arguments
    if($LASTEXITCODE -ne $ExpectedExit) {throw "Packaged CLI exit $LASTEXITCODE for $($Arguments[0]); expected $ExpectedExit"}
    if($output) {return ($output | Out-String | ConvertFrom-Json)}
}
function Check([string]$Name,[bool]$Pass) {
    $checks.Add(@{name=$Name;passed=$Pass})
    if(-not $Pass) {throw "Package assertion failed: $Name"}
    Write-Output "PASS $Name"
}
try {
    $help=Invoke-Cli @('help')
    Check 'packaged help exposes shortcuts' ($help.help -match 'shortcut <id>')
    $profile=Invoke-Cli @('create','Package shortcut test')
    $created=Invoke-Cli @('shortcut',[string]$profile.id,'--directory',$testRoot)
    $shell=New-Object -ComObject WScript.Shell
    $link=$shell.CreateShortcut($created.shortcut)
    try {
        Check 'link targets packaged CLI and original data root' ($link.TargetPath -eq $cli -and $link.Arguments -eq ('--data-root "'+$testRoot+'" launch '+$profile.id))
    } finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
    $null=Invoke-Cli @('rename',[string]$profile.id,'Renamed after shortcut')
    # Exercise the actual .lnk through Windows Shell; no profile credentials are involved.
    Start-Process -FilePath $created.shortcut -WindowStyle Hidden
    $deadline=[DateTime]::UtcNow.AddSeconds(45)
    do {
        Start-Sleep -Seconds 2
        $status=Invoke-Cli @('status',[string]$profile.id)
    } while($status.state -ne 'running' -and [DateTime]::UtcNow -lt $deadline)
    Check 'shortcut launches renamed profile' ($status.state -eq 'running' -and $status.name -eq 'Renamed after shortcut')
    $firstPid=$status.root.processId
    Start-Process -FilePath $created.shortcut -WindowStyle Hidden
    Start-Sleep -Seconds 5
    $status=Invoke-Cli @('status',[string]$profile.id)
    Check 'second shortcut launch retains owned root' ($status.root.processId -eq $firstPid)
    $null=Invoke-Cli @('stop',[string]$profile.id,'--force')
    $status=Invoke-Cli @('status',[string]$profile.id)
    Check 'packaged CLI stops shortcut instance' ($status.state -eq 'stopped')
    $other=Invoke-Cli @('create','Package concurrent gate test')
    $null=Invoke-Cli -Arguments @('launch-all') -ExpectedExit 4
    $firstStatus=Invoke-Cli @('status',[string]$profile.id)
    $otherStatus=Invoke-Cli @('status',[string]$other.id)
    Check 'packaged launch-all does not partially start profiles' ($firstStatus.state -eq 'stopped' -and $otherStatus.state -eq 'stopped')
} finally {
    try {$null=Invoke-Cli @('stop-all','--force')} catch {Write-Warning 'Inspect owned package-smoke profiles before cleanup.'}
    @{schemaVersion=1;timestamp=[DateTime]::UtcNow.ToString('o');checks=@($checks.ToArray());package=$package} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $testRoot 'package-report.json') -Encoding utf8
    Write-Output "Evidence: $testRoot"
}
