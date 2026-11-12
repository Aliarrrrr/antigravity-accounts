function Get-ProcessWorkingDirectory([int]$ProcessId) {
  try {
    if (-not ('AntigravityAccounts.ProcessDirectoryReader' -as [type])) {
      Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace AntigravityAccounts {
  public static class ProcessDirectoryReader {
    [StructLayout(LayoutKind.Sequential)]
    struct BasicInformation {
      public IntPtr Reserved1, Peb, Reserved2, Reserved3, ProcessId, Reserved4;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool IsWow64Process(IntPtr process, out bool wow64);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);
    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int informationClass, out BasicInformation information, int size, out int returned);

    static byte[] Read(IntPtr process, IntPtr address, int size) {
      var bytes = new byte[size];
      IntPtr read;
      return ReadProcessMemory(process, address, bytes, size, out read) && read.ToInt64() == size ? bytes : null;
    }

    public static string ReadDirectory(int processId) {
      if (IntPtr.Size != 8 || processId <= 0) return null;
      var process = OpenProcess(0x0410, false, processId);
      if (process == IntPtr.Zero) return null;
      try {
        bool wow64;
        if (!IsWow64Process(process, out wow64) || wow64) return null;
        BasicInformation info;
        int returned;
        int size = Marshal.SizeOf(typeof(BasicInformation));
        if (NtQueryInformationProcess(process, 0, out info, size, out returned) != 0 || returned != size) return null;
        var parameters = Read(process, IntPtr.Add(info.Peb, 0x20), 8);
        if (parameters == null) return null;
        var directory = Read(process, new IntPtr(BitConverter.ToInt64(parameters, 0) + 0x38), 16);
        if (directory == null) return null;
        int length = BitConverter.ToUInt16(directory, 0);
        int maximumLength = BitConverter.ToUInt16(directory, 2);
        if (length == 0 || length > maximumLength || length > 32766 || length % 2 != 0) return null;
        var text = Read(process, new IntPtr(BitConverter.ToInt64(directory, 8)), length);
        return text == null ? null : Encoding.Unicode.GetString(text);
      } finally { CloseHandle(process); }
    }
  }
}
'@
    }
    # Only the current-directory descriptor and its UTF-16 path are read.
    # The environment block and other process parameters are never inspected.
    return [AntigravityAccounts.ProcessDirectoryReader]::ReadDirectory($ProcessId)
  } catch { return $null }
}

function Get-AccountsNodeSidecarIds($Processes, [string]$InstallRoot,
  [scriptblock]$WorkingDirectoryReader = { param($ProcessId) Get-ProcessWorkingDirectory $ProcessId }) {
  $executable = Join-Path $InstallRoot 'Antigravity.exe'
  $cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
  $shim = Join-Path $env:APPDATA 'Antigravity\bin\agy-node.cmd'
  $loader = Join-Path $env:USERPROFILE '.gemini\antigravity\lib\node_modules\sidecar_sdk\register_loader.js'
  $argumentPrefix = '--import=' + ([Uri]::new($loader)).AbsoluteUri + ' '
  try { $shimLines = [IO.File]::ReadAllText($shim).Trim().Replace("`r`n", "`n").Split("`n") } catch { return }
  if ($shimLines.Count -ne 3 -or $shimLines[0] -cne '@echo off' -or
      $shimLines[1] -cne 'set ELECTRON_RUN_AS_NODE=1' -or
      $shimLines[2] -ine ('"' + $executable + '" %*')) { return }
  foreach ($entry in $Processes) {
    $arguments = $argumentPrefix + 'dist/server.cjs'
    $isSwarmBoard = $entry.CommandLine -match ('^"' + [regex]::Escape($executable) + '"\s+' + [regex]::Escape($argumentPrefix + 'main.mjs') + '\s*$')
    if ($isSwarmBoard) { $arguments = $argumentPrefix + 'main.mjs' }
    $childPattern = '^"' + [regex]::Escape($executable) + '"\s+' + [regex]::Escape($arguments) + '\s*$'
    if ($entry.ExecutablePath -ine $executable -or $entry.CommandLine -notmatch $childPattern) { continue }
    $parent = $Processes | Where-Object ProcessId -eq $entry.ParentProcessId | Select-Object -First 1
    $invocation = '(?:"' + [regex]::Escape($shim) + '"|' + [regex]::Escape($shim) + ')\s+' + [regex]::Escape($arguments)
    $parentPattern = '^(?:"' + [regex]::Escape($cmd) + '"|' + [regex]::Escape($cmd) + ')\s+/c\s+(?:"' + $invocation + '"|' + $invocation + ')\s*$'
    if (-not $parent -or $parent.ExecutablePath -ine $cmd -or $parent.CommandLine -notmatch $parentPattern) { continue }
    if ($isSwarmBoard) {
      $boardRoot = Join-Path $env:USERPROFILE '.gemini\antigravity\builtin\plugins\swarm\sidecars\swarm_board'
      try {
        $directory = & $WorkingDirectoryReader ([int]$entry.ProcessId)
        if (-not $directory -or [IO.Path]::GetFullPath($directory).TrimEnd('\') -ine $boardRoot) { continue }
        $main = Get-Item -LiteralPath (Join-Path $boardRoot 'main.mjs') -Force -ErrorAction Stop
        if ($main.PSIsContainer -or ($main.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
      } catch { continue }
    }
    [int]$entry.ProcessId
  }
}
