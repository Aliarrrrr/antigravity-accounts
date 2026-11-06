import { readFile, writeFile, mkdir, mkdtemp, rmdir, rename, stat, realpath, readdir, lstat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { buildCopy, applyPrepared, restorePrepared } from '../hostauth-adapter/src/package-copy.mjs';
import { sha256 } from '../hostauth-adapter/src/asar.mjs';
import { assertApplicationStopped } from '../hostauth-adapter/src/process-guard.mjs';
import { acquireInstallationLock } from './installer-lock.mjs';

async function exists(file) { try { await stat(file); return true; } catch (error) { if (error.code === 'ENOENT') return false; throw error; } }
const comparablePath = value => process.platform === 'win32' ? value.toLowerCase() : value;
const samePath = (left, right) => comparablePath(path.resolve(left)) === comparablePath(path.resolve(right));
const stateIO = { readFile, mkdir, mkdtemp, rmdir, realpath, lstat };
function requireExternalStateRoot(root, installRoot) {
  if (typeof root !== 'string' || !path.isAbsolute(root)) throw new Error('accounts_state_root_invalid');
  const relative = path.relative(path.resolve(installRoot), path.resolve(root));
  if (!relative || relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative))
    throw new Error('accounts_state_root_inside_installation');
}
async function regularDirectory(root, io) {
  const info = await io.lstat(root);
  if (!info.isDirectory() || info.isSymbolicLink()) throw new Error('accounts_state_root_invalid');
}
async function publicRootLocation(root, io) {
  try { await regularDirectory(root, io); }
  catch (error) { if (error.code === 'ENOENT') return undefined; throw error; }
  const parents = [];
  // These files contain installation paths or an enabled flag. The installer
  // never opens, enumerates or copies the component's account store.
  for (const name of ['adapter-installation.json', 'host-provider.json']) {
    const filename = path.join(root, name);
    let info;
    try { info = await io.lstat(filename); }
    catch (error) { if (error.code === 'ENOENT') continue; throw error; }
    if (!info.isFile() || info.isSymbolicLink()) throw new Error('accounts_state_metadata_invalid');
    parents.push(path.dirname(await io.realpath(filename)));
  }
  if (parents.some(parent => !samePath(parent, parents[0]))) throw new Error('accounts_state_root_conflict');
  const actual = parents[0] || await io.realpath(root);
  await regularDirectory(actual, io);
  return { root: path.resolve(actual), anchored: parents.length > 0 };
}

// An MSIX view may redirect individual files but leave realpath(their existing
// parent) unchanged. Resolve public metadata first, or resolve one newly created
// empty directory when preparing a new installation. No account bytes move.
export async function resolveAccountsStateRoot({ installRoot, stateRoot, create = false,
  defaultStateRoot = path.join(process.env.LOCALAPPDATA || '', 'AntigravityAccounts'), io: overrides } = {}) {
  const io = { ...stateIO, ...overrides };
  if (!path.isAbsolute(installRoot || '')) throw new Error('accounts_state_root_invalid');
  installRoot = await io.realpath(installRoot);
  await regularDirectory(installRoot, io);
  requireExternalStateRoot(stateRoot, installRoot);
  stateRoot = path.resolve(stateRoot);
  let configuredRoot;
  const configPath = path.join(installRoot, 'resources', 'hostauth-addon', 'config.json');
  try {
    const info = await io.lstat(configPath);
    if (!info.isFile() || info.isSymbolicLink()) throw new Error('accounts_state_metadata_invalid');
    const config = JSON.parse(await io.readFile(configPath, 'utf8'));
    if (Object.hasOwn(config, 'accountsStateRoot')) {
      requireExternalStateRoot(config.accountsStateRoot, installRoot);
      configuredRoot = config.accountsStateRoot;
    }
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
  const requested = await publicRootLocation(stateRoot, io);
  if (configuredRoot) {
    const configured = await publicRootLocation(configuredRoot, io);
    if (!configured) throw new Error('accounts_state_root_unavailable');
    requireExternalStateRoot(configured.root, installRoot);
    if (requested && !samePath(requested.root, configured.root) &&
        (requested.anchored || !samePath(stateRoot, defaultStateRoot))) throw new Error('accounts_state_root_conflict');
    if (!requested && !samePath(stateRoot, configuredRoot) && !samePath(stateRoot, defaultStateRoot))
      throw new Error('accounts_state_root_conflict');
    return configured.root;
  }
  if (requested?.anchored || !create) {
    const actual = requested?.root || stateRoot;
    requireExternalStateRoot(actual, installRoot);
    return actual;
  }
  await io.mkdir(stateRoot, { recursive: true });
  await regularDirectory(stateRoot, io);
  const temporary = await io.mkdtemp(path.join(stateRoot, '.resolve-state-'));
  try {
    await regularDirectory(temporary, io);
    const resolved = await io.realpath(temporary);
    if (path.basename(resolved) !== path.basename(temporary)) throw new Error('accounts_state_root_invalid');
    const actual = path.dirname(resolved);
    requireExternalStateRoot(actual, installRoot);
    await regularDirectory(actual, io);
    return actual;
  } finally {
    await regularDirectory(temporary, io);
    await io.rmdir(temporary);
  }
}

// Old receipts can retain logical MSIX paths. Resolve only installer-owned
// sibling directories under the same receipt root, never account-store files.
export async function resolveRecoveryReceiptPaths(receipt, { stateRoot } = {}) {
  const fields = [['backupRoot', 'restore-'], ['preparedRoot', 'prepared-'], ['packageRoot', 'recovery-package-']];
  const result = { ...receipt };
  const originalRoot = path.dirname(receipt.backupRoot || '');
  for (const [field, prefix] of fields) {
    const value = receipt[field];
    if (value === undefined || field === 'packageRoot' && !path.basename(value).startsWith(prefix)) continue;
    if (!path.isAbsolute(value || '') || !path.basename(value).startsWith(prefix) || !samePath(path.dirname(value), originalRoot))
      throw new Error('Recovery directory is outside the account adapter state directory');
    const candidate = path.join(stateRoot, path.basename(value));
    let actual = candidate;
    try {
      const info = await lstat(candidate);
      if (!info.isDirectory() || info.isSymbolicLink()) throw new Error('Recovery directory is outside the account adapter state directory');
      actual = await realpath(candidate);
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (!samePath(path.dirname(actual), stateRoot)) throw new Error('Recovery directory is outside the account adapter state directory');
    try {
      const original = await realpath(value);
      if (!samePath(original, actual)) throw new Error('accounts_state_root_conflict');
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
    result[field] = actual;
  }
  return result;
}
async function saveReceipt(file, receipt) {
  const stage = file + '.' + randomUUID() + '.new';
  await writeFile(stage, JSON.stringify(receipt, null, 2) + '\n', { flag: 'wx' });
  await rename(stage, file);
}

async function cacheRecoveryPackage({ stateRoot, suffix, packageRoot, preparedRoot }) {
  const cacheRoot = path.join(stateRoot, 'recovery-package-' + suffix);
  const sourceRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  await mkdir(cacheRoot, { recursive: false });
  const files = [];
  const copyPublicFile = async (source, relative) => {
    const info = await lstat(source);
    if (!info.isFile() || info.isSymbolicLink()) throw new Error('Recovery package source must be a regular file');
    const bytes = await readFile(source), target = path.join(cacheRoot, relative);
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, bytes, { flag: 'wx' });
    files.push({ path: relative.replaceAll(path.sep, '/'), sha256: sha256(bytes) });
  };
  // These are public installer sources. Neither the account state directory nor
  // the prepared copy's original ASAR/native-program directories are traversed.
  for (const name of ['manage-install.mjs', 'update-recovery.mjs', 'recovery-and-launch.mjs', 'installer-lock.mjs', 'Hold-InstallLock.ps1', 'Update-Handoff.ps1', 'native-oauth.mjs'])
    await copyPublicFile(path.join(sourceRoot, 'account-plugin', name), path.join('account-plugin', name));
  await copyPublicFile(path.join(packageRoot, 'account-plugin', 'window.cjs'), path.join('account-plugin', 'window.cjs'));
  await copyPublicFile(path.join(sourceRoot, 'hostauth-adapter', 'package.json'), path.join('hostauth-adapter', 'package.json'));
  for (const entry of await readdir(path.join(sourceRoot, 'hostauth-adapter', 'src'), { withFileTypes: true })) {
    if (!/\.(?:mjs|cjs)$/.test(entry.name)) continue;
    await copyPublicFile(path.join(sourceRoot, 'hostauth-adapter', 'src', entry.name), path.join('hostauth-adapter', 'src', entry.name));
  }
  const componentRoot = path.join(preparedRoot, 'resources', 'hostauth-addon', 'component');
  for (const entry of await readdir(componentRoot, { withFileTypes: true })) {
    if (!entry.isFile() || !/\.(?:exe|dll|json|config)$/i.test(entry.name) || /^language_server/i.test(entry.name))
      throw new Error('Unexpected independent component payload in recovery package');
    await copyPublicFile(path.join(componentRoot, entry.name), path.join('component', entry.name));
  }
  for (const file of files) {
    if (sha256(await readFile(path.join(cacheRoot, file.path))) !== file.sha256) throw new Error('Recovery package copy changed');
  }
  await writeFile(path.join(cacheRoot, 'package-manifest.json'), JSON.stringify({
    schemaVersion: 1, role: 'accounts-recovery-package', files
  }, null, 2) + '\n', { flag: 'wx' });
  return cacheRoot;
}

async function addonFiles(root, prefix = '') {
  if ((await lstat(root)).isSymbolicLink()) throw new Error('Updated installation contains an unexpected addon link');
  const files = new Map();
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const filename = path.join(root, entry.name), relative = prefix + entry.name;
    if (entry.isSymbolicLink()) throw new Error('Updated installation contains an unexpected addon link');
    if (entry.isDirectory()) {
      files.set(relative + '/', null);
      for (const [name, digest] of await addonFiles(filename, relative + '/')) files.set(name, digest);
    } else if (entry.isFile()) files.set(relative, sha256(await readFile(filename)));
    else throw new Error('Updated installation contains an unexpected addon entry');
  }
  return files;
}

export function isManagedRecoveryPath({ stateRoot, receiptRealPath, target, targetRealPath, prefix }) {
  if (![stateRoot, receiptRealPath, target, targetRealPath].every(value => typeof value === 'string' && path.isAbsolute(value))) return false;
  return path.relative(path.dirname(path.resolve(target)), path.resolve(stateRoot)) === '' &&
    path.basename(target).startsWith(prefix) && path.basename(targetRealPath) === path.basename(target) &&
    path.relative(path.dirname(targetRealPath), path.dirname(receiptRealPath)) === '';
}

async function retireUpdatedInstallation({ previous, originalReceipt = previous, resourcesRoot, stateRoot, suffix }) {
  if (previous.schemaVersion !== 1 || path.resolve(previous.resourcesRoot || '') !== resourcesRoot)
    throw new Error('Previous recovery receipt does not match this installation');
  const receiptPath = path.join(stateRoot, 'adapter-installation.json');
  const receiptStat = await lstat(receiptPath);
  if (!receiptStat.isFile() || receiptStat.isSymbolicLink()) throw new Error('Previous recovery receipt is not an installer-owned file');
  // MSIX may redirect individual files/directories without redirecting their
  // existing parent. Anchor recovery paths to the same real receipt location,
  // rather than assuming realpath(stateRoot) describes every child.
  const receiptRealPath = await realpath(receiptPath);
  for (const [target, prefix] of [[previous.backupRoot, 'restore-'], [previous.preparedRoot, 'prepared-']]) {
    if (!path.isAbsolute(target || '') || !isManagedRecoveryPath({ stateRoot, receiptRealPath, target, targetRealPath: await realpath(target), prefix }))
      throw new Error('Previous recovery directory is outside the account adapter state directory');
  }
  const addon = path.join(resourcesRoot, 'hostauth-addon');
  let retiredAddonRoot;
  if (await exists(addon)) {
    const expected = await addonFiles(path.join(previous.preparedRoot, 'resources', 'hostauth-addon'));
    const actual = await addonFiles(addon);
    if (actual.size !== expected.size || [...actual].some(([name, digest]) => expected.get(name) !== digest))
      throw new Error('Previous addon contents changed; no files were retired');
    retiredAddonRoot = path.join(previous.backupRoot, 'retired-addon-' + suffix);
  }
  const previousReceipt = path.join(stateRoot, 'adapter-installation-before-update-' + suffix + '.json');
  await writeFile(previousReceipt, JSON.stringify(originalReceipt, null, 2) + '\n', { flag: 'wx' });
  if (retiredAddonRoot) await rename(addon, retiredAddonRoot);
  return { previousReceipt, ...(retiredAddonRoot ? { retiredAddonRoot } : {}) };
}

// runLocked is provided only by the recovery coordinator while it already owns
// this installation's lock, so its localization callbacks share that ownership.
export async function withInstallationLock(resourcesRoot, action, acquireLock = acquireInstallationLock) {
  const release = await acquireLock(resourcesRoot);
  if (!release) throw new Error('accounts_installation_busy');
  try { return await action(release); }
  finally { await release(); }
}

export async function manageInstallation(options) {
  const { operation, installRoot, packageRoot, stateRoot, runLocked = withInstallationLock } = options;
  if (!['prepare', 'apply', 'restore'].includes(operation)) throw new Error('Expected prepare, apply or restore');
  for (const target of [installRoot, packageRoot, stateRoot]) if (!path.isAbsolute(target || '')) throw new Error('Installation, package and state paths must be absolute');
  const resourcesRoot = await realpath(path.join(installRoot, 'resources'));
  return runLocked(resourcesRoot, lock => manageInstallationUnlocked({ ...options, lock }));
}

async function manageInstallationUnlocked({ operation, installRoot, packageRoot, stateRoot, checkStopped = assertApplicationStopped, lock }) {
  if (!['prepare', 'apply', 'restore'].includes(operation)) throw new Error('Expected prepare, apply or restore');
  for (const target of [installRoot, packageRoot, stateRoot]) if (!path.isAbsolute(target || '')) throw new Error('Installation, package and state paths must be absolute');
  const resourcesRoot = await realpath(path.join(installRoot, 'resources'));
  stateRoot = await resolveAccountsStateRoot({ installRoot, stateRoot, create: true });
  const receiptPath = path.join(stateRoot, 'adapter-installation.json');
  await mkdir(stateRoot, { recursive: true });
  // Only installer-owned program paths and receipts are read in this directory.
  // The account vault is managed exclusively by the independent component.
  if (operation === 'restore') {
    await checkStopped({ installRoot });
    const receipt = await resolveRecoveryReceiptPaths(JSON.parse(await readFile(receiptPath, 'utf8')), { stateRoot });
    if (receipt.schemaVersion !== 1 || path.resolve(receipt.resourcesRoot) !== resourcesRoot || !path.isAbsolute(receipt.backupRoot || '')) throw new Error('Recovery receipt does not match this installation');
    const backupRoot = path.resolve(receipt.backupRoot);
    if (!samePath(path.dirname(backupRoot), stateRoot) || !path.basename(backupRoot).startsWith('restore-')) throw new Error('Recovery directory is outside the account adapter state directory');
    if (await exists(path.join(backupRoot, 'manifest.json'))) {
      lock.assertHeld?.();
      await restorePrepared({ resourcesRoot, backupRoot, processesStopped: true });
    } else if (sha256(await readFile(path.join(resourcesRoot, 'app.asar'))) !== receipt.originalAsarSha256 ||
        receipt.originalNativeSha256 && sha256(await readFile(path.join(resourcesRoot, 'bin', 'language_server.exe'))) !== receipt.originalNativeSha256) {
      throw new Error('Recovery data is incomplete and the installed archive changed; no files were overwritten');
    }
    await saveReceipt(receiptPath, { ...receipt, phase: 'restored' });
    return { restored: true, credentialFilesRead: false };
  }
  let previous, originalReceipt;
  if (operation === 'apply') {
    await checkStopped({ installRoot });
    if (await exists(receiptPath)) {
      originalReceipt = JSON.parse(await readFile(receiptPath, 'utf8'));
      previous = await resolveRecoveryReceiptPaths(originalReceipt, { stateRoot });
      if (previous.phase !== 'restored' && (previous.phase !== 'applied' ||
          sha256(await readFile(path.join(resourcesRoot, 'app.asar'))) === previous.patchedAsarSha256))
        throw new Error('An installation or unfinished recovery already exists. Use Restore before installing again');
    }
  }
  const suffix = randomUUID();
  const preparedRoot = path.join(stateRoot, 'prepared-' + suffix);
  const manifest = await buildCopy({
    sourceAsar: path.join(resourcesRoot, 'app.asar'), outputRoot: preparedRoot,
    componentExecutable: path.join(packageRoot, 'component', 'Antigravity.Accounts.exe'),
    uiDirectory: path.join(packageRoot, 'account-plugin'),
    languageServerPath: path.join(resourcesRoot, 'bin', 'language_server.exe'), accountsStateRoot: stateRoot
  });
  if (operation === 'prepare') return { preparedRoot, accountsStateRoot: stateRoot, runtimeVerified: false, credentialFilesRead: false };
  const recoveryPackageRoot = await cacheRecoveryPackage({ stateRoot, suffix, packageRoot, preparedRoot });
  // Building reads large native programs; an App/updater can start in between.
  // Recheck immediately before moving the old payload or replacing any file.
  await checkStopped({ installRoot });
  lock.assertHeld?.();
  // buildCopy has accepted both complete current official programs. An old
  // applied receipt may remain after the official updater replaced them. Keep
  // its receipt, originals, and matching leftover payload; never restore the
  // old programs over this recognized official version.
  const previousInstallation = previous?.phase === 'applied'
    ? await retireUpdatedInstallation({ previous, originalReceipt, resourcesRoot, stateRoot, suffix }) : undefined;
  const backupRoot = path.join(stateRoot, 'restore-' + suffix);
  const receipt = { schemaVersion: 1, phase: 'prepared', resourcesRoot, backupRoot, preparedRoot,
    packageRoot: recoveryPackageRoot, distributionRoot: path.resolve(packageRoot),
    originalAsarSha256: manifest.originalAsarSha256, patchedAsarSha256: manifest.patchedAsarSha256,
    originalNativeSha256: manifest.originalNativeSha256, patchedNativeSha256: manifest.patchedNativeSha256,
    ...(previousInstallation ? { previousInstallation } : {}) };
  await saveReceipt(receiptPath, receipt);
  lock.assertHeld?.();
  await applyPrepared({ preparedRoot, resourcesRoot, backupRoot, processesStopped: true, acknowledgeUnverifiedRuntime: true });
  await saveReceipt(receiptPath, { ...receipt, phase: 'applied' });
  return { installed: true, runtimeVerified: false, recoveryReceipt: receiptPath, accountsStateRoot: stateRoot, credentialFilesRead: false };
}

const comparable = value => process.platform === 'win32' ? value.toLowerCase() : value;
if (process.argv[1] && comparable(path.resolve(process.argv[1])) === comparable(fileURLToPath(import.meta.url))) {
  try {
    if (process.platform !== 'win32') throw new Error('This account adapter only supports Windows');
    // Electron's Node mode still patches fs to interpret .asar paths as virtual
    // directories. The installer needs the archive's raw bytes. This affects
    // only this helper process, never the official App or addon runtime.
    if (process.versions.electron) process.noAsar = true;
    const operation = process.argv[2];
    const options = new Map();
    for (let i = 3; i < process.argv.length; i += 2) {
      if (!['--install-root', '--package-root', '--state-root'].includes(process.argv[i]) || !process.argv[i + 1] || options.has(process.argv[i])) throw new Error('Unknown, repeated or incomplete installer argument');
      options.set(process.argv[i], process.argv[i + 1]);
    }
    const result = await manageInstallation({ operation,
      installRoot: options.get('--install-root') || path.join(process.env.LOCALAPPDATA, 'Programs', 'antigravity'),
      packageRoot: options.get('--package-root') || path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..'),
      stateRoot: options.get('--state-root') || path.join(process.env.LOCALAPPDATA, 'AntigravityAccounts')
    });
    console.log(JSON.stringify(result, null, 2));
  } catch (error) {
    // Installer errors may include program paths; token/account values are never
    // read by this entry point and cannot appear in these messages.
    console.error(error.message); process.exitCode = 1;
  }
}
