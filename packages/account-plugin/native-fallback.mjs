import nativePrograms from '../hostauth-adapter/src/native-program-profiles.cjs';
import { createHash } from 'node:crypto';
import { execFile } from 'node:child_process';
import { lstat, readFile, realpath } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';

const run = promisify(execFile);
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const samePath = (left, right) => path.resolve(left).toLowerCase() === path.resolve(right).toLowerCase();
const validDigest = value => typeof value === 'string' && /^[a-f0-9]{64}$/i.test(value);
const fail = () => { throw new Error('native_fallback_program_invalid'); };

export const adaptedNativeDigests = nativePrograms.ADAPTED_NATIVE_DIGESTS;

async function regularFile(filename) {
  const info = await lstat(filename);
  if (!info.isFile() || info.isSymbolicLink() || !samePath(await realpath(filename), filename)) fail();
  return readFile(filename);
}

function executableImage(bytes, { requireVersion = false } = {}) {
  if (bytes.length < 512 || bytes.toString('ascii', 0, 2) !== 'MZ') fail();
  const pe = bytes.readUInt32LE(60);
  if (pe < 64 || pe > bytes.length - 264 || bytes.toString('binary', pe, pe + 4) !== 'PE\0\0' ||
      bytes.readUInt16LE(pe + 4) !== 0x8664 || bytes.readUInt16LE(pe + 24) !== 0x20b) fail();
  const count = bytes.readUInt16LE(pe + 6), optionalSize = bytes.readUInt16LE(pe + 20);
  const table = pe + 24 + optionalSize;
  if (!count || count > 96 || optionalSize < 240 || table + count * 40 > bytes.length) fail();
  for (let index = 0; index < count; index++) {
    const entry = table + index * 40;
    const size = bytes.readUInt32LE(entry + 16), offset = bytes.readUInt32LE(entry + 20);
    if (offset + size > bytes.length) fail();
  }
  // The certificate directory is a file offset, unlike the other PE data
  // directories. A truncated signature must not count as a complete program.
  const certificateOffset = bytes.readUInt32LE(pe + 24 + 144);
  const certificateSize = bytes.readUInt32LE(pe + 24 + 148);
  if (certificateOffset + certificateSize > bytes.length) fail();
  if (!requireVersion) return;
  const resourceRva = bytes.readUInt32LE(pe + 24 + 128), resourceSize = bytes.readUInt32LE(pe + 24 + 132);
  function fromRva(rva, size) {
    for (let index = 0; index < count; index++) {
      const entry = table + index * 40, start = bytes.readUInt32LE(entry + 12);
      if (rva >= start && rva + size <= start + bytes.readUInt32LE(entry + 16))
        return bytes.readUInt32LE(entry + 20) + rva - start;
    }
    fail();
  }
  if (!resourceRva || resourceSize < 16) fail();
  const resource = fromRva(resourceRva, resourceSize), versions = new Set();
  function resources(relative) {
    if (relative < 0 || relative + 16 > resourceSize) fail();
    const offset = resource + relative;
    const entries = bytes.readUInt16LE(offset + 12) + bytes.readUInt16LE(offset + 14);
    if (entries > 512 || relative + 16 + entries * 8 > resourceSize) fail();
    return Array.from({ length: entries }, (_, index) => ({
      id: bytes.readUInt32LE(offset + 16 + index * 8),
      target: bytes.readUInt32LE(offset + 20 + index * 8),
    }));
  }
  function versionLeaves(target, depth) {
    if (depth > 3) fail();
    if (target & 0x80000000) {
      for (const item of resources(target & 0x7fffffff)) versionLeaves(item.target, depth + 1);
      return;
    }
    if (target + 16 > resourceSize) fail();
    const length = bytes.readUInt32LE(resource + target + 4);
    const offset = fromRva(bytes.readUInt32LE(resource + target), length);
    const key = Buffer.from('VS_VERSION_INFO\0', 'utf16le');
    if (length < 6 + key.length || bytes.readUInt16LE(offset) > length ||
        bytes.readUInt16LE(offset + 2) < 52 || !bytes.subarray(offset + 6, offset + 6 + key.length).equals(key)) fail();
    const fixed = offset + ((6 + key.length + 3) & ~3);
    if (fixed + 52 > offset + length || bytes.readUInt32LE(fixed) !== 0xfeef04bd) fail();
    const high = bytes.readUInt32LE(fixed + 8), low = bytes.readUInt32LE(fixed + 12);
    versions.add([high >>> 16, high & 0xffff, low >>> 16, low & 0xffff].join('.'));
  }
  const version = resources(0).filter(item => item.id === 16);
  if (version.length !== 1) fail();
  versionLeaves(version[0].target, 0);
  if (versions.size !== 1) fail();
  return [...versions][0];
}

function asarFiles(bytes) {
  if (bytes.length < 16 || bytes.readUInt32LE(0) !== 4) fail();
  const headerSize = bytes.readUInt32LE(4), jsonSize = bytes.readUInt32LE(12);
  if (headerSize < 8 || jsonSize > headerSize - 8 || headerSize + 8 > bytes.length) fail();
  const header = JSON.parse(bytes.subarray(16, 16 + jsonSize).toString('utf8'));
  const entries = new Map(), dataStart = headerSize + 8;
  function walk(directory, prefix = '') {
    if (!directory.files || typeof directory.files !== 'object' || Array.isArray(directory.files)) fail();
    for (const [name, entry] of Object.entries(directory.files)) {
      if (!name || name === '.' || name === '..' || /[\\/:\0]/.test(name) || !entry || typeof entry !== 'object') fail();
      const key = prefix + name;
      if (entry.files) { walk(entry, key + '/'); continue; }
      if (entry.link || !Number.isSafeInteger(entry.size) || entry.size < 0) fail();
      if (entry.unpacked) entries.set(key, { entry });
      else {
        if (typeof entry.offset !== 'string' || !/^\d+$/.test(entry.offset)) fail();
        const offset = Number(entry.offset);
        if (!Number.isSafeInteger(offset) || offset + dataStart + entry.size > bytes.length) fail();
        entries.set(key, { entry, bytes: bytes.subarray(dataStart + offset, dataStart + offset + entry.size) });
      }
    }
  }
  walk(header);
  return entries;
}

function integrity(entry, bytes) {
  if (entry.integrity && (entry.integrity.algorithm !== 'SHA256' ||
      !validDigest(entry.integrity.hash) || digest(bytes) !== entry.integrity.hash.toLowerCase())) fail();
}

async function inspectArchive(filename, unpackedRoot) {
  const archive = await regularFile(filename), entries = asarFiles(archive), unpacked = {};
  const packed = new Map();
  let unpackedBase;
  for (const [name, item] of entries) {
    let bytes = item.bytes;
    if (item.entry.unpacked) {
      unpackedBase ||= await realpath(unpackedRoot);
      if (!samePath(unpackedBase, unpackedRoot)) fail();
      const target = path.join(unpackedBase, ...name.split('/'));
      const resolved = await realpath(target), relative = path.relative(unpackedBase, resolved);
      if (!relative || relative === '..' || relative.startsWith('..' + path.sep) || path.isAbsolute(relative)) fail();
      bytes = await regularFile(target);
      if (bytes.length !== item.entry.size) fail();
      unpacked[name] = digest(bytes);
    }
    integrity(item.entry, bytes);
    packed.set(name, bytes);
  }
  const metadata = JSON.parse(packed.get('package.json')?.toString('utf8') || 'null');
  if (metadata?.name !== 'antigravity' || metadata.main !== 'dist/main.js' || !/^\d+\.\d+\.\d+(?:-[\w.-]+)?(?:\+[\w.-]+)?$/.test(metadata.version)) fail();
  if ([...entries.keys()].some(name => name.startsWith('dist/hostauth-addon/'))) fail();
  for (const name of ['dist/main.js', 'dist/languageServer.js', 'dist/hostBridgeServer.js']) {
    const source = packed.get(name)?.toString('utf8');
    if (!source || /hostauthAddon|hostauthSession|accountsUpdateHandoff|hostauth-addon|--use_host_auth=true/.test(source)) fail();
  }
  const preload = packed.get('dist/preload.js')?.toString('utf8') || '';
  const localized = /ANTIGRAVITY_ZH_CN_NATIVE_V1|__antigravityZhCN/.test(preload);
  return { metadata, asarSha256: digest(archive), unpacked, localized };
}

// This module is deliberately self-contained so a launcher can bundle an
// identical copy outside the application and an unavailable Accounts cache.
// It validates current programs only and never reads the account vault.
export async function inspectNativeFallback({ installRoot, receipt, sourceAsarPath, sourceAsarSha256 } = {}) {
  if (!path.isAbsolute(installRoot || '')) fail();
  installRoot = await realpath(installRoot);
  const resourcesRoot = await realpath(path.join(installRoot, 'resources'));
  if (receipt?.schemaVersion !== 1 || receipt.phase !== 'applied' ||
      !path.isAbsolute(receipt.resourcesRoot || '') || !samePath(receipt.resourcesRoot, resourcesRoot) ||
      !['originalAsarSha256', 'patchedAsarSha256', 'originalNativeSha256', 'patchedNativeSha256'].every(key => validDigest(receipt[key]))) fail();
  for (const filename of [path.join(resourcesRoot, 'app.asar.hostauth-new'), path.join(resourcesRoot, 'bin', 'language_server.exe.hostauth-new')]) {
    try { await lstat(filename); fail(); } catch (error) { if (error.code !== 'ENOENT') throw error; }
  }
  const executable = await regularFile(path.join(installRoot, 'Antigravity.exe'));
  const native = await regularFile(path.join(resourcesRoot, 'bin', 'language_server.exe'));
  const executableVersion = executableImage(executable, { requireVersion: true });
  executableImage(native);
  const nativeSha256 = digest(native);
  if (nativeSha256 === receipt.patchedNativeSha256.toLowerCase() || adaptedNativeDigests.includes(nativeSha256)) fail();
  const unpackedRoot = path.join(resourcesRoot, 'app.asar.unpacked');
  const current = await inspectArchive(path.join(resourcesRoot, 'app.asar'), unpackedRoot);
  if (executableVersion.split('.').slice(0, 3).join('.') !== current.metadata.version.split(/[-+]/, 1)[0]) fail();
  const executableSha256 = digest(executable);
  let localization;
  // An official update can leave an obsolete sidecar record. It has no
  // authority over a complete archive that contains no localization payload.
  if (current.localized) localization = JSON.parse((await regularFile(path.join(resourcesRoot, 'antigravity-zh-cn.install.json'))).toString('utf8'));
  if (localization && (localization.schemaVersion !== 1 || !path.isAbsolute(localization.installRoot || '') || !samePath(localization.installRoot, installRoot) ||
      localization.appVersion !== current.metadata.version || localization.patchedSha256?.toLowerCase() !== current.asarSha256 ||
      localization.executableSha256?.toLowerCase() !== executableSha256 || !validDigest(localization.sourceSha256) ||
      JSON.stringify(Object.entries(localization.unpacked || {}).map(([name, value]) => [name, typeof value === 'string' ? value.toLowerCase() : null]).sort()) !== JSON.stringify(Object.entries(current.unpacked).sort()))) fail();
  if (sourceAsarPath !== undefined) {
    if (!path.isAbsolute(sourceAsarPath) || !validDigest(sourceAsarSha256)) fail();
    const original = await inspectArchive(sourceAsarPath, unpackedRoot);
    if (original.asarSha256 !== sourceAsarSha256.toLowerCase() || original.metadata.version !== current.metadata.version ||
        localization && localization.sourceSha256.toLowerCase() !== original.asarSha256) fail();
  }
  return {
    appVersion: current.metadata.version, asarSha256: current.asarSha256, nativeSha256,
    executableSha256, unpackedSha256: digest(Buffer.from(JSON.stringify(Object.entries(current.unpacked).sort()))),
  };
}

export async function confirmNativeFallback() {
  if (process.platform !== 'win32') throw new Error('windows_required');
  const shell = path.join(process.env.SystemRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  const message = '账号插件暂时无法适配当前更新。继续将使用 Antigravity 原生登录，可能与之前选择的额度账号不同。原账号授权仍保留，兼容恢复后可以继续使用。\n\n使用原生登录继续？';
  const command = `Add-Type -AssemblyName System.Windows.Forms; $choice=[System.Windows.Forms.MessageBox]::Show('${message.replaceAll("'", "''")}', 'Antigravity 兼容模式', [System.Windows.Forms.MessageBoxButtons]::OKCancel, [System.Windows.Forms.MessageBoxIcon]::Information, [System.Windows.Forms.MessageBoxDefaultButton]::Button2); if($choice -eq [System.Windows.Forms.DialogResult]::OK){'continue'}else{'cancel'}`;
  const { stdout } = await run(shell, ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(command, 'utf16le').toString('base64')], { windowsHide: true });
  return stdout.trim() === 'continue';
}
