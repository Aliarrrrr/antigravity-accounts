param(
    [Parameter(Mandatory=$true)][string]$InstallRoot,
    [Parameter(Mandatory=$true)][string]$CandidatePath,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedHash,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$CandidateHash
)
$ErrorActionPreference='Stop'
function Get-AsarHash([string]$FilePath) {
    $stream=[IO.File]::OpenRead($FilePath)
    $hasher=[Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-','') }
    finally { $stream.Dispose(); $hasher.Dispose() }
}
function Assert-MaintenanceIdle {
    $busyJson=& (Join-Path $PSScriptRoot 'Maintenance-State.ps1') -InstallRoot $resolvedInstall
    $busy=ConvertFrom-Json -InputObject ($busyJson -join "`n")
    if($busy -and $busy.Count){throw 'maintenance_busy'}
}
$resolvedInstall=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$targetAsar=Join-Path $resolvedInstall 'resources\app.asar'
$candidateAsar=[IO.Path]::GetFullPath($CandidatePath)
if(-not (Test-Path -LiteralPath (Join-Path $resolvedInstall 'Antigravity.exe'))){throw 'wrong_application'}
Assert-MaintenanceIdle
if((Get-AsarHash $targetAsar) -ne $ExpectedHash){throw 'installation_changed'}
$stagePath=Join-Path ([IO.Path]::GetDirectoryName($targetAsar)) ('app.asar.stage-'+[Guid]::NewGuid().ToString('N'))
$rollbackPath=$stagePath+'.rollback'
try {
    [IO.File]::Copy($candidateAsar,$stagePath,$false)
    if((Get-AsarHash $candidateAsar) -ne $CandidateHash){throw 'candidate_hash_mismatch'}
    if((Get-AsarHash $stagePath) -ne $CandidateHash){throw 'stage_hash_mismatch'}
    Assert-MaintenanceIdle
    if((Get-AsarHash $targetAsar) -ne $ExpectedHash){throw 'installation_changed'}
    [IO.File]::Replace($stagePath,$targetAsar,$rollbackPath,$false)
    if((Get-AsarHash $targetAsar) -ne $CandidateHash){
        [IO.File]::Replace($rollbackPath,$targetAsar,$null,$false)
        throw 'replacement_verification_failed_original_restored'
    }
    Remove-Item -LiteralPath $rollbackPath -Force
} finally {
    if(Test-Path -LiteralPath $stagePath){Remove-Item -LiteralPath $stagePath -Force}
}
