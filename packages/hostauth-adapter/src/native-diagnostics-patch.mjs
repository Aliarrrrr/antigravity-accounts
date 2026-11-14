import { createHash } from 'node:crypto';

export const NATIVE_DIAGNOSTICS_VERSION = '2.32.1';
export const ORIGINAL_NATIVE_SHA256 = '65cdb8d132fc61056625081be7d07d4d22f57eba485de7edf0d24d98e77c891f';
// The complete patched digest is checked as well as the original image and
// individual instruction bytes. There is no version-guessing fallback.
export const PATCHED_NATIVE_SHA256 = '8ab537e29e484b7f31e97d1070e4726bc2e4d45180e7afa11d683057d8a5e693';

const route = '/debug/pprof/goroutine';
const previousChanges = Object.freeze([
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
export const NATIVE_DIAGNOSTICS_PROFILES = Object.freeze([
  Object.freeze({ version: NATIVE_DIAGNOSTICS_VERSION, originalSha256: ORIGINAL_NATIVE_SHA256, patchedSha256: PATCHED_NATIVE_SHA256,
    routeAddress: 0x143afdfd7n, changes: Object.freeze([
      { address: 0x1434c0242n, before: '0f84d4000000', after: 'e93500000090', purpose: 'goroutine-only route entry' },
      { address: 0x1434c0284n, before: '488d1daf905900', after: '488d1d4cdd6300', purpose: 'exact goroutine route literal' },
      { address: 0x1434c028bn, before: 'b90d000000', after: 'b916000000', purpose: 'goroutine route length' },
      { address: 0x1434c029cn, before: '488b8424d8010000', after: 'e97b000000909090', purpose: 'skip all remaining debug routes' },
    ]) }),
  Object.freeze({ version: '2.31.2', originalSha256: 'e40f0447109c43ece15e7025ca0864a138e0e16cb8864d4ccf64a00512a99e1a',
    patchedSha256: 'b02168614d75b680cd8b0814b0d01bf91c66f5e310ff8d772516b920c8813b5f',
    routeAddress: 0x1439c02aan, changes: Object.freeze([
      { address: 0x14339a7e2n, before: '0f84d4000000', after: 'e93500000090', purpose: 'goroutine-only route entry' },
      { address: 0x14339a824n, before: '488d1dd0265800', after: '488d1d7f5a6200', purpose: 'exact goroutine route literal' },
      { address: 0x14339a82bn, before: 'b90d000000', after: 'b916000000', purpose: 'goroutine route length' },
      { address: 0x14339a83cn, before: '488b8424d8010000', after: 'e97b000000909090', purpose: 'skip all remaining debug routes' },
    ]) }),
  Object.freeze({ version: '2.31.0', originalSha256: '77b76711786bd78b51a6879fa57e66f9f5d398ac5d369cf8b5b3e2fb5cfc741b',
    patchedSha256: '0bdb872c8092002dafdf8ef44f1b3da77c2b71ef09eaec47ea169911766b2a2b',
    routeAddress: 0x1439be1f7n, changes: Object.freeze([
      { address: 0x143397f62n, before: '0f84d4000000', after: 'e93500000090', purpose: 'goroutine-only route entry' },
      { address: 0x143397fa4n, before: '488d1d302f5800', after: '488d1d4c626200', purpose: 'exact goroutine route literal' },
      { address: 0x143397fabn, before: 'b90d000000', after: 'b916000000', purpose: 'goroutine route length' },
      { address: 0x143397fbcn, before: '488b8424d8010000', after: 'e97b000000909090', purpose: 'skip all remaining debug routes' },
    ]) }),
  Object.freeze({ version: '2.29.0', originalSha256: 'c98e7c331da5bcc37002a550e7d625b4c636f775fa2804f73777c6a500e1c3d5',
    patchedSha256: '169355a713b8199f6fe91dd5eaf81bc7f366544b2808014425fc820ccdd04bfc',
    routeAddress: 0x1436d189cn, changes: Object.freeze([
      { address: 0x1430ba6d7n, before: '0f84d4000000', after: 'e93500000090', purpose: 'goroutine-only route entry' },
      { address: 0x1430ba719n, before: '488d1d485b5700', after: '488d1d7c716100', purpose: 'exact goroutine route literal' },
      { address: 0x1430ba720n, before: 'b90d000000', after: 'b916000000', purpose: 'goroutine route length' },
      { address: 0x1430ba731n, before: '488b8424d8010000', after: 'e97b000000909090', purpose: 'skip all remaining debug routes' },
    ]) }),
  Object.freeze({ version: '2.28.2', originalSha256: 'a51a80eb1b355dc523dd9a6d7cec098ad450e3c9fb30d6001806cac86814f37b',
    patchedSha256: '684a9ea0bbba226335feac69f9fc45d27980522fe172979fd58de07f35c52441', routeAddress: 0x14359e538n, changes: previousChanges }),
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
