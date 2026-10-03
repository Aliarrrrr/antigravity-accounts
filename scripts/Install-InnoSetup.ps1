#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or
    [string]::IsNullOrWhiteSpace($env:GITHUB_ENV)) { throw 'Inno Setup provisioning requires a Windows CI runner.' }

# Official immutable release asset. SHA256 agrees with both the GitHub asset digest and .issig file:
# https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3
$url = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
$sha256 = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
$runnerRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP)
if (-not (Test-Path -LiteralPath $runnerRoot -PathType Container)) { throw 'CI runner temporary directory is unavailable.' }
$ownedRoot = Join-Path $runnerRoot ('agph-inno-6.7.3-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $ownedRoot | Out-Null
$installer = Join-Path $ownedRoot 'innosetup-6.7.3.exe'
$installRoot = Join-Path $ownedRoot 'compiler'
Invoke-WebRequest -Uri $url -OutFile $installer -TimeoutSec 120
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -cne $sha256) {
    throw 'Official Inno Setup 6.7.3 download did not match the pinned SHA256.'
}
# The official 6.7.3 setup.iss supports /PORTABLE=1: no uninstall registration or associations.
$arguments = @('/PORTABLE=1','/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS','/TASKS=',('/DIR="' + $installRoot + '"'))
$setup = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -PassThru
try {
    if (-not $setup.WaitForExit(60000)) {
        if (-not $setup.HasExited) { $setup.Kill(); $null = $setup.WaitForExit(5000) }
        throw 'Inno Setup 6.7.3 installation timed out.'
    }
    if ($setup.ExitCode -ne 0) { throw 'Inno Setup 6.7.3 installation failed.' }
} finally { $setup.Dispose() }
$compiler = Join-Path $installRoot 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Inno Setup 6.7.3 compiler was not installed.' }
$env:AGPH_ISCC = $compiler
"AGPH_ISCC=$compiler" | Add-Content -LiteralPath $env:GITHUB_ENV -Encoding utf8
Write-Output 'Official Inno Setup 6.7.3 verified and provisioned for this runner.'
