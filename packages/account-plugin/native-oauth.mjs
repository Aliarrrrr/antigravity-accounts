import nativePrograms from '../hostauth-adapter/src/native-program-profiles.cjs';
import { readFile } from 'node:fs/promises';
import { createHash, timingSafeEqual } from 'node:crypto';

// Public installed-app OAuth defaults in the reviewed 2.25.0 to 2.32.1
// programs. These are not account credentials. No user files, overrides,
// keyrings or databases are read. In 2.27.0 HostAuthProvider.GetTokenSource
// still calls getOAuth2Config; its instruction sequence differs only in
// relocated addresses and the default client ID and secret are unchanged.
const specifications = nativePrograms.OAUTH_SPECIFICATIONS;

export const nativeScopes = Object.freeze(['openid', ...[
  'cloud-platform', 'userinfo.email', 'userinfo.profile', 'cclog', 'experimentsandconfigs'
].map(scope => `https://www.googleapis.com/auth/${scope}`)]);

export function peAddressToOffset(binary, address, length) {
  const invalid = () => { throw new Error('unsupported_language_server_image'); };
  if (!Buffer.isBuffer(binary) || binary.length < 64 || binary.toString('ascii', 0, 2) !== 'MZ') invalid();
  const pe = binary.readUInt32LE(60);
  if (pe > binary.length - 264 || binary.toString('binary', pe, pe + 4) !== 'PE\0\0') invalid();
  const count = binary.readUInt16LE(pe + 6), optionalSize = binary.readUInt16LE(pe + 20);
  if (binary.readUInt16LE(pe + 24) !== 0x20b || count === 0 || count > 96) invalid();
  const base = binary.readBigUInt64LE(pe + 48), rva = address - base;
  const table = pe + 24 + optionalSize;
  if (rva < 0n || table + count * 40 > binary.length || !Number.isSafeInteger(length) || length < 1) invalid();
  for (let i = 0; i < count; i++) {
    const section = table + i * 40;
    const virtual = BigInt(binary.readUInt32LE(section + 12));
    const size = BigInt(binary.readUInt32LE(section + 16));
    const raw = BigInt(binary.readUInt32LE(section + 20));
    // Only actual raw bytes are readable. A PE's zero-filled virtual tail is
    // not backed by data in the image and must never be treated as constants.
    if (rva >= virtual && rva + BigInt(length) <= virtual + size) {
      const offset = raw + rva - virtual;
      if (offset < 0n || offset + BigInt(length) > BigInt(binary.length)) invalid();
      return Number(offset);
    }
  }
  invalid();
}

export function extractOAuthFromProgram(binary) {
  const spec = specifications.find(candidate => {
    try {
      const offset = peAddressToOffset(binary, candidate.code.address, candidate.code.length);
      const digest = createHash('sha256').update(binary.subarray(offset, offset + candidate.code.length)).digest();
      return timingSafeEqual(digest, Buffer.from(candidate.code.digest, 'hex'));
    } catch { return false; }
  });
  if (!spec) throw new Error('unsupported_native_oauth_configuration');
  const verified = Object.fromEntries(['code', 'id', 'secret'].map(name => {
    const item = spec[name];
    const offset = peAddressToOffset(binary, item.address, item.length);
    const value = binary.subarray(offset, offset + item.length);
    const digest = createHash('sha256').update(value).digest();
    if (!timingSafeEqual(digest, Buffer.from(item.digest, 'hex'))) throw new Error('unsupported_native_oauth_configuration');
    return [name, value];
  }));
  const clientId = verified.id.toString('ascii'), clientSecret = verified.secret.toString('ascii');
  if (!clientId.endsWith('.apps.googleusercontent.com') || /[^\x21-\x7e]/.test(clientId + clientSecret))
    throw new Error('unsupported_native_oauth_configuration');
  return { clientId, clientSecret, scopes: [...nativeScopes, ...(spec.legacyScopes ? ['https://www.googleapis.com/auth/aicode'] : [])] };
}

export async function extractNativeOAuth(languageServerPath) {
  return extractOAuthFromProgram(await readFile(languageServerPath));
}
