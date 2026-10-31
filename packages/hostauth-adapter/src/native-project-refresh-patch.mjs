import { createHash } from 'node:crypto';
import {
  ORIGINAL_NATIVE_SHA256,
  PATCHED_NATIVE_SHA256 as DIAGNOSTICS_NATIVE_SHA256,
} from './native-diagnostics-patch.mjs';

export { ORIGINAL_NATIVE_SHA256, DIAGNOSTICS_NATIVE_SHA256 };
export const PROJECT_REFRESH_VERSION = '2.28.2';
export const PROJECT_REFRESH_NATIVE_SHA256 = 'd728fa9d5ce8eb69a32acbb3b820d25b1c8df901d1cee435c2c2c13963f2871b';
export const PROVIDER_NATIVE_SHA256 = 'c132ea0fc1688dfd614cae5ac5c47b89c6c36753375981a0600170cef896dfea';

// The existing getCAICProject call reads a cached LoadCodeAssist response before
// its caller obtains the current HTTP client/token. An account change can then
// send the previous account's project with the current account's token. Call
// the native cache's existing forceRefresh method at this one call site instead.
// Both methods take (cache, dictionary, context) and return (response, error).
// forceRefresh returns its fetched response; it does not reload the cache after
// fetching. No code, stack frames, auth provider, or cache layout is inserted.
// In 2.28 the caller still reads this cache at CodeAssistClient+0x50.
// The constructor still binds that member to fetchLoadCodeAssistResponse;
// the cache methods and dictionary ABI retain the reviewed instruction flow.
const address = 0x1420bb593n;
const originalTarget = 0x1420ce620n;
const refreshedTarget = 0x1420cea20n;
const dictionaryAddress = 0x143d7fd80n;
const before = Buffer.from('e888300100', 'hex');
const after = Buffer.from('e888340100', 'hex');
const variants = Object.freeze([
  { beforeSha256: ORIGINAL_NATIVE_SHA256, afterSha256: PROJECT_REFRESH_NATIVE_SHA256, diagnostics: false },
  { beforeSha256: DIAGNOSTICS_NATIVE_SHA256, afterSha256: PROVIDER_NATIVE_SHA256, diagnostics: true },
]);
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
  return {
    schemaVersion: 1,
    version: PROJECT_REFRESH_VERSION,
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
      changedByteOffsets: [offset + 2],
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
  after.copy(output, checked.offset);
  checkedImage(output, 'patched');
  return { buffer: output, report: report('patched', checked) };
}

export function restoreNativeProjectRefresh(buffer) {
  const checked = checkedImage(buffer, 'patched');
  const output = Buffer.from(buffer);
  before.copy(output, checked.offset);
  checkedImage(output, 'original');
  return { buffer: output, report: report('original', checked) };
}
