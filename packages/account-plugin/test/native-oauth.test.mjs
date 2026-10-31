import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { access, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { peAddressToOffset, extractOAuthFromProgram, nativeScopes } from '../native-oauth.mjs';

function image() {
  const b = Buffer.alloc(2048); b.write('MZ'); b.writeUInt32LE(128, 60); b.write('PE\0\0', 128, 'binary');
  b.writeUInt16LE(1, 134); b.writeUInt16LE(240, 148); b.writeUInt16LE(0x20b, 152); b.writeBigUInt64LE(0x140000000n, 176);
  const section = 128 + 24 + 240;
  b.writeUInt32LE(4096, section + 8); b.writeUInt32LE(0x1000, section + 12);
  b.writeUInt32LE(256, section + 16); b.writeUInt32LE(1024, section + 20);
  return b;
}
test('PE address mapping permits actual file data and rejects zero-filled virtual tails', () => {
  const b = image(); assert.equal(peAddressToOffset(b, 0x140001020n, 10), 1056);
  assert.throws(() => peAddressToOffset(b, 0x140001100n, 1));
  assert.throws(() => peAddressToOffset(b, 0x1400010ffn, 2));
  assert.throws(() => peAddressToOffset(b, 0x13fffffffn, 1));
});
test('malformed or different native programs never produce guessed OAuth settings', () => {
  assert.throws(() => extractOAuthFromProgram(Buffer.alloc(100)));
  assert.throws(() => extractOAuthFromProgram(image()));
  const b = image(); b.writeUInt32LE(0xfffffff0, 60);
  assert.throws(() => peAddressToOffset(b, 0x140001000n, 1));
  assert.equal(new Set(nativeScopes).size, 6);
});

for (const reviewed of [
  { version: '2.25.0', directory: 'original-program-fixture', code: 0x141e85920n, id: 0x14338a555n, secret: 0x1432fa9c8n },
  { version: '2.26.0', directory: 'original-program-fixture-2.26.0', code: 0x141fd15c0n, id: 0x1435205edn, secret: 0x14348efaan },
  { version: '2.27.0', directory: 'original-program-fixture-2.27.0', code: 0x141fb02e0n, id: 0x1435a677en, secret: 0x14351367bn },
  { version: '2.28.1', directory: 'original-program-fixture-2.28.1', code: 0x1420a14e0n, id: 0x1436c1ed0n, secret: 0x14362d883n },
  { version: '2.28.2', program: process.env.ANTIGRAVITY_LANGUAGE_SERVER || `${process.env.LOCALAPPDATA}/Programs/antigravity/resources/bin/language_server.exe`, code: 0x1420a14e0n, id: 0x1436c1ed0n, secret: 0x14362d883n },
]) {
  const programPath = reviewed.program || fileURLToPath(new URL(`../../../artifacts/accounts/${reviewed.directory}/resources/bin/language_server.exe`, import.meta.url));
  let hasProgram = false;
  try { await access(programPath); hasProgram = true; } catch { /* Public native program copies are optional in CI. */ }
  test(`reviewed ${reviewed.version} native defaults are extracted and changed code or constants are rejected`, { skip: !hasProgram }, async () => {
    const binary = await readFile(programPath);
    const actual = extractOAuthFromProgram(binary);
    const digest = value => createHash('sha256').update(value).digest('hex');
    assert.equal(digest(actual.clientId), 'bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea');
    assert.equal(digest(actual.clientSecret), '1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60');
    assert.deepEqual(actual.scopes, [...nativeScopes, ...(['2.25.0', '2.26.0'].includes(reviewed.version) ? ['https://www.googleapis.com/auth/aicode'] : [])]);
    for (const address of [reviewed.code, reviewed.id, reviewed.secret]) {
      const offset = peAddressToOffset(binary, address, 1);
      binary[offset] ^= 1;
      assert.throws(() => extractOAuthFromProgram(binary), /unsupported_native_oauth_configuration/);
      binary[offset] ^= 1;
    }
    assert.equal(digest(extractOAuthFromProgram(binary).clientId), digest(actual.clientId));
  });
}
