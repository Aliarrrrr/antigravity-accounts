import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const escaped = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

// Only the known external Node launcher is independent of the App archive.
// Command lines stay in this installer process; errors expose no arguments.
export function verifiedNodeSidecarIds({ processes, installRoot, roamingRoot, userRoot, systemRoot, shimText }) {
  if (!Array.isArray(processes) || ![installRoot, roamingRoot, userRoot, systemRoot, shimText].every(value => typeof value === 'string' && value)) return [];
  const executable = path.join(installRoot, 'Antigravity.exe');
  const cmd = path.join(systemRoot, 'System32', 'cmd.exe');
  const shim = path.join(roamingRoot, 'Antigravity', 'bin', 'agy-node.cmd');
  const loader = path.join(userRoot, '.gemini', 'antigravity', 'lib', 'node_modules', 'sidecar_sdk', 'register_loader.js');
  const args = '--import=' + pathToFileURL(loader).href + ' dist/server.cjs';
  const expected = '@echo off\nset ELECTRON_RUN_AS_NODE=1\n"' + executable + '" %*';
  if (shimText.trim().replace(/\r\n/g, '\n') !== expected) return [];
  const childPattern = new RegExp('^"' + escaped(executable) + '"\\s+' + escaped(args) + '\\s*$', 'i');
  const parentPattern = new RegExp('^(?:"' + escaped(cmd) + '"|' + escaped(cmd) + ')\\s+/c\\s+(?:"' + escaped(shim) + '"|' + escaped(shim) + ')\\s+' + escaped(args) + '\\s*$', 'i');
  const equal = (a, b) => typeof a === 'string' && a.toLowerCase() === b.toLowerCase();
  return processes.filter(entry => {
    if (!equal(entry.ExecutablePath, executable) || !childPattern.test(entry.CommandLine || '')) return false;
    const parent = processes.find(item => item.ProcessId === entry.ParentProcessId);
    return parent && equal(parent.ExecutablePath, cmd) && parentPattern.test(parent.CommandLine || '');
  }).map(entry => entry.ProcessId);
}

function inspectSidecars(installRoot) {
  if (!path.isAbsolute(installRoot || '')) return [];
  try {
    const shell = path.join(process.env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
    // Read only Antigravity process metadata and their direct parents. Never
    // inspect the environment, native credential files or arbitrary processes.
    const command = "$children=@(Get-CimInstance Win32_Process -Filter \"Name = 'Antigravity.exe'\"); $items=@($children); foreach($id in @($children.ParentProcessId | Select-Object -Unique)){ $items+=@(Get-CimInstance Win32_Process -Filter (\"ProcessId = \"+$id)) }; ConvertTo-Json -InputObject @($items | Select-Object ProcessId,ParentProcessId,ExecutablePath,CommandLine) -Compress";
    const processes = JSON.parse(execFileSync(shell, ['-NoProfile', '-NonInteractive', '-Command', command], { windowsHide: true, encoding: 'utf8', timeout: 15000 }));
    const roamingRoot = process.env.APPDATA, userRoot = process.env.USERPROFILE, systemRoot = process.env.SystemRoot;
    const shimText = readFileSync(path.join(roamingRoot, 'Antigravity', 'bin', 'agy-node.cmd'), 'utf8');
    return verifiedNodeSidecarIds({ processes, installRoot, roamingRoot, userRoot, systemRoot, shimText });
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
