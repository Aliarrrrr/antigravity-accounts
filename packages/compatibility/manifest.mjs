import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';

export const REPOSITORY = 'Aliarrrrr/antigravity-accounts';
export const BUNDLE_APPLICATION = 'AntigravityCompatibility';
export const BUNDLE_ROOT_NAME = 'AntigravityCompatibility';
export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
export const VERSION_PATTERN = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-preview\.(0|[1-9]\d*))?$/;
const fail = code => { throw new Error(code); };

export function compareVersions(left, right) {
  const a = VERSION_PATTERN.exec(left), b = VERSION_PATTERN.exec(right);
  if (!a || !b) fail('compatibility_version_invalid');
  for (let i = 1; i <= 3; i++) if (BigInt(a[i]) !== BigInt(b[i])) return BigInt(a[i]) > BigInt(b[i]) ? 1 : -1;
  if (a[4] === undefined || b[4] === undefined) return a[4] === b[4] ? 0 : a[4] === undefined ? 1 : -1;
  return BigInt(a[4]) === BigInt(b[4]) ? 0 : BigInt(a[4]) > BigInt(b[4]) ? 1 : -1;
}

export function safeRelativePath(value) {
  if (typeof value !== 'string' || !value || value.length > 500 || /[\\:\x00-\x1f\x7f]/.test(value)) return false;
  return value.split('/').every(part => part && part !== '.' && part !== '..' && !/[. ]$/.test(part) &&
    !/^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(part));
}

function allowedPayload(relative) {
  if (!safeRelativePath(relative) || /\.(?:asar|dpapi|vscdb|db|sqlite|pdb|pem|pfx|key)$/i.test(relative) ||
      /(?:^|\/)(?:state|backups|verification|artifacts|\.git|\.nuget|obj|restore-[^/]*|credentials[^/]*|accounts\.json|host-provider\.json)(?:\/|$)/i.test(relative)) return false;
  if (/\.exe$/i.test(relative)) return relative === 'accounts/component/Antigravity.Accounts.exe';
  return /^(?:accounts\/|localization\/(?:src\/|scripts\/|dictionaries\/|node_modules\/|(?:durable|localize)\.mjs$|package(?:-lock)?\.json$|LICENSE(?:\.upstream)?$|THIRD_PARTY_NOTICES\.md$)|compatibility\/[^/]+\.(?:mjs|ps1)$|README\.md$|LICENSE$)/.test(relative);
}

export function inspectManifest(manifest, { expectedVersion, expectedCommit, minimumVersion } = {}) {
  if (!manifest || manifest.schemaVersion !== 1 || manifest.application !== BUNDLE_APPLICATION || manifest.repository !== REPOSITORY ||
      manifest.channel !== 'preview' || !VERSION_PATTERN.test(manifest.version || '') || !/^[a-f0-9]{40}$/.test(manifest.provenance?.commit || '') ||
      typeof manifest.provenance?.dirty !== 'boolean' || typeof manifest.provenance?.release !== 'boolean' ||
      manifest.provenance.release && manifest.provenance.dirty ||
      expectedVersion !== undefined && manifest.version !== expectedVersion || expectedCommit !== undefined && manifest.provenance.commit !== expectedCommit ||
      minimumVersion !== undefined && compareVersions(manifest.version, minimumVersion) < 0) fail('compatibility_manifest_invalid');
  for (const component of ['accounts', 'localization']) {
    if (manifest.components?.[component]?.path !== component || typeof manifest.components[component].version !== 'string') fail('compatibility_manifest_invalid');
  }
  if (!Array.isArray(manifest.files) || manifest.files.length === 0 || manifest.files.length > 20000) fail('compatibility_manifest_invalid');
  const listed = new Map();
  for (const file of manifest.files) {
    if (!file || !allowedPayload(file.path) || !/^[a-f0-9]{64}$/i.test(file.sha256 || '') || listed.has(file.path.toLowerCase())) fail('compatibility_manifest_invalid');
    listed.set(file.path.toLowerCase(), { path: file.path, sha256: file.sha256.toLowerCase() });
  }
  for (const required of ['accounts/package-manifest.json', 'accounts/component/Antigravity.Accounts.exe', 'accounts/account-plugin/manage-install.mjs',
    'localization/src/toolkit.mjs', 'localization/src/native-compatibility.mjs', 'localization/dictionaries/zh-CN.json',
    'localization/node_modules/@electron/asar/package.json', 'localization/node_modules/acorn/package.json',
    'compatibility/cli.mjs', 'compatibility/bootstrap.mjs', 'compatibility/release-client.mjs', 'compatibility/manifest.mjs']) {
    if (!listed.has(required.toLowerCase())) fail('compatibility_manifest_incomplete');
  }
  return listed;
}

async function walk(directory, prefix = '') {
  const result = [];
  for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
    const relative = prefix + entry.name;
    if (!safeRelativePath(relative) || entry.isSymbolicLink()) fail('compatibility_payload_invalid');
    if (entry.isDirectory()) result.push(...await walk(path.join(directory, entry.name), relative + '/'));
    else if (entry.isFile()) result.push(relative);
    else fail('compatibility_payload_invalid');
  }
  return result;
}

export async function validateBundle(bundleRoot, options = {}) {
  if (!path.isAbsolute(bundleRoot || '')) fail('compatibility_bundle_path_invalid');
  const rootInfo = await fs.lstat(bundleRoot);
  if (!rootInfo.isDirectory() || rootInfo.isSymbolicLink()) fail('compatibility_bundle_path_invalid');
  const manifestFile = path.join(bundleRoot, 'bundle-manifest.json');
  const stat = await fs.lstat(manifestFile);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 8 * 1024 * 1024) fail('compatibility_manifest_invalid');
  const manifest = JSON.parse(await fs.readFile(manifestFile, 'utf8'));
  const listed = inspectManifest(manifest, options), files = await walk(bundleRoot);
  if (files.length !== listed.size + 1) fail('compatibility_payload_mismatch');
  for (const relative of files) {
    if (relative === 'bundle-manifest.json') continue;
    const expected = listed.get(relative.toLowerCase());
    if (!expected || expected.path !== relative || sha256(await fs.readFile(path.join(bundleRoot, relative))) !== expected.sha256) fail('compatibility_payload_mismatch');
  }
  return manifest;
}

export async function createManifest(bundleRoot, identity) {
  const files = (await walk(bundleRoot)).filter(relative => relative !== 'bundle-manifest.json').sort();
  const manifest = { schemaVersion: 1, application: BUNDLE_APPLICATION, repository: REPOSITORY, channel: 'preview', ...identity,
    files: await Promise.all(files.map(async relative => ({ path: relative, sha256: sha256(await fs.readFile(path.join(bundleRoot, relative))) }))) };
  inspectManifest(manifest);
  return manifest;
}
