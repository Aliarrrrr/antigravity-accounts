import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
const { install, prepareHandoff } = createRequire(import.meta.url)('../src/update-handoff.cjs');

test('native update waits for readiness, preserves installer UI mode and suppresses only force-run', async () => {
  let acknowledge, prepares = 0, cancelled = 0;
  const calls = [], failures = [];
  const updater = { autoRunAppAfterInstall: true, quitAndInstallCalled: false,
    quitAndInstall(isSilent = false, force = false) {
      calls.push({ isSilent, force: isSilent ? force : this.autoRunAppAfterInstall }); this.quitAndInstallCalled = true;
    } };
  const original = updater.quitAndInstall;
  const handle = install({ autoUpdater: updater, platform: 'win32', onFailure: code => failures.push(code),
    prepare: async () => { prepares++; await new Promise(resolve => { acknowledge = resolve; }); return { cancel() { cancelled++; } }; } });
  updater.quitAndInstall(); updater.quitAndInstall();
  assert.equal(prepares, 1); assert.equal(calls.length, 0);
  acknowledge(); await handle.pending;
  assert.deepEqual(calls, [{ isSilent: false, force: false }]);
  assert.equal(updater.autoRunAppAfterInstall, true);
  updater.quitAndInstall(); await handle.pending;
  assert.equal(prepares, 1); assert.deepEqual(failures, []); assert.equal(cancelled, 0);
  handle.restore(); assert.equal(updater.quitAndInstall, original);
});

test('silent native updates retain silent mode and an explicitly requested force-run is not forwarded', async () => {
  const calls = [];
  const updater = { autoRunAppAfterInstall: true, quitAndInstallCalled: false,
    quitAndInstall(isSilent, force) { calls.push([isSilent, force]); this.quitAndInstallCalled = true; } };
  const handle = install({ autoUpdater: updater, platform: 'win32', prepare: async () => ({ cancel() {} }) });
  updater.quitAndInstall(true, true); await handle.pending;
  assert.deepEqual(calls, [[true, false]]);
});

for (const scenario of ['readiness-error', 'native-throws', 'native-rejects']) {
  test(`update ${scenario} is contained and never leaves an unhandled rejection`, async () => {
    const failures = []; let nativeCalls = 0, cancelled = 0;
    const updater = { autoRunAppAfterInstall: true, quitAndInstallCalled: false,
      quitAndInstall() { nativeCalls++; if (scenario === 'native-throws') throw new Error('DO_NOT_EXPOSE_RAW_INSTALLER_ERROR'); } };
    const handle = install({ autoUpdater: updater, platform: 'win32', onFailure: code => failures.push(code),
      prepare: async () => {
        if (scenario === 'readiness-error') throw Object.assign(new Error('DO_NOT_EXPOSE_RAW_PATH'), { code: 'update_handoff_timeout' });
        return { cancel() { cancelled++; } };
      } });
    assert.equal(updater.quitAndInstall(), undefined);
    await handle.pending;
    assert.equal(nativeCalls, scenario === 'readiness-error' ? 0 : 1);
    assert.equal(cancelled, scenario === 'readiness-error' ? 0 : 1);
    assert.equal(updater.autoRunAppAfterInstall, true);
    assert.deepEqual(failures, [scenario === 'readiness-error' ? 'update_handoff_timeout' : scenario === 'native-rejects' ? 'update_handoff_install_rejected' : 'update_handoff_failed']);
  });
}

async function fixture(t) {
  const temporary = await fs.realpath(os.tmpdir());
  const root = await fs.mkdtemp(path.join(temporary, 'accounts-update-handoff-'));
  t.after(async () => {
    const resolved = await fs.realpath(root);
    assert.equal(path.dirname(resolved), temporary); assert.match(path.basename(resolved), /^accounts-update-handoff-/);
    await fs.rm(resolved, { recursive: true, force: true });
  });
  const installRoot = path.join(root, 'App with spaces'), resourcesPath = path.join(installRoot, 'resources');
  const localAppData = path.join(root, 'local'), stateRoot = path.join(localAppData, 'AntigravityAccounts');
  const packageRoot = path.join(stateRoot, 'recovery-package-test');
  const updaterRoot = path.join(localAppData, 'antigravity-updater'), installerPath = path.join(updaterRoot, 'pending', 'AntigravitySetup.exe');
  for (const directory of [path.join(resourcesPath, 'bin'), path.join(packageRoot, 'account-plugin'), path.dirname(installerPath)]) await fs.mkdir(directory, { recursive: true });
  await fs.writeFile(path.join(installRoot, 'Antigravity.exe'), 'synthetic-program');
  await fs.writeFile(path.join(resourcesPath, 'app.asar'), 'synthetic-adapted-asar');
  await fs.writeFile(path.join(resourcesPath, 'bin/language_server.exe'), 'synthetic-native-program');
  await fs.writeFile(installerPath, 'synthetic-installer-not-executed');
  const listed = [];
  for (const relative of ['account-plugin/Update-Handoff.ps1', 'account-plugin/recovery-and-launch.mjs']) {
    const bytes = await fs.readFile(new URL('../../' + relative, import.meta.url));
    await fs.writeFile(path.join(packageRoot, relative), bytes);
    listed.push({ path: relative, sha256: createHash('sha256').update(bytes).digest('hex') });
  }
  await fs.writeFile(path.join(packageRoot, 'package-manifest.json'), JSON.stringify({ role: 'accounts-recovery-package', files: listed }));
  await fs.writeFile(path.join(stateRoot, 'adapter-installation.json'), JSON.stringify({ schemaVersion: 1, phase: 'applied', resourcesRoot: resourcesPath, packageRoot }));
  return { root, installRoot, resourcesPath, localAppData, stateRoot, packageRoot, updaterRoot, installerPath,
    autoUpdater: { installerPath, downloadedUpdateHelper: { cacheDir: updaterRoot } } };
}

test('readiness handoff uses only public metadata and validates helper bytes before spawning', async t => {
  const f = await fixture(t);
  let child, invocation;
  const result = await prepareHandoff({ ...f, appPid: process.pid, systemRoot: process.env.SystemRoot,
    spawnHelper(executable, args, options) {
      invocation = { executable, args, options };
      child = new EventEmitter(); child.stdin = new PassThrough(); child.stdout = new PassThrough(); child.stderr = new PassThrough();
      child.kill = () => { child.emit('exit', 1); return true; }; child.unref = () => {};
      setImmediate(() => child.stdout.write('ready\n')); return child;
    } });
  assert.match(invocation.executable, /powershell\.exe$/i); assert.equal(invocation.options.windowsHide, true);
  const request = JSON.parse(await fs.readFile(result.requestFile, 'utf8'));
  assert.equal(request.appPid, process.pid); assert.equal(request.installerPath, await fs.realpath(f.installerPath));
  assert.equal(request.receiptFile, await fs.realpath(path.join(f.stateRoot, 'adapter-installation.json')));
  assert.equal(JSON.stringify(request).includes('synthetic-installer-not-executed'), false);
  assert.equal(/(?:accessToken|refreshToken|vault|dpapi)/i.test(JSON.stringify(request)), false);
  await fs.writeFile(path.join(f.packageRoot, 'account-plugin/Update-Handoff.ps1'), 'changed helper');
  await assert.rejects(prepareHandoff({ ...f, appPid: process.pid, systemRoot: process.env.SystemRoot,
    spawnHelper() { assert.fail('Changed helper cannot run'); } }), { code: 'update_handoff_cache_invalid' });
});

function fakeReadyHelper() {
  const child = new EventEmitter(); child.stdin = new PassThrough(); child.stdout = new PassThrough(); child.stderr = new PassThrough();
  child.kill = () => { child.emit('exit', 1); return true; }; child.unref = () => {};
  setImmediate(() => child.stdout.write('ready\n'));
  return child;
}

test('the installed account directory pins receipt, cache, handoff and launcher across differing LOCALAPPDATA inputs', async t => {
  const f = await fixture(t);
  const configPath = path.join(f.resourcesPath, 'hostauth-addon', 'config.json');
  await fs.mkdir(path.dirname(configPath));
  await fs.writeFile(configPath, JSON.stringify({ schemaVersion: 1, accountsStateRoot: f.stateRoot }));
  await fs.writeFile(path.join(f.stateRoot, 'guarded-launcher.json'), '{"synthetic":"public-launcher"}');
  for (const localAppData of [f.localAppData, path.join(f.root, 'different-local-environment'), undefined]) {
    const result = await prepareHandoff({ ...f, localAppData, appPid: process.pid, systemRoot: process.env.SystemRoot, spawnHelper: fakeReadyHelper });
    const request = JSON.parse(await fs.readFile(result.requestFile, 'utf8'));
    assert.equal(request.stateRoot, f.stateRoot);
    assert.equal(path.dirname(result.requestFile), path.join(f.stateRoot, 'update-handoffs'));
    assert.equal(request.receiptFile, await fs.realpath(path.join(f.stateRoot, 'adapter-installation.json')));
    assert.equal(request.packageRoot, await fs.realpath(f.packageRoot));
    assert.equal(request.packageRootLogical, f.packageRoot);
    assert.equal(request.guardedLauncherFile, await fs.realpath(path.join(f.stateRoot, 'guarded-launcher.json')));
  }
  await assert.rejects(fs.access(path.join(f.root, 'different-local-environment/AntigravityAccounts/update-handoffs')), /ENOENT/);
});

test('invalid explicit account roots and malformed configuration never fall back to the ambient valid receipt', async t => {
  const f = await fixture(t);
  const configPath = path.join(f.resourcesPath, 'hostauth-addon', 'config.json');
  await fs.mkdir(path.dirname(configPath));
  const fileRoot = path.join(f.root, 'not-a-directory');
  await fs.writeFile(fileRoot, 'synthetic-public-path');
  const configs = [null, '', 'relative-directory', path.join(f.root, 'missing-directory'), f.installRoot, f.resourcesPath, fileRoot]
    .map(accountsStateRoot => JSON.stringify({ accountsStateRoot }));
  configs.push('{', 'null', '[]');
  for (const config of configs) {
    await fs.writeFile(configPath, config);
    await assert.rejects(prepareHandoff({ ...f, appPid: process.pid, systemRoot: process.env.SystemRoot,
      spawnHelper() { assert.fail('Invalid pinned roots must not launch the default-root helper'); } }), { code: 'update_handoff_configuration_invalid' });
  }
  await assert.rejects(fs.access(path.join(f.stateRoot, 'update-handoffs')), /ENOENT/);
});

test('legacy configuration without an explicit account root retains the localAppData interface', async t => {
  const f = await fixture(t);
  const configPath = path.join(f.resourcesPath, 'hostauth-addon', 'config.json');
  await fs.mkdir(path.dirname(configPath));
  await fs.writeFile(configPath, '{"schemaVersion":1}');
  const result = await prepareHandoff({ ...f, appPid: process.pid, systemRoot: process.env.SystemRoot, spawnHelper: fakeReadyHelper });
  const request = JSON.parse(await fs.readFile(result.requestFile, 'utf8'));
  assert.equal(request.stateRoot, path.join(f.localAppData, 'AntigravityAccounts'));
  await assert.rejects(prepareHandoff({ ...f, localAppData: 'relative-environment', appPid: process.pid, systemRoot: process.env.SystemRoot,
    spawnHelper() { assert.fail('Legacy configuration still requires an absolute ambient directory'); } }), { code: 'update_handoff_configuration_invalid' });
});

test('a helper that does not acknowledge readiness is stopped before installation', async t => {
  const f = await fixture(t); let killed = 0;
  await assert.rejects(prepareHandoff({ ...f, appPid: process.pid, systemRoot: process.env.SystemRoot, handshakeTimeoutMs: 25,
    spawnHelper() {
      const child = new EventEmitter(); child.stdin = new PassThrough(); child.stdout = new PassThrough(); child.stderr = new PassThrough();
      child.kill = () => { killed++; child.emit('exit', 1); return true; }; return child;
    } }), { code: 'update_handoff_timeout' });
  assert.equal(killed, 1);
});

test('Windows helper observes installer completion and dispatches the registered guard after the App exits', {
  skip: process.platform !== 'win32', timeout: 30000,
}, async t => {
  const f = await fixture(t);
  // These are copies of Node, never the official App or a real updater.
  await fs.copyFile(process.execPath, path.join(f.installRoot, 'Antigravity.exe'));
  await fs.copyFile(process.execPath, f.installerPath);
  const toolkitRoot = path.join(f.root, 'synthetic guard'), guardState = path.join(f.root, 'guard state');
  await fs.mkdir(path.join(toolkitRoot, 'scripts'), { recursive: true });
  await fs.mkdir(path.join(guardState, 'windows-persistence'), { recursive: true });
  const marker = path.join(guardState, 'guard-dispatched.txt');
  await fs.writeFile(path.join(toolkitRoot, 'scripts/Launch-Localized.vbs'),
    'Option Explicit\nDim f,s\nIf WScript.Arguments.Count <> 4 Then WScript.Quit 3\nSet f=CreateObject("Scripting.FileSystemObject")\nSet s=f.CreateTextFile(WScript.Arguments(3) & "\\guard-dispatched.txt",True)\ns.Write "guarded"\ns.Close\n');
  const launcher = { schemaVersion: 1, kind: 'localization', installRoot: f.installRoot, toolkitRoot, stateRoot: guardState, nodePath: process.execPath };
  await fs.writeFile(path.join(f.stateRoot, 'guarded-launcher.json'), JSON.stringify(launcher));
  await fs.writeFile(path.join(guardState, 'windows-persistence/install.json'), JSON.stringify({ ...launcher, installed: true }));
  const app = spawn(path.join(f.installRoot, 'Antigravity.exe'), ['-e', 'process.stdin.resume();process.stdin.on("end",()=>process.exit(0))'], {
    windowsHide: true, stdio: ['pipe', 'ignore', 'ignore'],
  });
  app.stdin.on('error', () => {});
  t.after(() => { if (app.exitCode === null) app.kill(); });
  await new Promise((resolve, reject) => { app.once('spawn', resolve); app.once('error', reject); });
  let result, helperErrors = '', requestFilename;
  try {
    result = await prepareHandoff({ ...f, appPid: app.pid, systemRoot: process.env.SystemRoot,
      spawnHelper(executable, args, options) {
        requestFilename = args[args.indexOf('-RequestFile') + 1];
        const helper = spawn(executable, [...args, '-TimeoutSeconds', '20', '-SettleSeconds', '1'], options);
        helper.stderr.on('data', bytes => { helperErrors += bytes; });
        return helper;
      } });
  } catch (error) {
    app.stdin.end();
    await new Promise(resolve => { if (app.exitCode !== null) resolve(); else app.once('exit', resolve); });
    let outcome;
    try { outcome = await fs.readFile(requestFilename + '.result.json', 'utf8'); } catch {}
    assert.fail(error.message + ': ' + helperErrors + ' ' + outcome);
  }
  const installer = spawn(f.installerPath, ['-e',
    'require("node:fs").writeFileSync(process.argv[1],"synthetic updated archive");setTimeout(()=>process.exit(0),1200)',
    path.join(f.resourcesPath, 'app.asar')], { windowsHide: true, stdio: 'ignore' });
  const installerDone = new Promise((resolve, reject) => { installer.once('exit', resolve); installer.once('error', reject); });
  app.stdin.end();
  assert.equal(await installerDone, 0);
  const deadline = Date.now() + 20000;
  let outcome;
  while (Date.now() < deadline) {
    try { outcome = JSON.parse(await fs.readFile(result.requestFile + '.result.json', 'utf8')); } catch { /* Wait for completed helper write. */ }
    if (['launched', 'error'].includes(outcome?.status)) break;
    await new Promise(resolve => setTimeout(resolve, 150));
  }
  assert.equal(outcome?.status, 'launched', JSON.stringify(outcome));
  assert.equal(await fs.readFile(marker, 'utf8'), 'guarded');
  assert.equal(outcome.modelCalls, 0); assert.equal(outcome.accountCalls, 0);
});
