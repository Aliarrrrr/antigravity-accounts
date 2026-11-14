param(
  [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\antigravity'),
  [string]$ToolkitRoot = (Split-Path $PSScriptRoot -Parent),
  [string]$NodePath = 'D:\Node.js\node.exe',
  [string]$StateRoot,
  [string]$ArgumentsJsonBase64 = 'W10=',
  [switch]$NoPopup
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
if (-not $StateRoot) { $StateRoot = Join-Path $ToolkitRoot 'state' }
$statusPath = Join-Path ([IO.Path]::GetFullPath($StateRoot)) 'status.json'

# This path uses only Windows/.NET so a missing Node runtime or a failed module
# import cannot turn a complete application installation into a dead shortcut.
function Start-WithoutLocalization([string]$reason) {
  $root = (Get-Item -LiteralPath ([IO.Path]::GetFullPath($InstallRoot)) -Force).FullName.TrimEnd('\')
  $state = [IO.Path]::GetFullPath($StateRoot).TrimEnd('\')
  if ($state -ieq $root -or $state.StartsWith($root+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'state_must_be_outside_installation' }
  New-Item -ItemType Directory -Path $state -Force | Out-Null
  $state = (Get-Item -LiteralPath $state -Force).FullName.TrimEnd('\')
  $handles = [Collections.Generic.List[IDisposable]]::new()
  $locks = [Collections.Generic.List[Threading.Mutex]]::new()
  $algorithm = [Security.Cryptography.SHA256]::Create()
  $utf8 = [Text.UTF8Encoding]::new($false, $true)
  function Assert-Regular([string]$file, [bool]$directory = $false) {
    $item = Get-Item -LiteralPath $file -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.PSIsContainer -ne $directory) { throw 'unsafe_launch_path' }
    return $item
  }
  function Open-ReadLocked([string]$file) {
    [void](Assert-Regular $file)
    $stream = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $handles.Add($stream)
    return $stream
  }
  function Hash-Bytes([byte[]]$bytes) { return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '') }
  function Enter-LaunchLock([string]$directory, [string]$name) {
    $identity = ($directory + [char]0 + $name).ToLowerInvariant()
    $suffix = (Hash-Bytes ([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
    $mutex = [Threading.Mutex]::new($false, ('Local\AntigravityZhCN-' + $suffix))
    $held = $false
    try {
      try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
      if (-not $held) { throw 'launch_operation_busy' }
      $locks.Add($mutex)
    } finally { if (-not $held) { $mutex.Dispose() } }
  }
  function Enter-AccountsLock([string]$directory) {
    $suffix = (Hash-Bytes ([Text.Encoding]::UTF8.GetBytes($directory.ToLowerInvariant()))).ToLowerInvariant()
    $mutex = [Threading.Mutex]::new($false, ('Local\AntigravityAccounts-Install-' + $suffix))
    $held = $false
    try {
      try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
      if (-not $held) { throw 'accounts_installation_busy' }
      $locks.Add($mutex)
    } finally { if (-not $held) { $mutex.Dispose() } }
  }
  function Assert-NoUpdater {
    $probe = Join-Path $PSScriptRoot 'Maintenance-State.ps1'
    if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw 'maintenance_check_missing' }
    $maintenanceJson = ((& $probe -InstallRoot $root) | Out-String).Trim()
    if ($maintenanceJson -notmatch '(?s)^\[.*\]$') { throw 'invalid_maintenance_state' }
    $busy = ConvertFrom-Json -InputObject $maintenanceJson
    foreach ($entry in $busy) {
      if ($entry -and $entry.kind -ne 'application') { throw 'application_update_in_progress' }
    }
  }
  function Native-Argument([string]$value) {
    $result = '"'; $slashes = 0
    foreach ($character in $value.ToCharArray()) {
      if ($character -eq '\') { $slashes++; continue }
      if ($character -eq '"') { $result += ('\' * (2 * $slashes + 1)) + '"' }
      else { $result += ('\' * $slashes) + $character }
      $slashes = 0
    }
    return $result + ('\' * (2 * $slashes)) + '"'
  }
  function Read-AccountsMetadata([string]$file) {
    try {
      $stream = Open-ReadLocked $file
      if ($stream.Length -gt 131072) { throw 'accounts_recovery_location_invalid' }
      $reader = [IO.StreamReader]::new($stream, $utf8, $true, 4096, $true)
      try { $value = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
      if ($value -isnot [pscustomobject]) { throw 'accounts_recovery_location_invalid' }
      return $value
    } catch { throw 'accounts_recovery_location_invalid' }
  }
  function Absolute-AccountsPath($value) {
    if ($value -isnot [string] -or $value.Contains([string][char]0) -or
        ($value -notmatch '^[A-Za-z]:[\\/]' -and $value -notmatch '^\\\\[^\\/]+[\\/][^\\/]+(?:[\\/]|$)')) {
      throw 'accounts_recovery_location_invalid'
    }
    try {
      $absolute = [IO.Path]::GetFullPath($value)
      if ($absolute -ieq [IO.Path]::GetPathRoot($absolute)) { return $absolute }
      return $absolute.TrimEnd('\')
    }
    catch { throw 'accounts_recovery_location_invalid' }
  }
  function Canonical-AccountsStatePath($value) {
    $directory = Absolute-AccountsPath $value
    if ($directory -ieq $root -or $directory.StartsWith($root+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'accounts_recovery_location_invalid' }
    try {
      $directory = Absolute-AccountsPath (Assert-Regular $directory $true).FullName
      $ancestor = $directory
      while ($ancestor) {
        [void](Assert-Regular $ancestor $true)
        $parentDirectory = [IO.Directory]::GetParent($ancestor)
        if (-not $parentDirectory -or $parentDirectory.FullName -ieq $ancestor) { break }
        $ancestor = $parentDirectory.FullName
      }
    } catch { throw 'accounts_recovery_location_invalid' }
    if ($directory -ieq $root -or $directory.StartsWith($root+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'accounts_recovery_location_invalid' }
    return $directory
  }
  function Accounts-StateRoot {
    $configRoot = $null; $hintRoot = $null
    $configFile = Join-Path $resources 'hostauth-addon\config.json'
    if (Test-Path -LiteralPath $configFile) {
      $config = Read-AccountsMetadata $configFile
      if ($config.PSObject.Properties['accountsStateRoot']) { $configRoot = Canonical-AccountsStatePath $config.accountsStateRoot }
    }
    $hintFile = Join-Path $state 'accounts-installation.json'
    if (Test-Path -LiteralPath $hintFile) {
      $hint = Read-AccountsMetadata $hintFile
      if (($hint.schemaVersion -isnot [int] -and $hint.schemaVersion -isnot [long]) -or $hint.schemaVersion -ne 1 -or
          (Absolute-AccountsPath $hint.installRoot) -ine $root) { throw 'accounts_recovery_location_invalid' }
      $hintRoot = Canonical-AccountsStatePath $hint.accountsStateRoot
    }
    if ($configRoot -and $hintRoot -and $configRoot -ine $hintRoot) { throw 'accounts_recovery_location_conflict' }
    $explicit = [bool]($configRoot -or $hintRoot)
    $directory = if ($configRoot) { $configRoot } elseif ($hintRoot) { $hintRoot } else { Join-Path $env:LOCALAPPDATA 'AntigravityAccounts' }
    return @{directory=$directory;explicit=$explicit}
  }
  function Assert-CompleteNativeImage([IO.Stream]$stream) {
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
    $stream.Position = 0
    if ($stream.Length -lt 512 -or $reader.ReadUInt16() -ne 0x5A4D) { throw 'accounts_repair_required: native_program_incomplete' }
    $stream.Position = 60; $pe = $reader.ReadUInt32()
    if ($pe -lt 64 -or $pe -gt $stream.Length - 264) { throw 'accounts_repair_required: native_program_incomplete' }
    $stream.Position = $pe
    if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'accounts_repair_required: native_program_incomplete' }
    $count = $reader.ReadUInt16()
    $stream.Position = $pe + 20; $optional = $reader.ReadUInt16()
    $stream.Position = $pe + 24
    if ($reader.ReadUInt16() -ne 0x20B -or $count -lt 1 -or $count -gt 96 -or $optional -lt 240 -or $pe + 24 + $optional + $count * 40 -gt $stream.Length) { throw 'accounts_repair_required: native_program_incomplete' }
    for ($index = 0; $index -lt $count; $index++) {
      $stream.Position = $pe + 24 + $optional + $index * 40 + 16
      $size = $reader.ReadUInt32(); $offset = $reader.ReadUInt32()
      if ([long]$offset + $size -gt $stream.Length) { throw 'accounts_repair_required: native_program_incomplete' }
    }
    $stream.Position = $pe + 24 + 144
    $certificate = $reader.ReadUInt32(); $certificateSize = $reader.ReadUInt32()
    if ([long]$certificate + $certificateSize -gt $stream.Length) { throw 'accounts_repair_required: native_program_incomplete' }
  }
  function Confirm-NativeMode {
    # NoPopup suppresses duplicate error dialogs from the VBS wrapper, not the
    # mandatory account-identity choice needed before a native-mode launch.
    Add-Type -AssemblyName System.Windows.Forms
    $message = '账号插件暂时无法适配当前更新。继续将使用 Antigravity 原生登录，可能与之前选择的额度账号不同。原账号授权仍保留，兼容恢复后可以继续使用。' + "`r`n`r`n" + '使用原生登录继续？'
    return [System.Windows.Forms.MessageBox]::Show($message, 'Antigravity 兼容模式', [System.Windows.Forms.MessageBoxButtons]::OKCancel,
      [System.Windows.Forms.MessageBoxIcon]::Information, [System.Windows.Forms.MessageBoxDefaultButton]::Button2) -eq [System.Windows.Forms.DialogResult]::OK
  }
  try {
    [void](Assert-Regular $root $true)
    [void](Assert-Regular $state $true)
    $resources = Join-Path $root 'resources'
    [void](Assert-Regular $resources $true)
    Enter-LaunchLock $state 'repair.lock'
    Enter-AccountsLock $resources
    $accountsReceipt = $null
    $accountsLocation = Accounts-StateRoot
    $accountsReceiptFile = Join-Path $accountsLocation.directory 'adapter-installation.json'
    if (Test-Path -LiteralPath $accountsReceiptFile) {
      [void](Assert-Regular $accountsReceiptFile)
      $candidateReceipt = Read-AccountsMetadata $accountsReceiptFile
      if ($accountsLocation.explicit -and ($candidateReceipt.resourcesRoot -ine $resources -or -not $candidateReceipt.packageRoot)) { throw 'accounts_recovery_receipt_invalid' }
      if ($candidateReceipt.resourcesRoot -ieq $resources -and $candidateReceipt.packageRoot -and $candidateReceipt.phase -ne 'restored') {
        if ($candidateReceipt.schemaVersion -ne 1 -or $candidateReceipt.phase -ne 'applied') { throw 'accounts_recovery_receipt_invalid' }
        $accountsReceipt = $candidateReceipt
      }
    } elseif ($accountsLocation.explicit) { throw 'accounts_repair_required' }
    Enter-LaunchLock $resources '.antigravity-zh-cn.lock'
    Assert-NoUpdater
    $argumentsJson = $utf8.GetString([Convert]::FromBase64String($ArgumentsJsonBase64))
    if ($argumentsJson.Trim() -notmatch '(?s)^\[.*\]$') { throw 'invalid_launch_arguments' }
    $arguments = ConvertFrom-Json -InputObject $argumentsJson
    foreach ($argument in $arguments) { if ($argument -isnot [string] -or $argument.Contains([string][char]0)) { throw 'invalid_launch_arguments' } }
    $executable = Open-ReadLocked (Join-Path $root 'Antigravity.exe')
    $reader = [IO.BinaryReader]::new($executable)
    if ($executable.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw 'invalid_application_executable' }
    $executable.Position = 60; $peOffset = $reader.ReadUInt32()
    if ($peOffset -gt $executable.Length - 24) { throw 'invalid_application_executable' }
    $executable.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x4550) { throw 'invalid_application_executable' }
    $archive = Open-ReadLocked (Join-Path $resources 'app.asar')
    if ($archive.Length -lt 16 -or $archive.Length -gt 268435456) { throw 'invalid_application_archive' }
    $bytes = [byte[]]::new([int]$archive.Length)
    $total = 0
    while ($total -lt $bytes.Length) {
      $read = $archive.Read($bytes, $total, $bytes.Length - $total)
      if ($read -eq 0) { throw 'incomplete_application_archive' }
      $total += $read
    }
    $headerSize = [BitConverter]::ToUInt32($bytes, 4)
    $jsonSize = [BitConverter]::ToUInt32($bytes, 12)
    if ([BitConverter]::ToUInt32($bytes, 0) -ne 4 -or $headerSize -lt 8 -or $headerSize -gt $bytes.Length - 8 -or [BitConverter]::ToUInt32($bytes, 8) -ne $headerSize - 4 -or $jsonSize -gt $headerSize - 8) { throw 'invalid_application_archive' }
    $header = $utf8.GetString($bytes, 16, $jsonSize) | ConvertFrom-Json
    $dataOffset = 8L + $headerSize
    $selected = @{}; $unpacked = @{}; $localized = $false
    function Read-ArchiveDirectory($directory, [string]$prefix = '') {
      if (-not $directory.files -or $directory.files -isnot [pscustomobject]) { throw 'invalid_archive_directory' }
      foreach ($property in $directory.files.PSObject.Properties) {
        if (-not $property.Name -or $property.Name -match '[/\\:]' -or $property.Name -in @('.', '..')) { throw 'unsafe_archive_path' }
        $name = $prefix + $property.Name; $entry = $property.Value
        if ($entry.files) { Read-ArchiveDirectory $entry ($name + '/'); continue }
        if ($entry.link -or $null -eq $entry.size -or $entry.size -lt 0 -or $entry.size -ne [Math]::Floor($entry.size)) { throw 'invalid_archive_entry' }
        $content = $null
        if ($entry.unpacked) {
          $file = Join-Path ($resources + '\app.asar.unpacked') $name.Replace('/', '\')
          $parent = Split-Path $file -Parent
          while ($parent.Length -gt $resources.Length) { [void](Assert-Regular $parent $true); $parent = Split-Path $parent -Parent }
          $stream = Open-ReadLocked $file
          if ($stream.Length -ne $entry.size) { throw 'incomplete_unpacked_file' }
          $hash = ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '')
          $unpacked[$name] = $hash
          if ($name -eq 'package.json' -or $name -like 'dist/*.js') {
            if ($stream.Length -gt 268435456) { throw 'invalid_application_archive' }
            $stream.Position = 0
            $contentReader = [IO.StreamReader]::new($stream, $utf8, $true, 4096, $true)
            try { $content = $contentReader.ReadToEnd() } finally { $contentReader.Dispose() }
          }
        } else {
          if ($entry.offset -isnot [string] -or $entry.offset -notmatch '^\d+$' -or [decimal]$entry.offset + [decimal]$entry.size -gt $bytes.Length - $dataOffset) { throw 'incomplete_archive_entry' }
          $offset = $dataOffset + [long]$entry.offset
          $hash = ([BitConverter]::ToString($algorithm.ComputeHash($bytes, [int]$offset, [int]$entry.size))).Replace('-', '')
          if ($name -eq 'package.json' -or $name -like 'dist/*.js') { $content = $utf8.GetString($bytes, [int]$offset, [int]$entry.size) }
        }
        if ($name -in @('package.json', 'dist/main.js', 'dist/languageServer.js', 'dist/hostBridgeServer.js')) { $selected[$name] = $content }
        if ($name.StartsWith('dist/hostauth-addon/') -or ($name -in @('dist/main.js','dist/languageServer.js','dist/hostBridgeServer.js') -and $content -match 'hostauthAddon|hostauthSession|accountsUpdateHandoff|hostauth-addon|--use_host_auth=true')) { $selected.accountsInjected = $true }
        if ($name -like 'dist/*.js' -and $content -match 'ANTIGRAVITY_ZH_CN_NATIVE_V1|__antigravityZhCN') { $selected.localized = $true }
        if ($entry.integrity -and ($entry.integrity.algorithm -ne 'SHA256' -or $entry.integrity.hash -ine $hash)) { throw 'archive_content_changed' }
      }
    }
    Read-ArchiveDirectory $header
    if (-not $selected['package.json'] -or -not $selected['dist/main.js']) { throw 'application_entry_missing' }
    $metadata = $selected['package.json'] | ConvertFrom-Json
    if ($metadata.name -ne 'antigravity' -or $metadata.main -ne 'dist/main.js' -or $metadata.version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.+-]+)?$') { throw 'wrong_application' }
    $localized = [bool]$selected.localized
    if ($localized) {
      $recordFile = Join-Path $resources 'antigravity-zh-cn.install.json'
      [void](Assert-Regular $recordFile)
      $record = [IO.File]::ReadAllText($recordFile, $utf8) | ConvertFrom-Json
      $executable.Position = 0
      $executableHash = ([BitConverter]::ToString($algorithm.ComputeHash($executable))).Replace('-', '')
      if ($record.schemaVersion -ne 1 -or $record.installRoot -ine $root -or $record.appVersion -ne $metadata.version -or $record.patchedSha256 -ine (Hash-Bytes $bytes) -or $record.executableSha256 -ine $executableHash -or @($record.unpacked.PSObject.Properties).Count -ne $unpacked.Count) { throw 'unverified_localized_installation' }
      foreach ($name in $unpacked.Keys) { if ($record.unpacked.$name -ine $unpacked[$name]) { throw 'unverified_localized_installation' } }
    }
    $nativeMode = $false
    if ($accountsReceipt) {
      $accountArchiveMatches = $accountsReceipt.patchedAsarSha256 -ieq (Hash-Bytes $bytes)
      if ($localized -and $record.sourceSha256 -ieq $accountsReceipt.patchedAsarSha256) { $accountArchiveMatches = $true }
      try { $nativeProgram = Open-ReadLocked (Join-Path $resources 'bin\language_server.exe') }
      catch { throw 'accounts_repair_required: native_program_missing' }
      $nativeDigest = ([BitConverter]::ToString($algorithm.ComputeHash($nativeProgram))).Replace('-', '')
      if (-not $accountArchiveMatches -or $nativeDigest -ine $accountsReceipt.patchedNativeSha256) {
        foreach ($key in @('originalAsarSha256','patchedAsarSha256','originalNativeSha256','patchedNativeSha256')) {
          if ($accountsReceipt.$key -isnot [string] -or $accountsReceipt.$key -notmatch '^[A-Fa-f0-9]{64}$') { throw 'accounts_repair_required: incomplete_receipt' }
        }
        $adaptedNativeDigests = @(
          '8ab537e29e484b7f31e97d1070e4726bc2e4d45180e7afa11d683057d8a5e693',
          '9728f6eb169fd63a7b154934c7151932152fcc93ca97720117237218df907490',
          '319c1abd18b4d12ac2149473f04b07be8671d230ecf12adf4cb7d1e3fe85edf6',
          'b02168614d75b680cd8b0814b0d01bf91c66f5e310ff8d772516b920c8813b5f',
          '33bad001301bf863bb7108e77667aaf24c649981335f5d220656c75e59818508',
          'ff323094c41c50508bd4b9d8cc66230c36d2c245df68a0216010603725aa866c',
          '0bdb872c8092002dafdf8ef44f1b3da77c2b71ef09eaec47ea169911766b2a2b',
          '39b06d761fbfa7f83090b7531b1e64c062150ba366cd97233ffc86bee3352677',
          'ff0bfbb49bd798cd07d6add6f7eda235605285fa5ac25731f86d26e30ca7ff6e',
          '169355a713b8199f6fe91dd5eaf81bc7f366544b2808014425fc820ccdd04bfc',
          '3269e7df7f3b34aba7b087c6ea8055163c4ace072c72ed376314775439b7453c',
          '9a58d7ec73af4da1f60b9682d6632ed54a9f62dd2752372c8f53f8cbe116eb85',
          '684a9ea0bbba226335feac69f9fc45d27980522fe172979fd58de07f35c52441',
          'd728fa9d5ce8eb69a32acbb3b820d25b1c8df901d1cee435c2c2c13963f2871b',
          'c132ea0fc1688dfd614cae5ac5c47b89c6c36753375981a0600170cef896dfea'
        )
        if ($selected.accountsInjected -or -not $selected['dist/languageServer.js'] -or -not $selected['dist/hostBridgeServer.js'] -or $nativeDigest -ieq $accountsReceipt.patchedNativeSha256 -or $adaptedNativeDigests -icontains $nativeDigest) { throw 'accounts_repair_required: partial_update' }
        Assert-CompleteNativeImage $executable
        Assert-CompleteNativeImage $nativeProgram
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'Antigravity.exe'))
        $baseVersion = @($version.FileMajorPart,$version.FileMinorPart,$version.FileBuildPart) -join '.'
        if ($baseVersion -cne ($metadata.version -split '[-+]')[0]) { throw 'accounts_repair_required: application_version_mismatch' }
        if (-not (Confirm-NativeMode)) { throw 'accounts_native_start_cancelled' }
        $nativeMode = $true
      }
    }
    Assert-NoUpdater
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $root 'Antigravity.exe'; $start.WorkingDirectory = $root
    $start.UseShellExecute = $false; $start.Arguments = (($arguments | ForEach-Object { Native-Argument $_ }) -join ' ')
    $child = [Diagnostics.Process]::Start($start)
    if (-not $child) { throw 'application_start_failed' }
    $child.Dispose()
    try {
      $status = @{status='degraded';launched=$true;localization=$(if ($localized) { 'stale' } else { 'unavailable' });appVersion=$metadata.version;reason=$reason;checkedUtc=[DateTime]::UtcNow.ToString('o')}
      if ($nativeMode) { $status.accounts='unavailable'; $status.mode='native'; $status.credentialFilesRead=$false }
      $stage = $statusPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
      [IO.File]::WriteAllText($stage, ($status | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
      Move-Item -LiteralPath $stage -Destination $statusPath -Force
    } catch {}
  } finally {
    foreach ($handle in $handles) { $handle.Dispose() }
    foreach ($mutex in $locks) { $mutex.ReleaseMutex(); $mutex.Dispose() }
    $algorithm.Dispose()
  }
}

$guardExit = $null
try {
  foreach ($file in @($NodePath, (Join-Path $ToolkitRoot 'durable.mjs'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'guard_runtime_missing' }
  }
  try {
    $ErrorActionPreference = 'Continue'
    & $NodePath (Join-Path $ToolkitRoot 'durable.mjs') launch --install-root $InstallRoot --state-root $StateRoot --args-base64 $ArgumentsJsonBase64 2>$null | Out-Null
    $guardExit = $LASTEXITCODE
  } finally { $ErrorActionPreference = 'Stop' }
  if ($LASTEXITCODE -ne 0) { throw ('guard_exit_' + $LASTEXITCODE) }
  exit 0
} catch {
  $failure = $_.Exception.Message
  # Exit 4 is an explicit refusal after application validation. It must never be
  # bypassed by the bootstrap recovery path.
  if ($guardExit -ne 4) {
    try { Start-WithoutLocalization $failure; exit 0 }
    catch {
      $failure += '; ' + $_.Exception.Message
      try {
        $previousStatus = $null
        if (Test-Path -LiteralPath $statusPath -PathType Leaf) { $previousStatus = ([IO.File]::ReadAllText($statusPath) | ConvertFrom-Json).status }
        $refusal = @{status='error';error=('bootstrap_launch_refused: '+$failure);previousStatus=$previousStatus;checkedUtc=[DateTime]::UtcNow.ToString('o')}
        $stage = $statusPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        [IO.File]::WriteAllText($stage, ($refusal | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $stage -Destination $statusPath -Force
      } catch {}
    }
  }
  $code = if ($LASTEXITCODE) { [string]$LASTEXITCODE } else { 'runtime_error' }
  $instruction = '若应用正在运行，请先保存工作并正常退出全部 Antigravity 窗口后重新打开；若关闭窗口后仍在后台运行，请在系统托盘菜单选择“退出”，然后重新打开；若新版不兼容，请查看记录中的原因。'
  try {
    $status = [IO.File]::ReadAllText($statusPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($status.status -eq 'pending_idle' -or $status.previousStatus -eq 'pending_idle') { $instruction = '请先保存工作，并正常退出全部 Antigravity 窗口后重新打开。若关闭窗口后仍在后台运行，请在系统托盘菜单选择“退出”，然后重新打开。' }
    elseif ($status.status -eq 'pending_update' -or $status.previousStatus -eq 'pending_update') { $instruction = '应用正在更新，请等待更新完成后重新打开。' }
    elseif ($status.status -eq 'error') { $instruction = '当前汉化检查失败；若新版不兼容，请查看记录中的原因。' }
  } catch {}
  $message = '无法完成汉化或启动 Antigravity（错误代码 ' + $code + '）。' + $instruction + "`r`n原因：" + $failure + "`r`n检查记录：" + $statusPath
  [Console]::Error.WriteLine($message)
  if (-not $NoPopup) {
    try { $shell = New-Object -ComObject WScript.Shell; $shell.Popup($message, 0, 'Antigravity 简体中文', 16) | Out-Null } catch {}
  }
  exit 1
}
