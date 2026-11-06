import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { ensureAccounts } from './update-recovery.mjs';
import { withInstallationLock } from './manage-install.mjs';

export async function recoverAndLaunch({ installRoot, packageRoot, stateRoot,
  runLocked = withInstallationLock, recover = ensureAccounts, spawnApp = spawn } = {}) {
  for (const value of [installRoot, packageRoot, stateRoot]) if (!path.isAbsolute(value || '')) throw new Error('update_handoff_configuration_invalid');
  const resourcesRoot = await fs.realpath(path.join(installRoot, 'resources'));
  // A localization-managed installation must use its registered guarded
  // launcher. This standalone path never silently drops that extra layer.
  try { await fs.access(path.join(resourcesRoot, 'antigravity-zh-cn.install.json')); throw new Error('update_handoff_localized_launcher_required'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  return runLocked(resourcesRoot, async lock => {
    const result = await recover({ installRoot, packageRoot, stateRoot,
      runLocked: (target, action) => {
        if (path.resolve(target).toLowerCase() !== resourcesRoot.toLowerCase()) throw new Error('update_handoff_installation_changed');
        lock.assertHeld?.(); return action(lock);
      } });
    if (!['ready', 'repaired'].includes(result.status)) throw new Error('update_handoff_repair_required');
    lock.assertHeld?.();
    const env = { ...process.env }; delete env.ELECTRON_RUN_AS_NODE;
    const child = spawnApp(path.join(installRoot, 'Antigravity.exe'), [], { cwd: installRoot, env, detached: true, stdio: 'ignore', windowsHide: false });
    await new Promise((resolve, reject) => { child.once('spawn', resolve); child.once('error', reject); });
    child.unref();
    return { repaired: result.status === 'repaired', launched: true, credentialFilesRead: false };
  });
}

if (process.argv[1] && path.resolve(process.argv[1]).toLowerCase() === fileURLToPath(import.meta.url).toLowerCase()) {
  try {
    if (process.platform !== 'win32') throw new Error('windows_required');
    if (process.versions.electron) process.noAsar = true;
    const values = new Map();
    for (let index = 2; index < process.argv.length; index += 2) {
      if (!['--install-root', '--package-root', '--state-root'].includes(process.argv[index]) || !process.argv[index + 1] || values.has(process.argv[index]))
        throw new Error('update_handoff_arguments_invalid');
      values.set(process.argv[index], process.argv[index + 1]);
    }
    console.log(JSON.stringify(await recoverAndLaunch({ installRoot: values.get('--install-root'),
      packageRoot: values.get('--package-root'), stateRoot: values.get('--state-root') })));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
