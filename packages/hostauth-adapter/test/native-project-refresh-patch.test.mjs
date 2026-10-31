import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { access, readFile } from 'node:fs/promises';
import path from 'node:path';
import { patchNativeDiagnostics } from '../src/native-diagnostics-patch.mjs';
import {
  patchNativeProjectRefresh, restoreNativeProjectRefresh, inspectNativeProjectRefresh,
  ORIGINAL_NATIVE_SHA256, DIAGNOSTICS_NATIVE_SHA256,
  PROJECT_REFRESH_NATIVE_SHA256, PROVIDER_NATIVE_SHA256,
} from '../src/native-project-refresh-patch.mjs';

const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const programPath = process.env.ANTIGRAVITY_LANGUAGE_SERVER || path.join(process.env.LOCALAPPDATA || '', 'Programs/antigravity/resources/bin/language_server.exe');
let recognizedProgram = false;
try {
  await access(programPath);
  const sha256 = digest(await readFile(programPath));
  recognizedProgram = sha256 === ORIGINAL_NATIVE_SHA256 || sha256 === DIAGNOSTICS_NATIVE_SHA256;
} catch { /* The native image is an optional local integration test input. */ }

test('unrecognized programs cannot be inspected, changed or restored', () => {
  for (const bytes of [undefined, Buffer.alloc(0), Buffer.from('MZ'), Buffer.alloc(4096)]) {
    for (const operation of [inspectNativeProjectRefresh, patchNativeProjectRefresh, restoreNativeProjectRefresh]) {
      assert.throws(() => operation(bytes), /unsupported_native_project_refresh_program/);
    }
  }
});

test('reviewed project refresh changes exactly one instruction byte and restores both image variants', { skip: !recognizedProgram }, async () => {
  const input = await readFile(programPath);
  const originals = digest(input) === ORIGINAL_NATIVE_SHA256 ? [input, patchNativeDiagnostics(input).buffer] : [input];
  for (const original of originals) {
    const originalDigest = digest(original);
    const { buffer: patched, report } = patchNativeProjectRefresh(original);
    assert.equal(digest(original), originalDigest, 'input is never mutated');
    assert.equal(report.runtimeValidated, false);
    assert.equal(report.version, '2.28.2');
    assert.equal(report.originalSha256, originalDigest);
    assert.equal(digest(patched), originalDigest === ORIGINAL_NATIVE_SHA256 ? PROJECT_REFRESH_NATIVE_SHA256 : PROVIDER_NATIVE_SHA256);
    const changed = [];
    for (let offset = 0; offset < original.length; offset++) if (original[offset] !== patched[offset]) changed.push(offset);
    assert.deepEqual(changed, report.changes[0].changedByteOffsets);
    assert.equal(changed.length, 1);
    const change = report.changes[0];
    assert.equal(change.before, 'e888300100');
    assert.equal(change.after, 'e888340100');
    assert.equal(0x1420bb593 + 5 + original.readInt32LE(change.offset + 1), 0x1420ce620);
    assert.equal(0x1420bb593 + 5 + patched.readInt32LE(change.offset + 1), 0x1420cea20);
    assert.equal(inspectNativeProjectRefresh(patched).state, 'patched');
    assert.throws(() => patchNativeProjectRefresh(patched), /unsupported_native_project_refresh_program/);
    assert.throws(() => restoreNativeProjectRefresh(original), /unsupported_native_project_refresh_program/);
    assert.deepEqual(restoreNativeProjectRefresh(patched).buffer, original);
    const damagedOriginal = Buffer.from(original);
    damagedOriginal[damagedOriginal.length - 1] ^= 1;
    assert.throws(() => patchNativeProjectRefresh(damagedOriginal), /unsupported_native_project_refresh_program/);
    patched[patched.length - 1] ^= 1;
    assert.throws(() => restoreNativeProjectRefresh(patched), /unsupported_native_project_refresh_program/);
  }
});
