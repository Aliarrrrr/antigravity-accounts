#!/usr/bin/env node
// This stable entry uses Node built-ins only. Engine code lives in immutable
// version directories; it is never imported into this entry before selection.
import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { randomUUID } from 'node:crypto';

const VERSION = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-preview\.(0|[1-9]\d*))?$/;
const fail = code => { throw new Error(code); };
const samePath = (a, b) => path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();
async function readJSON(filename) {
  const info = await fs.lstat(filename);
  if (!info.isFile() || info.isSymbolicLink() || info.size > 131072) fail('compatibility_bootstrap_invalid');
  return JSON.parse(await fs.readFile(filename, 'utf8'));
}
async function writeJSON(filename, value) {
  const temporary = filename + '.' + randomUUID() + '.new';
  await fs.writeFile(temporary, JSON.stringify(value, null, 2) + '\n', { flag: 'wx' });
  try { await fs.rename(temporary, filename); } catch (error) { await fs.rm(temporary, { force: true }); throw error; }
}
function validateConfig(config, stateRoot) {
  if (!config || config.schemaVersion !== 1 || !samePath(config.stateRoot || '', stateRoot) ||
      ['stateRoot', 'installRoot', 'accountsStateRoot', 'localizationStateRoot', 'toolkitRoot', 'nodePath'].some(key => !path.isAbsolute(config[key] || '')) ||
      !config.enabledComponents || ['accounts', 'localization'].some(key => typeof config.enabledComponents[key] !== 'boolean')) fail('compatibility_bootstrap_invalid');
  const relative = path.relative(config.installRoot, stateRoot);
  if (!relative || relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative)) fail('compatibility_bootstrap_invalid');
  return config;
}
export async function engineDirectory(stateRoot, active) {
  if (active?.schemaVersion !== 1 || !VERSION.test(active.version || '')) fail('compatibility_active_version_invalid');
  const expected = path.join(stateRoot, 'versions', active.version);
  if (!samePath(expected, active.directory || '')) fail('compatibility_active_version_invalid');
  const info = await fs.lstat(expected);
  if (!info.isDirectory() || info.isSymbolicLink() || !samePath(await fs.realpath(expected), expected)) fail('compatibility_active_version_invalid');
  return expected;
}
export function engineArgs(command, config, bundleRoot, forwarded = []) {
  const args = [command, '--bundle-root', bundleRoot];
  for (const [key, flag] of [['installRoot', '--install-root'], ['stateRoot', '--state-root'], ['accountsStateRoot', '--accounts-state-root'],
    ['localizationStateRoot', '--localization-state-root'], ['toolkitRoot', '--toolkit-root'], ['nodePath', '--node-path']]) args.push(flag, config[key]);
  if (forwarded.length) args.push('--', ...forwarded);
  return args;
}
export async function runEngine(bundleRoot, command, config, forwarded = []) {
  return new Promise((resolve, reject) => {
    const env = { ...process.env }; delete env.ELECTRON_RUN_AS_NODE;
    const child = spawn(config.nodePath, [path.join(bundleRoot, 'compatibility', 'cli.mjs'), ...engineArgs(command, config, bundleRoot, forwarded)],
      { windowsHide: true, env, stdio: ['ignore', 'pipe', 'pipe'] });
    let output = '', error = '';
    child.stdout.on('data', bytes => { output = (output + bytes).slice(-1024 * 1024); });
    child.stderr.on('data', bytes => { error = (error + bytes).slice(-65536); });
    child.once('error', () => reject(new Error('compatibility_engine_unavailable')));
    child.once('close', code => {
      if (code !== 0) {
        // A structured CLI failure means the engine loaded and refused an
        // operation. Never rerun that operation through an older engine.
        try { const value = JSON.parse(error.trim().split(/\r?\n/).at(-1)); if (typeof value.error === 'string') return reject(new Error('compatibility_engine_operation_failed')); } catch {}
        return reject(new Error('compatibility_engine_unavailable'));
      }
      try { resolve(JSON.parse(output.trim().split(/\r?\n/).at(-1))); }
      catch { reject(new Error('compatibility_engine_response_invalid')); }
    });
  });
}
async function previousEngine(stateRoot, active) {
  if (!VERSION.test(active.previousVersion || '')) fail('compatibility_engine_unavailable');
  const previous = { schemaVersion: 1, version: active.previousVersion, directory: path.join(stateRoot, 'versions', active.previousVersion) };
  await engineDirectory(stateRoot, previous);
  await writeJSON(path.join(stateRoot, 'active-version.json'), previous);
  return previous;
}
export async function installBootstrap({ bundleRoot, stateRoot, resolveAccountsRoot, activate, ...options }) {
  if (!path.isAbsolute(stateRoot || '') || !path.isAbsolute(bundleRoot || '')) fail('compatibility_bootstrap_invalid');
  await fs.mkdir(stateRoot, { recursive: true });
  // MSIX can redirect individual receipt files while realpath(the parent)
  // still names the logical directory. Reuse the installer's public-metadata
  // resolver before reading preferences or persisting any account location.
  if (!resolveAccountsRoot) ({ resolveAccountsStateRoot: resolveAccountsRoot } = await import(pathToFileURL(path.join(bundleRoot, 'accounts', 'account-plugin', 'manage-install.mjs')).href));
  options.accountsStateRoot = await resolveAccountsRoot({ installRoot: options.installRoot, stateRoot: options.accountsStateRoot, create: false });
  async function optional(filename) { try { return await readJSON(filename); } catch (error) { if (error.code !== 'ENOENT') throw error; } }
  const previousConfig = await optional(path.join(stateRoot, 'bootstrap.json'));
  const accountReceipt = await optional(path.join(options.accountsStateRoot, 'adapter-installation.json'));
  const localizationRegistration = await optional(path.join(options.localizationStateRoot, 'windows-persistence', 'install.json'));
  const localizationDisabled = await fs.stat(path.join(options.localizationStateRoot, 'windows-persistence', 'disabled')).then(() => true, error => { if (error.code === 'ENOENT') return false; throw error; });
  const enabledComponents = options.enabledComponents || previousConfig?.enabledComponents || {
    accounts: accountReceipt?.phase === 'applied', localization: localizationRegistration?.installed === true && !localizationDisabled,
  };
  const config = validateConfig({ schemaVersion: 1, ...options, stateRoot, enabledComponents }, stateRoot);
  if (!activate) ({ activateBundle: activate } = await import(pathToFileURL(path.join(bundleRoot, 'compatibility', 'release-client.mjs')).href));
  const active = await activate({ stateRoot, bundleRoot, config });
  const stableFile = path.join(stateRoot, 'bootstrap.mjs');
  try { await fs.copyFile(fileURLToPath(import.meta.url), stableFile, 1); }
  catch (error) { if (error.code !== 'EEXIST') throw error; }
  await writeJSON(path.join(stateRoot, 'bootstrap.json'), config);
  return { installed: true, ...active, accountsStateRoot: config.accountsStateRoot };
}
export async function launchBootstrap({ stateRoot, command = 'launch', forwarded = [] }) {
  const config = validateConfig(await readJSON(path.join(stateRoot, 'bootstrap.json')), stateRoot);
  let active = await readJSON(path.join(stateRoot, 'active-version.json'));
  let root = await engineDirectory(stateRoot, active);
  if (command === 'watch') {
    let watchCompatibility;
    try { ({ watchCompatibility } = await import(pathToFileURL(path.join(root, 'compatibility', 'watch.mjs')).href)); }
    catch {
      active = await previousEngine(stateRoot, active);
      root = await engineDirectory(stateRoot, active);
      ({ watchCompatibility } = await import(pathToFileURL(path.join(root, 'compatibility', 'watch.mjs')).href));
    }
    const result = await watchCompatibility({ config, bundleRoot: root, runEngine });
    if (result.restart) {
      const child = spawn(config.nodePath, [path.join(stateRoot, 'bootstrap.mjs'), 'watch', '--state-root', stateRoot], { windowsHide: true, detached: true, stdio: 'ignore' });
      child.unref();
    }
    return result;
  }
  try { return await runEngine(root, command, config, forwarded); }
  catch (error) {
    if (error.message !== 'compatibility_engine_unavailable' && error.message !== 'compatibility_engine_response_invalid') throw error;
    active = await previousEngine(stateRoot, active);
    root = await engineDirectory(stateRoot, active);
    return runEngine(root, command, config, forwarded);
  }
}
if (process.argv[1] && samePath(process.argv[1], fileURLToPath(import.meta.url))) {
  const argv = process.argv.slice(2), command = argv.shift() || 'launch', options = {}, forwarded = [];
  const names = new Map([['--bundle-root', 'bundleRoot'], ['--state-root', 'stateRoot'], ['--install-root', 'installRoot'],
    ['--accounts-state-root', 'accountsStateRoot'], ['--localization-state-root', 'localizationStateRoot'], ['--toolkit-root', 'toolkitRoot'], ['--node-path', 'nodePath']]);
  try {
    for (let i = 0; i < argv.length; i++) {
      if (argv[i] === '--') { forwarded.push(...argv.slice(i + 1)); break; }
      if (!names.has(argv[i]) || !argv[i + 1]) fail('compatibility_bootstrap_argument_invalid');
      const key = names.get(argv[i]); if (options[key]) fail('compatibility_bootstrap_argument_invalid'); options[key] = argv[++i];
    }
    const result = command === 'install' ? await installBootstrap(options) : await launchBootstrap({ ...options, command, forwarded });
    console.log(JSON.stringify(result));
  } catch (error) { console.error(JSON.stringify({ error: /^[a-z][a-z0-9_]{0,90}$/.test(error.message || '') ? error.message : 'compatibility_bootstrap_failed' })); process.exitCode = 1; }
}
