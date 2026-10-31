import { createHash } from 'node:crypto';

export const NATIVE_DIAGNOSTICS_VERSION = '2.28.2';
export const ORIGINAL_NATIVE_SHA256 = 'a51a80eb1b355dc523dd9a6d7cec098ad450e3c9fb30d6001806cac86814f37b';
// The complete patched digest is checked as well as the original image and
// individual instruction bytes. There is no version-guessing fallback.
export const PATCHED_NATIVE_SHA256 = '684a9ea0bbba226335feac69f9fc45d27980522fe172979fd58de07f35c52441';

const routeAddress = 0x14359e538n;
const route = '/debug/pprof/goroutine';
const changes = Object.freeze([
  // Bypass the dev-mode conditional and metrics setup. Register only the one
  // exact goroutine path through the existing pprof.Index handler.
  { address: 0x142f9b582n, before: '0f84d4000000', after: 'e93500000090', purpose: 'goroutine-only route entry' },
  { address: 0x142f9b5c4n, before: '488d1d48265600', after: '488d1d6d2f6000', purpose: 'exact goroutine route literal' },
  { address: 0x142f9b5cbn, before: 'b90d000000', after: 'b916000000', purpose: 'goroutine route length' },
  // This is the instruction immediately after ServeMux.HandleFunc returns.
  // Skip cmdline, profile, symbol and trace registrations without altering any
  // global dev-mode setting or authentication code.
  { address: 0x142f9b5dcn, before: '488b8424c0010000', after: 'e97b000000909090', purpose: 'skip all remaining debug routes' },
]);

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
  if (!Buffer.isBuffer(buffer) || digest(buffer) !== (state === 'original' ? ORIGINAL_NATIVE_SHA256 : PATCHED_NATIVE_SHA256)) fail();
  const routeOffset = addressOffset(buffer, routeAddress, route.length);
  if (buffer.toString('ascii', routeOffset, routeOffset + route.length) !== route) fail();
  return changes.map(change => {
    const before = Buffer.from(change.before, 'hex'), after = Buffer.from(change.after, 'hex');
    if (before.length !== after.length) fail();
    const offset = addressOffset(buffer, change.address, before.length);
    if (!buffer.subarray(offset, offset + before.length).equals(state === 'original' ? before : after)) fail();
    return { ...change, offset, before, after };
  });
}

function report(state, checked) {
  return {
    schemaVersion: 1,
    version: NATIVE_DIAGNOSTICS_VERSION,
    state,
    originalSha256: ORIGINAL_NATIVE_SHA256,
    patchedSha256: PATCHED_NATIVE_SHA256,
    route,
    routeQueryUsed: 'debug=1',
    devModeEnabled: false,
    runtimeValidated: false,
    changes: checked.map(change => ({ address: `0x${change.address.toString(16)}`, offset: change.offset, before: change.before.toString('hex'), after: change.after.toString('hex'), purpose: change.purpose })),
  };
}

export function inspectNativeDiagnostics(buffer) {
  const state = Buffer.isBuffer(buffer) && digest(buffer) === PATCHED_NATIVE_SHA256 ? 'patched' : 'original';
  return report(state, checkedImage(buffer, state));
}

export function patchNativeDiagnostics(buffer) {
  const checked = checkedImage(buffer, 'original');
  const output = Buffer.from(buffer);
  for (const change of checked) change.after.copy(output, change.offset);
  checkedImage(output, 'patched');
  return { buffer: output, report: report('patched', checked) };
}

export function restoreNativeDiagnostics(buffer) {
  const checked = checkedImage(buffer, 'patched');
  const output = Buffer.from(buffer);
  for (const change of checked) change.before.copy(output, change.offset);
  checkedImage(output, 'original');
  return { buffer: output, report: report('original', checked) };
}
