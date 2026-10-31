import test from 'node:test';
import assert from 'node:assert/strict';
import { copyFile, mkdir, mkdtemp, readFile, realpath, rm, rmdir, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { manageInstallation, isManagedRecoveryPath } from '../manage-install.mjs';
import { sha256 } from '../../hostauth-adapter/src/asar.mjs';

const ui = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const program = process.env.ACCOUNTS_TEST_INSTALL || path.join(process.env.LOCALAPPDATA || '', 'Programs', 'antigravity');
const archive = path.join(program, 'resources', 'app.asar');
const native = path.join(program, 'resources', 'bin', 'language_server.exe');

test('recovery directories must share the actual receipt location, including a redirected app-data overlay', () => {
  const stateRoot=path.resolve('/local/AntigravityAccounts'), realRoot=path.resolve('/package/LocalCache/Local/AntigravityAccounts');
  const input={stateRoot,receiptRealPath:path.join(realRoot,'adapter-installation.json'),target:path.join(stateRoot,'restore-old'),
    targetRealPath:path.join(realRoot,'restore-old'),prefix:'restore-'};
  assert.equal(isManagedRecoveryPath(input),true);
  for(const change of [
    {target:path.resolve('/outside/restore-old')}, {targetRealPath:path.resolve('/outside/restore-old')},
    {targetRealPath:path.join(realRoot,'restore-other')}, {prefix:'prepared-'}, {target:'relative'},
  ]) assert.equal(isManagedRecoveryPath({...input,...change}),false);
});

test('installation coordinator prepares, applies and restores only a copied installation', {
  skip: !existsSync(archive) || !existsSync(native), timeout: 45000
}, async () => {
  const temporary = await realpath(os.tmpdir());
  const root = await mkdtemp(path.join(temporary, 'accounts-install-test-'));
  try {
    const installRoot = path.join(root, 'app');
    const resources = path.join(installRoot, 'resources');
    const packageRoot = path.join(root, 'package');
    const stateRoot = path.join(root, 'state');
    await mkdir(path.join(resources, 'bin'), { recursive: true });
    await copyFile(archive, path.join(resources, 'app.asar'));
    await copyFile(native, path.join(resources, 'bin', 'language_server.exe'));
    await mkdir(path.join(packageRoot, 'component'), { recursive: true });
    // The coordinator never executes this payload. The real published exe is
    // checked separately through its actual stdio transport.
    await writeFile(path.join(packageRoot, 'component', 'Antigravity.Accounts.exe'), 'synthetic-component');
    for (const relative of ['window.cjs']) {
      const target = path.join(packageRoot, 'account-plugin', relative);
      await mkdir(path.dirname(target), { recursive: true }); await copyFile(path.join(ui, relative), target);
    }
    const before = sha256(await readFile(archive));
    const nativeBefore = sha256(await readFile(native));
    let checks = 0;
    const options = { installRoot, packageRoot, stateRoot, checkStopped: async () => { checks++; } };
    const result = await manageInstallation({ ...options, operation: 'apply' });
    assert.equal(result.installed, true); assert.equal(result.runtimeVerified, false);
    assert.notEqual(sha256(await readFile(path.join(resources, 'app.asar'))), before);
    assert.notEqual(sha256(await readFile(path.join(resources, 'bin', 'language_server.exe'))), nativeBefore);
    await assert.rejects(manageInstallation({ ...options, operation: 'apply' }), /already exists/);
    assert.equal((await manageInstallation({ ...options, operation: 'restore' })).restored, true);
    assert.equal(sha256(await readFile(path.join(resources, 'app.asar'))), before);
    assert.equal(sha256(await readFile(archive)), before);
    assert.equal(sha256(await readFile(path.join(resources, 'bin', 'language_server.exe'))), nativeBefore);
    assert.equal(sha256(await readFile(native)), nativeBefore);
    assert.equal(existsSync(path.join(resources, 'hostauth-addon')), false);
    const later = Buffer.from('simulate a later official update');
    await writeFile(path.join(resources, 'app.asar'), later);
    await assert.rejects(manageInstallation({ ...options, operation: 'restore' }), /updated or modified/);
    assert.deepEqual(await readFile(path.join(resources, 'app.asar')), later);
    assert.equal(checks, 4);
  } finally {
    const resolved = await realpath(root);
    if (resolved !== root || path.dirname(resolved) !== temporary || !path.basename(resolved).startsWith('accounts-install-test-'))
      throw new Error('Refusing cleanup outside the owned temporary directory');
    await rm(resolved, { recursive: true });
  }
});

test('coordinator rejects unsupported actions and relative destinations before writing', async () => {
  await assert.rejects(manageInstallation({ operation: 'delete', installRoot: 'x', packageRoot: 'y', stateRoot: 'z' }), /Expected/);
  await assert.rejects(manageInstallation({ operation: 'apply', installRoot: 'x', packageRoot: 'y', stateRoot: 'z' }), /absolute/);
});

test('recognized official programs can replace a superseded installation while retaining its recovery files', {
  skip: !existsSync(archive) || !existsSync(native), timeout: 45000
}, async () => {
  const temporary = await realpath(os.tmpdir());
  const root = await mkdtemp(path.join(temporary, 'accounts-update-test-'));
  try {
    const installRoot = path.join(root, 'app'), resources = path.join(installRoot, 'resources');
    const packageRoot = path.join(root, 'package'), stateRoot = path.join(root, 'state');
    await mkdir(path.join(resources, 'bin'), { recursive: true });
    await copyFile(archive, path.join(resources, 'app.asar'));
    await copyFile(native, path.join(resources, 'bin/language_server.exe'));
    await mkdir(path.join(packageRoot, 'component'), { recursive: true });
    await writeFile(path.join(packageRoot, 'component/Antigravity.Accounts.exe'), 'synthetic-component');
    for (const relative of ['window.cjs']) {
      const target = path.join(packageRoot, 'account-plugin', relative);
      await mkdir(path.dirname(target), { recursive: true }); await copyFile(path.join(ui, relative), target);
    }
    const options = { installRoot, packageRoot, stateRoot, checkStopped: async () => {} };
    await manageInstallation({ ...options, operation: 'apply' });
    const receiptPath = path.join(stateRoot, 'adapter-installation.json');
    const oldReceiptBytes = await readFile(receiptPath), oldReceipt = JSON.parse(oldReceiptBytes);
    const originalAsar = await readFile(archive), originalNative = await readFile(native);
    // Simulate the updater replacing both official programs but leaving the
    // independent addon directory and installation receipt in place.
    await writeFile(path.join(resources, 'app.asar'), originalAsar);
    await writeFile(path.join(resources, 'bin/language_server.exe'), originalNative);
    const unknownAddonFile = path.join(resources, 'hostauth-addon/unexpected.txt');
    await writeFile(unknownAddonFile, 'preserve unknown addon data');
    await assert.rejects(manageInstallation({ ...options, operation: 'apply' }), /addon contents changed/);
    assert.deepEqual(await readFile(receiptPath), oldReceiptBytes);
    assert.deepEqual(await readFile(path.join(resources, 'app.asar')), originalAsar);
    assert.equal(await readFile(unknownAddonFile, 'utf8'), 'preserve unknown addon data');
    await rm(unknownAddonFile);
    const unknownAddonDirectory = path.join(resources, 'hostauth-addon/custom-empty');
    await mkdir(unknownAddonDirectory);
    await assert.rejects(manageInstallation({ ...options, operation: 'apply' }), /addon contents changed/);
    assert.deepEqual(await readFile(receiptPath), oldReceiptBytes);
    assert.equal(existsSync(unknownAddonDirectory), true);
    await rmdir(unknownAddonDirectory);
    // An unrecognized program is rejected before moving the old payload or
    // replacing the old receipt, even if its addon matches the prepared copy.
    await writeFile(path.join(resources, 'app.asar'), 'unrecognized updated program');
    await assert.rejects(manageInstallation({ ...options, operation: 'apply' }));
    assert.deepEqual(await readFile(receiptPath), oldReceiptBytes);
    assert.equal(existsSync(path.join(resources, 'hostauth-addon/component/Antigravity.Accounts.exe')), true);
    await writeFile(path.join(resources, 'app.asar'), originalAsar);
    await manageInstallation({ ...options, operation: 'apply' });
    const next = JSON.parse(await readFile(receiptPath));
    assert.equal(next.phase, 'applied');
    assert.notEqual(next.backupRoot, oldReceipt.backupRoot);
    assert.deepEqual(JSON.parse(await readFile(next.previousInstallation.previousReceipt)), oldReceipt);
    assert.equal(path.dirname(next.previousInstallation.retiredAddonRoot), oldReceipt.backupRoot);
    assert.equal(await readFile(path.join(next.previousInstallation.retiredAddonRoot, 'component/Antigravity.Accounts.exe'), 'utf8'), 'synthetic-component');
    for (const backup of [oldReceipt.backupRoot, next.backupRoot]) {
      assert.deepEqual(await readFile(path.join(backup, 'app.asar')), originalAsar);
      assert.deepEqual(await readFile(path.join(backup, 'language_server.exe')), originalNative);
    }
    await manageInstallation({ ...options, operation: 'restore' });
    assert.deepEqual(await readFile(path.join(resources, 'app.asar')), originalAsar);
    assert.deepEqual(await readFile(path.join(resources, 'bin/language_server.exe')), originalNative);
    assert.equal(existsSync(next.previousInstallation.retiredAddonRoot), true);
  } finally {
    const resolved = await realpath(root);
    if (resolved !== root || path.dirname(resolved) !== temporary || !path.basename(resolved).startsWith('accounts-update-test-'))
      throw new Error('Refusing cleanup outside the owned temporary directory');
    await rm(resolved, { recursive: true });
  }
});
