import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';

export const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');

export function readAsar(bytes) {
  if (bytes.length < 16 || bytes.readUInt32LE(0) !== 4) throw new Error('Unsupported ASAR header');
  const headerSize = bytes.readUInt32LE(4);
  const jsonSize = bytes.readUInt32LE(12);
  if (headerSize < 8 || jsonSize > headerSize - 8 || 8 + headerSize > bytes.length) throw new Error('Invalid ASAR header bounds');
  const header = JSON.parse(bytes.subarray(16, 16 + jsonSize).toString('utf8'));
  const dataStart = 8 + headerSize;
  function entry(name) {
    if (!name || name.split('/').some((part) => !part || part === '.' || part === '..')) throw new Error('Invalid archive path');
    let item = header;
    for (const part of name.split('/')) item = item?.files?.[part];
    if (!item) throw new Error(`Missing archive file: ${name}`);
    return item;
  }
  function read(name) {
    const item = entry(name);
    if (item.link || item.unpacked || item.files) throw new Error(`Archive file is not packed: ${name}`);
    const offset = Number(item.offset);
    if (!Number.isSafeInteger(offset) || offset < 0 || !Number.isSafeInteger(item.size) || item.size < 0 || dataStart + offset + item.size > bytes.length) throw new Error(`Invalid archive file bounds: ${name}`);
    return bytes.subarray(dataStart + offset, dataStart + offset + item.size);
  }
  return { header, read, entry };
}

export async function loadAsar(filename) { return readAsar(await readFile(filename)); }

export function rewriteAsar(originalBytes, replacements) {
  const original = readAsar(originalBytes);
  const header = structuredClone(original.header);
  for (const [name, value] of replacements) {
    if (!Buffer.isBuffer(value)) throw new Error('Archive replacements must be buffers');
    const parts = name.split('/');
    if (parts.some((part) => !part || part === '.' || part === '..')) throw new Error('Invalid replacement path');
    let item = header;
    for (const part of parts.slice(0, -1)) {
      item.files ||= {};
      item.files[part] ||= { files: {} };
      item = item.files[part];
      if (!item.files) throw new Error('Replacement path crosses a file');
    }
    item.files ||= {};
    const previous = item.files[parts.at(-1)];
    if (previous?.unpacked || previous?.link || previous?.files) throw new Error('Cannot replace an unpacked file, link or directory');
    item.files[parts.at(-1)] = { ...previous, size: value.length };
  }
  const buffers = [];
  let offset = 0;
  function visit(directory, prefix = '') {
    for (const [name, item] of Object.entries(directory.files || {})) {
      const filename = prefix + name;
      if (item.files) { visit(item, filename + '/'); continue; }
      if (item.link || item.unpacked) continue;
      const bytes = replacements.get(filename) || original.read(filename);
      item.offset = String(offset);
      item.size = bytes.length;
      if (replacements.has(filename)) {
        const blockSize = 4 * 1024 * 1024;
        const blocks = [];
        for (let start = 0; start < bytes.length; start += blockSize) blocks.push(sha256(bytes.subarray(start, start + blockSize)));
        item.integrity = { algorithm: 'SHA256', hash: sha256(bytes), blockSize, blocks };
      }
      buffers.push(bytes);
      offset += bytes.length;
    }
  }
  visit(header);
  const json = Buffer.from(JSON.stringify(header));
  const paddedLength = (json.length + 3) & ~3;
  const headerBuffer = Buffer.alloc(8 + paddedLength);
  headerBuffer.writeUInt32LE(headerBuffer.length - 4, 0);
  headerBuffer.writeUInt32LE(json.length, 4);
  json.copy(headerBuffer, 8);
  const size = Buffer.alloc(8);
  size.writeUInt32LE(4, 0);
  size.writeUInt32LE(headerBuffer.length, 4);
  return Buffer.concat([size, headerBuffer, ...buffers]);
}
