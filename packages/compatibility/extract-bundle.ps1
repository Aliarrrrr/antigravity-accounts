param([Parameter(Mandatory)][string]$ZipPath, [Parameter(Mandatory)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if (Test-Path -LiteralPath $root) { throw 'compatibility_extract_target_exists' }
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ZipPath))
try {
  $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  [long]$total = 0
  foreach ($entry in $archive.Entries) {
    $name = $entry.FullName
    if ($name.Contains('\') -or -not $name.StartsWith('AntigravityCompatibility/', [StringComparison]::Ordinal) -or $name -match '[:\x00-\x1f\x7f]') { throw 'compatibility_zip_path_invalid' }
    $parts = $name.TrimEnd('/').Split('/')
    foreach ($part in $parts) { if (-not $part -or $part -in @('.','..') -or $part -match '[. ]$' -or $part -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)') { throw 'compatibility_zip_path_invalid' } }
    if (-not $seen.Add($name.TrimEnd('/')) -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'compatibility_zip_duplicate_or_link' }
    $total += $entry.Length
    if ($total -gt 536870912 -or $entry.Length -gt 268435456 -or $seen.Count -gt 25000) { throw 'compatibility_zip_too_large' }
    $target = [IO.Path]::GetFullPath((Join-Path $root $name))
    if (-not $target.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'compatibility_zip_path_invalid' }
  }
  New-Item -ItemType Directory -Path $root | Out-Null
  foreach ($entry in $archive.Entries) {
    $target = Join-Path $root $entry.FullName
    if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Path $target -Force | Out-Null; continue }
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
  }
} finally { $archive.Dispose() }
