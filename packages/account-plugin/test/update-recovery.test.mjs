import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { ensureAccounts } from '../update-recovery.mjs';
import { acquireInstallationLock } from '../installer-lock.mjs';
import { sha256 } from '../../hostauth-adapter/src/asar.mjs';

async function fixture(t, { installed = true, phase = 'applied' } = {}) {
  const temporary = await realpath(os.tmpdir());
  const root = await mkdtemp(path.join(temporary, 'accounts-recovery-test-'));
  t.after(async () => {
    const resolved = await realpath(root);
    if (resolved !== root || path.dirname(resolved) !== temporary || !path.basename(resolved).startsWith('accounts-recovery-test-'))
      throw new Error('Refusing cleanup outside owned temporary directory');
    await rm(resolved, { recursive: true });
  });
  const installRoot = path.join(root, 'app'), resourcesRoot = path.join(installRoot, 'resources');
  const stateRoot = path.join(root, 'state'), packageRoot = path.join(root, 'package');
  const receiptPath = path.join(stateRoot, 'adapter-installation.json');
  await mkdir(path.join(resourcesRoot, 'bin'), { recursive: true });
  await mkdir(stateRoot);
  await writeFile(path.join(resourcesRoot, 'app.asar'), 'adapted-archive');
  await writeFile(path.join(resourcesRoot, 'bin', 'language_server.exe'), 'adapted-native');
  const receipt = { schemaVersion: 1, phase, resourcesRoot,
    originalAsarSha256: sha256(Buffer.from('official-archive')),
    patchedAsarSha256: sha256(Buffer.from('adapted-archive')),
    originalNativeSha256: sha256(Buffer.from('official-native')),
    patchedNativeSha256: sha256(Buffer.from('adapted-native')) };
  if (installed) await writeFile(receiptPath, JSON.stringify(receipt));
  const addon = path.join(resourcesRoot, 'hostauth-addon');
  await mkdir(path.join(addon, 'component'), { recursive: true });
  await mkdir(path.join(addon, 'native'));
  await writeFile(path.join(addon, 'config.json'), '{"schemaVersion":1,"component":"component/Accounts.exe"}');
  await writeFile(path.join(addon, 'component', 'Accounts.exe'), 'component-placeholder');
  await writeFile(path.join(addon, 'native', 'language_server.exe'), 'provider-placeholder');
  const runLocked = async (target, action) => { assert.equal(target, resourcesRoot); return action(() => {}); };
  return { installRoot, resourcesRoot, packageRoot, stateRoot, receipt, receiptPath, runLocked };
}

test('recovery does not reinstall an absent or deliberately restored adapter', async t => {
  for (const setup of [{ installed: false }, { phase: 'restored' }]) {
    const f = await fixture(t, setup);
    const result = await ensureAccounts({ ...f, manage: () => { throw new Error('must not install'); } });
    assert.equal(result.status, 'disabled');
    assert.equal(result.reason, setup.installed === false ? 'not_installed' : 'restored');
  }
});

test('unchanged account programs remain ready through a recorded localization layer', async t => {
  const f = await fixture(t);
  const options = { ...f, manage: () => { throw new Error('must not rebuild'); },
    checkStopped: () => { throw new Error('ready checks may run while the App is open'); },
    beforeApply: () => { throw new Error('must preserve current localization'); } };
  assert.equal((await ensureAccounts(options)).status, 'ready');
  await writeFile(path.join(f.resourcesRoot, 'app.asar'), 'localized-adapted-archive');
  await writeFile(path.join(f.resourcesRoot, 'antigravity-zh-cn.install.json'), JSON.stringify({
    schemaVersion: 1, installRoot: f.installRoot, sourceSha256: f.receipt.patchedAsarSha256.toUpperCase(),
    patchedSha256: sha256(Buffer.from('localized-adapted-archive')).toUpperCase()
  }));
  assert.equal((await ensureAccounts(options)).status, 'ready');
});

test('recovery uses the persisted state root after the default app-data view changes', async t => {
  const f = await fixture(t), previousLocal = process.env.LOCALAPPDATA;
  const defaultRoot = path.join(path.dirname(f.stateRoot), 'AntigravityAccounts');
  await mkdir(defaultRoot);
  await writeFile(path.join(f.resourcesRoot, 'hostauth-addon', 'config.json'), JSON.stringify({
    schemaVersion: 1, component: 'component/Accounts.exe', accountsStateRoot: f.stateRoot
  }));
  process.env.LOCALAPPDATA = path.dirname(defaultRoot);
  try {
    assert.equal((await ensureAccounts({ ...f, stateRoot: defaultRoot })).status, 'ready');
    const legacyRoot = path.join(path.dirname(f.stateRoot), 'legacy-view');
    const receipt = { ...f.receipt, backupRoot: path.join(legacyRoot, 'restore-old'), preparedRoot: path.join(legacyRoot, 'prepared-old'),
      packageRoot: path.join(legacyRoot, 'recovery-package-old') };
    await writeFile(f.receiptPath, JSON.stringify(receipt));
    await writeFile(path.join(f.resourcesRoot, 'app.asar'), 'updated-official-archive');
    let repairs = 0;
    const result = await ensureAccounts({ ...f, stateRoot: defaultRoot, packageRoot: receipt.packageRoot, checkStopped: () => {}, manage: async input => {
      repairs++;
      assert.equal(input.stateRoot, f.stateRoot);
      assert.equal(input.packageRoot, path.join(f.stateRoot, 'recovery-package-old'));
    } });
    assert.equal(result.status, 'repaired'); assert.equal(repairs, 1);
    assert.equal(await readFile(f.receiptPath, 'utf8'), JSON.stringify(receipt));
  } finally {
    if (previousLocal === undefined) delete process.env.LOCALAPPDATA;
    else process.env.LOCALAPPDATA = previousLocal;
  }
});

test('unfinished recovery and a partial payload are not reported as ready', async t => {
  const f = await fixture(t, { phase: 'prepared' });
  await assert.rejects(ensureAccounts(f), /unfinished/);
  await writeFile(f.receiptPath, JSON.stringify({ ...f.receipt, phase: 'applied' }));
  await rm(path.join(f.resourcesRoot, 'hostauth-addon', 'component', 'Accounts.exe'));
  await assert.rejects(ensureAccounts(f), /ENOENT/);
});

test('an active App defers updater repair without touching localization or receipt', async t => {
  const f = await fixture(t);
  await writeFile(path.join(f.resourcesRoot, 'app.asar'), 'updated-official-archive');
  const before = await readFile(f.receiptPath);
  const result = await ensureAccounts({ ...f,
    checkStopped: () => { throw Object.assign(new Error('active application'), { code: 'application_running' }); },
    beforeApply: () => { throw new Error('must not touch localization'); },
    manage: () => { throw new Error('must not install while active'); }
  });
  assert.equal(result.status, 'pending_idle');
  assert.deepEqual(await readFile(f.receiptPath), before);
  assert.equal(await readFile(path.join(f.resourcesRoot, 'app.asar'), 'utf8'), 'updated-official-archive');
});

test('updater repair shares one lock and restores the localization layer after either result', async t => {
  for (const fails of [false, true]) {
    const f = await fixture(t);
    await writeFile(path.join(f.resourcesRoot, 'app.asar'), 'updated-official-archive');
    const before = await readFile(f.receiptPath), calls = [];
    const options = { ...f, checkStopped: () => {},
      beforeApply: () => { calls.push('restore-localization'); },
      afterApply: result => { calls.push(result.installed ? 'reapply-after-success' : 'reapply-after-failure'); },
      manage: async input => {
        assert.equal(input.operation, 'apply');
        assert.equal(input.packageRoot, f.packageRoot);
        await input.runLocked(f.resourcesRoot, () => { calls.push('apply-current-program'); });
        await assert.rejects(async () => input.runLocked(path.dirname(f.resourcesRoot), () => {}), /installation changed/);
        if (fails) throw new Error('incompatible-current-program');
      }
    };
    if (fails) await assert.rejects(ensureAccounts(options), /incompatible-current-program/);
    else assert.equal((await ensureAccounts(options)).status, 'repaired');
    assert.deepEqual(calls, ['restore-localization', 'apply-current-program', fails ? 'reapply-after-failure' : 'reapply-after-success']);
    assert.deepEqual(await readFile(f.receiptPath), before);
  }
});

test('manual installation excludes a concurrent recovery until its kernel lock is released', { skip: process.platform !== 'win32' }, async t => {
  const f = await fixture(t);
  const release = await acquireInstallationLock(f.resourcesRoot);
  assert.ok(release);
  try {
    const options = { ...f, runLocked: undefined, manage: () => { throw new Error('must not apply'); } };
    assert.equal((await ensureAccounts(options)).status, 'pending_update');
    release.assertHeld();
  } finally { await release(); }
  const next = await acquireInstallationLock(f.resourcesRoot);
  assert.ok(next);
  await next();
});
