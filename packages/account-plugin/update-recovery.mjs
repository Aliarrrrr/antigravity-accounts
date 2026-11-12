import { lstat, readFile, realpath } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { manageInstallation, withInstallationLock, resolveAccountsStateRoot, resolveRecoveryReceiptPaths } from './manage-install.mjs';
import { sha256 } from '../hostauth-adapter/src/asar.mjs';
import { assertApplicationStopped } from '../hostauth-adapter/src/process-guard.mjs';
import { inspectNativeFallback } from './native-fallback.mjs';

function compatibilityFailure(error) {
  return typeof error?.message === 'string' && (
    /^unsupported_native_(?:oauth_configuration|diagnostics_program|project_refresh_program|frontend(?:_archive|_contract)?)(?::|$)/.test(error.message) ||
    /^Unsupported (?:exact program source:|host RPC dependency:)/.test(error.message) ||
    ['Native program and application source contracts differ', 'Expected exactly one complete source anchor'].includes(error.message)
  );
}

const comparable = value => process.platform === 'win32' ? value.toLowerCase() : value;
const samePath = (left, right) => comparable(path.resolve(left)) === comparable(path.resolve(right));
const sameDigest = (left, right) => typeof left === 'string' && typeof right === 'string' &&
  /^[a-f0-9]{64}$/i.test(left) && left.toLowerCase() === right.toLowerCase();
async function jsonFile(filename) {
  try {
    const info = await lstat(filename);
    if (!info.isFile() || info.isSymbolicLink()) throw new Error('Account recovery metadata must be a regular file');
    return JSON.parse(await readFile(filename, 'utf8'));
  } catch (error) { if (error.code === 'ENOENT') return undefined; throw error; }
}

function applicationBusy(error) {
  return error.code === 'application_running' || error.message.startsWith('Exit all Antigravity, language-server');
}

// This hook runs before localization is applied, under the launcher's existing
// repair lock. It owns no shortcuts, startup entry or independent watcher.
// beforeApply/afterApply let that launcher restore/reapply its own layer while
// the Accounts lock remains held. No account component is started here.
export async function ensureAccounts({ installRoot, packageRoot, stateRoot,
  beforeApply, afterApply, checkStopped = assertApplicationStopped,
  runLocked = withInstallationLock, manage = manageInstallation, inspectFallback = inspectNativeFallback } = {}) {
  for (const target of [installRoot, packageRoot, stateRoot])
    if (!path.isAbsolute(target || '')) throw new Error('Installation, package and state paths must be absolute');
  const resourcesRoot = await realpath(path.join(installRoot, 'resources'));
  try {
    return await runLocked(resourcesRoot, async lock => {
      stateRoot = await resolveAccountsStateRoot({ installRoot, stateRoot });
      const receiptPath = path.join(stateRoot, 'adapter-installation.json');
      let receipt = await jsonFile(receiptPath);
      if (!receipt) return { status: 'disabled', reason: 'not_installed', credentialFilesRead: false };
      if (receipt.schemaVersion !== 1 || !path.isAbsolute(receipt.resourcesRoot || '') || !samePath(receipt.resourcesRoot, resourcesRoot))
        throw new Error('Recovery receipt does not match this installation');
      if (receipt.phase === 'restored') return { status: 'disabled', reason: 'restored', credentialFilesRead: false };
      if (receipt.phase !== 'applied') throw new Error('An unfinished Accounts recovery requires Restore before automatic repair');
      const originalPackageRoot = receipt.packageRoot;
      receipt = await resolveRecoveryReceiptPaths(receipt, { stateRoot });
      if (originalPackageRoot && samePath(packageRoot, originalPackageRoot)) packageRoot = receipt.packageRoot;
      const archiveHash = sha256(await readFile(path.join(resourcesRoot, 'app.asar')));
      const nativeHash = sha256(await readFile(path.join(resourcesRoot, 'bin', 'language_server.exe')));
      const localization = await jsonFile(path.join(resourcesRoot, 'antigravity-zh-cn.install.json'));
      const localizedAccountArchive = localization?.schemaVersion === 1 &&
        path.isAbsolute(localization.installRoot || '') && samePath(localization.installRoot, installRoot) &&
        sameDigest(localization.sourceSha256, receipt.patchedAsarSha256) && sameDigest(localization.patchedSha256, archiveHash);
      if ((sameDigest(archiveHash, receipt.patchedAsarSha256) || localizedAccountArchive) && sameDigest(nativeHash, receipt.patchedNativeSha256)) {
        // The archive can remain intact while an updater removes the external
        // component. Do not report a usable installation in that partial state.
        const config = await jsonFile(path.join(resourcesRoot, 'hostauth-addon', 'config.json'));
        if (!config || typeof config.component !== 'string' || path.isAbsolute(config.component))
          throw new Error('Accounts payload is incomplete; use Restore and Install-Preview');
        const addonRoot = path.join(resourcesRoot, 'hostauth-addon');
        const component = path.resolve(addonRoot, config.component);
        const relative = path.relative(addonRoot, component);
        if (!relative || relative === '..' || relative.startsWith('..' + path.sep) || path.isAbsolute(relative))
          throw new Error('Accounts payload path is invalid');
        for (const filename of [component, path.join(addonRoot, 'native', 'language_server.exe')]) {
          const info = await lstat(filename);
          if (!info.isFile() || info.isSymbolicLink()) throw new Error('Accounts payload is incomplete; use Restore and Install-Preview');
        }
        return { status: 'ready', credentialFilesRead: false };
      }
      try { await checkStopped({ installRoot }); }
      catch (error) {
        if (applicationBusy(error)) return { status: 'pending_idle', credentialFilesRead: false };
        throw error;
      }
      lock.assertHeld?.();
      let beforeInvoked = false, installed = false, failure;
      try {
        beforeInvoked = true;
        await beforeApply?.({ installRoot, resourcesRoot, receipt });
        lock.assertHeld?.();
        await manage({ operation: 'apply', installRoot, packageRoot, stateRoot, checkStopped,
          // The outer coordinator owns this exact installation's mutex.
          runLocked: (target, action) => {
            if (!samePath(target, resourcesRoot)) throw new Error('Account recovery installation changed');
            lock.assertHeld?.();
            return action(lock);
          } });
        installed = true;
      } catch (error) { failure = error; }
      finally {
        if (beforeInvoked) {
          try { await afterApply?.({ installRoot, resourcesRoot, installed, error: failure }); }
          catch (error) {
            if (!failure) failure = error;
            else failure = new AggregateError([failure, error], 'Accounts repair and localization recovery failed');
          }
        }
      }
      if (failure) {
        if (!compatibilityFailure(failure)) throw failure;
        lock.assertHeld?.();
        // A failed adaptation can leave the official update untouched. Only
        // that complete, unadapted program may use its own native sign-in.
        const program = await inspectFallback({ installRoot, receipt });
        return { status: 'degraded', mode: 'native', reason: failure.message, program, credentialFilesRead: false };
      }
      return { status: 'repaired', runtimeVerified: false, credentialFilesRead: false };
    });
  } catch (error) {
    if (error.message === 'accounts_installation_busy') return { status: 'pending_update', credentialFilesRead: false };
    throw error;
  }
}

if (process.argv[1] && comparable(path.resolve(process.argv[1])) === comparable(fileURLToPath(import.meta.url))) {
  try {
    if (process.platform !== 'win32') throw new Error('This account adapter only supports Windows');
    if (process.versions.electron) process.noAsar = true;
    const options = new Map();
    for (let index = 2; index < process.argv.length; index += 2) {
      if (!['--install-root', '--package-root', '--state-root'].includes(process.argv[index]) || !process.argv[index + 1] || options.has(process.argv[index]))
        throw new Error('Unknown, repeated or incomplete recovery argument');
      options.set(process.argv[index], process.argv[index + 1]);
    }
    const result = await ensureAccounts({
      installRoot: options.get('--install-root') || path.join(process.env.LOCALAPPDATA, 'Programs', 'antigravity'),
      packageRoot: options.get('--package-root') || path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..'),
      stateRoot: options.get('--state-root') || path.join(process.env.LOCALAPPDATA, 'AntigravityAccounts')
    });
    console.log(JSON.stringify(result, null, 2));
    if (['pending_idle', 'pending_update'].includes(result.status)) process.exitCode = 2;
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
