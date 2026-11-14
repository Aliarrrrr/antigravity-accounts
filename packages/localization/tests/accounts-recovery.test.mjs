import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { accountRecoveryFiles, ensureInstalledAccounts, acquireAccountsLaunchLock, registerGuardedLauncher, resolveAccountsRecoveryRoot } from '../src/accounts-recovery.mjs';
import { installationStamp, launchLocalized } from '../src/durable.mjs';
import { sha256, TOOL_ROOT } from '../src/toolkit.mjs';

const accountPluginSource = new URL('../../account-plugin/', import.meta.url);
let nativeLockAvailable = process.platform === 'win32';
try { await fs.access(new URL('installer-lock.mjs', accountPluginSource)); }
catch { nativeLockAvailable = false; }

async function fixture(t) {
  const temporary = await fs.realpath(os.tmpdir());
  const root = await fs.mkdtemp(path.join(temporary, 'agy-accounts-recovery-'));
  t.after(async () => {
    const resolved = await fs.realpath(root);
    if (resolved !== root || path.dirname(resolved) !== temporary || !path.basename(resolved).startsWith('agy-accounts-recovery-'))
      throw new Error('Refusing cleanup outside owned test directory');
    await fs.rm(resolved, { recursive: true });
  });
  const installRoot = path.join(root, 'install'), stateRoot = path.join(root, 'localization');
  const accountsStateRoot = path.join(root, 'accounts'), packageRoot = path.join(root, 'package');
  await fs.mkdir(path.join(installRoot, 'resources'), { recursive: true });
  await fs.mkdir(path.join(packageRoot, 'account-plugin'), { recursive: true });
  await fs.mkdir(path.join(packageRoot, 'hostauth-adapter'));
  await fs.mkdir(accountsStateRoot);
  const modulePath = path.join(packageRoot, 'account-plugin', 'update-recovery.mjs');
  await fs.writeFile(modulePath, 'export async function ensureAccounts() { return {status:"ready"}; }');
  await fs.writeFile(path.join(packageRoot, 'hostauth-adapter', 'package.json'), '{"version":"preview.8"}');
  const receiptPath = path.join(accountsStateRoot, 'adapter-installation.json');
  const receipt = { schemaVersion: 1, phase: 'applied', resourcesRoot: path.join(installRoot, 'resources'), packageRoot };
  await fs.writeFile(receiptPath, JSON.stringify(receipt));
  return { root, installRoot, stateRoot, accountsStateRoot, packageRoot, modulePath, receipt, receiptPath,
    processProbe: async () => [] };
}

test('an explicit account location survives updater removal of the installed configuration', async t => {
  const f = await fixture(t);
  const addon = path.join(f.installRoot, 'resources', 'hostauth-addon');
  await fs.mkdir(addon);
  await fs.writeFile(path.join(addon, 'config.json'), JSON.stringify({ accountsStateRoot: f.accountsStateRoot }));
  let loaded = 0;
  const options = { stateRoot: f.stateRoot, processProbe: async () => [], loadModule: async () => ({
    async ensureAccounts(input) { loaded++; assert.equal(input.stateRoot, f.accountsStateRoot); return { status: 'ready' }; }
  }) };
  assert.equal((await ensureInstalledAccounts(f.installRoot, options)).status, 'ready');
  const pointer = path.join(f.stateRoot, 'accounts-installation.json');
  assert.deepEqual(JSON.parse(await fs.readFile(pointer)), {
    schemaVersion: 1, installRoot: f.installRoot, accountsStateRoot: f.accountsStateRoot,
  });
  await fs.unlink(path.join(addon, 'config.json'));
  assert.equal(await resolveAccountsRecoveryRoot(f.installRoot, { stateRoot: f.stateRoot }), f.accountsStateRoot);
  assert.equal((await ensureInstalledAccounts(f.installRoot, options)).status, 'ready');
  assert.equal(loaded, 2);
  const files = await accountRecoveryFiles(f.installRoot, { stateRoot: f.stateRoot });
  assert(files.includes(pointer)); assert(files.includes(f.receiptPath));
  await fs.unlink(f.receiptPath);
  await assert.rejects(ensureInstalledAccounts(f.installRoot, options), {
    message: 'accounts_recovery_receipt_missing', accountsRecoveryRequired: true,
  });
});

test('conflicting, malformed or in-install account locations cannot silently choose another store', async t => {
  const f = await fixture(t);
  const addon = path.join(f.installRoot, 'resources', 'hostauth-addon');
  await fs.mkdir(addon);
  const configPath = path.join(addon, 'config.json'), pointer = path.join(f.stateRoot, 'accounts-installation.json');
  await fs.mkdir(f.stateRoot);
  await fs.writeFile(pointer, JSON.stringify({ schemaVersion: 1, installRoot: f.installRoot, accountsStateRoot: f.accountsStateRoot }));
  const other = path.join(f.root, 'other-accounts'); await fs.mkdir(other);
  await fs.writeFile(configPath, JSON.stringify({ accountsStateRoot: other }));
  await assert.rejects(resolveAccountsRecoveryRoot(f.installRoot, { stateRoot: f.stateRoot }), /accounts_recovery_location_conflict/);
  for (const root of ['relative-path', f.installRoot, path.join(f.installRoot, 'resources', 'accounts')]) {
    await fs.writeFile(configPath, JSON.stringify({ accountsStateRoot: root }));
    await assert.rejects(resolveAccountsRecoveryRoot(f.installRoot, { stateRoot: f.stateRoot }), /accounts_recovery_(?:location_invalid|package_inside_installation)/);
  }
  await fs.unlink(configPath);
  await fs.writeFile(pointer, JSON.stringify({ schemaVersion: 1, installRoot: f.installRoot }));
  await assert.rejects(ensureInstalledAccounts(f.installRoot, { stateRoot: f.stateRoot }), {
    message: 'accounts_recovery_location_invalid', accountsRecoveryRequired: true,
  });
});

test('legacy installer package paths resolve beside the physical receipt without copying account data', async t => {
  const f = await fixture(t);
  const cache = path.join(f.accountsStateRoot, 'recovery-package-synthetic');
  await fs.cp(f.packageRoot, cache, { recursive: true });
  const logical = path.join(f.root, 'unmapped-logical-accounts');
  await fs.writeFile(f.receiptPath, JSON.stringify({ ...f.receipt,
    backupRoot: path.join(logical, 'restore-synthetic'), preparedRoot: path.join(logical, 'prepared-synthetic'),
    packageRoot: path.join(logical, 'recovery-package-synthetic'),
  }));
  const result = await ensureInstalledAccounts(f.installRoot, { ...f, loadModule: async () => ({
    async ensureAccounts(input) { assert.equal(input.packageRoot, cache); assert.equal(input.stateRoot, f.accountsStateRoot); return { status: 'ready' }; }
  }) });
  assert.equal(result.status, 'ready');
  assert.equal(JSON.parse(await fs.readFile(f.receiptPath)).packageRoot, path.join(logical, 'recovery-package-synthetic'));
});

test('only the registered installation loads Accounts, while old receipts and Restore remain disabled', async t => {
  const f = await fixture(t);
  const loaded = [];
  const options = { ...f, loadModule: async url => { loaded.push(url); return {
    ensureAccounts: async input => {
      assert.equal(input.stateRoot, f.accountsStateRoot);
      assert.equal(input.packageRoot, f.packageRoot);
      return { status: 'ready' };
    }
  }; } };
  assert.equal((await ensureInstalledAccounts(f.installRoot, options)).status, 'ready');
  assert.equal(loaded.length, 1);
  assert.match(loaded[0], /update-recovery\.mjs\?revision=/);
  for (const change of [{ phase: 'restored' }, { packageRoot: undefined }, { resourcesRoot: path.join(f.root, 'other', 'resources') }]) {
    await fs.writeFile(f.receiptPath, JSON.stringify({ ...f.receipt, ...change }));
    assert.equal((await ensureInstalledAccounts(f.installRoot, options)).status, 'disabled');
  }
  assert.equal(loaded.length, 1);
});

test('update relaunch registers only the installed enabled guard and only public paths', async t => {
  const f = await fixture(t);
  assert.equal((await registerGuardedLauncher(f.installRoot, f)).registered, false);
  const persistenceRoot = path.join(f.stateRoot, 'windows-persistence');
  await fs.mkdir(persistenceRoot, { recursive: true });
  const persistence = { schemaVersion: 1, installed: true, installRoot: f.installRoot, toolkitRoot: TOOL_ROOT,
    stateRoot: f.stateRoot, nodePath: process.execPath };
  const manifest = path.join(persistenceRoot, 'install.json');
  await fs.writeFile(manifest, JSON.stringify(persistence));
  assert.equal((await registerGuardedLauncher(f.installRoot, f)).registered, true);
  const recordPath = path.join(f.accountsStateRoot, 'guarded-launcher.json');
  const before = await fs.readFile(recordPath);
  assert.deepEqual(JSON.parse(before), { schemaVersion: 1, kind: 'localization', installRoot: f.installRoot,
    toolkitRoot: TOOL_ROOT, stateRoot: f.stateRoot, nodePath: process.execPath });
  assert.equal((await registerGuardedLauncher(f.installRoot, f)).registered, true);
  assert.deepEqual(await fs.readFile(recordPath), before);
  await fs.writeFile(path.join(persistenceRoot, 'disabled'), 'disabled');
  assert.equal((await registerGuardedLauncher(f.installRoot, f)).registered, false);
  await fs.unlink(path.join(persistenceRoot, 'disabled'));
  await fs.writeFile(manifest, JSON.stringify({ ...persistence, installRoot: path.join(f.root, 'another-installation') }));
  await assert.rejects(registerGuardedLauncher(f.installRoot, f), /accounts_guarded_launcher_invalid/);
  assert.deepEqual(await fs.readFile(recordPath), before);
});

test('the existing watcher observes Accounts receipt and distribution file changes', async t => {
  const f = await fixture(t);
  const files = await accountRecoveryFiles(f.installRoot, f);
  for (const filename of [f.receiptPath, f.modulePath, path.join(f.packageRoot, 'hostauth-adapter', 'package.json')])
    assert.ok(files.includes(filename));
  const first = await installationStamp(f.installRoot, f);
  await fs.appendFile(f.modulePath, '\n// updated recovery');
  assert.notEqual(await installationStamp(f.installRoot, f), first);
});

test('a registered compatibility bootstrap is retained rather than replaced by the legacy localization guard',async t=>{
  const f=await fixture(t),compatibilityState=path.join(f.root,'compatibility-state');
  await fs.mkdir(compatibilityState);
  const bootstrapPath=path.join(compatibilityState,'bootstrap.mjs');
  await fs.writeFile(bootstrapPath,'// owned compatibility bootstrap');
  const filename=path.join(f.accountsStateRoot,'guarded-launcher.json');
  const record={schemaVersion:1,kind:'compatibility',installRoot:f.installRoot,stateRoot:compatibilityState,
    nodePath:process.execPath,bootstrapPath};
  await fs.writeFile(filename,JSON.stringify(record));const before=await fs.readFile(filename);
  assert.deepEqual(await registerGuardedLauncher(f.installRoot,f),{registered:true,kind:'compatibility'});
  assert.deepEqual(await fs.readFile(filename),before);
  for(const change of [{bootstrapPath:path.join(f.root,'outside.mjs')},{installRoot:path.join(f.root,'other-install')},{stateRoot:'relative'}]){
    await fs.writeFile(filename,JSON.stringify({...record,...change}));
    await assert.rejects(registerGuardedLauncher(f.installRoot,f),/accounts_guarded_launcher_invalid/);
  }
});

test('Accounts coordinates localization restore and reapply under its own recovery callbacks', async t => {
  const f = await fixture(t), calls = [];
  await fs.writeFile(path.join(f.installRoot, 'resources', 'antigravity-zh-cn.install.json'), JSON.stringify({ sourceSha256: 'original' }));
  const backup = path.join(f.stateRoot, 'backups', '2.29.0', 'original');
  await fs.mkdir(backup, { recursive: true });
  await fs.writeFile(path.join(backup, 'manifest.json'), '{}');
  const result = await ensureInstalledAccounts(f.installRoot, { ...f,
    inspect: async () => ({ isLocalized: true, appVersion: '2.29.0' }),
    restore: async (target, options) => {
      assert.equal(target, f.installRoot);
      assert.equal(options.backupRoot, path.join(f.stateRoot, 'backups'));
      assert.equal(options.compatibleSource, true);
      calls.push('restore-localization');
    },
    relocalize: async () => { calls.push('reapply-localization'); },
    loadModule: async () => ({ ensureAccounts: async input => {
      await input.checkStopped();
      await input.beforeApply();
      calls.push('apply-accounts');
      await input.afterApply({ installed: true });
      return { status: 'repaired' };
    } })
  });
  assert.equal(result.status, 'repaired');
  assert.deepEqual(calls, ['restore-localization', 'apply-accounts', 'reapply-localization']);
});

test('a localization refresh failure keeps a successful Accounts repair available', async t => {
  const f = await fixture(t);
  const result = await ensureInstalledAccounts(f.installRoot, { ...f,
    relocalize: async () => { throw new Error('incompatible_native_source'); },
    loadModule: async () => ({ ensureAccounts: async input => {
      await input.afterApply({ installed: true });
      return { status: 'repaired' };
    } })
  });
  assert.equal(result.status, 'repaired');
  assert.equal(result.localizationError, 'incompatible_native_source');
});

test('an incompatible enabled Accounts adaptation blocks the existing launcher before any App spawn', async t => {
  const f = await fixture(t);
  await fs.writeFile(f.modulePath, 'export async function ensureAccounts() { throw new Error("unsupported_host_contract"); }');
  await assert.rejects(launchLocalized(f.installRoot, { ...f, settleMs: 0 }), error => {
    assert.equal(error.message, 'unsupported_host_contract');
    assert.equal(error.accountsRecoveryRequired, true);
    assert.equal(error.launchBlocked, true);
    return true;
  });
  const status = JSON.parse(await fs.readFile(path.join(f.stateRoot, 'status.json'), 'utf8'));
  assert.equal(status.status, 'error');
});

test('a pending Accounts repair also blocks forwarding to a now-unadapted App', async t => {
  const f = await fixture(t);
  await fs.writeFile(f.modulePath, 'export async function ensureAccounts() { return {status:"pending_idle"}; }');
  await assert.rejects(launchLocalized(f.installRoot, { ...f, settleMs: 0 }), error => {
    assert.equal(error.message, 'accounts_repair_pending_idle');
    assert.equal(error.launchBlocked, true);
    return true;
  });
});

test('final launch retains the real Accounts mutex and refuses changed programs without repair', { skip: !nativeLockAvailable }, async t => {
  const f = await fixture(t);
  const lockSource = new URL('installer-lock.mjs', accountPluginSource);
  const recoverySource = new URL('update-recovery.mjs', accountPluginSource);
  await fs.writeFile(path.join(f.packageRoot, 'account-plugin', 'installer-lock.mjs'),
    `export { acquireInstallationLock } from ${JSON.stringify(lockSource.href)};`);
  await fs.writeFile(f.modulePath, `export { ensureAccounts } from ${JSON.stringify(recoverySource.href)};`);
  const resources = path.join(f.installRoot, 'resources'), addon = path.join(resources, 'hostauth-addon');
  await fs.mkdir(path.join(resources, 'bin'));
  await fs.mkdir(path.join(addon, 'component'), { recursive: true });
  await fs.mkdir(path.join(addon, 'native'));
  await fs.writeFile(path.join(resources, 'app.asar'), 'adapted-archive');
  await fs.writeFile(path.join(resources, 'bin', 'language_server.exe'), 'adapted-native');
  await fs.writeFile(path.join(addon, 'config.json'), '{"schemaVersion":1,"component":"component/Accounts.exe"}');
  await fs.writeFile(path.join(addon, 'component', 'Accounts.exe'), 'synthetic-component');
  await fs.writeFile(path.join(addon, 'native', 'language_server.exe'), 'synthetic-provider');
  const receipt = { ...f.receipt, patchedAsarSha256: sha256(Buffer.from('adapted-archive')),
    patchedNativeSha256: sha256(Buffer.from('adapted-native')) };
  await fs.writeFile(f.receiptPath, JSON.stringify(receipt));
  const { acquireInstallationLock } = await import(lockSource.href);
  const release = await acquireAccountsLaunchLock(f.installRoot, f);
  assert.equal(typeof release, 'function');
  try {
    release.assertHeld();
    assert.equal(await acquireInstallationLock(resources), null, 'manual Accounts writer must remain excluded after readiness validation');
  } finally { await release(); }
  await fs.writeFile(path.join(resources, 'bin', 'language_server.exe'), 'unrecognized-updater-program');
  await assert.rejects(acquireAccountsLaunchLock(f.installRoot, f), { message: 'accounts_repair_required', accountsRecoveryRequired: true });
  assert.equal(await fs.readFile(path.join(resources, 'bin', 'language_server.exe'), 'utf8'), 'unrecognized-updater-program');
  const available = await acquireInstallationLock(resources);
  assert.ok(available, 'failed validation must release the mutex');
  try {
    await assert.rejects(acquireAccountsLaunchLock(f.installRoot, f), { message: 'accounts_installation_busy', accountsRecoveryRequired: true });
  } finally { await available(); }
  await fs.writeFile(f.receiptPath, JSON.stringify({ ...receipt, phase: 'restored' }));
  await fs.unlink(path.join(f.packageRoot, 'account-plugin', 'installer-lock.mjs'));
  const restoredRelease=await acquireAccountsLaunchLock(f.installRoot, f);
  assert.equal(typeof restoredRelease,'function','intentional Restore must not depend on the unused recovery cache');
  await restoredRelease();
});
