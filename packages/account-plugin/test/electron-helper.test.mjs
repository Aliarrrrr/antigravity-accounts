import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';
import { sha256 } from '../../hostauth-adapter/src/asar.mjs';

const source = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const install = process.env.ACCOUNTS_TEST_INSTALL || path.join(process.env.LOCALAPPDATA || '', 'Programs', 'antigravity');
const executable = process.env.ACCOUNTS_ELECTRON_EXECUTABLE || path.join(install, 'Antigravity.exe');

test('official Electron in Node mode can prepare raw ASAR bytes without launching App or LS', {
  skip: process.platform !== 'win32' || !existsSync(executable), timeout: 45000
}, async () => {
  const temporary = await realpath(os.tmpdir());
  const root = await mkdtemp(path.join(temporary, 'accounts-electron-helper-test-'));
  try {
    const packageRoot = path.join(root, 'package');
    await mkdir(path.join(packageRoot, 'component'), { recursive: true });
    await writeFile(path.join(packageRoot, 'component/Antigravity.Accounts.exe'), 'synthetic-not-executed');
    // Use a link-free copy of UI files; no official account data is involved.
    for (const relative of ['window.cjs']) {
      const filename = path.join(packageRoot, 'account-plugin', relative);
      await mkdir(path.dirname(filename), { recursive: true });
      await writeFile(filename, await readFile(path.join(source, relative)));
    }
    const original = path.join(install, 'resources/app.asar');
    const before = sha256(await readFile(original));
    const result = spawnSync(executable, [path.join(source, 'manage-install.mjs'), 'prepare',
      '--install-root', install, '--package-root', packageRoot, '--state-root', path.join(root, 'state')], {
      env: { ...process.env, ELECTRON_RUN_AS_NODE: '1' }, windowsHide: true, encoding: 'utf8', timeout: 30000
    });
    assert.equal(result.status, 0, result.stderr || result.error?.message);
    const response = JSON.parse(result.stdout);
    assert.equal(response.runtimeVerified, false); assert.equal(response.credentialFilesRead, false);
    assert.equal(sha256(await readFile(original)), before);
  } finally {
    const resolved = await realpath(root);
    if (resolved !== root || path.dirname(resolved) !== temporary || !path.basename(resolved).startsWith('accounts-electron-helper-test-'))
      throw new Error('Refusing cleanup outside the owned temporary directory');
    await rm(resolved, { recursive: true });
  }
});
