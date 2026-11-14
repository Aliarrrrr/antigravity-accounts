'use strict';

// Electron redirects .asar paths through a virtual filesystem. Program-image
// metadata and digests must describe the disk file, without changing the
// application's global process.noAsar setting or any other user's fs module.
const diskFs = require(process.versions.electron ? 'original-fs' : 'node:fs');
const fs = diskFs.promises;
const path = require('node:path');
const { createHash, randomUUID } = require('node:crypto');
const { createReadStream } = diskFs;
const { spawn } = require('node:child_process');

const installations = new WeakMap();
const samePath = (left, right) => path.resolve(left).toLowerCase() === path.resolve(right).toLowerCase();
const inside = (file, root) => {
  const relative = path.relative(path.resolve(root), path.resolve(file));
  return relative !== '' && relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative);
};
function fail(code) { throw Object.assign(new Error(code), { code }); }
async function regular(filename) {
  const info = await fs.lstat(filename);
  if (!info.isFile() || info.isSymbolicLink()) fail('update_handoff_file_invalid');
  return info;
}
async function fileDigest(filename) {
  const digest = createHash('sha256');
  for await (const bytes of createReadStream(filename)) digest.update(bytes);
  return digest.digest('hex');
}

async function accountStateRoot(resourcesRoot, installRoot, localAppData) {
  let config;
  try { config = JSON.parse(await fs.readFile(path.join(resourcesRoot, 'hostauth-addon', 'config.json'), 'utf8')); }
  catch (error) { if (error.code !== 'ENOENT') fail('update_handoff_configuration_invalid'); }
  if (config !== undefined && (!config || typeof config !== 'object' || Array.isArray(config))) fail('update_handoff_configuration_invalid');
  if (config && Object.prototype.hasOwnProperty.call(config, 'accountsStateRoot')) {
    const value = config.accountsStateRoot;
    if (typeof value !== 'string' || !path.isAbsolute(value) || /[\0\r\n]/.test(value)) fail('update_handoff_configuration_invalid');
    const root = path.resolve(value);
    try {
      const physical = await fs.realpath(root);
      if (!(await fs.stat(root)).isDirectory() || inside(root, installRoot) || samePath(root, installRoot) ||
          inside(physical, installRoot) || samePath(physical, installRoot)) fail('update_handoff_configuration_invalid');
    } catch { fail('update_handoff_configuration_invalid'); }
    return root;
  }
  if (typeof localAppData !== 'string' || !path.isAbsolute(localAppData)) fail('update_handoff_configuration_invalid');
  return path.join(localAppData, 'AntigravityAccounts');
}

// Public program/installer metadata only. The account component and vault are
// not opened by the handoff. The PowerShell child outlives the replaced App.
async function prepareHandoff({ autoUpdater, resourcesPath, localAppData, appPid,
  systemRoot, spawnHelper = spawn, handshakeTimeoutMs = 15000 }) {
  if (![resourcesPath, systemRoot].every(value => typeof value === 'string' && path.isAbsolute(value)) ||
      !Number.isSafeInteger(appPid) || appPid < 1) fail('update_handoff_configuration_invalid');
  const resourcesRoot = await fs.realpath(resourcesPath);
  const installRoot = path.dirname(resourcesRoot);
  const stateRoot = await accountStateRoot(resourcesRoot, installRoot, localAppData);
  const receiptFile = path.join(stateRoot, 'adapter-installation.json');
  await regular(receiptFile);
  const receipt = JSON.parse(await fs.readFile(receiptFile, 'utf8'));
  if (receipt.schemaVersion !== 1 || receipt.phase !== 'applied' || !path.isAbsolute(receipt.resourcesRoot || '') ||
      !samePath(receipt.resourcesRoot, resourcesRoot) || !path.isAbsolute(receipt.packageRoot || '') ||
      !samePath(path.dirname(receipt.packageRoot), stateRoot) || !path.basename(receipt.packageRoot).startsWith('recovery-package-'))
    fail('update_handoff_receipt_invalid');
  const packageRoot = await fs.realpath(receipt.packageRoot);
  if (inside(packageRoot, installRoot) || samePath(packageRoot, installRoot)) fail('update_handoff_cache_invalid');
  const helper = path.join(packageRoot, 'account-plugin', 'Update-Handoff.ps1');
  const recovery = path.join(packageRoot, 'account-plugin', 'recovery-and-launch.mjs');
  const directoryReader = path.join(packageRoot, 'account-plugin', 'Process-WorkingDirectory.ps1');
  await regular(helper); await regular(recovery); await regular(directoryReader);
  const cacheManifest = JSON.parse(await fs.readFile(path.join(packageRoot, 'package-manifest.json'), 'utf8'));
  if (cacheManifest.role !== 'accounts-recovery-package' || !Array.isArray(cacheManifest.files)) fail('update_handoff_cache_invalid');
  for (const [relative, filename] of [['account-plugin/Update-Handoff.ps1', helper], ['account-plugin/recovery-and-launch.mjs', recovery],
    ['account-plugin/Process-WorkingDirectory.ps1', directoryReader]]) {
    const listed = cacheManifest.files.filter(item => item.path === relative);
    if (listed.length !== 1 || listed[0].sha256 !== await fileDigest(filename)) fail('update_handoff_cache_invalid');
  }
  const installerPath = autoUpdater.installerPath;
  const updaterRoot = autoUpdater.downloadedUpdateHelper?.cacheDir;
  if (!path.isAbsolute(installerPath || '') || !path.isAbsolute(updaterRoot || '') ||
      !inside(installerPath, path.join(updaterRoot, 'pending')) || path.extname(installerPath).toLowerCase() !== '.exe')
    fail('update_handoff_installer_unavailable');
  const installerInfo = await regular(installerPath);
  if (!installerInfo.size || installerInfo.size > 2 * 1024 * 1024 * 1024) fail('update_handoff_installer_invalid');
  const programFiles = ['Antigravity.exe', 'resources/app.asar', 'resources/bin/language_server.exe'];
  const programState = [];
  for (const relative of programFiles) {
    const filename = path.join(installRoot, relative), info = await regular(filename);
    programState.push({ relative, length: info.size, lastWriteUtcMs: info.mtimeMs });
  }
  const handoffRoot = path.join(stateRoot, 'update-handoffs');
  await fs.mkdir(handoffRoot, { recursive: true });
  const requestFile = path.join(handoffRoot, 'handoff-' + randomUUID() + '.json');
  const request = { schemaVersion: 1, installRoot, resourcesRoot, stateRoot, packageRoot, appPid,
    receiptFile: await fs.realpath(receiptFile), packageRootLogical: receipt.packageRoot,
    installerPath: await fs.realpath(installerPath), updaterRoot: await fs.realpath(updaterRoot),
    installerSha256: await fileDigest(installerPath), archiveSha256: await fileDigest(path.join(resourcesRoot, 'app.asar')), programState };
  try {
    const registrationFile = path.join(stateRoot, 'guarded-launcher.json');
    await regular(registrationFile); request.guardedLauncherFile = await fs.realpath(registrationFile);
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
  await fs.writeFile(requestFile, JSON.stringify(request, null, 2) + '\n', { flag: 'wx' });
  const powershell = path.join(systemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  const child = spawnHelper(powershell, ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
    '-File', helper, '-RequestFile', await fs.realpath(requestFile)], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  child.stdin?.on('error', () => {});
  child.stderr?.resume();
  await new Promise((resolve, reject) => {
    let output = '', settled = false;
    const finish = error => {
      if (settled) return;
      settled = true; clearTimeout(timer);
      if (error) { child.kill(); reject(error); } else resolve();
    };
    const timer = setTimeout(() => finish(Object.assign(new Error('update_handoff_timeout'), { code: 'update_handoff_timeout' })), handshakeTimeoutMs);
    child.once('error', () => finish(Object.assign(new Error('update_handoff_start_failed'), { code: 'update_handoff_start_failed' })));
    child.once('exit', () => finish(Object.assign(new Error('update_handoff_start_failed'), { code: 'update_handoff_start_failed' })));
    child.stdout?.on('data', bytes => {
      output += bytes;
      if (output.length > 1024) return finish(Object.assign(new Error('update_handoff_response_invalid'), { code: 'update_handoff_response_invalid' }));
      if (output.includes('\n')) finish(output.slice(0, output.indexOf('\n')).trim() === 'ready' ? undefined :
        Object.assign(new Error('update_handoff_response_invalid'), { code: 'update_handoff_response_invalid' }));
    });
  });
  child.stdin?.end();
  child.stdout?.destroy(); child.stderr?.destroy(); child.unref();
  return { requestFile, cancel: () => child.kill() };
}

// Keep the native updater's installation UI choice. Only automatic direct-EXE
// relaunch is suppressed both for a guarded restart and an unavailable helper.
function install({ electron, autoUpdater, resourcesPath = process.resourcesPath,
  localAppData = process.env.LOCALAPPDATA, systemRoot = process.env.SystemRoot,
  appPid = process.pid, platform = process.platform, prepare = prepareHandoff, onFailure } = {}) {
  if (platform !== 'win32' || !autoUpdater || typeof autoUpdater.quitAndInstall !== 'function') return { installed: false };
  if (installations.has(autoUpdater)) return installations.get(autoUpdater);
  const original = autoUpdater.quitAndInstall;
  let pending, stopped = false;
  const notify = (error, nativeUpdateContinues = false) => {
    const code = /^update_handoff_[a-z_]+$/.test(error?.code || '') ? error.code : 'update_handoff_failed';
    if (onFailure) {
      try { onFailure(code, { nativeUpdateContinues }); } catch { /* Notification failure must not stop the native updater. */ }
      return;
    }
    const message = nativeUpdateContinues
      ? 'Automatic Accounts repair and restart could not be prepared (' + code + '). Antigravity will finish its normal update and remain closed. After installation, use your existing Chinese shortcut to confirm the account for native sign-in.'
      : 'Accounts update restart failed (' + code + ').';
    const failure = new Error(message);
    try { autoUpdater.emit?.('error', failure); } catch { /* Keep failures contained in native menu callers. */ }
    try { electron?.dialog?.showErrorBox('Antigravity Accounts', failure.message); } catch { /* Native notification is best-effort. */ }
  };
  autoUpdater.quitAndInstall = function (isSilent = false) {
    if (stopped || pending || autoUpdater.quitAndInstallCalled) return;
    if (typeof isSilent !== 'boolean') { notify({ code: 'update_handoff_arguments_invalid' }); return; }
    const nativeContext = this, nativeArguments = Array.from(arguments);
    pending = (async () => {
      let handoff;
      try {
        try { handoff = await prepare({ autoUpdater, resourcesPath, localAppData, appPid, systemRoot }); }
        catch (error) {
          if (stopped) return;
          notify(error, true);
          const previous = nativeContext.autoRunAppAfterInstall;
          try {
            // The update still proceeds, while the guarded shortcut owns the
            // account choice after an unavailable repair/relaunch handoff.
            nativeContext.autoRunAppAfterInstall = false;
            original.apply(nativeContext, [isSilent, false, ...nativeArguments.slice(2)]);
            if (nativeContext.quitAndInstallCalled === false) fail('update_handoff_install_rejected');
          } finally { nativeContext.autoRunAppAfterInstall = previous; }
          return;
        }
        if (stopped) { handoff.cancel(); return; }
        const previous = autoUpdater.autoRunAppAfterInstall;
        try {
          autoUpdater.autoRunAppAfterInstall = false;
          original.call(nativeContext, isSilent, false);
          if (nativeContext.quitAndInstallCalled === false) fail('update_handoff_install_rejected');
        } finally { autoUpdater.autoRunAppAfterInstall = previous; }
      } catch (error) { handoff?.cancel(); notify(error); }
    })().finally(() => { pending = undefined; });
  };
  const handle = { installed: true, get pending() { return pending ?? Promise.resolve(); },
    restore() { stopped = true; if (autoUpdater.quitAndInstall !== original) autoUpdater.quitAndInstall = original; installations.delete(autoUpdater); } };
  installations.set(autoUpdater, handle);
  return handle;
}

module.exports = { install, prepareHandoff };
