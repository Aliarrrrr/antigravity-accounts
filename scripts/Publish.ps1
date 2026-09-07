#requires -Version 7.0
[CmdletBinding()]
param([switch]$FrameworkDependent)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$sdk=Join-Path $root '.tools\dotnet\dotnet.exe'
if(-not(Test-Path -LiteralPath $sdk)) {$sdk='dotnet'}
& (Join-Path $PSScriptRoot 'Build.ps1') -Test
if($LASTEXITCODE -ne 0) {throw 'Build/tests failed; package not produced.'}
$stamp=[DateTime]::Now.ToString('yyyyMMdd-HHmmss')
$packageRoot=Join-Path $root "artifacts\package\$stamp\AGProfileHub"
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
$selfContained=if($FrameworkDependent){'false'}else{'true'}
foreach($spec in @(@{name='Cli';directory='cli'},@{name='Gui';directory='hub'})) {
    $project=Join-Path $root "src\AGProfileHub.$($spec.name)\AGProfileHub.$($spec.name).csproj"
    & $sdk publish $project -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=false -o (Join-Path $packageRoot $spec.directory)
    if($LASTEXITCODE -ne 0) {throw "Publish failed: $($spec.name)"}
}
foreach($name in @('README.md','CAPABILITY_REPORT.md','MANUAL_TEST_PLAN.md','SECURITY.md','CHANGELOG.md','FINAL_REPORT.md')) {
    $file=Join-Path $root $name
    if(Test-Path -LiteralPath $file) {Copy-Item -LiteralPath $file -Destination $packageRoot}
}
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'scripts') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Probe-Antigravity.ps1') -Destination (Join-Path $packageRoot 'scripts')
$manifest=@{schemaVersion=1;builtAt=[DateTime]::UtcNow.ToString('o');runtime='win-x64';selfContained=(-not $FrameworkDependent);experimental=$true;authIsolation='manual-verification-required'}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'package-manifest.json') -Encoding utf8
'@start "" "%~dp0hub\AGProfileHub.Gui.exe"' | Set-Content -LiteralPath (Join-Path $packageRoot 'Start Hub.cmd') -Encoding ascii
'@"%~dp0cli\agprofile.exe" %*' | Set-Content -LiteralPath (Join-Path $packageRoot 'agprofile.cmd') -Encoding ascii
$zip=Join-Path (Split-Path $packageRoot -Parent) 'AGProfileHub-win-x64.zip'
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zip
Get-FileHash -Algorithm SHA256 -LiteralPath $zip | Select-Object Algorithm,Hash | ConvertTo-Json | Set-Content -LiteralPath ($zip+'.sha256.json') -Encoding utf8
Write-Output "Package: $packageRoot"
Write-Output "Archive: $zip"
