import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { existsSync } from 'node:fs';
import { rewriteAsar } from '../src/asar.mjs';
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
      quitAndInstall() {
        nativeCalls++;
        if (scenario === 'native-throws') throw new Error('DO_NOT_EXPOSE_RAW_INSTALLER_ERROR');
        if (scenario === 'readiness-error') this.quitAndInstallCalled = true;
      } };
    const handle = install({ autoUpdater: updater, platform: 'win32', onFailure: code => failures.push(code),
      prepare: async () => {
        if (scenario === 'readiness-error') throw Object.assign(new Error('DO_NOT_EXPOSE_RAW_PATH'), { code: 'update_handoff_timeout' });
        return { cancel() { cancelled++; } };
      } });
    assert.equal(updater.quitAndInstall(), undefined);
    await handle.pending;
    assert.equal(nativeCalls, 1);
    assert.equal(cancelled, scenario === 'readiness-error' ? 0 : 1);
    assert.equal(updater.autoRunAppAfterInstall, true);
    assert.deepEqual(failures, [scenario === 'readiness-error' ? 'update_handoff_timeout' : scenario === 'native-rejects' ? 'update_handoff_install_rejected' : 'update_handoff_failed']);
  });
}

test('unavailable Accounts handoff continues the official update with its receiver and extra arguments while suppressing direct restart', async () => {
  const calls = [], notices = [];
  let attempts = 0;
  const updater = { autoRunAppAfterInstall: true, quitAndInstallCalled: false,
    quitAndInstall(...args) { calls.push({ receiver: this, args, autoRun: this.autoRunAppAfterInstall }); this.quitAndInstallCalled = true; } };
  const handle = install({ autoUpdater: updater, platform: 'win32',
    onFailure(code, options) { notices.push({ code, ...options }); },
    prepare: async () => { attempts++; throw Object.assign(new Error('missing compatible recovery module'), { code: 'update_handoff_cache_invalid' }); } });
  updater.quitAndInstall(true, true, 'retained installer argument');
  updater.quitAndInstall(false, false);
  await handle.pending;
  assert.equal(attempts, 1);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].receiver, updater);
  assert.deepEqual(calls[0].args, [true, false, 'retained installer argument']);
  assert.equal(calls[0].autoRun, false);
  assert.equal(updater.autoRunAppAfterInstall, true);
  assert.deepEqual(notices, [{ code: 'update_handoff_cache_invalid', nativeUpdateContinues: true }]);
  updater.quitAndInstall(); await handle.pending;
  assert.equal(calls.length, 1);
});

test('handoff failure remains visible while notification errors cannot prevent the official update', async () => {
  const calls = [], messages = [], errors = [];
  const updater = { autoRunAppAfterInstall: false, quitAndInstallCalled: false,
    emit(event, error) { errors.push([event, error.message]); throw new Error('native error listener failed'); },
    quitAndInstall(...args) { calls.push(args); this.quitAndInstallCalled = true; } };
  const handle = install({ autoUpdater: updater, platform: 'win32',
    electron: { dialog: { showErrorBox(title, message) { messages.push([title, message]); } } },
    prepare: async () => { throw Object.assign(new Error('private path must not leak'), { code: 'update_handoff_timeout' }); } });
  updater.quitAndInstall(); await handle.pending;
  assert.deepEqual(calls, [[false, false]]);
  assert.equal(updater.autoRunAppAfterInstall, false);
  assert.equal(errors.length, 1);
  assert.match(messages[0][1], /update_handoff_timeout/);
  assert.match(messages[0][1], /finish its normal update and remain closed/);
  assert.match(messages[0][1], /existing Chinese shortcut to confirm the account/);
  assert.doesNotMatch(messages[0][1], /private path/);
  const another = { quitAndInstallCalled: false, quitAndInstall() { this.quitAndInstallCalled = true; } };
  const anotherHandle = install({ autoUpdater: another, platform: 'win32', onFailure() { throw new Error('failed notification'); },
    prepare: async () => { throw new Error('missing helper'); } });
  another.quitAndInstall(); await anotherHandle.pending;
  assert.equal(another.quitAndInstallCalled, true);
});

test('the native updater receiver is retained when the installed method is invoked with another receiver', async () => {
  const calls = [];
  const receiver = { quitAndInstallCalled: false, autoRunAppAfterInstall: true };
  const updater = { quitAndInstallCalled: false,
    quitAndInstall(...args) { calls.push({ receiver: this, args, autoRun: this.autoRunAppAfterInstall }); this.quitAndInstallCalled = true; } };
  const handle = install({ autoUpdater: updater, platform: 'win32', onFailure() {},
    prepare: async () => { throw new Error('helper unavailable'); } });
  updater.quitAndInstall.call(receiver, false, true);
  await handle.pending;
  assert.equal(calls.length, 1);
  assert.equal(calls[0].receiver, receiver);
  assert.deepEqual(calls[0].args, [false, false]);
  assert.equal(calls[0].autoRun, false);
  assert.equal(receiver.autoRunAppAfterInstall, true);
  assert.equal(receiver.quitAndInstallCalled, true);
  assert.equal(updater.quitAndInstallCalled, false);
});

test('an official installer failure after unavailable handoff restores relaunch configuration and remains contained', async () => {
  const failures = [], observed = [];
  const updater = { autoRunAppAfterInstall: true, quitAndInstallCalled: false,
    quitAndInstall(...args) {
      observed.push({ args, autoRun: this.autoRunAppAfterInstall });
      throw new Error('private native installer failure');
    } };
  const handle = install({ autoUpdater: updater, platform: 'win32', onFailure: code => failures.push(code),
    prepare: async () => { throw Object.assign(new Error('private preparation failure'), { code: 'update_handoff_cache_invalid' }); } });
  assert.equal(updater.quitAndInstall(false, true), undefined);
  await handle.pending;
  assert.deepEqual(observed, [{ args: [false, false], autoRun: false }]);
  assert.equal(updater.autoRunAppAfterInstall, true);
  assert.deepEqual(failures, ['update_handoff_cache_invalid', 'update_handoff_failed']);
});

test('restoring the hook while preparation is pending cancels takeover of the native updater', async () => {
  let rejectPreparation, calls = 0;
  const updater = { quitAndInstallCalled: false, quitAndInstall() { calls++; } };
  const original = updater.quitAndInstall;
  const handle = install({ autoUpdater: updater, platform: 'win32',
    prepare: () => new Promise((resolve, reject) => { rejectPreparation = reject; }) });
  updater.quitAndInstall();
  const pending = handle.pending;
  handle.restore();
  rejectPreparation(new Error('late preparation failure'));
  await pending;
  assert.equal(calls, 0);
  assert.equal(updater.quitAndInstall, original);
});

async function fixture(t) {
  const temporary = await fs.realpath(os.tmpdir());
  const root = await fs.mkdtemp(path.join(temporary, 'accounts-update-handoff-'));
  const cleanup = [];
  t.after(async () => {
    for (const action of cleanup) await action();
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
  for (const relative of ['account-plugin/Update-Handoff.ps1', 'account-plugin/recovery-and-launch.mjs', 'account-plugin/Process-WorkingDirectory.ps1']) {
    const bytes = await fs.readFile(new URL('../../' + relative, import.meta.url));
    await fs.writeFile(path.join(packageRoot, relative), bytes);
    listed.push({ path: relative, sha256: createHash('sha256').update(bytes).digest('hex') });
  }
  await fs.writeFile(path.join(packageRoot, 'package-manifest.json'), JSON.stringify({ role: 'accounts-recovery-package', files: listed }));
  await fs.writeFile(path.join(stateRoot, 'adapter-installation.json'), JSON.stringify({ schemaVersion: 1, phase: 'applied', resourcesRoot: resourcesPath, packageRoot }));
  return { root, cleanup, installRoot, resourcesPath, localAppData, stateRoot, packageRoot, updaterRoot, installerPath,
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

const electronExecutable = process.env.ACCOUNTS_ELECTRON_EXECUTABLE || path.join(process.env.LOCALAPPDATA || '', 'Programs/antigravity/Antigravity.exe');
test('real Electron handoff reads the disk ASAR without changing global archive behavior', {
  skip: process.platform !== 'win32' || !existsSync(electronExecutable), timeout: 20000,
}, async t => {
  const f = await fixture(t);
  const json = Buffer.from('{"files":{}}'), header = Buffer.alloc(8 + ((json.length + 3) & ~3));
  header.writeUInt32LE(header.length - 4); header.writeUInt32LE(json.length, 4); json.copy(header, 8);
  const size = Buffer.alloc(8); size.writeUInt32LE(4); size.writeUInt32LE(header.length, 4);
  const archive = rewriteAsar(Buffer.concat([size, header]), new Map([['dummy.txt', Buffer.from('public synthetic program')]]));
  await fs.writeFile(path.join(f.resourcesPath, 'app.asar'), archive);
  const parameters = { resourcesPath: f.resourcesPath, localAppData: f.localAppData, autoUpdater: f.autoUpdater, appPid: process.pid, systemRoot: process.env.SystemRoot };
  const script = `
    const {EventEmitter}=require('node:events'),{PassThrough}=require('node:stream');
    const normal=require('node:fs'),disk=require('original-fs'),crypto=require('node:crypto');
    const {prepareHandoff}=require(${JSON.stringify(path.resolve('packages/hostauth-adapter/src/update-handoff.cjs'))});
    const parameters=${JSON.stringify(parameters)};
    const filename=require('node:path').join(parameters.resourcesPath,'app.asar');
    const before=!!process.noAsar;
    const virtualBefore=normal.lstatSync(filename).isDirectory();
    parameters.spawnHelper=()=>{const child=new EventEmitter();child.stdin=new PassThrough();child.stdout=new PassThrough();child.stderr=new PassThrough();child.kill=()=>true;child.unref=()=>{};setImmediate(()=>child.stdout.write('ready\\n'));return child;};
    prepareHandoff(parameters).then(result=>{
      const request=JSON.parse(disk.readFileSync(result.requestFile,'utf8'));
      const stat=request.programState.find(value=>value.relative==='resources/app.asar');
      console.log(JSON.stringify({virtualBefore,virtualAfter:normal.lstatSync(filename).isDirectory(),globalUnchanged:before===!!process.noAsar,diskSize:stat.length,hashMatches:request.archiveSha256===crypto.createHash('sha256').update(disk.readFileSync(filename)).digest('hex')}));
    }).catch(error=>{console.error(error.code||error.message);process.exitCode=1;});
  `;
  const result = spawnSync(electronExecutable, ['-e', script], { env: { ...process.env, ELECTRON_RUN_AS_NODE: '1' }, windowsHide: true, encoding: 'utf8', timeout: 15000 });
  assert.equal(result.status, 0, result.stderr || result.error?.message);
  assert.deepEqual(JSON.parse(result.stdout), { virtualBefore: true, virtualAfter: true, globalUnchanged: true, diskSize: archive.length, hashMatches: true });
});

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

for (const scenario of ['ordinary update', 'Swarm Board remains', 'Swarm Board has another directory', 'new desktop remains']) {
test(`Windows helper observes installer completion and checks remaining processes: ${scenario}`, {
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
  const userRoot = path.join(f.root, 'user'), roamingRoot = path.join(f.root, 'roaming');
  const helperEnv = { ...process.env, USERPROFILE: userRoot, APPDATA: roamingRoot };
  if (scenario !== 'ordinary update') {
    const lifetime = path.join(f.root, 'background-alive'), ready = path.join(f.root, 'background-ready');
    await fs.writeFile(lifetime, 'alive');
    const stub = 'import fs from "node:fs";fs.writeFileSync(' + JSON.stringify(ready) + ',"ready");setInterval(()=>{if(!fs.existsSync(' + JSON.stringify(lifetime) + '))process.exit(0)},25);';
    let background;
    if (scenario === 'new desktop remains') {
      background = spawn(path.join(f.installRoot, 'Antigravity.exe'), ['--input-type=module', '-e', stub], {
        windowsHide: true, stdio: 'ignore', env: helperEnv,
      });
    } else {
      const boardRoot = path.join(userRoot, '.gemini/antigravity/builtin/plugins/swarm/sidecars/swarm_board');
      const loader = path.join(userRoot, '.gemini/antigravity/lib/node_modules/sidecar_sdk/register_loader.js');
      const shim = path.join(roamingRoot, 'Antigravity/bin/agy-node.cmd');
      const directory = scenario === 'Swarm Board remains' ? boardRoot : path.join(f.root, 'other sidecar');
      for (const folder of [boardRoot, path.dirname(loader), path.dirname(shim), directory]) await fs.mkdir(folder, { recursive: true });
      await fs.writeFile(loader, 'export {};');
      await fs.writeFile(path.join(boardRoot, 'main.mjs'), stub);
      await fs.writeFile(path.join(directory, 'main.mjs'), stub);
      await fs.writeFile(shim, '@echo off\r\nset ELECTRON_RUN_AS_NODE=1\r\n"' + path.join(f.installRoot, 'Antigravity.exe').toLowerCase() + '" %*\r\n');
      const command = '""' + shim + '" --import=' + pathToFileURL(loader).href + ' main.mjs"';
      background = spawn(path.join(process.env.SystemRoot, 'System32/cmd.exe'), ['/c', command], {
        cwd: directory, env: helperEnv, windowsHide: true, windowsVerbatimArguments: true, stdio: 'ignore',
      });
    }
    f.cleanup.push(async () => {
      await fs.rm(lifetime, { force: true });
      if (background.exitCode === null) await new Promise(resolve => {
        const timer = setTimeout(() => { background.kill(); resolve(); }, 3000);
        background.once('exit', () => { clearTimeout(timer); resolve(); });
      });
    });
    const readyDeadline = Date.now() + 5000;
    let started = false;
    while (Date.now() < readyDeadline) {
      try { await fs.access(ready); started = true; break; } catch {}
      if (background.exitCode !== null) break;
      await new Promise(resolve => setTimeout(resolve, 25));
    }
    assert.equal(started, true, 'Synthetic background process did not start');
  }
  const app = spawn(path.join(f.installRoot, 'Antigravity.exe'), ['-e', 'process.stdin.resume();process.stdin.on("end",()=>process.exit(0))'], {
    windowsHide: true, stdio: ['pipe', 'ignore', 'ignore'],
  });
  app.stdin.on('error', () => {});
  f.cleanup.push(async () => {
    if (app.exitCode !== null) return;
    app.stdin.end();
    await new Promise(resolve => {
      const timer = setTimeout(() => { app.kill(); resolve(); }, 3000);
      app.once('exit', () => { clearTimeout(timer); resolve(); });
    });
  });
  await new Promise((resolve, reject) => { app.once('spawn', resolve); app.once('error', reject); });
  let result, helperErrors = '', requestFilename;
  try {
    result = await prepareHandoff({ ...f, appPid: app.pid, systemRoot: process.env.SystemRoot,
      spawnHelper(executable, args, options) {
        requestFilename = args[args.indexOf('-RequestFile') + 1];
        const helper = spawn(executable, [...args, '-TimeoutSeconds', '20', '-SettleSeconds', '1'], { ...options, env: helperEnv });
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
    'require("node:fs").writeFileSync(process.argv[1],"synthetic updated archive");setTimeout(()=>process.exit(0),2600)',
    path.join(f.resourcesPath, 'app.asar')], { windowsHide: true, stdio: 'ignore' });
  const installerDone = new Promise((resolve, reject) => { installer.once('exit', resolve); installer.once('error', reject); });
  app.stdin.end();
  const writtenDeadline = Date.now() + 5000;
  while (Date.now() < writtenDeadline && await fs.readFile(path.join(f.resourcesPath, 'app.asar'), 'utf8') !== 'synthetic updated archive')
    await new Promise(resolve => setTimeout(resolve, 25));
  await new Promise(resolve => setTimeout(resolve, 1400));
  assert.equal(installer.exitCode, null, 'The updater must still be running during the forwarding check');
  await assert.rejects(fs.access(marker), /ENOENT/, 'The App must not reopen before the updater exits');
  assert.equal(await installerDone, 0);
  const deadline = Date.now() + 20000;
  let outcome;
  while (Date.now() < deadline) {
    try { outcome = JSON.parse(await fs.readFile(result.requestFile + '.result.json', 'utf8')); } catch { /* Wait for completed helper write. */ }
    if (['launched', 'error'].includes(outcome?.status)) break;
    await new Promise(resolve => setTimeout(resolve, 150));
  }
  if (scenario === 'Swarm Board has another directory' || scenario === 'new desktop remains') {
    assert.equal(outcome?.status, 'error', JSON.stringify(outcome));
    assert.equal(outcome?.errorCode, 'update_handoff_unexpected_application');
    await assert.rejects(fs.access(marker), /ENOENT/);
  } else {
    assert.equal(outcome?.status, 'launched', JSON.stringify(outcome));
    assert.equal(await fs.readFile(marker, 'utf8'), 'guarded');
  }
  assert.equal(outcome.modelCalls, 0); assert.equal(outcome.accountCalls, 0);
});
}

test('the Windows handoff sidecar helper refuses unmatched arguments and missing working directories', { skip: process.platform !== 'win32' }, async t => {
  const f = await fixture(t);
  const userRoot = path.join(f.root, 'sidecar user'), roaming = path.join(f.root, 'sidecar roaming');
  const board = path.join(userRoot, '.gemini/antigravity/builtin/plugins/swarm/sidecars/swarm_board');
  const shim = path.join(roaming, 'Antigravity/bin/agy-node.cmd');
  for (const directory of [board, path.dirname(shim)]) await fs.mkdir(directory, { recursive: true });
  await fs.writeFile(path.join(board, 'main.mjs'), 'export {};');
  const executable = path.join(f.installRoot, 'Antigravity.exe'), cmd = path.join(process.env.SystemRoot, 'System32/cmd.exe');
  const args = '--import=' + pathToFileURL(path.join(userRoot, '.gemini/antigravity/lib/node_modules/sidecar_sdk/register_loader.js')).href + ' main.mjs';
  const wrapper = '@echo off\r\nset ELECTRON_RUN_AS_NODE=1\r\n"' + executable.toLowerCase() + '" %*\r\n';
  await fs.writeFile(shim, wrapper);
  const parent = { ProcessId: 201, CommandLine: cmd + ' /c ""' + shim + '" ' + args + '"', ExecutablePath: cmd };
  const child = { ProcessId: 101, ParentProcessId: 201, CommandLine: '"' + executable + '" ' + args, ExecutablePath: executable };
  const cases = [
    { name: 'known directory', processes: [parent, child], directory: board, expected: [101] },
    { name: 'unknown directory', processes: [parent, child], directory: null, expected: [] },
    { name: 'missing parent', processes: [child], directory: board, expected: [] },
    { name: 'different parent entry', processes: [{ ...parent, CommandLine: parent.CommandLine.replace('main.mjs', 'other.mjs') }, child], directory: board, expected: [] },
    { name: 'extra child arguments', processes: [parent, { ...child, CommandLine: child.CommandLine + ' --type=renderer' }], directory: board, expected: [] },
    { name: 'different import', processes: [parent, { ...child, CommandLine: child.CommandLine.replace('sidecar_sdk', 'other_sdk') }], directory: board, expected: [] },
  ];
  const dataset = path.join(f.root, 'sidecar-cases.json'), harness = path.join(f.root, 'check-sidecars.ps1');
  await fs.writeFile(dataset, JSON.stringify(cases));
  await fs.writeFile(harness, `param([string]$Helper,[string]$Dataset,[string]$InstallRoot)
$ErrorActionPreference='Stop'
. $Helper
$cases=Get-Content -LiteralPath $Dataset -Raw | ConvertFrom-Json
$results=@(foreach($case in $cases){
  $ids=@(Get-AccountsNodeSidecarIds -Processes @($case.processes) -InstallRoot $InstallRoot -WorkingDirectoryReader {param($ProcessId) $case.directory})
  [pscustomobject]@{name=$case.name;ids=$ids}
})
ConvertTo-Json -InputObject $results -Depth 4 -Compress
`);
  const run = spawnSync(path.join(process.env.SystemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe'), ['-NoProfile', '-File', harness,
    '-Helper', path.join(f.packageRoot, 'account-plugin/Process-WorkingDirectory.ps1'), '-Dataset', dataset, '-InstallRoot', f.installRoot], {
    env: { ...process.env, USERPROFILE: userRoot, APPDATA: roaming }, windowsHide: true, timeout: 30000, encoding: 'utf8',
  });
  assert.equal(run.status, 0, run.stderr || run.error?.message);
  const results = JSON.parse(run.stdout);
  for (const scenario of cases) assert.deepEqual(results.find(item => item.name === scenario.name).ids, scenario.expected, scenario.name);
});
