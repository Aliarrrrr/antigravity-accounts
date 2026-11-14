#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageRoot, [string]$ZipPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$package = (Resolve-Path -LiteralPath $PackageRoot).ProviderPath
$env:COMPATIBILITY_PACKAGE_ROOT = $package
try {
  & node --input-type=module -e "import {validateBundle} from './packages/compatibility/manifest.mjs';const m=await validateBundle(process.env.COMPATIBILITY_PACKAGE_ROOT);console.log(JSON.stringify({version:m.version,payloadFiles:m.files.length}));"
  if ($LASTEXITCODE -ne 0) { throw 'Compatibility manifest validation failed.' }
} finally { Remove-Item Env:\COMPATIBILITY_PACKAGE_ROOT -ErrorAction SilentlyContinue }
& (Join-Path $PSScriptRoot 'Test-AccountsPackage.ps1') -PackageRoot (Join-Path $package 'accounts')
if ($ZipPath) {
  $manifest = Get-Content -LiteralPath (Join-Path $package 'bundle-manifest.json') -Raw | ConvertFrom-Json
  $expected = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach ($file in $manifest.files) { $expected.Add($file.path,$file.sha256) }
  $expected.Add('bundle-manifest.json',(Get-FileHash -LiteralPath (Join-Path $package 'bundle-manifest.json') -Algorithm SHA256).Hash)
  $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).ProviderPath)
  try {
    foreach ($entry in $archive.Entries) {
      $name = $entry.FullName.Replace('\','/')
      if ($name.EndsWith('/')) { continue }
      if (-not $name.StartsWith('AntigravityCompatibility/',[StringComparison]::Ordinal)) { throw 'Unexpected compatibility ZIP root.' }
      $relative = $name.Substring('AntigravityCompatibility/'.Length)
      if (-not $seen.Add($relative) -or -not $expected.ContainsKey($relative)) { throw 'Unexpected compatibility ZIP entry.' }
      $stream = $entry.Open()
      try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
      if ($hash -ine $expected[$relative]) { throw 'Compatibility ZIP contents differ.' }
    }
    if ($seen.Count -ne $expected.Count) { throw 'Compatibility ZIP payload is incomplete.' }
  } finally { $archive.Dispose() }
}
Write-Output 'Verified compatibility bundle and payload.'
