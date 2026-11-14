import { createHash } from 'node:crypto';
import nativePrograms from './native-program-profiles.cjs';
import {
  ORIGINAL_NATIVE_SHA256,
  PATCHED_NATIVE_SHA256 as DIAGNOSTICS_NATIVE_SHA256,
  NATIVE_DIAGNOSTICS_PROFILES,
} from './native-diagnostics-patch.mjs';

export { ORIGINAL_NATIVE_SHA256, DIAGNOSTICS_NATIVE_SHA256 };
export const PROJECT_REFRESH_VERSION = '2.32.1';
export const PROJECT_REFRESH_NATIVE_SHA256 = '9728f6eb169fd63a7b154934c7151932152fcc93ca97720117237218df907490';
export const PROVIDER_NATIVE_SHA256 = '319c1abd18b4d12ac2149473f04b07be8671d230ecf12adf4cb7d1e3fe85edf6';

// The existing getCAICProject call reads a cached LoadCodeAssist response before
// its caller obtains the current HTTP client/token. An account change can then
// send the previous account's project with the current account's token. Call
// the native cache's existing forceRefresh method at this one call site instead.
// Both methods take (cache, dictionary, context) and return (response, error).
// forceRefresh returns its fetched response; it does not reload the cache after
// fetching. No code, stack frames, auth provider, or cache layout is inserted.
// The reviewed callers load CodeAssistClient+0x50. The 2.31 cache adds member
// storage and moves its fetcher to +0x40. Its forceRefresh still passes the
// same cache, dictionary and context, returning the fetched response and error.
export const PROJECT_REFRESH_PROFILES = Object.freeze(nativePrograms.NATIVE_PROGRAM_PROFILES.map(profile => Object.freeze({
  version: profile.version, projectSha256: profile.projectSha256, providerSha256: profile.providerSha256, ...profile.projectRefresh,
})));
const variants = Object.freeze(PROJECT_REFRESH_PROFILES.flatMap(profile => {
  const diagnostics = NATIVE_DIAGNOSTICS_PROFILES.find(item => item.version === profile.version);
  return [
    { ...profile, beforeSha256: diagnostics.originalSha256, afterSha256: profile.projectSha256, diagnostics: false },
    { ...profile, beforeSha256: diagnostics.patchedSha256, afterSha256: profile.providerSha256, diagnostics: true },
  ];
}));
const digest = buffer => createHash('sha256').update(buffer).digest('hex');
function fail() { throw new Error('unsupported_native_project_refresh_program'); }

function addressOffset(buffer, va, length) {
  if (!Buffer.isBuffer(buffer) || buffer.length < 64 || buffer.toString('ascii', 0, 2) !== 'MZ') fail();
  const pe = buffer.readUInt32LE(60);
  if (pe > buffer.length - 264 || buffer.toString('binary', pe, pe + 4) !== 'PE\0\0') fail();
  const count = buffer.readUInt16LE(pe + 6), optionalSize = buffer.readUInt16LE(pe + 20);
  if (buffer.readUInt16LE(pe + 24) !== 0x20b || count < 1 || count > 96) fail();
  const table = pe + 24 + optionalSize;
  if (table + count * 40 > buffer.length) fail();
  const rva = va - buffer.readBigUInt64LE(pe + 48);
  if (rva < 0n) fail();
  for (let index = 0; index < count; index++) {
    const section = table + index * 40;
    const virtual = BigInt(buffer.readUInt32LE(section + 12));
    const rawSize = BigInt(buffer.readUInt32LE(section + 16));
    if (rva >= virtual && rva + BigInt(length) <= virtual + rawSize) {
      const offset = BigInt(buffer.readUInt32LE(section + 20)) + rva - virtual;
      if (offset + BigInt(length) > BigInt(buffer.length)) fail();
      return Number(offset);
    }
  }
  fail();
}

function checkedImage(buffer, requiredState) {
  if (!Buffer.isBuffer(buffer)) fail();
  const sha256 = digest(buffer);
  const variant = variants.find(value => requiredState === 'original' ? value.beforeSha256 === sha256 : value.afterSha256 === sha256);
  if (!variant) fail();
  const { address, originalTarget, refreshedTarget, dictionaryAddress } = variant;
  const before = Buffer.from(variant.before, 'hex'), after = Buffer.from(variant.after, 'hex');
  const offset = addressOffset(buffer, address, before.length);
  const instruction = requiredState === 'original' ? before : after;
  if (!buffer.subarray(offset, offset + instruction.length).equals(instruction)) fail();
  // Cache.Get loads the private forceRefresh dictionary from this member. This
  // reviewed instantiation uses the same dictionary address, preserving the ABI.
  if (buffer.readBigUInt64LE(addressOffset(buffer, dictionaryAddress + 0x40n, 8)) !== dictionaryAddress) fail();
  const target = address + 5n + BigInt(buffer.readInt32LE(offset + 1));
  if (target !== (requiredState === 'original' ? originalTarget : refreshedTarget)) fail();
  return { variant, offset };
}

function report(state, { variant, offset }) {
  const { address, originalTarget, refreshedTarget } = variant;
  const before = Buffer.from(variant.before, 'hex'), after = Buffer.from(variant.after, 'hex');
  return {
    schemaVersion: 1,
    version: variant.version,
    state,
    diagnostics: variant.diagnostics,
    originalSha256: variant.beforeSha256,
    patchedSha256: variant.afterSha256,
    runtimeValidated: false,
    changes: [{
      address: `0x${address.toString(16)}`, offset,
      before: before.toString('hex'), after: after.toString('hex'),
      originalTarget: `0x${originalTarget.toString(16)}`,
      patchedTarget: `0x${refreshedTarget.toString(16)}`,
      changedByteOffsets: Array.from(before, (byte, index) => byte === after[index] ? undefined : offset + index).filter(value => value !== undefined),
      purpose: 'refresh account project before returning it to request construction',
    }],
  };
}

export function inspectNativeProjectRefresh(buffer) {
  const sha256 = Buffer.isBuffer(buffer) ? digest(buffer) : '';
  const state = variants.some(value => value.afterSha256 === sha256) ? 'patched' : 'original';
  return report(state, checkedImage(buffer, state));
}

export function patchNativeProjectRefresh(buffer) {
  const checked = checkedImage(buffer, 'original');
  const output = Buffer.from(buffer);
  Buffer.from(checked.variant.after, 'hex').copy(output, checked.offset);
  checkedImage(output, 'patched');
  return { buffer: output, report: report('patched', checked) };
}

export function restoreNativeProjectRefresh(buffer) {
  const checked = checkedImage(buffer, 'patched');
  const output = Buffer.from(buffer);
  Buffer.from(checked.variant.before, 'hex').copy(output, checked.offset);
  checkedImage(output, 'original');
  return { buffer: output, report: report('original', checked) };
}
