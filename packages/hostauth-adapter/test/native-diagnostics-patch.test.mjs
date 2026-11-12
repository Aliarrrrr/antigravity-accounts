import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { access, readFile } from 'node:fs/promises';
import path from 'node:path';
import {
  patchNativeDiagnostics, restoreNativeDiagnostics, inspectNativeDiagnostics,
  NATIVE_DIAGNOSTICS_PROFILES,
} from '../src/native-diagnostics-patch.mjs';

const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const programPath = process.env.ANTIGRAVITY_LANGUAGE_SERVER || path.join(process.env.LOCALAPPDATA || '', 'Programs/antigravity/resources/bin/language_server.exe');
let hasProgram = false;
try { await access(programPath); hasProgram = true; } catch { /* No installed program is required by CI. */ }

test('unrecognized native images are rejected before any patch or restoration', () => {
  for (const bytes of [undefined, Buffer.alloc(0), Buffer.from('MZ'), Buffer.alloc(4096)]) {
    assert.throws(() => patchNativeDiagnostics(bytes), /unsupported_native_diagnostics_program/);
    assert.throws(() => restoreNativeDiagnostics(bytes), /unsupported_native_diagnostics_program/);
    assert.throws(() => inspectNativeDiagnostics(bytes), /unsupported_native_diagnostics_program/);
  }
});

test('reviewed image changes only the route instructions and restores byte-for-byte', { skip: !hasProgram }, async () => {
  const original = await readFile(programPath);
  const profile = NATIVE_DIAGNOSTICS_PROFILES.find(value => value.originalSha256 === digest(original));
  assert.ok(profile, 'integration input must be a reviewed original program');
  assert.equal(inspectNativeDiagnostics(original).state, 'original');
  const { buffer: patched, report } = patchNativeDiagnostics(original);
  assert.equal(digest(original), profile.originalSha256, 'source buffer is never mutated');
  assert.equal(digest(patched), profile.patchedSha256);
  assert.equal(report.route, '/debug/pprof/goroutine');
  assert.equal(report.version, profile.version);
  assert.equal(report.devModeEnabled, false);
  assert.equal(report.runtimeValidated, false);
  const allowed = new Set(report.changes.flatMap(change => Array.from({ length: change.before.length / 2 }, (_, index) => change.offset + index)));
  let changed = 0;
  for (let index = 0; index < original.length; index++) {
    if (original[index] === patched[index]) continue;
    changed++;
    assert.equal(allowed.has(index), true, `unexpected program change at ${index}`);
  }
  assert.equal(changed, 16);
  assert.deepEqual(report.changes.map(change => change.address), profile.changes.map(change => `0x${change.address.toString(16)}`));
  // Verify relative branch destinations and the pre-existing literal they use.
  const [entry, literal, length, exit] = report.changes;
  const entryAddress = Number(profile.changes[0].address);
  assert.equal(entryAddress + 5 + patched.readInt32LE(entry.offset + 1), entryAddress + 0x3a);
  assert.equal(Number(profile.changes[1].address) + 7 + patched.readInt32LE(literal.offset + 3), Number(profile.routeAddress));
  assert.equal(patched.readUInt32LE(length.offset + 1), 22);
  assert.equal(Number(profile.changes[3].address) + 5 + patched.readInt32LE(exit.offset + 1), entryAddress + 0xda);
  assert.equal(inspectNativeDiagnostics(patched).state, 'patched');
  assert.throws(() => patchNativeDiagnostics(patched), /unsupported_native_diagnostics_program/);
  assert.throws(() => restoreNativeDiagnostics(original), /unsupported_native_diagnostics_program/);
  const restored = restoreNativeDiagnostics(patched);
  assert.deepEqual(restored.buffer, original);
  assert.equal(restored.report.state, 'original');
});

test('changes elsewhere in either original or patched program are rejected', { skip: !hasProgram }, async () => {
  const original = await readFile(programPath);
  const { buffer: patched } = patchNativeDiagnostics(original);
  original[original.length - 1] ^= 1;
  patched[patched.length - 1] ^= 1;
  assert.throws(() => patchNativeDiagnostics(original), /unsupported_native_diagnostics_program/);
  assert.throws(() => restoreNativeDiagnostics(patched), /unsupported_native_diagnostics_program/);
});
