import { createHash } from 'node:crypto';
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
export const PROJECT_REFRESH_PROFILES = Object.freeze([
  Object.freeze({ version: PROJECT_REFRESH_VERSION, projectSha256: PROJECT_REFRESH_NATIVE_SHA256, providerSha256: PROVIDER_NATIVE_SHA256,
    address: 0x142860333n, originalTarget: 0x142874600n, refreshedTarget: 0x142874b60n, dictionaryAddress: 0x14441a140n,
    before: 'e8c8420100', after: 'e828480100' }),
  Object.freeze({ version: '2.31.2', projectSha256: '33bad001301bf863bb7108e77667aaf24c649981335f5d220656c75e59818508',
    providerSha256: 'ff323094c41c50508bd4b9d8cc66230c36d2c245df68a0216010603725aa866c',
    address: 0x14231e533n, originalTarget: 0x142333de0n, refreshedTarget: 0x142334340n, dictionaryAddress: 0x14429f1c0n,
    before: 'e8a8580100', after: 'e8085e0100' }),
  Object.freeze({ version: '2.31.0', projectSha256: '39b06d761fbfa7f83090b7531b1e64c062150ba366cd97233ffc86bee3352677',
    providerSha256: 'ff0bfbb49bd798cd07d6add6f7eda235605285fa5ac25731f86d26e30ca7ff6e',
    address: 0x14231bf73n, originalTarget: 0x142331560n, refreshedTarget: 0x142331ac0n, dictionaryAddress: 0x14429d1c0n,
    before: 'e8e8550100', after: 'e8485b0100' }),
  Object.freeze({ version: '2.29.0', projectSha256: '3269e7df7f3b34aba7b087c6ea8055163c4ace072c72ed376314775439b7453c',
    providerSha256: '9a58d7ec73af4da1f60b9682d6632ed54a9f62dd2752372c8f53f8cbe116eb85',
    address: 0x142114b73n, originalTarget: 0x142128540n, refreshedTarget: 0x1421289e0n, dictionaryAddress: 0x143ece9e0n,
    before: 'e8c8390100', after: 'e8683e0100' }),
  Object.freeze({ version: '2.28.2', projectSha256: 'd728fa9d5ce8eb69a32acbb3b820d25b1c8df901d1cee435c2c2c13963f2871b',
    providerSha256: 'c132ea0fc1688dfd614cae5ac5c47b89c6c36753375981a0600170cef896dfea',
    address: 0x1420bb593n, originalTarget: 0x1420ce620n, refreshedTarget: 0x1420cea20n, dictionaryAddress: 0x143d7fd80n,
    before: 'e888300100', after: 'e888340100' }),
]);
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
