import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { readAsar, rewriteAsar, sha256 } from '../../hostauth-adapter/src/asar.mjs';
import { inspectNativeFallback, adaptedNativeDigests } from '../native-fallback.mjs';
import { ensureAccounts } from '../update-recovery.mjs';
import { recoverAndLaunch } from '../recovery-and-launch.mjs';
import { EventEmitter } from 'node:events';
import { NATIVE_DIAGNOSTICS_PROFILES } from '../../hostauth-adapter/src/native-diagnostics-patch.mjs';
import { PROJECT_REFRESH_PROFILES } from '../../hostauth-adapter/src/native-project-refresh-patch.mjs';

function archive(files) {
  const json = Buffer.from('{"files":{}}'), header = Buffer.alloc(8 + ((json.length + 3) & ~3));
  header.writeUInt32LE(header.length - 4); header.writeUInt32LE(json.length, 4); json.copy(header, 8);
  const size = Buffer.alloc(8); size.writeUInt32LE(4); size.writeUInt32LE(header.length, 4);
  return rewriteAsar(Buffer.concat([size, header]), new Map(Object.entries(files).map(([name, text]) => [name, Buffer.from(text)])));
}

function image(version = '9.0.0') {
  const bytes = Buffer.alloc(2048), pe = 128, section = pe + 24 + 240, resource = 512;
  bytes.write('MZ'); bytes.writeUInt32LE(pe, 60); bytes.write('PE\0\0', pe, 'binary');
  bytes.writeUInt16LE(0x8664, pe + 4); bytes.writeUInt16LE(1, pe + 6); bytes.writeUInt16LE(240, pe + 20); bytes.writeUInt16LE(0x20b, pe + 24);
  bytes.writeUInt32LE(0x1000, pe + 24 + 128); bytes.writeUInt32LE(512, pe + 24 + 132);
  bytes.writeUInt32LE(0x1000, section + 12); bytes.writeUInt32LE(1536, section + 16); bytes.writeUInt32LE(resource, section + 20);
  for (const [relative, id, target] of [[0, 16, 0x80000020], [32, 1, 0x80000040], [64, 0x409, 96]]) {
    bytes.writeUInt16LE(1, resource + relative + 14); bytes.writeUInt32LE(id, resource + relative + 16); bytes.writeUInt32LE(target, resource + relative + 20);
  }
  bytes.writeUInt32LE(0x1080, resource + 96); bytes.writeUInt32LE(128, resource + 100);
  const value = resource + 128;
  bytes.writeUInt16LE(128, value); bytes.writeUInt16LE(52, value + 2); Buffer.from('VS_VERSION_INFO\0', 'utf16le').copy(bytes, value + 6);
  const fixed = value + 40, [major, minor, patch] = version.split('.').map(Number);
  bytes.writeUInt32LE(0xfeef04bd, fixed); bytes.writeUInt32LE((major << 16) | minor, fixed + 8); bytes.writeUInt32LE(patch << 16, fixed + 12);
  return bytes;
}

async function fixture(t) {
  const temporary = await fs.realpath(os.tmpdir()), root = await fs.mkdtemp(path.join(temporary, 'accounts-native-fallback-'));
  t.after(async () => {
    assert.equal(await fs.realpath(root), root); assert.equal(path.dirname(root), temporary);
    await fs.rm(root, { recursive: true, force: true });
  });
  const installRoot = path.join(root, 'app'), resourcesRoot = path.join(installRoot, 'resources'), stateRoot = path.join(root, 'state');
  await fs.mkdir(path.join(resourcesRoot, 'bin'), { recursive: true }); await fs.mkdir(stateRoot);
  const native = image(), executable = image();
  const original = archive({ 'package.json': JSON.stringify({ name: 'antigravity', main: 'dist/main.js', version: '9.0.0' }),
    'dist/main.js': 'console.log("updated native app");', 'dist/languageServer.js': 'module.exports = {};', 'dist/hostBridgeServer.js': 'module.exports = {};' });
  await fs.writeFile(path.join(installRoot, 'Antigravity.exe'), executable);
  await fs.writeFile(path.join(resourcesRoot, 'bin', 'language_server.exe'), native);
  await fs.writeFile(path.join(resourcesRoot, 'app.asar'), original);
  const receipt = { schemaVersion: 1, phase: 'applied', resourcesRoot,
    originalAsarSha256: sha256(Buffer.from('old native archive')), patchedAsarSha256: sha256(Buffer.from('old adapted archive')),
    originalNativeSha256: sha256(Buffer.from('old native image')), patchedNativeSha256: sha256(Buffer.from('old adapted image')) };
  await fs.writeFile(path.join(stateRoot, 'adapter-installation.json'), JSON.stringify(receipt));
  await fs.writeFile(path.join(stateRoot, 'accounts.dpapi'), 'retained account authorization');
  const runLocked = async (target, action) => { assert.equal(target, resourcesRoot); return action({ assertHeld() {} }); };
  return { root, installRoot, resourcesRoot, stateRoot, packageRoot: path.join(root, 'package'), receipt, runLocked, original, checkStopped() {} };
}

test('fallback rejects every native image modified by a supported Accounts profile', () => {
  const expected = [...new Set([...NATIVE_DIAGNOSTICS_PROFILES.map(profile => profile.patchedSha256),
    ...PROJECT_REFRESH_PROFILES.flatMap(profile => [profile.projectSha256, profile.providerSha256])])];
  assert.deepEqual([...adaptedNativeDigests].sort(), expected.sort());
});

test('unsupported complete update returns native mode without changing programs, receipt or authorization', async t => {
  const f = await fixture(t), receiptPath = path.join(f.stateRoot, 'adapter-installation.json');
  const before = await fs.readFile(receiptPath);
  const result = await ensureAccounts({ ...f, checkStopped() {}, manage() { throw new Error('unsupported_native_oauth_configuration'); } });
  assert.equal(result.status, 'degraded'); assert.equal(result.mode, 'native');
  assert.equal(result.program.appVersion, '9.0.0'); assert.equal(result.program.asarSha256, sha256(f.original));
  assert.deepEqual(await fs.readFile(path.join(f.resourcesRoot, 'app.asar')), f.original);
  assert.deepEqual(await fs.readFile(receiptPath), before);
  assert.equal(await fs.readFile(path.join(f.stateRoot, 'accounts.dpapi'), 'utf8'), 'retained account authorization');
  let recovered = false;
  assert.equal((await ensureAccounts({ ...f, checkStopped() {}, manage() { recovered = true; } })).status, 'repaired');
  assert.equal(recovered, true, 'a compatible package can attempt repair after native-mode use');
});

test('partial native replacement, missing program, version mismatch and injected hooks cannot use native fallback', async t => {
  for (const change of ['patched-native', 'missing-native', 'wrong-version', 'hook', 'truncated', 'stage', 'prepared']) {
    const f = await fixture(t);
    if (change === 'patched-native') f.receipt.patchedNativeSha256 = sha256(image());
    if (change === 'missing-native') await fs.unlink(path.join(f.resourcesRoot, 'bin', 'language_server.exe'));
    if (change === 'wrong-version') await fs.writeFile(path.join(f.installRoot, 'Antigravity.exe'), image('9.1.0'));
    if (change === 'hook') await fs.writeFile(path.join(f.resourcesRoot, 'app.asar'), rewriteAsar(f.original, new Map([['dist/main.js', Buffer.from('require("./hostauth-addon/runtime.cjs");')]])));
    if (change === 'truncated') await fs.writeFile(path.join(f.resourcesRoot, 'bin', 'language_server.exe'), image().subarray(0, 700));
    if (change === 'stage') await fs.writeFile(path.join(f.resourcesRoot, 'app.asar.hostauth-new'), 'incomplete');
    if (change === 'prepared') f.receipt.phase = 'prepared';
    await assert.rejects(inspectNativeFallback(f), undefined, change);
  }
});

test('archive content integrity is checked beyond the entry-point files', async t => {
  const f = await fixture(t);
  const bytes = rewriteAsar(f.original, new Map([['deep/data.txt', Buffer.from('content')]]));
  bytes[bytes.length - 1] ^= 1;
  await fs.writeFile(path.join(f.resourcesRoot, 'app.asar'), bytes);
  await assert.rejects(inspectNativeFallback(f), /native_fallback_program_invalid/);
});

test('a complete raw update ignores an obsolete localization record and retains the full package version', async t => {
  const f = await fixture(t);
  await fs.writeFile(path.join(f.resourcesRoot, 'antigravity-zh-cn.install.json'), '{obsolete updater record');
  const metadata = Buffer.from(JSON.stringify({ name: 'antigravity', main: 'dist/main.js', version: '9.0.0-preview.1+local' }));
  await fs.writeFile(path.join(f.resourcesRoot, 'app.asar'), rewriteAsar(f.original, new Map([['package.json', metadata]])));
  const result = await inspectNativeFallback(f);
  assert.equal(result.appVersion, '9.0.0-preview.1+local');
  assert.equal(await fs.readFile(path.join(f.resourcesRoot, 'antigravity-zh-cn.install.json'), 'utf8'), '{obsolete updater record');
});

test('a localized native archive requires its own matching localization record', async t => {
  const f = await fixture(t);
  const localized = rewriteAsar(f.original, new Map([['dist/preload.js', Buffer.from('globalThis.__antigravityZhCN = {};')]]));
  await fs.writeFile(path.join(f.resourcesRoot, 'app.asar'), localized);
  await assert.rejects(inspectNativeFallback(f), /ENOENT/);
  const record = { schemaVersion: 1, installRoot: f.installRoot, appVersion: '9.0.0', sourceSha256: sha256(f.original),
    patchedSha256: sha256(localized), executableSha256: sha256(image()), unpacked: {} };
  const recordPath = path.join(f.resourcesRoot, 'antigravity-zh-cn.install.json');
  await fs.writeFile(recordPath, JSON.stringify(record));
  assert.equal((await inspectNativeFallback(f)).asarSha256, sha256(localized));
  await fs.writeFile(recordPath, JSON.stringify({ ...record, appVersion: '8.0.0' }));
  await assert.rejects(inspectNativeFallback(f), /native_fallback_program_invalid/);
});

test('missing or changed unpacked content blocks native startup', async t => {
  const f = await fixture(t), extra = Buffer.from('unpacked original');
  const original = rewriteAsar(f.original, new Map([['unpacked.txt', extra]]));
  const parsed = readAsar(original), header = structuredClone(parsed.header), dataStart = 8 + original.readUInt32LE(4);
  header.files['unpacked.txt'].unpacked = true;
  const json = Buffer.from(JSON.stringify(header)), padded = (json.length + 3) & ~3, headerBytes = Buffer.alloc(8 + padded);
  headerBytes.writeUInt32LE(headerBytes.length - 4); headerBytes.writeUInt32LE(json.length, 4); json.copy(headerBytes, 8);
  const size = Buffer.alloc(8); size.writeUInt32LE(4); size.writeUInt32LE(headerBytes.length, 4);
  await fs.writeFile(path.join(f.resourcesRoot, 'app.asar'), Buffer.concat([size, headerBytes, original.subarray(dataStart)]));
  const unpackedRoot = path.join(f.resourcesRoot, 'app.asar.unpacked'); await fs.mkdir(unpackedRoot);
  await assert.rejects(inspectNativeFallback(f), /ENOENT/);
  const unpackedPath = path.join(unpackedRoot, 'unpacked.txt'); await fs.writeFile(unpackedPath, extra);
  assert.equal(typeof (await inspectNativeFallback(f)).unpackedSha256, 'string');
  await fs.writeFile(unpackedPath, Buffer.alloc(extra.length));
  await assert.rejects(inspectNativeFallback(f), /native_fallback_program_invalid/);
});

test('unknown failures and an active app preserve the existing refusal or waiting state', async t => {
  const f = await fixture(t);
  await assert.rejects(ensureAccounts({ ...f, checkStopped() {}, manage() { throw new Error('disk write failed'); } }), /disk write failed/);
  const result = await ensureAccounts({ ...f, checkStopped() { throw Object.assign(new Error('busy'), { code: 'application_running' }); }, manage() { throw new Error('must not run'); } });
  assert.equal(result.status, 'pending_idle');
});

test('standalone fallback requires confirmation and rechecks the current programs under the installation lock', async t => {
  const f = await fixture(t), program = await inspectNativeFallback(f), calls = [];
  await fs.writeFile(path.join(f.resourcesRoot, 'antigravity-zh-cn.install.json'), '{obsolete updater record');
  const options = { ...f, recover: async () => ({ status: 'degraded', mode: 'native', reason: 'unsupported_native_oauth_configuration', program }),
    spawnApp(executable, args) {
      calls.push({ executable, args });
      const child = new EventEmitter(); child.unref = () => {}; queueMicrotask(() => child.emit('spawn')); return child;
    } };
  assert.equal((await recoverAndLaunch({ ...options, confirmNativeMode: async () => false })).cancelled, true);
  assert.equal(calls.length, 0);
  assert.equal((await recoverAndLaunch({ ...options, confirmNativeMode: async () => true })).launched, true);
  assert.equal(calls.length, 1);
  await assert.rejects(recoverAndLaunch({ ...options, checkStopped() { throw new Error('updater active'); }, confirmNativeMode: async () => true }), /updater active/);
  assert.equal(calls.length, 1);
  await assert.rejects(recoverAndLaunch({ ...options, confirmNativeMode: async () => {
    await fs.appendFile(path.join(f.resourcesRoot, 'app.asar'), 'changed while the dialog was open'); return true;
  } }), /native_fallback_program_changed/);
  assert.equal(calls.length, 1);
});

test('the manual CMD routes recovery, confirmation and launch through one entry point', async () => {
  const publish = await fs.readFile(new URL('../../../scripts/Publish-Accounts.ps1', import.meta.url), 'utf8');
  const launch = publish.slice(publish.indexOf('        $launch = @('), publish.indexOf('        $manifest ='));
  assert.match(launch, /recovery-and-launch\.mjs.*--show-accounts true/);
  assert.doesNotMatch(launch, /start ""/);
  assert.doesNotMatch(launch, /update-recovery\.mjs/);
});

test('standalone launch forwards a verified localization registration with the account-manager argument', async t => {
  const f = await fixture(t), toolkitRoot = path.join(f.root, 'localization'), localizationState = path.join(f.root, 'localization-state');
  await fs.mkdir(path.join(toolkitRoot, 'scripts'), { recursive: true });
  await fs.mkdir(path.join(localizationState, 'windows-persistence'), { recursive: true });
  for (const name of ['Launch-Localized.vbs', 'Launch-Localized.ps1']) await fs.writeFile(path.join(toolkitRoot, 'scripts', name), 'test launcher');
  await fs.writeFile(path.join(toolkitRoot, 'durable.mjs'), 'test guard');
  const nodePath = path.join(f.root, 'node.exe'); await fs.writeFile(nodePath, 'test runtime');
  const registration = { schemaVersion: 1, kind: 'localization', installRoot: f.installRoot, toolkitRoot, stateRoot: localizationState, nodePath };
  await fs.writeFile(path.join(f.stateRoot, 'guarded-launcher.json'), JSON.stringify(registration));
  await fs.writeFile(path.join(localizationState, 'windows-persistence', 'install.json'), JSON.stringify({ ...registration, installed: true }));
  const calls = [];
  const options = { ...f, showAccounts: true, recover() { throw new Error('guard owns recovery'); },
    spawnApp(executable, args, spawnOptions) {
      calls.push({ executable, args, spawnOptions });
      const child = new EventEmitter(); child.unref = () => {}; queueMicrotask(() => child.emit('spawn')); return child;
    } };
  assert.equal((await recoverAndLaunch(options)).guarded, true);
  assert.equal(path.basename(calls[0].executable), 'wscript.exe');
  assert.deepEqual(calls[0].args, [path.join(toolkitRoot, 'scripts', 'Launch-Localized.vbs'), nodePath, toolkitRoot, f.installRoot, localizationState, '--accounts']);
  assert.equal(calls[0].spawnOptions.env.ELECTRON_RUN_AS_NODE, undefined);
  await fs.writeFile(path.join(localizationState, 'windows-persistence', 'disabled'), 'disabled');
  await assert.rejects(recoverAndLaunch(options), /update_handoff_launcher_invalid/);
  assert.equal(calls.length, 1);
});
