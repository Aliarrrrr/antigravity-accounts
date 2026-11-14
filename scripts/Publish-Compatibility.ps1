#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AccountsPackageRoot, [Parameter(Mandatory)][string]$OutputRoot, [switch]$Release)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Accounts.Common.ps1')
$version = Get-AccountsVersion $root
$git = Get-AccountsGitState $root
if ($Release) { Assert-AccountsRelease -Root $root -Version $version -State $git }
$destination = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $destination) { throw 'Choose a new compatibility output directory.' }
$accounts = (Resolve-Path -LiteralPath $AccountsPackageRoot).ProviderPath
& (Join-Path $PSScriptRoot 'Test-AccountsPackage.ps1') -PackageRoot $accounts
& node (Join-Path $root 'packages/localization/scripts/Sync-NativePrograms.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Localization native-program descriptions could not be synchronized.' }
$package = Join-Path $destination 'AntigravityCompatibility'
New-Item -ItemType Directory -Path $package -Force | Out-Null
Copy-Item -LiteralPath $accounts -Destination (Join-Path $package 'accounts') -Recurse
$localizationSource = Join-Path $root 'packages/localization'
$localization = Join-Path $package 'localization'
New-Item -ItemType Directory -Path $localization | Out-Null
foreach ($name in @('src','scripts','dictionaries','durable.mjs','localize.mjs','package.json','package-lock.json','LICENSE','LICENSE.upstream','THIRD_PARTY_NOTICES.md')) {
  Copy-Item -LiteralPath (Join-Path $localizationSource $name) -Destination $localization -Recurse
}
& npm ci --prefix $localization --omit=dev --ignore-scripts --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw 'Localization production dependencies could not be installed.' }
$compatibility = Join-Path $package 'compatibility'
New-Item -ItemType Directory -Path $compatibility | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'packages/compatibility') -File | Where-Object Extension -In @('.mjs','.ps1') | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $compatibility }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-Compatibility.ps1') -Destination $compatibility
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $package
$readme = @'
# Antigravity Compatibility

This bundle contains the Accounts distribution, the Chinese localization engine and production dependencies, and the shared update coordinator. Official program files and account data are not included.

Use the registered standalone Node runtime. The installer preserves the existing account and localization state directories. It updates only components already installed and enabled. Prepare completes before the current application is changed, and application files are replaced only after normal exit.

Run compatibility/Install-Compatibility.ps1 with explicit BundleRoot, StateRoot, LocalizationStateRoot, AccountsStateRoot, ToolkitRoot, NodePath and InstallRoot paths. The stable bootstrap registers the startup and shortcuts. See accounts/README.md and accounts/VALIDATION.md for supported program combinations and installed-client checks.

Automatic checks use only releases from Aliarrrrr/antigravity-accounts on the preview channel. A new official program that is not supported remains unmodified; official updates continue. Independent-account fallback requires the existing explicit native-identity confirmation. A compatible future package can restore the saved plugin configuration without moving the account store.
'@
[IO.File]::WriteAllText((Join-Path $package 'README.md'), $readme, [Text.UTF8Encoding]::new($false))
$identity = @{
  version=$version;provenance=@{commit=$git.commit;dirty=$git.dirty;release=[bool]$Release}
  components=@{accounts=@{path='accounts';version=$version};localization=@{path='localization';version=(Get-Content (Join-Path $localization 'package.json') -Raw | ConvertFrom-Json).version}}
}
$identityFile = Join-Path $destination 'bundle-identity.json'
$identity | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $identityFile -Encoding utf8
$env:COMPATIBILITY_PACKAGE_ROOT = $package
$env:COMPATIBILITY_IDENTITY_FILE = $identityFile
try {
  & node --input-type=module -e "import fs from 'node:fs/promises';import path from 'node:path';import {createManifest} from './packages/compatibility/manifest.mjs';const root=process.env.COMPATIBILITY_PACKAGE_ROOT;const identity=JSON.parse(await fs.readFile(process.env.COMPATIBILITY_IDENTITY_FILE,'utf8'));const manifest=await createManifest(root,identity);await fs.writeFile(path.join(root,'bundle-manifest.json'),JSON.stringify(manifest,null,2)+'\n');"
  if ($LASTEXITCODE -ne 0) { throw 'Compatibility manifest creation failed.' }
} finally { Remove-Item Env:\COMPATIBILITY_PACKAGE_ROOT -ErrorAction SilentlyContinue; Remove-Item Env:\COMPATIBILITY_IDENTITY_FILE -ErrorAction SilentlyContinue }
Remove-Item -LiteralPath $identityFile
$zip = Join-Path $destination ('AntigravityCompatibility-' + $version + '-win-x64.zip')
Compress-Archive -LiteralPath $package -DestinationPath $zip
& (Join-Path $PSScriptRoot 'Test-CompatibilityPackage.ps1') -PackageRoot $package -ZipPath $zip
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
@{file=[IO.Path]::GetFileName($zip);sha256=$zipHash} | ConvertTo-Json | Set-Content -LiteralPath ($zip + '.sha256.json') -Encoding utf8
[IO.File]::WriteAllText((Join-Path $destination 'COMPATIBILITY-SHA256SUMS.txt'), $zipHash + '  ' + [IO.Path]::GetFileName($zip) + "`n", [Text.UTF8Encoding]::new($false))
Write-Output ('Compatibility bundle: ' + $zip)
