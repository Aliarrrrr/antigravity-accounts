#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageRoot, [switch]$Desktop)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageRoot).Path
$cli = Join-Path $package 'cli/agprofile-next.exe'
$data = Join-Path $env:TEMP ('agprofile-next-package-test-' + [guid]::NewGuid().ToString('N'))
$report = [ordered]@{schemaVersion=1;packageHashes=$false;cli=$false;gui=$false;shortcut=$false;desktop='not-run';protocol='not-run';manualAcceptance=$false;dataRoot=$data}
function Invoke-Next([string[]]$Arguments) {
    $output = & $cli --data-root $data @Arguments --json
    if ($LASTEXITCODE -ne 0) { throw 'Next CLI operation failed.' }
    ($output -join "`n") | ConvertFrom-Json
}
$manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
foreach ($file in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $package $file.path))
    if (-not $path.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escaped package.' }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Package digest mismatch.' }
}
$report.packageHashes = $true
$null = Invoke-Next @('version')
$a = Invoke-Next @('new','smoke-A')
$b = Invoke-Next @('new','smoke-B')
$null = Invoke-Next @('bridge',$a.id,'off')
$null = Invoke-Next @('bridge',$b.id,'off')
$report.cli = @((Invoke-Next @('ls'))).Count -eq 2
$shortcutDir = Join-Path $data 'shortcuts'; New-Item -ItemType Directory -Path $shortcutDir | Out-Null
$shortcut = Invoke-Next @('shortcut',$a.id,$shortcutDir)
$report.shortcut = Test-Path -LiteralPath $shortcut.path
$image = Join-Path $data 'gui.png'
$gui = Start-Process (Join-Path $package 'hub/AGProfileHubNext.Gui.exe') -ArgumentList @('--data-root',('"'+$data+'"'),'--smoke-image',('"'+$image+'"')) -WindowStyle Hidden -PassThru
if (-not $gui.WaitForExit(15000)) { throw 'Next GUI smoke timed out.' }
$report.gui = $gui.ExitCode -eq 0 -and (Test-Path -LiteralPath $image)
$report.guiImage = $image
if ($Desktop) {
    $executable = Join-Path $env:LOCALAPPDATA 'Programs/antigravity/Antigravity.exe'
    $null = Invoke-Next @('configure-executable',$executable)
    $proof = Invoke-Next @('probe')
    if (-not ($proof.fileTokenStore -and $proof.independentHome -and $proof.independentUserData -and $proof.processesStopped -and $proof.protocolRestored)) { throw 'Desktop probe failed.' }
    $previous = (Get-Item 'HKCU:/Software/Classes/antigravity/shell/open/command').GetValue('')
    $registered = $false
    try {
        $null = Invoke-Next @('open',$a.id)
        $null = Invoke-Next @('open',$b.id)
        $statuses = @(Invoke-Next @('ls'))
        if (@($statuses | Where-Object state -eq 'running').Count -ne 2) { throw 'Two proven profiles are not running.' }
        if ($previous -and $previous.Contains('callback')) { $report.protocol='skipped-existing-manager-ownership' }
        else {
            $null = Invoke-Next @('configure-deeplink','register'); $registered=$true
            $null = Invoke-Next @('begin-login',$a.id)
            # Synthetic unauthenticated URI only; never initiates Google login.
            $null = Invoke-Next @('callback','antigravity://hub-probe-no-auth')
            Start-Sleep -Seconds 2
            $afterCallback = @(Invoke-Next @('ls'))
            if (@($afterCallback | Where-Object state -eq 'running').Count -ne 2 -or
                ($afterCallback | Where-Object profileId -eq $a.id).root.processId -ne ($statuses | Where-Object profileId -eq $a.id).root.processId) {
                throw 'Callback did not retain the existing proven A root.'
            }
            $null = Invoke-Next @('configure-deeplink','restore'); $registered=$false
            $current = (Get-Item 'HKCU:/Software/Classes/antigravity/shell/open/command').GetValue('')
            if ($current -ne $previous) { throw 'Protocol restoration failed.' }
            $report.protocol='synthetic-handoff-and-restoration-passed'
        }
        $null = Invoke-Next @('close',$a.id)
        if ((Invoke-Next @('status',$b.id)).state -ne 'running') { throw 'Closing A affected B.' }
        $null = Invoke-Next @('open',$a.id)
        if ((Invoke-Next @('status',$b.id)).state -ne 'running') { throw 'Restarting A affected B.' }
        $report.desktop='two-no-login-profiles-launch-close-restart-passed'
    } finally {
        if ($registered) { $null = Invoke-Next @('configure-deeplink','restore') }
        # Only this script's fresh UUIDs; no unrelated Profile is ever stopped.
        $null = Invoke-Next @('close',$a.id,'--force')
        $null = Invoke-Next @('close',$b.id,'--force')
    }
}
if (-not ($report.cli -and $report.gui -and $report.shortcut)) { throw 'Package smoke failed.' }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $data 'report.json') -Encoding utf8
$report | ConvertTo-Json -Depth 6
