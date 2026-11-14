import fs from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { TOOL_ROOT, inspectInstallation, restoreInstallation } from './toolkit.mjs';
import { acquireAccountsInstallationLock } from './process-lock.mjs';
import { inspectNativeFallback } from './native-fallback.mjs';

export const DEFAULT_ACCOUNTS_STATE_ROOT = path.join(process.env.LOCALAPPDATA || '', 'AntigravityAccounts');
const comparable = value => process.platform === 'win32' ? value.toLowerCase() : value;
const samePath = (left, right) => comparable(path.resolve(left)) === comparable(path.resolve(right));
const locationFile = stateRoot => path.join(stateRoot, 'accounts-installation.json');
async function regularJson(filename) {
  try {
    const info = await fs.lstat(filename);
    if (!info.isFile() || info.isSymbolicLink()) throw new Error('accounts_recovery_metadata_invalid');
    return JSON.parse(await fs.readFile(filename, 'utf8'));
  } catch (error) { if (error.code === 'ENOENT') return undefined; throw error; }
}

async function publicAccountRoot(installRoot, root) {
  if (!path.isAbsolute(root || '')) throw new Error('accounts_recovery_location_invalid');
  root = path.resolve(root);
  // A packaged installer may see a merged LocalAppData directory. Resolve an
  // existing public receipt, never an account store, to find its backing folder.
  try {
    const receiptPath = path.join(root, 'adapter-installation.json');
    const info = await fs.lstat(receiptPath);
    if (!info.isFile() || info.isSymbolicLink()) throw new Error('accounts_recovery_metadata_invalid');
    root = path.dirname(await fs.realpath(receiptPath));
  }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  const relative = path.relative(installRoot, root);
  if (!relative || relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative))
    throw new Error('accounts_recovery_package_inside_installation');
  try {
    const info = await fs.lstat(root);
    if (!info.isDirectory() || info.isSymbolicLink()) throw new Error('accounts_recovery_location_invalid');
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
  return root;
}

export async function resolveAccountsRecoveryRoot(installRoot, { accountsStateRoot,
  stateRoot = path.join(TOOL_ROOT, 'state') } = {}) {
  const config = await regularJson(path.join(installRoot, 'resources', 'hostauth-addon', 'config.json'));
  const location = await regularJson(locationFile(stateRoot));
  if (location && (location.schemaVersion !== 1 || Object.keys(location).length !== 3 ||
      !path.isAbsolute(location.installRoot || '') || !path.isAbsolute(location.accountsStateRoot || '') ||
      !samePath(location.installRoot, installRoot) ||
      Object.keys(location).some(key => !['schemaVersion', 'installRoot', 'accountsStateRoot'].includes(key))))
    throw new Error('accounts_recovery_location_invalid');
  const supplied = [accountsStateRoot, config?.accountsStateRoot, location?.accountsStateRoot].filter(value => value !== undefined);
  const roots = await Promise.all(supplied.map(root => publicAccountRoot(installRoot, root)));
  if (roots.some(root => !samePath(root, roots[0]))) throw new Error('accounts_recovery_location_conflict');
  return roots[0] || publicAccountRoot(installRoot, DEFAULT_ACCOUNTS_STATE_ROOT);
}

async function rememberAccountRoot(installRoot, stateRoot, accountsStateRoot) {
  const record = { schemaVersion: 1, installRoot: path.resolve(installRoot), accountsStateRoot };
  const filename = locationFile(stateRoot), existing = await regularJson(filename);
  if (JSON.stringify(existing) === JSON.stringify(record)) return;
  await fs.mkdir(stateRoot, { recursive: true });
  const stage = filename + '.' + randomUUID() + '.new';
  await fs.writeFile(stage, JSON.stringify(record, null, 2) + '\n', { flag: 'wx' });
  await fs.rename(stage, filename);
}

// Public launch paths only. The update handoff can use the existing guarded
// launcher after NSIS exits, without keeping the installed Electron EXE open.
export async function registerGuardedLauncher(installRoot, { stateRoot, accountsStateRoot } = {}) {
  if (!path.isAbsolute(stateRoot || '')) return { registered: false };
  accountsStateRoot = await resolveAccountsRecoveryRoot(installRoot, { stateRoot, accountsStateRoot });
  const filename = path.join(accountsStateRoot, 'guarded-launcher.json');
  const existing = await regularJson(filename);
  if(existing?.kind==='compatibility') {
    const keys=['schemaVersion','kind','installRoot','stateRoot','nodePath','bootstrapPath'];
    if(existing.schemaVersion!==1 || Object.keys(existing).some(key=>!keys.includes(key)) ||
        ![existing.installRoot,existing.stateRoot,existing.nodePath,existing.bootstrapPath].every(value=>path.isAbsolute(value||'')) ||
        !samePath(existing.installRoot,installRoot) || !samePath(existing.bootstrapPath,path.join(existing.stateRoot,'bootstrap.mjs')))
      throw new Error('accounts_guarded_launcher_invalid');
    const relative=path.relative(path.resolve(installRoot),path.resolve(existing.stateRoot));
    if(!relative || relative!=='..'&&!relative.startsWith('..'+path.sep)&&!path.isAbsolute(relative))
      throw new Error('accounts_guarded_launcher_invalid');
    for(const value of [existing.nodePath,existing.bootstrapPath]) {
      const info=await fs.lstat(value);
      if(!info.isFile()||info.isSymbolicLink()) throw new Error('accounts_guarded_launcher_invalid');
    }
    return {registered:true,kind:'compatibility'};
  }
  const persistence = await regularJson(path.join(stateRoot, 'windows-persistence', 'install.json'));
  if (!persistence?.installed) return { registered: false };
  try { await fs.access(path.join(stateRoot, 'windows-persistence', 'disabled')); return { registered: false }; }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  if (persistence.schemaVersion !== 1 || ![persistence.installRoot, persistence.toolkitRoot, persistence.stateRoot, persistence.nodePath].every(value => path.isAbsolute(value || '')) ||
      !samePath(persistence.installRoot, installRoot) || !samePath(persistence.toolkitRoot, TOOL_ROOT) || !samePath(persistence.stateRoot, stateRoot))
    throw new Error('accounts_guarded_launcher_invalid');
  for (const filename of [persistence.nodePath, path.join(TOOL_ROOT, 'scripts', 'Launch-Localized.vbs'),
    path.join(TOOL_ROOT, 'scripts', 'Launch-Localized.ps1'), path.join(TOOL_ROOT, 'durable.mjs')]) {
    const info = await fs.lstat(filename);
    if (!info.isFile() || info.isSymbolicLink()) throw new Error('accounts_guarded_launcher_invalid');
  }
  const record = { schemaVersion: 1, kind: 'localization', installRoot: path.resolve(installRoot), toolkitRoot: TOOL_ROOT,
    stateRoot: path.resolve(stateRoot), nodePath: persistence.nodePath };
  if (JSON.stringify(existing) === JSON.stringify(record)) return { registered: true };
  const stage = filename + '.' + randomUUID() + '.new';
  await fs.writeFile(stage, JSON.stringify(record, null, 2) + '\n', { flag: 'wx' });
  await fs.rename(stage, filename);
  return { registered: true };
}

async function registeredInstallation(installRoot, accountsStateRoot, stateRoot = path.join(TOOL_ROOT, 'state')) {
  const receipt = await regularJson(path.join(accountsStateRoot, 'adapter-installation.json'));
  if (!receipt) {
    const config = await regularJson(path.join(installRoot, 'resources', 'hostauth-addon', 'config.json'));
    if (config?.accountsStateRoot !== undefined || await regularJson(locationFile(stateRoot)))
      throw new Error('accounts_recovery_receipt_missing');
    return { reason: 'not_installed' };
  }
  // Separate copied installations used for development do not inherit another
  // installation's account configuration.
  if (!path.isAbsolute(receipt.resourcesRoot || '') || !samePath(receipt.resourcesRoot, path.join(installRoot, 'resources')))
    return { reason: 'different_installation' };
  if (receipt.phase === 'restored') return { reason: 'restored' };
  if (receipt.schemaVersion !== 1 || receipt.phase !== 'applied') throw new Error('accounts_recovery_receipt_invalid');
  // Older previews did not register their distribution directory. The first
  // installation of the new package adds it without consulting account data.
  if (!receipt.packageRoot) return { reason: 'legacy_receipt' };
  if (!path.isAbsolute(receipt.packageRoot))
    throw new Error('accounts_recovery_receipt_invalid');
  if (path.basename(receipt.packageRoot).startsWith('recovery-package-')) {
    // Older receipts may name a logical LocalAppData folder. Anchor their
    // installer-owned package beside the receipt found in the physical folder.
    const previousRoot = path.dirname(receipt.backupRoot || '');
    for (const [key, prefix] of [['backupRoot', 'restore-'], ['preparedRoot', 'prepared-'], ['packageRoot', 'recovery-package-']]) {
      if (!path.isAbsolute(receipt[key] || '') || !path.basename(receipt[key]).startsWith(prefix) ||
          !samePath(path.dirname(receipt[key]), previousRoot)) throw new Error('accounts_recovery_receipt_invalid');
    }
    const candidate = path.join(accountsStateRoot, path.basename(receipt.packageRoot));
    let actual = candidate;
    try {
      const info = await fs.lstat(candidate);
      if (!info.isDirectory() || info.isSymbolicLink()) throw new Error('accounts_recovery_module_invalid');
      actual = await fs.realpath(candidate);
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (!samePath(path.dirname(actual), accountsStateRoot)) throw new Error('accounts_recovery_module_invalid');
    try {
      if (!samePath(await fs.realpath(receipt.packageRoot), actual)) throw new Error('accounts_recovery_location_conflict');
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
    return { receipt: { ...receipt, packageRoot: actual } };
  }
  return { receipt };
}

async function loadPackageModule(installRoot, receipt, name, loadModule) {
  const packageRoot = await fs.realpath(receipt.packageRoot);
  const relative = path.relative(installRoot, packageRoot);
  if (!relative || relative === '.' || relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative))
    throw new Error('accounts_recovery_package_inside_installation');
  const modulePath = path.join(packageRoot, 'account-plugin', name);
  const moduleInfo = await fs.lstat(modulePath);
  if (!moduleInfo.isFile() || moduleInfo.isSymbolicLink()) throw new Error('accounts_recovery_module_invalid');
  // A watcher can outlive an installer update. Load the current public module,
  // while dependencies in an installer-owned UUID directory remain unchanged.
  const moduleUrl = pathToFileURL(modulePath);
  moduleUrl.searchParams.set('revision', moduleInfo.size + '-' + moduleInfo.mtimeMs);
  return { packageRoot, module: await loadModule(moduleUrl.href) };
}

// The final App launch must exclude manual Accounts writers too. Recovery's
// earlier check releases its mutex before the launcher validates/spawns. Hold
// that same mutex here, with repair disabled, until the child has spawned.
export async function acquireAccountsLaunchLock(installRoot, {
  accountsStateRoot, stateRoot, nativeFallback, loadModule = url => import(url)
} = {}) {
  let release;
  try {
    accountsStateRoot = await resolveAccountsRecoveryRoot(installRoot, { stateRoot, accountsStateRoot });
    const { receipt } = await registeredInstallation(installRoot, accountsStateRoot, stateRoot);
    const resourcesRoot = await fs.realpath(path.join(installRoot, 'resources'));
    release = await acquireAccountsInstallationLock(resourcesRoot);
    if (!release) throw new Error('accounts_installation_busy');
    release.assertHeld?.();
    if (!receipt) return release;
    if (nativeFallback) {
      const program = await inspectNativeFallback({ installRoot, receipt });
      if (['appVersion','asarSha256','nativeSha256','executableSha256','unpackedSha256']
        .some(key => program[key] !== nativeFallback.program?.[key])) throw new Error('accounts_recovery_installation_changed');
      return release;
    }
    const recovery = await loadPackageModule(installRoot, receipt, 'update-recovery.mjs', loadModule);
    if (typeof recovery.module.ensureAccounts !== 'function') throw new Error('accounts_recovery_module_invalid');
    const status = await recovery.module.ensureAccounts({ installRoot, packageRoot: recovery.packageRoot, stateRoot: accountsStateRoot,
      runLocked: (target, action) => {
        if (!samePath(target, resourcesRoot)) throw new Error('accounts_recovery_installation_changed');
        release.assertHeld?.();
        return action(release);
      },
      // An updater/manual writer may have completed since preflight. Recheck
      // its result without changing programs from the final launch section.
      checkStopped: () => { throw new Error('accounts_repair_required'); }
    });
    if (status.status !== 'ready' && !(status.status === 'disabled' && status.reason === 'restored'))
      throw new Error('accounts_repair_required');
    release.assertHeld?.();
    return release;
  } catch (error) {
    try { await release?.(); } catch { /* The launch remains refused. */ }
    const failure = error instanceof Error ? error : new Error('accounts_recovery_failed');
    failure.accountsRecoveryRequired = true;
    throw failure;
  }
}

export async function inspectAccountsNativeFallback(installRoot, options = {}) {
  const accountsStateRoot = await resolveAccountsRecoveryRoot(installRoot, options);
  const { receipt } = await registeredInstallation(installRoot, accountsStateRoot, options.stateRoot);
  if (!receipt) throw new Error('accounts_native_fallback_unavailable');
  return inspectNativeFallback({ installRoot, receipt });
}

export async function accountRecoveryFiles(installRoot, { accountsStateRoot, stateRoot = path.join(TOOL_ROOT, 'state') } = {}) {
  accountsStateRoot = await resolveAccountsRecoveryRoot(installRoot, { stateRoot, accountsStateRoot });
  const files = [locationFile(stateRoot), path.join(installRoot, 'resources', 'hostauth-addon', 'config.json'),
    path.join(accountsStateRoot, 'adapter-installation.json')];
  const { receipt } = await registeredInstallation(installRoot, accountsStateRoot, stateRoot);
  if (receipt) files.push(
    path.join(receipt.packageRoot, 'package-manifest.json'),
    path.join(receipt.packageRoot, 'hostauth-adapter', 'package.json'),
    path.join(receipt.packageRoot, 'account-plugin', 'update-recovery.mjs'),
    path.join(receipt.packageRoot, 'account-plugin', 'manage-install.mjs'),
    path.join(receipt.packageRoot, 'account-plugin', 'installer-lock.mjs')
  );
  return files;
}

// Called while the existing localization launcher owns repair.lock, before its
// ordinary localization pass. Accounts owns its separate installer mutex. Its
// callbacks acquire/release the localization archive-operation mutex normally.
export async function ensureInstalledAccounts(installRoot, { stateRoot = path.join(TOOL_ROOT, 'state'),
  accountsStateRoot, processProbe, relocalize,
  loadModule = url => import(url), inspect = inspectInstallation, restore = restoreInstallation } = {}) {
  let registered, loadingRecovery = false;
  try {
    accountsStateRoot = await resolveAccountsRecoveryRoot(installRoot, { stateRoot, accountsStateRoot });
    registered = await registeredInstallation(installRoot, accountsStateRoot, stateRoot);
    if (!registered.receipt) return { status: 'disabled', reason: registered.reason };
    await rememberAccountRoot(installRoot, stateRoot, accountsStateRoot);
    await registerGuardedLauncher(installRoot, { stateRoot, accountsStateRoot });
    loadingRecovery = true;
    const { packageRoot, module: recovery } = await loadPackageModule(installRoot, registered.receipt, 'update-recovery.mjs', loadModule);
    if (typeof recovery.ensureAccounts !== 'function') throw new Error('accounts_recovery_module_invalid');
    loadingRecovery = false;
    let restoredLocalization = false;
    let localizationError;
    const result = await recovery.ensureAccounts({ installRoot, packageRoot, stateRoot: accountsStateRoot,
      checkStopped: async () => {
        if ((await processProbe(installRoot)).length)
          throw Object.assign(new Error('application_running'), { code: 'application_running' });
      },
      beforeApply: async () => {
        const info = await inspect(installRoot);
        if (!info.isLocalized) return;
        const record = await regularJson(path.join(installRoot, 'resources', 'antigravity-zh-cn.install.json'));
        if (!record) throw new Error('original_backup_missing');
        let backupRoot;
        for (const candidate of [path.join(stateRoot, 'backups'), path.join(TOOL_ROOT, 'backups')]) {
          try { await fs.access(path.join(candidate, info.appVersion, record.sourceSha256, 'manifest.json')); backupRoot = candidate; break; }
          catch (error) { if (error.code !== 'ENOENT') throw error; }
        }
        if (!backupRoot) throw new Error('original_backup_missing');
        await restore(installRoot, { backupRoot, compatibleSource: true });
        restoredLocalization = true;
      },
      afterApply: async ({ installed }) => {
        // Relocalize a successfully adapted program, or restore the previous
        // localized program when compatibility rejected the new source.
        if (installed || restoredLocalization) {
          try { await relocalize(); }
          catch (error) { if (!installed) throw error; localizationError = error.message; }
        }
      }
    });
    return localizationError ? { ...result, localizationError } : result;
  } catch (error) {
    if (loadingRecovery && registered?.receipt &&
      (['ENOENT','ERR_MODULE_NOT_FOUND'].includes(error.code) || error instanceof SyntaxError || error.message === 'accounts_recovery_module_invalid')) {
      const release = await acquireAccountsInstallationLock(path.join(installRoot, 'resources'));
      if (!release) return { status: 'pending_update', credentialFilesRead: false };
      try {
        if ((await processProbe(installRoot)).length) return { status: 'pending_idle', credentialFilesRead: false };
        release.assertHeld?.();
        const program = await inspectNativeFallback({ installRoot, receipt: registered.receipt });
        return { status: 'degraded', mode: 'native', reason: 'accounts_recovery_runtime_unavailable', program, credentialFilesRead: false };
      } finally { await release(); }
    }
    // An enabled Accounts installation must not silently launch the original
    // official account when its adaptation cannot be repaired.
    const failure = error instanceof Error ? error : new Error('accounts_recovery_failed');
    failure.accountsRecoveryRequired = true;
    throw failure;
  }
}
