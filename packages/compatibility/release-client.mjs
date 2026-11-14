import fs from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { REPOSITORY, BUNDLE_ROOT_NAME, VERSION_PATTERN, compareVersions, sha256, validateBundle } from './manifest.mjs';

export const CHECK_INTERVAL_MS = 6 * 60 * 60 * 1000;
const run = promisify(execFile);
const API = `https://api.github.com/repos/${REPOSITORY}`;
const fail = code => { throw new Error(code); };
const samePath = (a, b) => path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();
const regularJSON = async filename => {
  try { const s = await fs.lstat(filename); if (!s.isFile() || s.isSymbolicLink() || s.size > 8 * 1024 * 1024) fail('compatibility_update_state_invalid'); return JSON.parse(await fs.readFile(filename, 'utf8')); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
};
export async function writeJSON(filename, value) {
  const temporary = filename + '.' + randomUUID() + '.new';
  await fs.mkdir(path.dirname(filename), { recursive: true });
  await fs.writeFile(temporary, JSON.stringify(value, null, 2) + '\n', { flag: 'wx' });
  try { await fs.rename(temporary, filename); } catch (error) { await fs.rm(temporary, { force: true }); throw error; }
}

async function responseBytes(fetchImpl, url, maximum, timeoutMs) {
  const controller = new AbortController(), timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetchImpl(url, { headers: { Accept: 'application/vnd.github+json', 'User-Agent': 'AntigravityCompatibility' }, signal: controller.signal });
    if (!response.ok || response.status !== 200) fail('compatibility_download_failed');
    const length = Number(response.headers?.get?.('content-length'));
    if (Number.isFinite(length) && length > maximum) fail('compatibility_download_too_large');
    const reader = response.body?.getReader?.();
    if (!reader) { const bytes = Buffer.from(await response.arrayBuffer()); if (bytes.length > maximum) fail('compatibility_download_too_large'); return bytes; }
    const chunks = []; let size = 0;
    try {
      while (true) { const { done, value } = await reader.read(); if (done) break; size += value.length; if (size > maximum) fail('compatibility_download_too_large'); chunks.push(Buffer.from(value)); }
    } finally { await reader.cancel().catch(() => {}); }
    return Buffer.concat(chunks, size);
  } finally { clearTimeout(timer); }
}
const fetchJSON = async (fetchImpl, url, maximum, timeoutMs) => JSON.parse((await responseBytes(fetchImpl, url, maximum, timeoutMs)).toString('utf8'));

export function selectRelease(releases, currentVersion) {
  if (!VERSION_PATTERN.test(currentVersion || '') || !Array.isArray(releases)) fail('compatibility_release_invalid');
  const selected = releases.filter(release => release && release.draft === false && release.prerelease === true &&
    typeof release.tag_name === 'string' && release.tag_name.startsWith('accounts-v') && VERSION_PATTERN.test(release.tag_name.slice(10)) &&
    release.tag_name.slice(10).includes('-preview.') && compareVersions(release.tag_name.slice(10), currentVersion) > 0 &&
    release.assets?.some(item => item.name === `${BUNDLE_ROOT_NAME}-${release.tag_name.slice(10)}-win-x64.zip`))
    .sort((a, b) => compareVersions(b.tag_name.slice(10), a.tag_name.slice(10)))[0];
  if (!selected) return;
  const version = selected.tag_name.slice(10), zipName = `${BUNDLE_ROOT_NAME}-${version}-win-x64.zip`;
  const expectedURL = name => `https://github.com/${REPOSITORY}/releases/download/${selected.tag_name}/${name}`;
  const asset = name => {
    const matches = selected.assets?.filter(item => item.name === name) || [];
    if (matches.length !== 1 || matches[0].browser_download_url !== expectedURL(name)) fail('compatibility_release_invalid');
    return matches[0];
  };
  // Older previews did not contain a bundle; skip them rather than treating
  // their Accounts-only ZIP as a localization engine update.
  if (!selected.assets?.some(item => item.name === zipName)) return;
  return { version, tag: selected.tag_name, zip: asset(zipName), proof: asset(zipName + '.sha256.json') };
}

async function releaseCommit(fetchImpl, tag, timeoutMs) {
  let reference = await fetchJSON(fetchImpl, `${API}/git/ref/tags/${encodeURIComponent(tag)}`, 131072, timeoutMs);
  for (let depth = 0; depth < 3; depth++) {
    const object = reference?.object;
    if (!/^[a-f0-9]{40}$/.test(object?.sha || '')) fail('compatibility_release_invalid');
    if (object.type === 'commit') return object.sha;
    if (object.type !== 'tag') fail('compatibility_release_invalid');
    reference = await fetchJSON(fetchImpl, `${API}/git/tags/${object.sha}`, 131072, timeoutMs);
  }
  fail('compatibility_release_invalid');
}

export async function extractBundle(zipPath, outputRoot) {
  if (process.platform !== 'win32') fail('compatibility_zip_requires_windows');
  const script = fileURLToPath(new URL('./extract-bundle.ps1', import.meta.url));
  const powershell = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  await run(powershell, ['-NoLogo', '-NoProfile', '-NonInteractive', '-File', script, '-ZipPath', zipPath, '-OutputRoot', outputRoot],
    { windowsHide: true, timeout: 120000, maxBuffer: 131072 });
  return path.join(outputRoot, BUNDLE_ROOT_NAME);
}

export async function checkForUpdates({ stateRoot, currentVersion, hostStamp = '', fetchImpl = fetch,
  now = () => Date.now(), timeoutMs = 15000, extract = extractBundle, force = false } = {}) {
  if (!path.isAbsolute(stateRoot || '') || !VERSION_PATTERN.test(currentVersion || '')) fail('compatibility_update_configuration_invalid');
  await fs.mkdir(stateRoot, { recursive: true });
  const stateFile = path.join(stateRoot, 'update-check.json'), previous = await regularJSON(stateFile), checkedAt = now();
  if (!force && previous?.schemaVersion === 1 && previous.hostStamp === hostStamp && Number.isSafeInteger(previous.checkedAt) &&
      checkedAt >= previous.checkedAt && checkedAt - previous.checkedAt < CHECK_INTERVAL_MS) return { status: 'deferred', retryAt: previous.checkedAt + CHECK_INTERVAL_MS };
  const record = { schemaVersion: 1, checkedAt, hostStamp, currentVersion };
  try {
    const releases = await fetchJSON(fetchImpl, `${API}/releases?per_page=30`, 4 * 1024 * 1024, timeoutMs);
    const minimum = previous?.highestVersion && compareVersions(previous.highestVersion, currentVersion) > 0 ? previous.highestVersion : currentVersion;
    const release = selectRelease(releases, currentVersion);
    if (!release) { await writeJSON(stateFile, { ...record, highestVersion: minimum, status: 'current' }); return { status: 'current' }; }
    if (compareVersions(release.version, minimum) < 0) fail('compatibility_release_downgrade');
    const proof = await fetchJSON(fetchImpl, release.proof.browser_download_url, 4096, timeoutMs);
    if (proof.file !== release.zip.name || !/^[a-f0-9]{64}$/i.test(proof.sha256 || '') ||
        release.zip.digest && release.zip.digest.toLowerCase() !== 'sha256:' + proof.sha256.toLowerCase()) fail('compatibility_release_invalid');
    const commit = await releaseCommit(fetchImpl, release.tag, timeoutMs);
    const pendingFile = path.join(stateRoot, 'pending-bundle.json'), previousPending = await regularJSON(pendingFile);
    if (previousPending?.version === release.version) {
      if (previousPending.sha256 !== proof.sha256.toLowerCase() || previousPending.commit !== commit) fail('compatibility_release_replaced');
      try {
        await validateBundle(previousPending.bundleRoot, { expectedVersion: release.version, expectedCommit: commit, minimumVersion: minimum });
        await writeJSON(stateFile, { ...record, highestVersion: release.version, status: 'prepared' });
        return { status: 'prepared', ...previousPending };
      } catch { /* Redownload this same release after an interrupted or damaged preparation. */ }
    }
    const bytes = await responseBytes(fetchImpl, release.zip.browser_download_url, 256 * 1024 * 1024, Math.max(timeoutMs, 120000));
    if (sha256(bytes) !== proof.sha256.toLowerCase()) fail('compatibility_zip_mismatch');
    const staging = path.join(stateRoot, 'downloads', release.version + '-' + randomUUID());
    await fs.mkdir(staging, { recursive: true });
    const zipPath = path.join(staging, release.zip.name);
    await fs.writeFile(zipPath, bytes, { flag: 'wx' });
    const bundleRoot = await extract(zipPath, path.join(staging, 'extracted'));
    const manifest = await validateBundle(bundleRoot, { expectedVersion: release.version, expectedCommit: commit, minimumVersion: minimum });
    if (manifest.provenance.release !== true || manifest.provenance.dirty) fail('compatibility_release_invalid');
    const pending = { schemaVersion: 1, version: release.version, bundleRoot, sha256: proof.sha256.toLowerCase(), commit };
    await writeJSON(path.join(stateRoot, 'pending-bundle.json'), pending);
    await writeJSON(stateFile, { ...record, highestVersion: release.version, status: 'prepared' });
    return { status: 'prepared', ...pending };
  } catch (error) {
    const errorCode = /^compatibility_[a-z_]+$/.test(error.message || '') ? error.message : 'compatibility_update_unavailable';
    await writeJSON(stateFile, { ...record, highestVersion: previous?.highestVersion || currentVersion, status: 'unavailable', errorCode });
    return { status: 'unavailable', errorCode };
  }
}

export async function activateBundle({ stateRoot, bundleRoot, config, probe } = {}) {
  if (!path.isAbsolute(stateRoot || '') || !path.isAbsolute(bundleRoot || '')) fail('compatibility_update_configuration_invalid');
  const activeFile = path.join(stateRoot, 'active-version.json'), previous = await regularJSON(activeFile);
  const manifest = await validateBundle(bundleRoot, { minimumVersion: previous?.version });
  const versionRoot = path.join(stateRoot, 'versions', manifest.version);
  if (!samePath(versionRoot, bundleRoot)) {
    await fs.mkdir(path.dirname(versionRoot), { recursive: true });
    if (!await fs.lstat(versionRoot).then(() => true, error => { if (error.code === 'ENOENT') return false; throw error; })) {
      const staged = path.join(stateRoot, 'versions', '.stage-' + randomUUID());
      try {
        await fs.cp(bundleRoot, staged, { recursive: true, force: false, errorOnExist: true });
        await validateBundle(staged, { expectedVersion: manifest.version, expectedCommit: manifest.provenance.commit });
        await fs.rename(staged, versionRoot);
      } catch (error) {
        // staged is a newly allocated direct child of this versions directory.
        if (samePath(path.dirname(staged), path.join(stateRoot, 'versions')) && path.basename(staged).startsWith('.stage-')) await fs.rm(staged, { recursive: true, force: true });
        throw error;
      }
    }
  }
  await validateBundle(versionRoot, { expectedVersion: manifest.version, expectedCommit: manifest.provenance.commit });
  config ||= await regularJSON(path.join(stateRoot, 'bootstrap.json'));
  if (typeof probe === 'function') await probe(versionRoot, config);
  else {
    const args = ['inspect', '--bundle-root', versionRoot];
    for (const [key, flag] of [['installRoot', '--install-root'], ['stateRoot', '--state-root'], ['accountsStateRoot', '--accounts-state-root'],
      ['localizationStateRoot', '--localization-state-root'], ['nodePath', '--node-path'], ['toolkitRoot', '--toolkit-root']]) if (config?.[key]) args.push(flag, config[key]);
    await run(config?.nodePath || process.execPath, [path.join(versionRoot, 'compatibility', 'cli.mjs'), ...args], { windowsHide: true, timeout: 120000, maxBuffer: 1024 * 1024 });
  }
  const active = { schemaVersion: 1, version: manifest.version, directory: versionRoot,
    ...(previous?.version && previous.version !== manifest.version ? { previousVersion: previous.version } : previous?.previousVersion ? { previousVersion: previous.previousVersion } : {}) };
  await writeJSON(activeFile, active);
  return { status: 'activated', ...active };
}
