import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { access, readFile } from 'node:fs/promises';
import path from 'node:path';
import {
  patchNativeDiagnostics, restoreNativeDiagnostics, inspectNativeDiagnostics,
  ORIGINAL_NATIVE_SHA256, PATCHED_NATIVE_SHA256,
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

test('reviewed 2.28.2 image changes only the route instructions and restores byte-for-byte', { skip: !hasProgram }, async () => {
  const original = await readFile(programPath);
  assert.equal(digest(original), ORIGINAL_NATIVE_SHA256);
  assert.equal(inspectNativeDiagnostics(original).state, 'original');
  const { buffer: patched, report } = patchNativeDiagnostics(original);
  assert.equal(digest(original), ORIGINAL_NATIVE_SHA256, 'source buffer is never mutated');
  assert.equal(digest(patched), PATCHED_NATIVE_SHA256);
  assert.equal(report.route, '/debug/pprof/goroutine');
  assert.equal(report.version, '2.28.2');
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
  assert.deepEqual(report.changes.map(change => change.address), ['0x142f9b582', '0x142f9b5c4', '0x142f9b5cb', '0x142f9b5dc']);
  // Verify relative branch destinations and the pre-existing literal they use.
  const [entry, literal, length, exit] = report.changes;
  assert.equal(0x142f9b582 + 5 + patched.readInt32LE(entry.offset + 1), 0x142f9b5bc);
  assert.equal(0x142f9b5c4 + 7 + patched.readInt32LE(literal.offset + 3), 0x14359e538);
  assert.equal(patched.readUInt32LE(length.offset + 1), 22);
  assert.equal(0x142f9b5dc + 5 + patched.readInt32LE(exit.offset + 1), 0x142f9b65c);
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
