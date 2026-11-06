import { readFile } from 'node:fs/promises';
import { createHash, timingSafeEqual } from 'node:crypto';

// Public installed-app OAuth defaults in the reviewed 2.25.0 to 2.29.0
// programs. These are not account credentials. No user files, overrides,
// keyrings or databases are read. In 2.27.0 HostAuthProvider.GetTokenSource
// still calls getOAuth2Config; its instruction sequence differs only in
// relocated addresses and the default client ID and secret are unchanged.
const specifications = Object.freeze([
  {
    code: { address: 0x1420f77c0n, length: 1184, digest: '8f8f77b4a75c5e3e5f4a5754cd898d6ed2e27ffc10cdea864970d20637c54fc2' },
    id: { address: 0x1437f90ecn, length: 73, digest: 'bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea' },
    secret: { address: 0x143761911n, length: 35, digest: '1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60' }
  },
  {
    legacyScopes: true,
    code: { address: 0x141e85920n, length: 1184, digest: 'e68d7c2e7adce0f69d450f663de7c0c63dd1b85adf73e87989af530b6ae49434' },
    id: { address: 0x14338a555n, length: 73, digest: 'bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea' },
    secret: { address: 0x1432fa9c8n, length: 35, digest: '1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60' }
  },
  {
    legacyScopes: true,
    code: { address: 0x141fd15c0n, length: 1184, digest: '3fa2eca92509332e2918fd4dbf0061fe807d808b0047971008a43f7a2ca9a6b0' },
    id: { address: 0x1435205edn, length: 73, digest: 'bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea' },
    secret: { address: 0x14348efaan, length: 35, digest: '1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60' }
  },
  {
    code: { address: 0x141fb02e0n, length: 1184, digest: 'd92f33d6afa4b8fa4cdcf632c6e1c2fb93cdc0264a527f0cd482227a4ded71ba' },
    id: { address: 0x1435a677en, length: 73, digest: 'bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea' },
    secret: { address: 0x14351367bn, length: 35, digest: '1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60' }
  },
  {
    code: { address: 0x14209fee0n, length: 1184, digest: '7d541382ede235246edf572dc0a193f980282fdb361ccdee674463e46c54d7c2' },
    id: { address: 0x1436c0ed0n, length: 73, digest: 'bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea' },
    secret: { address: 0x14362c883n, length: 35, digest: '1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60' }
  },
  {
    code: { address: 0x1420a14e0n, length: 1184, digest: 'ddadb67ad6b8a2f4729e9ae0a74bdf276145e71e0e304c7108298f98ac595670' },
    id: { address: 0x1436c1ed0n, length: 73, digest: 'bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea' },
    secret: { address: 0x14362d883n, length: 35, digest: '1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60' }
  },
  {
    code: { address: 0x1420a14e0n, length: 1184, digest: 'f3a60d03f6493db9fa389fbfa726d3186f742245d54d9f94dcd8aa43560f1825' },
    id: { address: 0x1436c1ed0n, length: 73, digest: 'bf00c418024ba6bf606ccdc37120976e41bc429dd1d46ecf16a729aa532626ea' },
    secret: { address: 0x14362d883n, length: 35, digest: '1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60' }
  },
]);
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
