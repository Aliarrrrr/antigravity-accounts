import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import vm from 'node:vm';
import { access, mkdtemp, mkdir, readFile, writeFile, rm, readdir } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { readAsar, rewriteAsar, sha256 } from '../src/asar.mjs';
import { descriptorBytes, descriptorSource } from '../src/descriptor.mjs';
import { exactReplace, patchSources, SUPPORTED_VERSION } from '../src/patch-plan.mjs';
import { buildCopy, applyPrepared, restorePrepared } from '../src/package-copy.mjs';

function emptyArchive() {
  const json = Buffer.from('{"files":{}}');
  const header = Buffer.alloc(8 + ((json.length + 3) & ~3));
  header.writeUInt32LE(header.length - 4);
  header.writeUInt32LE(json.length, 4);
  json.copy(header, 8);
  const size = Buffer.alloc(8);
  size.writeUInt32LE(4);
  size.writeUInt32LE(header.length, 4);
  return Buffer.concat([size, header]);
}

function wireFields(bytes) {
  let cursor = 0;
  const number = () => {
    let value = 0, scale = 1, byte;
    do { byte = bytes[cursor++]; if (byte === undefined) throw new Error('Truncated protobuf'); value += (byte & 127) * scale; scale *= 128; } while (byte & 128);
    return value;
  };
  const result = [];
  while (cursor < bytes.length) {
    const tag = number();
    if ((tag & 7) === 2) { const length = number(); result.push([tag >> 3, bytes.subarray(cursor, cursor += length)]); }
    else if ((tag & 7) === 0) result.push([tag >> 3, number()]);
    else throw new Error('Unexpected wire type');
  }
  return result;
}

test('ASAR rewriting preserves unrelated file data and writes valid content integrity', () => {
  const before = rewriteAsar(emptyArchive(), new Map([['a.txt', Buffer.from('original')], ['deep/b.txt', Buffer.from('keep')]]));
  const after = rewriteAsar(before, new Map([['a.txt', Buffer.from('patched')], ['deep/added.cjs', Buffer.from('new')]]));
  const archive = readAsar(after);
  assert.equal(archive.read('deep/b.txt').toString(), 'keep');
  assert.equal(archive.read('a.txt').toString(), 'patched');
  assert.equal(archive.entry('a.txt').integrity.hash, sha256(Buffer.from('patched')));
  assert.throws(() => archive.read('../a.txt'));
  assert.throws(() => readAsar(Buffer.alloc(16)));
});

test('patching refuses unsupported versions and non-unique exact anchors', () => {
  for (const version of ['2.24.0', '2.28.1', '2.28.3']) {
    const metadata = Buffer.from(JSON.stringify({ name: 'antigravity', version, main: 'dist/main.js' }));
    assert.throws(() => patchSources(readAsar(rewriteAsar(emptyArchive(), new Map([['package.json', metadata]])))), /Unsupported/);
  }
  assert.throws(() => exactReplace('one one', 'one', 'two'));
  assert.throws(() => exactReplace('one', 'missing', 'two'));
  assert.equal(exactReplace('before one after', 'one', 'two'), 'before two after');
});

test('descriptor includes empty request, expected token field numbers and all existing RPCs', () => {
  const fields = wireFields(descriptorBytes());
  const messages = fields.filter(([tag]) => tag === 4).map(([, value]) => wireFields(value));
  const getName = (items) => items.find(([tag]) => tag === 1)[1].toString();
  assert.equal(getName(messages[5]), 'GetOAuthTokenRequest');
  assert.equal(messages[5].filter(([tag]) => tag === 2).length, 0);
  assert.equal(getName(messages[7]), 'OAuthTokenInfo');
  const tokenFields = messages[7].filter(([tag]) => tag === 2).map(([, value]) => wireFields(value));
  assert.deepEqual(tokenFields.map((items) => [getName(items), items.find(([tag]) => tag === 3)[1]]), [['access_token', 1], ['token_type', 2], ['refresh_token', 3], ['expiry', 4], ['is_gcp_tos', 6]]);
  const service = wireFields(fields.find(([tag]) => tag === 6)[1]);
  assert.deepEqual(service.filter(([tag]) => tag === 2).map(([, value]) => getName(wireFields(value))), ['GetUpdateStatus', 'ApplyUpdate', 'GetOAuthToken']);
});

const programAsar = process.env.ANTIGRAVITY_PROGRAM_ASAR || path.join(process.env.LOCALAPPDATA || '', 'Programs/antigravity/resources/app.asar');
const programNative = process.env.ANTIGRAVITY_LANGUAGE_SERVER || path.join(path.dirname(programAsar), 'bin', 'language_server.exe');
let hasProgram = false;
try { await access(programAsar); hasProgram = true; } catch { /* CI need not have Antigravity installed. */ }

test('actual native launch selects HostAuth only for an activated independent provider', { skip: !hasProgram }, async () => {
  const archive = readAsar(await readFile(programAsar));
  const patched = patchSources(archive);
  const captures = [];
  const exports = {};
  const nativeRequire = createRequire(import.meta.url);
  const sentinel = new Error('offline-spawn-captured');
  const resourcesPath = path.resolve('/offline/antigravity/resources');
  const nativeProgramPath = 'hostauth-addon/native/language_server.exe';
  let providerNativeExists = true;
  const overrides = {
    child_process: { spawn(command, args, options) { captures.push({ command, args, options }); throw sentinel; } },
    electron: { app: { isPackaged: true, getVersion: () => SUPPORTED_VERSION } },
    'shell-env': { shellEnvSync: () => ({ AICODE_DEV_PROJECT: 'test-shell-project' }) },
    fs: { createWriteStream: () => ({}), statSync(filename) {
      assert.equal(filename, path.join(resourcesPath, nativeProgramPath));
      if (!providerNativeExists) throw new Error('ENOENT');
      return { isFile: () => true };
    } },
    './paths': { getLsLogPath: () => '/unused/log', getAppDataDirName: () => 'unused', getActivePortFilePath: () => '/unused/port' },
    './utils': { setupNodeWrapper(env) { env.OFFLINE_ORIGINAL_WRAPPER = 'preserved'; } },
    './wsl': {},
  };
  const originalEnv = { USER_INFO_OVERRIDE_URL: 'http://127.0.0.1:12345/test-userinfo' };
  vm.runInNewContext(patched.get('dist/languageServer.js').toString(), { exports, __dirname: '/unused/program', process: { platform: 'win32', env: originalEnv, resourcesPath }, require: name => overrides[name] || nativeRequire(name) });
  const providerOptions = { useHostAuth: true, nativeProgramPath, hostBridgeUrl: 'http://127.0.0.1:4567', hostBridgeToken: 'new-local-bridge-capability' };
  for (const options of [{}, { useHostAuth: false }, providerOptions]) {
    await assert.rejects(exports.startLanguageServer(0, 'local-csrf-value', options), error => error === sentinel);
    assert.equal(captures.at(-1).args.includes('--use_host_auth=true'), options.useHostAuth === true);
    assert.equal(captures.at(-1).command, path.join(resourcesPath, options.useHostAuth ? nativeProgramPath : 'bin/language_server.exe'));
    assert.equal(captures.at(-1).options.env.CHROME_DEVTOOLS_MCP_JS, path.join(resourcesPath, 'app.asar.unpacked/node_modules/chrome-devtools-mcp/build/src/bin/chrome-devtools-mcp.js'));
    assert.equal(captures.at(-1).options.env.OFFLINE_ORIGINAL_WRAPPER, 'preserved');
    assert.equal(captures.at(-1).options.env.AICODE_DEV_PROJECT, options.useHostAuth ? undefined : 'test-shell-project');
    assert.equal(captures.at(-1).options.env.USER_INFO_OVERRIDE_URL, options.useHostAuth ? undefined : originalEnv.USER_INFO_OVERRIDE_URL);
  }
  assert.equal(originalEnv.USER_INFO_OVERRIDE_URL, 'http://127.0.0.1:12345/test-userinfo', 'only the provider child environment is changed');
  const plainArgs = Array.from(captures[0].args);
  const providerArgs = Array.from(captures.at(-1).args).filter(arg => arg !== '--use_host_auth=true' && !arg.startsWith('--host_bridge_'));
  assert.deepEqual(providerArgs, plainArgs);
  const before = captures.length;
  await assert.rejects(exports.startLanguageServer(0, 'local-csrf-value', { useHostAuth: true }), /Independent host authentication/);
  await assert.rejects(exports.startLanguageServer(0, 'local-csrf-value', { useHostAuth: true, hostBridgeUrl: 'http://127.0.0.1:4567', hostBridgeToken: 'local', wsl: {} }), /Independent host authentication/);
  for (const invalidPath of [undefined, '../outside.exe', '/outside.exe', 'C:\\outside.exe', 'hostauth-addon/native/other.exe', 'hostauth-addon/native/../native/language_server.exe']) {
    await assert.rejects(exports.startLanguageServer(0, 'local-csrf-value', { ...providerOptions, nativeProgramPath: invalidPath }), /Independent native program path is invalid/);
  }
  await assert.rejects(exports.startLanguageServer(0, 'local-csrf-value', { nativeProgramPath }), /requires host authentication/);
  providerNativeExists = false;
  await assert.rejects(exports.startLanguageServer(0, 'local-csrf-value', providerOptions), /Independent native program is unavailable/);
  assert.equal(captures.length, before);
  const main = patched.get('dist/main.js').toString();
  assert.match(main, /useHostAuth: hostauthSession\.usesHostAuth/);
  assert.match(main, /nativeProgramPath: hostauthSession\.nativeProgramPath/);
  assert.match(main, /setLanguageServerEndpoint\(\{ baseUrl: `https:\/\/127\.0\.0\.1:\$\{newPort\}`, csrf \}\)/);
  assert.match(main, /markLanguageServerStarted\(\{ baseUrl: `https:\/\/127\.0\.0\.1:\$\{handle\.port\}`, csrf \}\)/);
  assert.match(main, /require\("\.\/beyondcorpPac"\)/);
  assert.match(main, /configureWebviewSessionProxy\)\(contents\.session\)/);
  assert.match(main, /await \(0, beyondcorpPac_1\.preconfigureWebviewPartitionProxies\)\(\)/);
  assert.match(main, /webauthn_1\.configurePlatformWebAuthn/);
  assert.match(main, /webauthn_1\.attachWebviewWebAuthnUserActivationTracking/);
  assert.match(main, /webviewBrowser_1\.configureWebviewBrowserContents/);
  assert.match(main, /const windows = \(0, utils_1\.getHubWindows\)\(\)/);
});

test('local supported program copy is patched, survives restore, and rejects later updater changes', { skip: !hasProgram }, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-copy-'));
  const original = await readFile(programAsar);
  const source = readAsar(original);
  const replacements = patchSources(source);
  assert.equal(replacements.has('dist/preload.js'), false, 'the official/localization preload is retained');
  for (const [name, bytes] of replacements) {
    assert.notEqual(sha256(source.read(name)), sha256(bytes));
    new (await import('node:vm')).Script(bytes.toString(), { filename: name });
  }
  assert.match(replacements.get('dist/languageServer.js').toString(), /--use_host_auth=true/);
  assert.match(replacements.get('dist/main.js').toString(), /getOAuthToken: \(\) => hostauthSession.getOAuthToken/);
  const tamperedSources = rewriteAsar(original, new Map([['dist/main.js', Buffer.concat([source.read('dist/main.js'), Buffer.from('\n')])]]));
  assert.throws(() => patchSources(readAsar(tamperedSources)), /Unsupported exact program source/);
  try {
    const component = path.join(root, 'publish/Accounts.exe');
    const ui = path.join(root, 'ui');
    await mkdir(path.dirname(component));
    await writeFile(component, 'fake component; never execute');
    await writeFile(path.join(path.dirname(component), 'developer-private.tmp'), 'must not be installed');
    for (const name of ['window.cjs', 'preload.cjs', 'ui/index.html', 'ui/app.js', 'ui/style.css']) {
      await mkdir(path.dirname(path.join(ui, name)), { recursive: true });
      await writeFile(path.join(ui, name), 'offline-test-file');
    }
    const output = path.join(root, 'prepared');
    const manifest = await buildCopy({ sourceAsar: programAsar, outputRoot: output, componentExecutable: component, uiDirectory: ui, languageServerPath: programNative, nativeOAuthExtractor: async () => ({ clientId: 'public-test-client', clientSecret: 'public-test-constant', scopes: ['test'] }) });
    assert.equal(manifest.runtimeVerified, false);
    assert.equal(manifest.payloadComplete, true);
    assert.deepEqual(await readdir(path.join(output, 'resources/hostauth-addon/component')), ['Accounts.exe']);
    const providerProgram = await readFile(path.join(output, 'resources', 'hostauth-addon', 'native', 'language_server.exe'));
    assert.equal(sha256(providerProgram), manifest.providerNativeSha256);
    assert.notEqual(manifest.providerNativeSha256, manifest.patchedNativeSha256, 'the project refresh adaptation is restricted to the independent provider copy');
    const prepared = readAsar(await readFile(path.join(output, 'resources/app.asar')));
    assert.deepEqual(prepared.read('dist/preload.js'), source.read('dist/preload.js'));
    new vm.Script(prepared.read('dist/hostauth-addon/model-menu-preload.cjs').toString());
    assert.throws(() => prepared.entry('dist/hostauth-addon/account-plugin/ui/index.html'), /Missing archive file/);
    assert.throws(() => prepared.entry('dist/hostauth-addon/account-plugin/preload.cjs'), /Missing archive file/);
    // Check every unchanged packed original file, not only the changed files.
    function verify(directory, prefix = '') {
      for (const [name, entry] of Object.entries(directory.files || {})) {
        const filename = prefix + name;
        if (entry.files) verify(entry, filename + '/');
        else if (!entry.unpacked && !entry.link && !replacements.has(filename)) assert.deepEqual(prepared.read(filename), source.read(filename), filename);
      }
    }
    verify(source.header);
    assert.deepEqual(await readFile(path.join(output, 'restore/app.asar')), original);
    const resources = path.join(root, 'simulation/resources');
    await mkdir(resources, { recursive: true });
    await writeFile(path.join(resources, 'app.asar'), original);
    await mkdir(path.join(resources, 'bin'));
    const originalNative = await readFile(programNative);
    await writeFile(path.join(resources, 'bin', 'language_server.exe'), originalNative);
    const backup = path.join(root, 'recovery');
    await assert.rejects(applyPrepared({ preparedRoot: output, resourcesRoot: resources, backupRoot: backup }), /shutdown/);
    // The native file is replaced first. Refuse the second replacement and
    // check that the first is restored, with the complete originals retained.
    const unfinished = path.join(resources, 'app.asar.hostauth-new');
    await writeFile(unfinished, 'unfinished installer file');
    const failedBackup = path.join(root, 'failed-recovery');
    await assert.rejects(applyPrepared({ preparedRoot: output, resourcesRoot: resources, backupRoot: failedBackup, processesStopped: true, acknowledgeUnverifiedRuntime: true }), /unfinished/);
    assert.deepEqual(await readFile(path.join(resources, 'app.asar')), original);
    assert.deepEqual(await readFile(path.join(resources, 'bin', 'language_server.exe')), originalNative);
    assert.deepEqual(await readFile(path.join(failedBackup, 'language_server.exe')), originalNative);
    await assert.rejects(access(path.join(resources, 'hostauth-addon')), /ENOENT/);
    await rm(unfinished);
    await applyPrepared({ preparedRoot: output, resourcesRoot: resources, backupRoot: backup, processesStopped: true, acknowledgeUnverifiedRuntime: true });
    assert.equal(sha256(await readFile(path.join(resources, 'app.asar'))), manifest.patchedAsarSha256);
    assert.equal(sha256(await readFile(path.join(resources, 'bin', 'language_server.exe'))), manifest.patchedNativeSha256);
    await restorePrepared({ resourcesRoot: resources, backupRoot: backup, processesStopped: true });
    assert.deepEqual(await readFile(path.join(resources, 'app.asar')), original);
    assert.deepEqual(await readFile(path.join(resources, 'bin', 'language_server.exe')), originalNative);
    await assert.rejects(access(path.join(resources, 'hostauth-addon', 'native', 'language_server.exe')), /ENOENT/);
    await writeFile(path.join(resources, 'bin', 'language_server.exe'), Buffer.from('later native update'));
    await assert.rejects(restorePrepared({ resourcesRoot: resources, backupRoot: backup, processesStopped: true }), /native program was updated/);
    assert.deepEqual(await readFile(path.join(resources, 'bin', 'language_server.exe')), Buffer.from('later native update'));
    await writeFile(path.join(resources, 'bin', 'language_server.exe'), originalNative);
    await writeFile(path.join(resources, 'app.asar'), Buffer.from('later updater version'));
    await assert.rejects(restorePrepared({ resourcesRoot: resources, backupRoot: backup, processesStopped: true }), /updated or modified/);
    assert.deepEqual(await readFile(programAsar), original);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('descriptor serializes with the installed Buf 2.11 implementation without starting any host code', { skip: !hasProgram }, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-buf-'));
  const archive = readAsar(await readFile(programAsar));
  const prefix = 'node_modules/@bufbuild/protobuf/';
  try {
    const directory = archive.entry(prefix.slice(0, -1));
    async function extract(item, name = '') {
      for (const [part, entry] of Object.entries(item.files || {})) {
        const filename = name + part;
        if (entry.files) await extract(entry, filename + '/');
        else if (!entry.unpacked && !entry.link) {
          const target = path.join(root, prefix, filename);
          await mkdir(path.dirname(target), { recursive: true });
          await writeFile(target, archive.read(prefix + filename));
        }
      }
    }
    await extract(directory);
    const generated = path.join(root, 'host-bridge.cjs');
    await writeFile(generated, descriptorSource());
    const require = createRequire(generated);
    const proto = require(generated);
    const buf = require('@bufbuild/protobuf');
    const message = buf.create(proto.GetOAuthTokenResponseSchema, { tokenInfo: { accessToken: 'fake-access', tokenType: 'Bearer', refreshToken: 'fake-refresh', expiry: { seconds: 2000000000n, nanos: 1000000 }, isGcpTos: false } });
    const decoded = buf.fromBinary(proto.GetOAuthTokenResponseSchema, buf.toBinary(proto.GetOAuthTokenResponseSchema, message));
    assert.equal(decoded.tokenInfo.accessToken, 'fake-access');
    assert.equal(decoded.tokenInfo.refreshToken, 'fake-refresh');
    assert.equal(decoded.tokenInfo.expiry.seconds, 2000000000n);
    assert.equal(proto.HostBridgeService.methods.length, 3);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('actual supported Connect bridge serves dummy OAuth over JSON/proto, keeps update methods and rejects wrong bridge bearer', { skip: !hasProgram, timeout: 15000 }, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-connect-'));
  const archive = readAsar(await readFile(programAsar));
  const patched = readAsar(rewriteAsar(await readFile(programAsar), patchSources(archive)));
  let bridge;
  const previousWarn = console.warn;
  const warnings = [];
  console.warn = (...values) => warnings.push(values.join(' '));
  try {
    for (const packageName of ['@bufbuild/protobuf', '@connectrpc/connect', '@connectrpc/connect-node']) {
      const prefix = 'node_modules/' + packageName + '/';
      async function extract(item, name = '') {
        for (const [part, entry] of Object.entries(item.files || {})) {
          const filename = name + part;
          if (entry.files) await extract(entry, filename + '/');
          else if (!entry.unpacked && !entry.link) {
            const target = path.join(root, prefix, filename);
            await mkdir(path.dirname(target), { recursive: true });
            await writeFile(target, archive.read(prefix + filename));
          }
        }
      }
      await extract(archive.entry(prefix.slice(0, -1)));
    }
    for (const name of ['dist/hostBridgeServer.js', 'dist/proto/host_bridge_pb.js']) {
      await mkdir(path.dirname(path.join(root, name)), { recursive: true });
      await writeFile(path.join(root, name), patched.read(name));
    }
    const require = createRequire(path.join(root, 'offline-test.cjs'));
    const { startHostBridgeServer } = require('./dist/hostBridgeServer.js');
    const proto = require('./dist/proto/host_bridge_pb.js');
    const buf = require('@bufbuild/protobuf');
    let tokenCalls = 0;
    let failToken = false;
    bridge = await startHostBridgeServer({
      getUpdateStatus: async () => ({ currentVersion: SUPPORTED_VERSION, latestVersion: SUPPORTED_VERSION, updateAvailable: false }),
      applyUpdate: async () => true,
      getOAuthToken: async () => {
        tokenCalls++;
        if (failToken) throw new Error('FAKE_PRIVATE_TOKEN_MUST_NOT_ESCAPE');
        return { accessToken: 'fake-independent-access', tokenType: 'Bearer', refreshToken: 'fake-independent-refresh', expiry: { seconds: 2000000000n, nanos: 123000000 }, isGcpTos: false };
      },
    });
    const request = (method, { type = 'application/json', bearer = bridge.token } = {}) => fetch(bridge.url + '/exa.host_bridge_pb.HostBridgeService/' + method, {
      method: 'POST', headers: { 'Content-Type': type, 'Connect-Protocol-Version': '1', Authorization: 'Bearer ' + bearer },
      body: type === 'application/proto' ? Buffer.alloc(0) : '{}',
    });
    const jsonResponse = await request('GetOAuthToken');
    assert.equal(jsonResponse.status, 200);
    const json = await jsonResponse.json();
    assert.equal(json.tokenInfo.accessToken, 'fake-independent-access');
    assert.equal(json.tokenInfo.expiry, '2033-05-18T03:33:20.123Z');
    const binaryResponse = await request('GetOAuthToken', { type: 'application/proto' });
    assert.equal(binaryResponse.status, 200);
    const token = buf.fromBinary(proto.GetOAuthTokenResponseSchema, new Uint8Array(await binaryResponse.arrayBuffer()));
    assert.equal(token.tokenInfo.expiry.seconds, 2000000000n);
    assert.equal(token.tokenInfo.expiry.nanos, 123000000);
    assert.equal(token.tokenInfo.refreshToken, 'fake-independent-refresh');
    const unauthorized = await request('GetOAuthToken', { bearer: 'wrong-capability' });
    assert.equal(unauthorized.status, 401);
    assert.deepEqual(await unauthorized.json(), { error: 'unauthorized' });
    assert.equal(tokenCalls, 2);
    const update = await request('GetUpdateStatus');
    assert.equal((await update.json()).status.currentVersion, SUPPORTED_VERSION);
    const apply = await request('ApplyUpdate');
    assert.equal((await apply.json()).accepted, true);
    failToken = true;
    const failure = await request('GetOAuthToken');
    assert.equal(failure.ok, false);
    assert.doesNotMatch(await failure.text(), /FAKE_PRIVATE_TOKEN_MUST_NOT_ESCAPE/);
    assert.doesNotMatch(warnings.join('\n'), /fake-independent|FAKE_PRIVATE_TOKEN/);
  } finally {
    console.warn = previousWarn;
    if (bridge) await bridge.close();
    await rm(root, { recursive: true, force: true });
  }
});
