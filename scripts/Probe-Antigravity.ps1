#requires -Version 7.0
[CmdletBinding()]
param([string]$Executable = "$env:LOCALAPPDATA\Programs\antigravity\Antigravity.exe", [ValidateRange(5,60)][int]$ObserveSeconds = 15, [switch]$Visible)
$ErrorActionPreference = 'Stop'
if(-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw "Antigravity executable not found: $Executable"
}
$Executable = [IO.Path]::GetFullPath($Executable)
$probeParent = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'AGProfileHub-Probes'))
$probeRoot = Join-Path $probeParent ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$report = [ordered]@{schemaVersion=1; timestamp=[DateTime]::UtcNow.ToString('o'); antigravity=@{path=$Executable;version=(Get-Item -LiteralPath $Executable).VersionInfo.ProductVersion}; strategy='isolated-process-home-and-user-data'; capabilities=@{multiInstance='unknown';userDataIsolation='unknown';homeStateIsolation='unknown';authIsolation='not-evaluated-by-probe';workspaceLaunch='unknown';extensionsIsolation='not-applicable-desktop-2'}; authenticationNote='Probe does not evaluate authentication. Manual A/B verification found concurrent Google-account isolation incompatible on Desktop 2.12.2.'; samples=@();cleanup='pending'}
function Start-ProbeInstance([string]$Label) {
    $dir = Join-Path $probeRoot $Label
    foreach ($sub in @('home','roaming','local','temp','user-data')) { New-Item -ItemType Directory -Path (Join-Path $dir $sub) -Force | Out-Null }
    $psi = [Diagnostics.ProcessStartInfo]::new($Executable)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    if($Visible) {$psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal}
    $psi.WorkingDirectory = $dir
    $psi.ArgumentList.Add('--user-data-dir=' + (Join-Path $dir 'user-data'))
    $psi.Environment['USERPROFILE'] = Join-Path $dir 'home'
    $psi.Environment['HOME'] = Join-Path $dir 'home'
    $psi.Environment['APPDATA'] = Join-Path $dir 'roaming'
    $psi.Environment['LOCALAPPDATA'] = Join-Path $dir 'local'
    $psi.Environment['TEMP'] = Join-Path $dir 'temp'
    $psi.Environment['TMP'] = Join-Path $dir 'temp'
    $psi.Environment['HOMEDRIVE'] = [IO.Path]::GetPathRoot($psi.Environment['USERPROFILE']).TrimEnd('\')
    $psi.Environment['HOMEPATH'] = $psi.Environment['USERPROFILE'].Substring(2)
    foreach($key in @('ELECTRON_RUN_AS_NODE','ELECTRON_OZONE_PLATFORM_HINT','DEV_URL','JETSKI_LS_PORT','NODE_OPTIONS')) {$null = $psi.Environment.Remove($key)}
    $proc = [Diagnostics.Process]::Start($psi)
    # Drain and discard vendor output: it may contain ephemeral bridge/CSRF secrets.
    $proc.BeginOutputReadLine()
    $proc.BeginErrorReadLine()
    $owned.Add($proc)
    return $proc
}
function Sample([string]$Stage) {
    $roots = @($owned | Where-Object { -not $_.HasExited } | ForEach-Object { $_.Id })
    $all = @(Get-CimInstance Win32_Process)
    $ids = [Collections.Generic.HashSet[int]]::new()
    foreach ($id in $roots) { $null=$ids.Add($id) }
    do { $added=$false; foreach($p in $all) { if($ids.Contains([int]$p.ParentProcessId) -and $ids.Add([int]$p.ProcessId)) {$added=$true} } } while($added)
    # Never persist process command lines: language_server contains internal session secrets.
    $processes = @($all | Where-Object {$ids.Contains([int]$_.ProcessId)} | Select-Object ProcessId,ParentProcessId,Name)
    $paths = @{}
    foreach($label in @('a','b')) {
        $dir=Join-Path $probeRoot $label
        $paths[$label]=@{userDataCreated=(Test-Path -LiteralPath (Join-Path $dir 'user-data\Local State'));homeGeminiCreated=(Test-Path -LiteralPath (Join-Path $dir 'home\.gemini'));topDirectories=@(Get-ChildItem -LiteralPath $dir -Directory -Recurse -ErrorAction SilentlyContinue | ForEach-Object {$_.FullName.Substring($dir.Length+1)} | Select-Object -First 60)}
    }
    $windows=@($owned | Where-Object {-not $_.HasExited} | ForEach-Object {$_.Refresh(); @{pid=$_.Id;hasWindow=($_.MainWindowHandle -ne 0)}})
    $report.samples+=@{stage=$Stage;roots=$roots;processes=$processes;paths=$paths;windows=$windows}
}
try {
    $a=Start-ProbeInstance 'a'
    Start-Sleep -Seconds $ObserveSeconds
    $b=Start-ProbeInstance 'b'
    Start-Sleep -Seconds $ObserveSeconds
    Sample 'both-started'
    if(-not $a.HasExited -and -not $b.HasExited) {$report.capabilities.multiInstance='observed'}
    $sample=$report.samples[-1]
    if($sample.paths.a.userDataCreated -and $sample.paths.b.userDataCreated) {$report.capabilities.userDataIsolation='observed'}
    if($sample.paths.a.homeGeminiCreated -and $sample.paths.b.homeGeminiCreated) {$report.capabilities.homeStateIsolation='observed'}
    $report.capabilities['separateWindows'] = 'unknown'
    if(@($sample.windows | Where-Object {$_.hasWindow}).Count -eq 2) {$report.capabilities.separateWindows='observed'}
    if(-not $a.HasExited) {
        $null=$a.CloseMainWindow()
        if(-not $a.WaitForExit(5000)) {$a.Kill($true);$a.WaitForExit(5000)|Out-Null}
    }
    Sample 'a-stopped'
    $report['stopIsolationObserved']=(-not $b.HasExited)
} catch { $report['errorType']=$_.Exception.GetType().FullName; throw }
finally {
    foreach($proc in $owned) { if(-not $proc.HasExited) {$null=$proc.CloseMainWindow();if(-not $proc.WaitForExit(5000)) {$proc.Kill($true);$null=$proc.WaitForExit(5000)}} }
    $resolved=[IO.Path]::GetFullPath($probeRoot)
    if(-not $resolved.StartsWith($probeParent + '\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Unsafe cleanup target'}
    $links=@(Get-ChildItem -LiteralPath $resolved -Force -Recurse | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint})
    if($links.Count -eq 0 -and @($owned | Where-Object {-not $_.HasExited}).Count -eq 0) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
        $report.cleanup='completed'
    } else {$report.cleanup='retained-for-safety'}
    $out=Join-Path $PSScriptRoot '..\artifacts\probe'
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $out 'capability-report.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 12
}
