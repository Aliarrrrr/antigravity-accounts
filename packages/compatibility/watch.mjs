import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { checkForUpdates, activateBundle, writeJSON } from './release-client.mjs';
import { notifyUpdate, reconcileWindows } from './windows-registration.mjs';

async function readJSON(filename) { try { return JSON.parse(await fs.readFile(filename, 'utf8')); } catch (error) { if (error.code !== 'ENOENT') throw error; } }
export async function hostStamp(installRoot) {
  return JSON.stringify(await Promise.all(['Antigravity.exe', 'resources/app.asar', 'resources/bin/language_server.exe'].map(async file => {
    try { const value = await fs.stat(path.join(installRoot, file)); return [file, value.size, value.mtimeMs, value.birthtimeMs]; }
    catch (error) { if (error.code === 'ENOENT') return [file, null]; throw error; }
  })));
}

export async function watchCompatibility({ config, bundleRoot, runEngine, check = checkForUpdates, activate = activateBundle,
  pause = ms => new Promise(resolve => setTimeout(resolve, ms)), intervalMs = 5000, maxTicks = Infinity, acquireLock,
  notify = notifyUpdate, reconcile = reconcileWindows } = {}) {
  if (typeof runEngine !== 'function') throw new Error('compatibility_watcher_configuration_invalid');
  if (!acquireLock) {
    const locks = await import(pathToFileURL(path.join(bundleRoot, 'localization', 'src', 'process-lock.mjs')).href);
    acquireLock = locks.acquireProcessLock;
  }
  const release = await acquireLock(config.stateRoot, 'watcher.lock');
  if (!release) return { status: 'watcher_already_running' };
  let previousStamp, phase, restart = false;
  async function notice(errorCode) {
    const filename = path.join(config.stateRoot, 'update-notice.json');
    const previous = await readJSON(filename);
    if (previous?.seen?.[errorCode] || previous?.errorCode === errorCode && previous.shown === true) return;
    // Persist the attempt before invoking the desktop helper. A failed popup
    // must not spam every five seconds or block the normal launcher.
    const attemptedUtc = new Date().toISOString();
    await writeJSON(filename, { schemaVersion: 1, errorCode, shown: true, attemptedUtc, seen: { ...previous?.seen, [errorCode]: attemptedUtc } });
    try { await notify(errorCode); } catch { /* Notification delivery does not own application startup. */ }
  }
  try {
    for (let tick = 0; tick < maxTicks; tick++) {
      release.assertHeld?.();
      if (await fs.stat(path.join(config.stateRoot, 'disabled')).then(() => true, error => { if (error.code === 'ENOENT') return false; throw error; })) break;
      try {
        const stamp = await hostStamp(config.installRoot);
        const active = await readJSON(path.join(config.stateRoot, 'active-version.json'));
        if (!active?.version) throw new Error('compatibility_active_version_invalid');
        const update = await check({ stateRoot: config.stateRoot, currentVersion: active.version, hostStamp: stamp });
        if (update.status === 'unavailable') await notice(update.errorCode);
        const pending = await readJSON(path.join(config.stateRoot, 'pending-bundle.json'));
        const candidate = pending?.bundleRoot || bundleRoot;
        if (stamp !== previousStamp || ['pending_idle', 'pending_update', 'busy'].includes(phase) || candidate !== bundleRoot) {
          const result = await runEngine(candidate, 'ensure', config);
          phase = result.installationState || result.phase || result.status;
          previousStamp = await hostStamp(config.installRoot);
          if (result.activationAllowed === true) await reconcile(config.stateRoot);
          if (candidate !== bundleRoot && result.activationAllowed === true) {
            await activate({ stateRoot: config.stateRoot, bundleRoot: candidate, config });
            await fs.rm(path.join(config.stateRoot, 'pending-bundle.json'), { force: true });
            restart = true;
            break;
          }
        }
      } catch (error) {
        phase = 'failed';
        const errorCode = /^[a-z][a-z0-9_]{0,90}$/.test(error.message || '') ? error.message : 'compatibility_watcher_failed';
        await notice(errorCode);
      }
      if (tick + 1 < maxTicks) await pause(intervalMs);
    }
  } finally { await release(); }
  return { status: restart ? 'watcher_restarts_for_engine_update' : 'watcher_stopped', restart };
}
