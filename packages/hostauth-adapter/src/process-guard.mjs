import { execFileSync } from 'node:child_process';
import { lstatSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const escaped = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

// Only the known external Node launcher is independent of the App archive.
// Command lines stay in this installer process; errors expose no arguments.
function nodeSidecarCandidates({ processes, installRoot, roamingRoot, userRoot, systemRoot, shimText }) {
  if (!Array.isArray(processes) || ![installRoot, roamingRoot, userRoot, systemRoot, shimText].every(value => typeof value === 'string' && value)) return [];
  const executable = path.join(installRoot, 'Antigravity.exe');
  const cmd = path.join(systemRoot, 'System32', 'cmd.exe');
  const shim = path.join(roamingRoot, 'Antigravity', 'bin', 'agy-node.cmd');
  const loader = path.join(userRoot, '.gemini', 'antigravity', 'lib', 'node_modules', 'sidecar_sdk', 'register_loader.js');
  const importArgument = '--import=' + pathToFileURL(loader).href;
  const lines = shimText.trim().replace(/\r\n/g, '\n').split('\n');
  if (lines.length !== 3 || lines[0] !== '@echo off' || lines[1] !== 'set ELECTRON_RUN_AS_NODE=1') return [];
  const shimInvocation = /^"([^"\r\n]+)" %\*$/.exec(lines[2]);
  // App startup may write a differently cased Windows path into its launcher.
  // Only that path is case-insensitive; commands, quoting and %* stay exact.
  if (!shimInvocation || shimInvocation[1].toLowerCase() !== executable.toLowerCase()) return [];
  const signatures = ['dist/server.cjs', 'main.mjs'].map(entrypoint => {
    const args = importArgument + ' ' + entrypoint;
    const invocation = '(?:"' + escaped(shim) + '"|' + escaped(shim) + ')\\s+' + escaped(args);
    // cmd /c may enclose the complete quoted invocation in one extra pair.
    return { entrypoint,
      childPattern: new RegExp('^"' + escaped(executable) + '"\\s+' + escaped(args) + '\\s*$', 'i'),
      parentPattern: new RegExp('^(?:"' + escaped(cmd) + '"|' + escaped(cmd) + ')\\s+/c\\s+(?:"' + invocation + '"|' + invocation + ')\\s*$', 'i'),
    };
  });
  const equal = (a, b) => typeof a === 'string' && a.toLowerCase() === b.toLowerCase();
  return processes.flatMap(entry => {
    if (!equal(entry.ExecutablePath, executable)) return [];
    const signature = signatures.find(item => item.childPattern.test(entry.CommandLine || ''));
    if (!signature) return [];
    const parent = processes.find(item => item.ProcessId === entry.ParentProcessId);
    return parent && equal(parent.ExecutablePath, cmd) && signature.parentPattern.test(parent.CommandLine || '')
      ? [{ entry, entrypoint: signature.entrypoint }] : [];
  });
}

function verifiedSwarmDirectory(entry, userRoot) {
  const expected = path.join(userRoot, '.gemini', 'antigravity', 'builtin', 'plugins', 'swarm', 'sidecars', 'swarm_board');
  if (typeof entry.CurrentDirectory !== 'string' || !path.isAbsolute(entry.CurrentDirectory) ||
      path.resolve(entry.CurrentDirectory).toLowerCase() !== path.resolve(expected).toLowerCase()) return false;
  try { return lstatSync(path.join(expected, 'main.mjs')).isFile(); }
  catch { return false; }
}

export function verifiedNodeSidecarIds(options) {
  return nodeSidecarCandidates(options).filter(({ entry, entrypoint }) =>
    entrypoint === 'dist/server.cjs' || verifiedSwarmDirectory(entry, options.userRoot)
  ).map(({ entry }) => entry.ProcessId);
}

function readProcessDirectories(shell, processIds) {
  if (processIds.some(id => !Number.isSafeInteger(id) || id <= 0 || id > 0x7fffffff)) return [];
  const command = `
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ProgressPreference = 'SilentlyContinue'
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
        int infoSize = Marshal.SizeOf(typeof(BasicInformation));
        if (NtQueryInformationProcess(process, 0, out info, infoSize, out returned) != 0 || returned != infoSize) return null;
        var parameters = Read(process, IntPtr.Add(info.Peb, 0x20), 8);
        if (parameters == null) return null;
        var directory = Read(process, new IntPtr(BitConverter.ToInt64(parameters, 0) + 0x38), 16);
        if (directory == null) return null;
        int length = BitConverter.ToUInt16(directory, 0);
        int maximumLength = BitConverter.ToUInt16(directory, 2);
        if (length == 0 || length > 32766 || length % 2 != 0 || length > maximumLength) return null;
        var text = Read(process, new IntPtr(BitConverter.ToInt64(directory, 8)), length);
        return text == null ? null : Encoding.Unicode.GetString(text);
      } finally { CloseHandle(process); }
    }
  }
}
'@
$items = foreach ($processId in @(${processIds.join(',')})) {
  $directory = $null
  try { $directory = [AntigravityAccounts.ProcessDirectoryReader]::ReadDirectory($processId) } catch {}
  [pscustomobject]@{ ProcessId = $processId; CurrentDirectory = $directory }
}
ConvertTo-Json -InputObject @($items) -Compress
`;
  // Query only the directory descriptor and its UTF-16 text for already
  // matched external sidecars. No environment block is read.
  const encoded = Buffer.from(command, 'utf16le').toString('base64');
  return JSON.parse(execFileSync(shell, ['-NoProfile', '-NonInteractive', '-EncodedCommand', encoded], {
    windowsHide: true, encoding: 'utf8', timeout: 15000,
  }));
}

function inspectSidecars(installRoot) {
  if (!path.isAbsolute(installRoot || '')) return [];
  try {
    const shell = path.join(process.env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
    // Read only Antigravity process metadata and their direct parents. Never
    // inspect the environment, native credential files or arbitrary processes.
    const command = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; $children=@(Get-CimInstance Win32_Process -Filter \"Name = 'Antigravity.exe'\"); $items=@($children); foreach($id in @($children.ParentProcessId | Select-Object -Unique)){ $items+=@(Get-CimInstance Win32_Process -Filter (\"ProcessId = \"+$id)) }; ConvertTo-Json -InputObject @($items | Select-Object ProcessId,ParentProcessId,ExecutablePath,CommandLine) -Compress";
    const processes = JSON.parse(execFileSync(shell, ['-NoProfile', '-NonInteractive', '-Command', command], { windowsHide: true, encoding: 'utf8', timeout: 15000 }));
    const roamingRoot = process.env.APPDATA, userRoot = process.env.USERPROFILE, systemRoot = process.env.SystemRoot;
    const shimText = readFileSync(path.join(roamingRoot, 'Antigravity', 'bin', 'agy-node.cmd'), 'utf8');
    const options = { processes, installRoot, roamingRoot, userRoot, systemRoot, shimText };
    const candidates = nodeSidecarCandidates(options).filter(item => item.entrypoint === 'main.mjs');
    if (candidates.length) {
      const directories = readProcessDirectories(shell, candidates.map(({ entry }) => entry.ProcessId));
      for (const { entry } of candidates) entry.CurrentDirectory = directories.find(item => item.ProcessId === entry.ProcessId)?.CurrentDirectory;
    }
    return verifiedNodeSidecarIds(options);
  } catch { return []; }
}

export function assertApplicationStopped({ tasklistText, ownPid = process.pid, isNodeSidecar = process.env.ELECTRON_RUN_AS_NODE === '1', installRoot, sidecarIds } = {}) {
  if (!Number.isSafeInteger(ownPid) || ownPid <= 0) throw new Error('Invalid installer process identifier');
  const inspect = tasklistText === undefined;
  if (tasklistText === undefined) {
    if (process.platform !== 'win32') throw new Error('The installation entry point only supports Windows');
    tasklistText = execFileSync('tasklist.exe', ['/FO', 'CSV', '/NH'], { windowsHide: true, encoding: 'utf8' });
  }
  if (typeof tasklistText !== 'string' || !tasklistText.trim()) throw new Error('Unable to determine whether Antigravity is closed');
  const rows = tasklistText.split(/\r?\n/).flatMap((line) => {
    const fields = line.match(/^"([^"]+)","([0-9]+)"/);
    return fields ? [fields] : [];
  });
  if (rows.length === 0) throw new Error('Unable to determine whether Antigravity is closed');
  const verified = new Set(sidecarIds || (inspect ? inspectSidecars(installRoot) : []));
  const otherProcesses = rows.some((fields) => {
    const name = fields[1].toLowerCase();
    if (name === 'antigravity.exe') return !(Number(fields[2]) === ownPid && isNodeSidecar === true) && !verified.has(Number(fields[2]));
    return /^language_server(?:_[a-z0-9_]+)?\.exe$/.test(name) || /^(?:antigravity|profilehub)\.accounts\.exe$/.test(name) || /^antigravity.*(?:setup|install|update).*\.exe$/.test(name);
  });
  if (otherProcesses) throw new Error('Exit all Antigravity, language-server and independent account-component processes before applying or restoring the adapter');
  return { processesStopped: true };
}
