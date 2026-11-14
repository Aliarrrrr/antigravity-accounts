import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { ensureAccounts } from './update-recovery.mjs';
import { withInstallationLock, resolveAccountsStateRoot } from './manage-install.mjs';
import { inspectNativeFallback, confirmNativeFallback } from './native-fallback.mjs';
import { assertApplicationStopped } from '../hostauth-adapter/src/process-guard.mjs';
import { readAsar } from '../hostauth-adapter/src/asar.mjs';

const samePath = (left, right) => path.resolve(left).toLowerCase() === path.resolve(right).toLowerCase();
async function publicJson(filename) {
  try {
    const info = await fs.lstat(filename);
    if (!info.isFile() || info.isSymbolicLink() || !samePath(await fs.realpath(filename), filename)) throw new Error('update_handoff_launcher_invalid');
    return JSON.parse(await fs.readFile(filename, 'utf8'));
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
}

async function guardedLauncher(installRoot, stateRoot) {
  const launcher = await publicJson(path.join(stateRoot, 'guarded-launcher.json'));
  if (!launcher) return;
  if (launcher.schemaVersion === 1 && launcher.kind === 'compatibility') {
    if (![launcher.installRoot, launcher.stateRoot, launcher.nodePath, launcher.bootstrapPath].every(value => path.isAbsolute(value || '')) ||
        !samePath(launcher.installRoot, installRoot) || !samePath(launcher.bootstrapPath, path.join(launcher.stateRoot, 'bootstrap.mjs')))
      throw new Error('update_handoff_launcher_invalid');
    const config = await publicJson(path.join(launcher.stateRoot, 'bootstrap.json'));
    if (!config || config.schemaVersion !== 1 || !samePath(config.installRoot, installRoot) ||
        !samePath(config.stateRoot, launcher.stateRoot) || !samePath(config.accountsStateRoot, stateRoot) || !samePath(config.nodePath, launcher.nodePath))
      throw new Error('update_handoff_launcher_invalid');
    for (const file of [launcher.nodePath, launcher.bootstrapPath]) { const info = await fs.lstat(file); if (!info.isFile() || info.isSymbolicLink()) throw new Error('update_handoff_launcher_invalid'); }
    return { launcher, compatibility: true };
  }
  const keys = ['installRoot', 'toolkitRoot', 'stateRoot', 'nodePath'];
  if (launcher.schemaVersion !== 1 || launcher.kind !== 'localization' ||
      keys.some(key => !path.isAbsolute(launcher[key] || '')) || !samePath(launcher.installRoot, installRoot))
    throw new Error('update_handoff_launcher_invalid');
  const integration = await publicJson(path.join(launcher.stateRoot, 'windows-persistence', 'install.json'));
  if (!integration || integration.schemaVersion !== 1 || integration.installed !== true ||
      keys.some(key => typeof integration[key] !== 'string' || !samePath(integration[key], launcher[key])))
    throw new Error('update_handoff_launcher_invalid');
  try { await fs.lstat(path.join(launcher.stateRoot, 'windows-persistence', 'disabled')); throw new Error('update_handoff_launcher_invalid'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  const vbs = path.join(launcher.toolkitRoot, 'scripts', 'Launch-Localized.vbs');
  for (const filename of [vbs, launcher.nodePath, path.join(launcher.toolkitRoot, 'scripts', 'Launch-Localized.ps1'),
    path.join(launcher.toolkitRoot, 'durable.mjs')]) {
    const info = await fs.lstat(filename);
    if (!info.isFile() || info.isSymbolicLink()) throw new Error('update_handoff_launcher_invalid');
  }
  return { launcher, vbs };
}

async function startChild(spawnApp, executable, args, options) {
  const child = spawnApp(executable, args, options);
  await new Promise((resolve, reject) => { child.once('spawn', resolve); child.once('error', reject); });
  child.unref();
}

export async function recoverAndLaunch({ installRoot, packageRoot, stateRoot,
  runLocked = withInstallationLock, recover = ensureAccounts, spawnApp = spawn,
  confirmNativeMode = confirmNativeFallback, inspectFallback = inspectNativeFallback, showAccounts = false,
  checkStopped = assertApplicationStopped } = {}) {
  for (const value of [installRoot, packageRoot, stateRoot]) if (!path.isAbsolute(value || '')) throw new Error('update_handoff_configuration_invalid');
  if (typeof showAccounts !== 'boolean') throw new Error('update_handoff_arguments_invalid');
  const resourcesRoot = await fs.realpath(path.join(installRoot, 'resources'));
  stateRoot = await resolveAccountsStateRoot({ installRoot, stateRoot });
  const guarded = await guardedLauncher(installRoot, stateRoot);
  const env = { ...process.env }; delete env.ELECTRON_RUN_AS_NODE;
  if (guarded) {
    if (guarded.compatibility) {
      await startChild(spawnApp, guarded.launcher.nodePath, [guarded.launcher.bootstrapPath, 'launch', '--state-root', guarded.launcher.stateRoot, ...(showAccounts ? ['--', '--accounts'] : [])],
        { cwd: installRoot, env, detached: true, stdio: 'ignore', windowsHide: true });
      return { dispatched: true, guarded: true, credentialFilesRead: false };
    }
    const { launcher, vbs } = guarded;
    await startChild(spawnApp, path.join(process.env.SystemRoot, 'System32', 'wscript.exe'),
      [vbs, launcher.nodePath, launcher.toolkitRoot, installRoot, launcher.stateRoot, ...(showAccounts ? ['--accounts'] : [])],
      { cwd: installRoot, env, detached: true, stdio: 'ignore', windowsHide: true });
    return { dispatched: true, guarded: true, credentialFilesRead: false };
  }
  // A localization-managed installation must use its registered guarded
  // launcher. This standalone path never silently drops that extra layer.
  try {
    await fs.access(path.join(resourcesRoot, 'antigravity-zh-cn.install.json'));
    const archive = readAsar(await fs.readFile(path.join(resourcesRoot, 'app.asar')));
    let preload = '';
    try { preload = archive.read('dist/preload.js').toString('utf8'); }
    catch { /* A raw update may no longer have the previous preload entry. */ }
    if (/ANTIGRAVITY_ZH_CN_NATIVE_V1|__antigravityZhCN/.test(preload)) throw new Error('update_handoff_localized_launcher_required');
  }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  return runLocked(resourcesRoot, async lock => {
    const result = await recover({ installRoot, packageRoot, stateRoot, checkStopped,
      runLocked: (target, action) => {
        if (path.resolve(target).toLowerCase() !== resourcesRoot.toLowerCase()) throw new Error('update_handoff_installation_changed');
        lock.assertHeld?.(); return action(lock);
      } });
    const nativeMode = result.status === 'degraded' && result.mode === 'native';
    if (!['ready', 'repaired'].includes(result.status) && !nativeMode) throw new Error('update_handoff_repair_required');
    if (nativeMode) {
      if (!await confirmNativeMode(result)) return { launched: false, cancelled: true, status: 'degraded', mode: 'native', credentialFilesRead: false };
      lock.assertHeld?.();
      await checkStopped({ installRoot });
      const receipt = await publicJson(path.join(stateRoot, 'adapter-installation.json'));
      const current = await inspectFallback({ installRoot, receipt });
      if (JSON.stringify(current) !== JSON.stringify(result.program)) throw new Error('native_fallback_program_changed');
    }
    lock.assertHeld?.();
    await startChild(spawnApp, path.join(installRoot, 'Antigravity.exe'), showAccounts && !nativeMode ? ['--accounts'] : [],
      { cwd: installRoot, env, detached: true, stdio: 'ignore', windowsHide: false });
    return { repaired: result.status === 'repaired', launched: true,
      ...(nativeMode ? { status: 'degraded', mode: 'native', reason: result.reason } : {}), credentialFilesRead: false };
  });
}

if (process.argv[1] && path.resolve(process.argv[1]).toLowerCase() === fileURLToPath(import.meta.url).toLowerCase()) {
  try {
    if (process.platform !== 'win32') throw new Error('windows_required');
    if (process.versions.electron) process.noAsar = true;
    const values = new Map();
    for (let index = 2; index < process.argv.length; index += 2) {
      if (!['--install-root', '--package-root', '--state-root', '--show-accounts'].includes(process.argv[index]) || !process.argv[index + 1] || values.has(process.argv[index]))
        throw new Error('update_handoff_arguments_invalid');
      values.set(process.argv[index], process.argv[index + 1]);
    }
    const installRoot = values.get('--install-root') || path.join(process.env.LOCALAPPDATA, 'Programs', 'antigravity');
    if (values.has('--show-accounts') && !['true', 'false'].includes(values.get('--show-accounts'))) throw new Error('update_handoff_arguments_invalid');
    console.log(JSON.stringify(await recoverAndLaunch({ installRoot,
      packageRoot: values.get('--package-root') || path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..'),
      stateRoot: values.get('--state-root') || path.join(process.env.LOCALAPPDATA, 'AntigravityAccounts'),
      showAccounts: values.get('--show-accounts') === 'true' })));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
