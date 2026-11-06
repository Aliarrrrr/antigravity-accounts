import { inflateRawSync } from 'node:zlib';
import { sha256 } from './asar.mjs';

const identifier = '[$A-Z_a-z][$\\w]*';
function unique(source, pattern, name) {
  const matches = [...source.matchAll(new RegExp('(?<![$\\w])(?:' + pattern + ')', 'g'))];
  if (matches.length !== 1) throw new Error('unsupported_native_frontend_contract: ' + name);
  return matches[0];
}

// Bind by native action/schema contracts, not compiler-assigned variable names.
// These fragments deliberately retain property names and behavior. Ambiguous or
// changed contracts are refused before the program copy is installed.
export function inspectFrontendBindings(source) {
  if (typeof source !== 'string' || source.length > 64 * 1024 * 1024) throw new Error('unsupported_native_frontend');
  const schema = unique(source, `name:"userStatus",initialState:\\(\\)=>${identifier}\\((${identifier})\\.UserStatusSchema\\)`, 'user-status')[1];
  const setStatus = unique(source, `(${identifier})=${identifier}\\.actions\\.setUserStatus,`, 'set-status')[1];
  const modelPicked = unique(source, `(?:var|let|const) (${identifier})=${identifier}\\.modelPicked,`, 'model-picked')[1];
  const addListener = unique(source, `(?:var|let|const) (${identifier})=${identifier}\\(${identifier}\\("listenerMiddleware/add"\\)`, 'add-listener')[1];
  const decoder = unique(source, `function (${identifier})\\(a,b,c\\)\\{a=${identifier}\\(a\\);try\\{${identifier}\\(a,b,c\\?\\{\\.\\.\\.${identifier},\\.\\.\\.c\\}:${identifier}\\)\\}catch\\(d\\)\\{b=d;if\\(b instanceof Error&&${identifier}\\.includes\\(b.name\\)&&"field"in b&&typeof b.field=="function"\\)throw Error\\(\x60cannot decode \\$\\{d.field\\(\\)\\} from JSON: \\$\\{d.message\\}\x60,\\{cause:d\\}\\);throw d;\\}return a.message\\}`, 'protobuf-decoder')[1];
  const equals = unique(source, `function (${identifier})\\(a,b,c,d\\)\\{return b\\.\\$typeName!=a.typeName\\|\\|c\\.\\$typeName!=a.typeName\\?!1:b===c\\?!0:${identifier}\\(${identifier}\\(a,b\\),${identifier}\\(a,c\\),d\\)\\}`, 'protobuf-equals')[1];
  const filter = unique(source, `function (${identifier})\\(a,b\\)\\{if\\(!a\\|\\|!b\\|\\|!a.cascadeModelConfigData\\)return a;b=a.cascadeModelConfigData;var c=\\(b.clientModelConfigs\\?\\?\\[\\]\\)\\.filter\\(d=>${identifier}\\(d.label\\)\\);return\\{\\.\\.\\.a,cascadeModelConfigData:\\{\\.\\.\\.b,clientModelConfigs:c\\}\\}\\}`, 'model-filter')[1];
  const catalog = unique(source, `(${identifier})=${identifier}\\(\\[${identifier},${identifier},${identifier}\\],\\(a,b,c\\)=>\\(\\{visibleModelConfigs:a,customModels:b,modelSorts:c\\}\\)\\)`, 'model-catalog')[1];
  const resolveModel = unique(source, `function (${identifier})\\(\\{visibleModelConfigs:a,customModels:b,modelSorts:c\\},d\\)\\{`, 'model-resolution')[1];
  const findModel = unique(source, `function (${identifier})\\(a,b\\)\\{for\\(let c of b\\)if\\(a===c.value\\)return c\\}`, 'concrete-model')[1];
  const feature = unique(source, `(?:var|let|const) (${identifier})=\\(0,${identifier}\\.createContext\\)\\(\\{get:\\(\\)=>null,getBaseline:\\(\\)=>null,getGatingCustomFlags:\\(\\)=>\\[\\],featureNames:\\[\\]`, 'features')[1];
  const redux = unique(source, `(${identifier})=Symbol.for\\("react-redux-context"\\),(${identifier})=typeof globalThis!=="undefined"\\?globalThis:\\{\\};var (${identifier});if\\((${identifier})\\.createContext\\)\\{var (${identifier})=\\2\\[\\1\\]\\?\\?\\(\\2\\[\\1\\]=new Map\\),(${identifier})=\\5.get\\(\\4.createContext\\);\\6\\|\\|\\(\\6=\\4.createContext\\(null\\),\\5.set\\(\\4.createContext,\\6\\)\\);\\3=\\6\\}else \\3=\\{\\};var (${identifier})=\\3;`, 'redux-context')[7];
  return { schemaVersion: 1, frontendSha256: sha256(Buffer.from(source)), symbols: {
    schema, decoder, equals, setStatus, modelPicked, addListener, filter, catalog, resolveModel, findModel, feature, redux,
  } };
}

// The Go executable contains a normal ZIP. Read its central directory, rather
// than searching for compressed JavaScript text or executing the application.
export function extractNativeFrontend(binary) {
  const candidates = [];
  for (let end = binary.indexOf(Buffer.from('504b0506', 'hex')); end !== -1; end = binary.indexOf(Buffer.from('504b0506', 'hex'), end + 4)) {
    if (end + 22 > binary.length || binary.readUInt16LE(end + 4) || binary.readUInt16LE(end + 6)) continue;
    const count = binary.readUInt16LE(end + 10), size = binary.readUInt32LE(end + 12), directory = binary.readUInt32LE(end + 16);
    if (!count || count !== binary.readUInt16LE(end + 8) || count > 20000) continue;
    const start = end - size - directory;
    if (start < 0 || end + 22 + binary.readUInt16LE(end + 20) > binary.length) continue;
    let cursor = start + directory, valid = true;
    const found = [];
    for (let i = 0; i < count; i++) {
      if (cursor + 46 > end || binary.readUInt32LE(cursor) !== 0x02014b50) { valid = false; break; }
      const nameSize = binary.readUInt16LE(cursor + 28), extra = binary.readUInt16LE(cursor + 30), comment = binary.readUInt16LE(cursor + 32);
      if (cursor + 46 + nameSize + extra + comment > end) { valid = false; break; }
      const name = binary.toString('utf8', cursor + 46, cursor + 46 + nameSize);
      if (/(^|\/)main(?:-[^/]+)?\.js$/.test(name)) found.push({
        start, local: binary.readUInt32LE(cursor + 42), flags: binary.readUInt16LE(cursor + 8), method: binary.readUInt16LE(cursor + 10),
        compressed: binary.readUInt32LE(cursor + 20), length: binary.readUInt32LE(cursor + 24),
      });
      cursor += 46 + nameSize + extra + comment;
    }
    if (valid && cursor === end && found.length) candidates.push(...found);
  }
  if (candidates.length !== 1) throw new Error('unsupported_native_frontend_archive');
  const entry = candidates[0], local = entry.start + entry.local;
  if (entry.flags & 1 || ![0, 8].includes(entry.method) || entry.length > 64 * 1024 * 1024 ||
      local < 0 || local + 30 > binary.length || binary.readUInt32LE(local) !== 0x04034b50) throw new Error('unsupported_native_frontend_archive');
  const offset = local + 30 + binary.readUInt16LE(local + 26) + binary.readUInt16LE(local + 28);
  if (offset + entry.compressed > binary.length) throw new Error('unsupported_native_frontend_archive');
  const bytes = binary.subarray(offset, offset + entry.compressed);
  const result = entry.method === 8 ? inflateRawSync(bytes, { maxOutputLength: entry.length }) : bytes;
  if (result.length !== entry.length) throw new Error('unsupported_native_frontend_archive');
  return new TextDecoder('utf-8', { fatal: true }).decode(result);
}
