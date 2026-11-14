import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
const run = promisify(execFile);
const shell = () => path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
export async function notifyUpdate(errorCode) {
  if (process.platform !== 'win32') return;
  if (!/^[a-z][a-z0-9_]{0,90}$/.test(errorCode)) throw new Error('compatibility_notice_invalid');
  await run(shell(), ['-NoLogo', '-NoProfile', '-NonInteractive', '-File', fileURLToPath(new URL('./notify-update.ps1', import.meta.url)), '-ErrorCode', errorCode], { windowsHide: true, timeout: 15000, maxBuffer: 65536 });
}
export async function reconcileWindows(stateRoot) {
  if (process.platform !== 'win32') return;
  await run(shell(), ['-NoLogo', '-NoProfile', '-NonInteractive', '-File', fileURLToPath(new URL('./reconcile-windows.ps1', import.meta.url)), '-StateRoot', stateRoot], { windowsHide: true, timeout: 15000, maxBuffer: 65536 });
}
