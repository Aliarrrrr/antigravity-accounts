#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageRoot, [string]$ZipPath)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageRoot).ProviderPath.TrimEnd([IO.Path]::DirectorySeparatorChar)
$manifestPath = Join-Path $package 'package-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Accounts.Common.ps1')
if ($manifest.application -cne 'AntigravityAccounts' -or $manifest.version -cne (Get-AccountsVersion $root) -or $manifest.runtime -cne 'win-x64' -or $manifest.selfContained -ne $true -or $manifest.singleFileCompressed -ne $true -or $manifest.debugSymbols -ne $false) { throw 'Unexpected package identity or build settings.' }
if ($manifest.provenance.commit -cnotmatch '^[0-9a-f]{40}$' -or $manifest.provenance.dirty -isnot [bool] -or $manifest.provenance.sdk -cne '10.0.400') { throw 'Missing source/build provenance.' }
if ($manifest.provenance.release -and ($manifest.provenance.dirty -or $manifest.provenance.tags -cnotcontains ('accounts-v' + $manifest.version))) { throw 'Release provenance must name a clean, version-tagged commit.' }

$entries = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    if ($relative -cmatch '\\' -or $relative -match '(^/|(^|/)\.\.?(/|$)|:)' -or [string]::IsNullOrWhiteSpace($relative) -or $relative -ieq 'package-manifest.json' -or $entry.sha256 -cnotmatch '^[0-9A-Fa-f]{64}$') { throw "Invalid manifest entry: $relative" }
    if (-not $entries.TryAdd($relative, $entry.sha256.ToLowerInvariant())) { throw "Duplicate manifest entry: $relative" }
}
if ($entries.Count -eq 0) { throw 'Package manifest has no payload.' }
$files = @(Get-ChildItem -LiteralPath $package -Recurse -Force)
if ($files | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Package cannot contain links or junctions.' }
$payload = @($files | Where-Object { -not $_.PSIsContainer -and $_.FullName -cne $manifestPath })
if ($payload.Count -ne $entries.Count) { throw 'Package and manifest file counts differ.' }
foreach ($file in $payload) {
    $relative = [IO.Path]::GetRelativePath($package,$file.FullName).Replace('\','/')
    if (-not $entries.ContainsKey($relative)) { throw "Unlisted package file: $relative" }
    if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entries[$relative]) { throw "Package file differs from manifest: $relative" }
    if ($relative -match '(^|/)(node_modules|test|tests|bin|obj|restore-[^/]*|credentials[^/]*|accounts\.json|config\.json|provider-enabled\.json)(/|$)' -or $file.Extension -in @('.asar','.dpapi','.vscdb','.db','.sqlite','.pdb','.pem','.pfx','.key') -or ($file.Extension -eq '.exe' -and $relative -cne 'component/Antigravity.Accounts.exe')) { throw "Excluded vendor, account or build file: $relative" }
    if ($relative -cnotmatch '^(component/Antigravity\.Accounts\.exe|README\.md|VALIDATION\.md|COMPATIBILITY_UPDATES\.md|LICENSE|(Prepare|Install-Preview|Restore|Start-With-Accounts)\.cmd|account-plugin/(plugin\.json|README\.md|assets/[^/]+|window\.cjs|native-oauth\.mjs|manage-install\.mjs|update-recovery\.mjs|recovery-and-launch\.mjs|native-fallback\.mjs|installer-lock\.mjs|Hold-InstallLock\.ps1|Update-Handoff\.ps1|Process-WorkingDirectory\.ps1)|hostauth-adapter/(src/[^/]+|scripts/[^/]+|README\.md|package\.json))$') { throw "Unexpected package path: $relative" }
    if ($file.Extension -eq '.md') {
        $body = Get-Content -LiteralPath $file.FullName -Raw
        if ($body -match '(?i)[A-Z]:[\\/]Users[\\/](?!<|\{|%|\$)[^\\/\s]+' -or $body -match '(?i)\b[A-Z0-9._%+-]+@(?!example\.(?:com|org|net|test)\b)[A-Z0-9.-]+\.[A-Z]{2,}\b') { throw "Personal email or machine-user path in package document: $relative" }
    }
}
foreach ($required in @('component/Antigravity.Accounts.exe','README.md','VALIDATION.md','LICENSE','Install-Preview.cmd','Restore.cmd','Start-With-Accounts.cmd','Prepare.cmd','account-plugin/plugin.json','hostauth-adapter/package.json','hostauth-adapter/src/descriptor.mjs')) {
    if (-not $entries.ContainsKey($required)) { throw "Missing package file: $required" }
}
foreach ($relative in @('account-plugin/plugin.json','hostauth-adapter/package.json')) {
    if ((Get-Content -LiteralPath (Join-Path $package $relative) -Raw | ConvertFrom-Json).version -cne $manifest.version) { throw "Component metadata versions differ: $relative" }
}
if ($ZipPath) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).ProviderPath)
    try {
        $expected = [Collections.Generic.Dictionary[string,string]]::new($entries,[StringComparer]::OrdinalIgnoreCase)
        $expected.Add('package-manifest.json',(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant())
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $prefix = [IO.Path]::GetFileName($package) + '/'
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\','/')
            if ($name.EndsWith('/')) { continue }
            if (-not $name.StartsWith($prefix,[StringComparison]::Ordinal)) { throw "Unexpected ZIP root: $name" }
            $relative = $name.Substring($prefix.Length)
            if (-not $seen.Add($relative) -or -not $expected.ContainsKey($relative)) { throw "Unexpected or duplicate ZIP entry: $relative" }
            $stream = $entry.Open()
            try { $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
            if ($digest -cne $expected[$relative]) { throw "ZIP entry differs from package: $relative" }
        }
        if ($seen.Count -ne $expected.Count) { throw 'ZIP is missing package files.' }
    } finally { $archive.Dispose() }
}
Write-Output ('Verified Accounts package: ' + $entries.Count + ' payload files; version ' + $manifest.version + $(if ($ZipPath) { '; ZIP contents match.' } else { '.' }))
