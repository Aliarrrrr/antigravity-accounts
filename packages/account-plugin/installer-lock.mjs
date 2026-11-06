import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { realpath } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// The lock belongs to the installation, not its selectable state directory.
// All manual and automatic Accounts writers use this same kernel mutex.
export async function acquireInstallationLock(resourcesRoot) {
  if (process.platform !== 'win32') throw new Error('The installation lock only supports Windows');
  resourcesRoot = await realpath(resourcesRoot);
  const identity = resourcesRoot.toLowerCase();
  const mutexName = 'Local\\AntigravityAccounts-Install-' + createHash('sha256').update(identity).digest('hex');
  const helper = fileURLToPath(new URL('./Hold-InstallLock.ps1', import.meta.url));
  const powershell = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  const child = spawn(powershell, ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
    '-File', helper, '-MutexName', mutexName], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  child.stdin.on('error', () => {});
  child.stderr.resume();
  let exited = false;
  const closed = new Promise(resolve => {
    child.once('error', error => { exited = true; resolve({ error }); });
    child.once('close', code => { exited = true; resolve({ code }); });
  });
  const answer = new Promise((resolve, reject) => {
    let output = '';
    const timeout = setTimeout(() => { child.kill(); reject(new Error('accounts_installation_lock_timeout')); }, 15000);
    child.stdout.on('data', bytes => {
      output += bytes;
      if (!output.includes('\n')) return;
      clearTimeout(timeout);
      const line = output.slice(0, output.indexOf('\n')).trim();
      if (line === 'acquired' || line === 'busy') resolve(line);
      else reject(new Error('accounts_installation_lock_failed'));
    });
    child.once('error', () => { clearTimeout(timeout); reject(new Error('accounts_installation_lock_failed')); });
    child.once('close', () => { clearTimeout(timeout); reject(new Error('accounts_installation_lock_failed')); });
  });
  const assertHeld = () => {
    if (exited || child.exitCode !== null || child.signalCode !== null || child.stdin.destroyed || child.stdin.writableEnded)
      throw new Error('accounts_installation_lock_lost');
  };
  const stop = async () => {
    if (!exited) child.stdin.end('release\n');
    const result = await closed;
    if (result.error || result.code !== 0) throw new Error('accounts_installation_lock_failed');
  };
  let held;
  try { held = await answer; }
  catch (error) { child.stdin.destroy(); await closed; throw error; }
  if (held === 'busy') { await stop(); return null; }
  let releasing;
  const release = () => releasing ??= stop();
  release.assertHeld = assertHeld;
  return release;
}
