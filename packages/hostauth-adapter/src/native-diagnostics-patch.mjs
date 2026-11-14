import { createHash } from 'node:crypto';
import nativePrograms from './native-program-profiles.cjs';

export const NATIVE_DIAGNOSTICS_PROFILES = Object.freeze(nativePrograms.NATIVE_PROGRAM_PROFILES.map(profile => Object.freeze({
  version: profile.version, originalSha256: profile.originalSha256, patchedSha256: profile.diagnosticsSha256, ...profile.diagnostics,
})));
export const NATIVE_DIAGNOSTICS_VERSION = NATIVE_DIAGNOSTICS_PROFILES[0].version;
export const ORIGINAL_NATIVE_SHA256 = NATIVE_DIAGNOSTICS_PROFILES[0].originalSha256;
export const PATCHED_NATIVE_SHA256 = NATIVE_DIAGNOSTICS_PROFILES[0].patchedSha256;
const route = '/debug/pprof/goroutine';

function digest(buffer) { return createHash('sha256').update(buffer).digest('hex'); }
function fail() { throw new Error('unsupported_native_diagnostics_program'); }

function addressOffset(buffer, address, length) {
  if (!Buffer.isBuffer(buffer) || buffer.length < 64 || buffer.toString('ascii', 0, 2) !== 'MZ') fail();
  const pe = buffer.readUInt32LE(60);
  if (pe > buffer.length - 264 || buffer.toString('binary', pe, pe + 4) !== 'PE\0\0') fail();
  const count = buffer.readUInt16LE(pe + 6), optionalSize = buffer.readUInt16LE(pe + 20);
  if (buffer.readUInt16LE(pe + 24) !== 0x20b || count < 1 || count > 96) fail();
  const table = pe + 24 + optionalSize;
  if (table + count * 40 > buffer.length) fail();
  const rva = address - buffer.readBigUInt64LE(pe + 48);
  if (rva < 0n) fail();
  for (let index = 0; index < count; index++) {
    const section = table + index * 40;
    const virtual = BigInt(buffer.readUInt32LE(section + 12));
    const rawSize = BigInt(buffer.readUInt32LE(section + 16));
    const rawOffset = BigInt(buffer.readUInt32LE(section + 20));
    if (rva >= virtual && rva + BigInt(length) <= virtual + rawSize) {
      const offset = rawOffset + rva - virtual;
      if (offset + BigInt(length) > BigInt(buffer.length)) fail();
      return Number(offset);
    }
  }
  fail();
}

function checkedImage(buffer, state) {
  if (!Buffer.isBuffer(buffer)) fail();
  const imageDigest = digest(buffer);
  const profile = NATIVE_DIAGNOSTICS_PROFILES.find(item => imageDigest === (state === 'original' ? item.originalSha256 : item.patchedSha256));
  if (!profile) fail();
  const routeOffset = addressOffset(buffer, profile.routeAddress, route.length);
  if (buffer.toString('ascii', routeOffset, routeOffset + route.length) !== route) fail();
  const changes = profile.changes.map(change => {
    const before = Buffer.from(change.before, 'hex'), after = Buffer.from(change.after, 'hex');
    if (before.length !== after.length) fail();
    const offset = addressOffset(buffer, change.address, before.length);
    if (!buffer.subarray(offset, offset + before.length).equals(state === 'original' ? before : after)) fail();
    return { ...change, offset, before, after };
  });
  return { profile, changes };
}

function report(state, { profile, changes }) {
  return {
    schemaVersion: 1,
    version: profile.version,
    state,
    originalSha256: profile.originalSha256,
    patchedSha256: profile.patchedSha256,
    route,
    routeQueryUsed: 'debug=1',
    devModeEnabled: false,
    runtimeValidated: false,
    changes: changes.map(change => ({ address: `0x${change.address.toString(16)}`, offset: change.offset, before: change.before.toString('hex'), after: change.after.toString('hex'), purpose: change.purpose })),
  };
}

export function inspectNativeDiagnostics(buffer) {
  const imageDigest = Buffer.isBuffer(buffer) ? digest(buffer) : '';
  const state = NATIVE_DIAGNOSTICS_PROFILES.some(profile => imageDigest === profile.patchedSha256) ? 'patched' : 'original';
  return report(state, checkedImage(buffer, state));
}

export function patchNativeDiagnostics(buffer) {
  const checked = checkedImage(buffer, 'original');
  const output = Buffer.from(buffer);
  for (const change of checked.changes) change.after.copy(output, change.offset);
  checkedImage(output, 'patched');
  return { buffer: output, report: report('patched', checked) };
}

export function restoreNativeDiagnostics(buffer) {
  const checked = checkedImage(buffer, 'patched');
  const output = Buffer.from(buffer);
  for (const change of checked.changes) change.before.copy(output, change.offset);
  checkedImage(output, 'original');
  return { buffer: output, report: report('original', checked) };
}
