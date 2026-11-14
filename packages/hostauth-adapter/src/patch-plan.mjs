import { sha256 } from './asar.mjs';
import { descriptorSource } from './descriptor.mjs';
import { readFileSync } from 'node:fs';
import { matchesSourceScope } from './source-scope-contracts.mjs';

const reviewedScopes = JSON.parse(readFileSync(new URL('./source-scope-profiles.json', import.meta.url), 'utf8'));

export const SUPPORTED_VERSION = '2.32.1';
export const SOURCE_SHA256 = Object.freeze({
  'dist/main.js': 'b1d133d4aacd31170a23e277008f45b5d4bdc150ae44059a9d27059c8780cd62',
  'dist/languageServer.js': '89d9fe0b57fc85ac590c91bd8d5248ed5b3fdef79dbe2dc5f5086fc6af681b79',
  'dist/hostBridgeServer.js': 'db4d7dc0e356828ac26ecf461c2c5d34c96612d69c7a9a765221513351378784',
  'dist/proto/host_bridge_pb.js': 'f80259dd94c70ce6befb46d6d9a08617e7ebee97e1e2975d60b657603bab417d',
  'dist/updater.js': 'ef38da806d1db759ff94ec8b1181f057440150cedbab7992b633df8933951e04',
  'node_modules/electron-updater/out/BaseUpdater.js': '9cff8b894ed709f6d9a5b938614fc377be89e1ea5bd1cc1f4cd6143048d918a3',
  'node_modules/electron-updater/out/NsisUpdater.js': '31f36b4ef5cadd35a93df91d9454aa70044e41c0e6b4c799783dca4302cd03b6',
  'dist/lsDefaults.js': 'a423ac7026b38c4e98d82217b50ead0d22ede92c62e752fdf1fd4c6d189e5af4',
  'dist/loginShellEnvReads.js': '668b6c7ff54172cc27fe7ff2e4dba98729d4ca83d4fb6a61f171862aa98a7f09',
  'dist/utils.js': '17879de917b8129ab7a4aa0c760d13f28c1afb1af44150f7f0950799e6af5d50',
});
const source231Sha256 = Object.freeze({
  ...Object.fromEntries(Object.entries(SOURCE_SHA256).filter(([name]) => !['dist/lsDefaults.js', 'dist/loginShellEnvReads.js'].includes(name))),
  'dist/main.js': '0d297f7b38f73777e61fdcde103f026b1ae69af91e37edc937c9b37d5fe73d6e',
  'dist/languageServer.js': '6f671aaf1a6812e75bb692e1def7a0b6eb3b2ac27b12cdc066e0b7f39ac723c4',
  'dist/utils.js': '0c4d101773515c827dd91c3b60bdcc4a04591758bb9b4280c46350987c7d17dd',
});
const previousSourceSha256 = Object.freeze({
  'dist/utils.js': '2f39dd7b944436bd5007297ccc15c42c897d8e6cecd5a0af93bdf6bfc1708b12',
  'dist/main.js': '66fa884c96dee5dd6a536711d35fc90fee853e9a2eede40fb4d1ab576e4ea50f',
  'dist/languageServer.js': '5c781d05050506d7643a88b1eeadc9bb4f320d7133f84b075e6338a6ce158d26',
  'dist/hostBridgeServer.js': 'db4d7dc0e356828ac26ecf461c2c5d34c96612d69c7a9a765221513351378784',
  'dist/proto/host_bridge_pb.js': 'f80259dd94c70ce6befb46d6d9a08617e7ebee97e1e2975d60b657603bab417d',
  'dist/updater.js': 'd8f44a0bb440c3f20ce0564bf46de80defe709d7f088f00b738726c7b9120e1b',
  'node_modules/electron-updater/out/BaseUpdater.js': '9cff8b894ed709f6d9a5b938614fc377be89e1ea5bd1cc1f4cd6143048d918a3',
  'node_modules/electron-updater/out/NsisUpdater.js': '31f36b4ef5cadd35a93df91d9454aa70044e41c0e6b4c799783dca4302cd03b6',
});
export const SOURCE_CONTRACTS = Object.freeze([
  Object.freeze({ version: SUPPORTED_VERSION, sourceSha256: SOURCE_SHA256, shellEnvironment: 'prefetched', baseArguments: 'shared' }),
  Object.freeze({ version: '2.31.2', sourceSha256: source231Sha256, shellEnvironment: 'prefetched', baseArguments: 'inline' }),
  Object.freeze({ version: '2.31.0', sourceSha256: source231Sha256, shellEnvironment: 'prefetched', baseArguments: 'inline' }),
  Object.freeze({ version: '2.29.0', sourceSha256: Object.freeze({ ...source231Sha256,
    'dist/main.js': 'e5bf4ae92a586b222034c7de27e0e58dd8922deb0edc9649d290bc06182b2c6c',
    'dist/utils.js': '277c626bedc704f12927eee26836350078835eb43ae1847cdc346b0be21b2d9d',
  }), shellEnvironment: 'prefetched', baseArguments: 'inline' }),
  Object.freeze({ version: '2.28.2', sourceSha256: previousSourceSha256, shellEnvironment: 'sync', baseArguments: 'inline' }),
]);
const dependencyVersions = Object.freeze({ '@bufbuild/protobuf': '2.11.0', '@connectrpc/connect': '2.2.0', '@connectrpc/connect-node': '2.2.0', 'electron-updater': '6.8.9' });

// A package-version change alone does not invalidate the adaptation. Every
// complete affected file or its protected AST scopes and RPC dependencies must
// match a reviewed contract; insertion-anchor matches alone are never enough.
export function inspectSourceCompatibility(archive) {
  const packageInfo = JSON.parse(archive.read('package.json').toString('utf8'));
  if (packageInfo.name !== 'antigravity' || !/^\d+\.\d+\.\d+$/.test(packageInfo.version) || packageInfo.main !== 'dist/main.js') throw new Error('Unsupported Antigravity version or entry point');
  const availableSha256 = {};
  for (const name of new Set(SOURCE_CONTRACTS.flatMap(candidate => Object.keys(candidate.sourceSha256)))) {
    try { availableSha256[name] = sha256(archive.read(name)); } catch { /* A missing file cannot satisfy that source contract. */ }
  }
  let matchingContracts = SOURCE_CONTRACTS.filter(candidate => Object.entries(candidate.sourceSha256).every(([name, expected]) => availableSha256[name] === expected));
  let scopeMatch = false;
  if (!matchingContracts.length) {
    matchingContracts = SOURCE_CONTRACTS.filter(candidate => {
      const expected = reviewedScopes.contracts.find(value => value.version === candidate.version);
      return Object.keys(candidate.sourceSha256).every(name => {
        if (availableSha256[name] === candidate.sourceSha256[name]) return true;
        try { return matchesSourceScope(archive.read(name).toString('utf8'), name, expected?.files[name]); }
        catch { return false; }
      });
    });
    scopeMatch = matchingContracts.length > 0;
  }
  const contract = matchingContracts.find(candidate => candidate.version === packageInfo.version) || matchingContracts[0];
  if (!contract) throw new Error('Unsupported exact program source: no complete reviewed source contract matches');
  const sourceSha256 = Object.fromEntries(Object.keys(contract.sourceSha256).map(name => [name, availableSha256[name]]));
  for (const [name, version] of Object.entries(dependencyVersions)) {
    let installed;
    try { installed = JSON.parse(archive.read(`node_modules/${name}/package.json`).toString('utf8')).version; } catch { /* Report only the public dependency name. */ }
    if (installed !== version) throw new Error(`Unsupported host RPC dependency: ${name}`);
  }
  return { antigravityVersion: packageInfo.version, sourceContractVersion: contract.version,
    match: scopeMatch ? 'reviewed-scopes' : packageInfo.version === contract.version ? 'reviewed-version' : 'unchanged-source-contract',
    sourceSha256, dependencyVersions: { ...dependencyVersions }, shellEnvironment: contract.shellEnvironment, baseArguments: contract.baseArguments };
}

export function exactReplace(source, before, after) {
  const first = source.indexOf(before);
  if (first < 0 || source.indexOf(before, first + before.length) >= 0) throw new Error('Expected exactly one complete source anchor');
  return source.slice(0, first) + after + source.slice(first + before.length);
}

export function patchSources(archive) {
  const compatibility = inspectSourceCompatibility(archive);
  const sources = new Map();
  for (const name of Object.keys(compatibility.sourceSha256)) sources.set(name, archive.read(name).toString('utf8'));
  let main = sources.get('dist/main.js');
  main = exactReplace(main, 'const hostBridgeServer_1 = require("./hostBridgeServer");', 'const hostBridgeServer_1 = require("./hostBridgeServer");\nconst hostauthAddon = require("./hostauth-addon/runtime.cjs");\nconst accountsUpdateHandoff = require("./hostauth-addon/update-handoff.cjs");');
  main = exactReplace(main, 'let hostBridgeServer;', 'let hostBridgeServer;\nlet hostauthSession;');
  main = exactReplace(main,
    '    try {\n        hostBridgeServer = await (0, hostBridgeServer_1.startHostBridgeServer)({\n            getUpdateStatus: updater_1.getHostUpdateStatus,\n            applyUpdate: updater_1.applyHostUpdate,\n        });',
    '    try {\n        hostauthSession = await hostauthAddon.prepare({\n            electron: electron_1, wsl: wslOptions, headless: HEADLESS,\n            onFatal: () => { void (0, languageServer_1.killLanguageServer)().finally(() => electron_1.app.quit()); },\n        });\n        hostBridgeServer = await (0, hostBridgeServer_1.startHostBridgeServer)({\n            getUpdateStatus: updater_1.getHostUpdateStatus,\n            applyUpdate: updater_1.applyHostUpdate,\n            getOAuthToken: () => hostauthSession.getOAuthToken(),\n        });');
  main = exactReplace(main,
    "        // Non-fatal: the app is still usable, the LS just can't drive updates.\n        console.error('Failed to start host bridge server:', err.message);",
    "        if (!hostauthSession || hostauthSession.usesHostAuth) {\n            await closeHostBridgeServer();\n            electron_1.dialog.showErrorBox('Independent account adapter', 'Independent account setup or host bridge startup failed. No language server was started.');\n            electron_1.app.quit();\n            return;\n        }\n        console.error('Failed to start host bridge server.');");
  main = exactReplace(main, '            hostBridgeToken: hostBridgeServer?.token,', '            hostBridgeToken: hostBridgeServer?.token,\n            useHostAuth: hostauthSession.usesHostAuth,\n            nativeProgramPath: hostauthSession.nativeProgramPath,');
  main = exactReplace(main, '        (0, menu_1.setupApplicationMenu)(url);', '        (0, menu_1.setupApplicationMenu)(url);\n        hostauthSession.markLanguageServerStarted({ baseUrl: `https://127.0.0.1:${handle.port}`, csrf });');
  main = exactReplace(main,
    '                (0, languageServer_1.setupLocalCertTrust)();',
    '                (0, languageServer_1.setupLocalCertTrust)();\n                hostauthSession.setLanguageServerEndpoint({ baseUrl: `https://127.0.0.1:${newPort}`, csrf });');
  main = exactReplace(main,
    '                    for (const win of windows) {\n                        void win.loadURL(newUrl);\n                    }',
    '                    for (const win of windows) {\n                        if (hostauthSession?.isAccountWindow(win)) continue;\n                        void win.loadURL(newUrl);\n                    }');
  main = exactReplace(main,
    "        // The app menu bar isn't reachable on Windows (hidden title bar), so",
    "        (0, tray_1.insertTrayMenuItem)(2, {\n            label: '额度账号…',\n            click: () => { void Promise.resolve().then(() => hostauthSession.showAccounts()).catch(() => { console.error('Account manager could not open.'); }); },\n        });\n        if (process.argv.includes('--accounts')) {\n            void Promise.resolve().then(() => hostauthSession.showAccounts()).catch(() => { console.error('Account manager could not open.'); });\n        }\n        // The app menu bar isn't reachable on Windows (hidden title bar), so");
  main = exactReplace(main,
    "async function closeHostBridgeServer() {\n    if (!hostBridgeServer) {\n        return;\n    }\n    try {\n        await hostBridgeServer.close();\n    }\n    catch (err) {\n        console.error('Failed to close host bridge server:', err);\n    }\n    hostBridgeServer = undefined;\n}",
    "async function closeHostBridgeServer() {\n    if (hostBridgeServer) {\n        try { await hostBridgeServer.close(); }\n        catch { console.error('Failed to close host bridge server.'); }\n        hostBridgeServer = undefined;\n    }\n    if (hostauthSession) {\n        await hostauthSession.close();\n        hostauthSession = undefined;\n    }\n}");
  main = exactReplace(main, '    (0, updater_1.initAutoUpdater)(HEADLESS, settingsService);',
    '    accountsUpdateHandoff.install({ electron: electron_1, autoUpdater: require("electron-updater").autoUpdater });\n    (0, updater_1.initAutoUpdater)(HEADLESS, settingsService);');
  let ls = sources.get('dist/languageServer.js');
  ls = exactReplace(ls, 'const { headless, hostBridgeUrl, hostBridgeToken, wsl } = options;', 'const { headless, hostBridgeUrl, hostBridgeToken, wsl, useHostAuth, nativeProgramPath } = options;');
  ls = exactReplace(ls,
    "        const logStream = fs.createWriteStream((0, paths_1.getLsLogPath)(), { flags: 'w' });",
    "        if (useHostAuth === true && (!hostBridgeUrl || !hostBridgeToken || wsl)) {\n            reject(new Error('Independent host authentication is required; startup was stopped.'));\n            return;\n        }\n        let independentNativeProgram;\n        if (useHostAuth === true) {\n            if (nativeProgramPath !== 'hostauth-addon/native/language_server.exe' || !process.resourcesPath || !path_1.default.isAbsolute(process.resourcesPath)) {\n                reject(new Error('Independent native program path is invalid; startup was stopped.'));\n                return;\n            }\n            independentNativeProgram = path_1.default.join(process.resourcesPath, nativeProgramPath);\n            try {\n                if (!fs.statSync(independentNativeProgram).isFile()) throw new Error();\n            }\n            catch {\n                reject(new Error('Independent native program is unavailable; startup was stopped.'));\n                return;\n            }\n        }\n        else if (nativeProgramPath !== undefined) {\n            reject(new Error('Independent native program requires host authentication; startup was stopped.'));\n            return;\n        }\n        const logStream = fs.createWriteStream((0, paths_1.getLsLogPath)(), { flags: 'w' });");
  const argumentAnchor = compatibility.baseArguments === 'shared' ? '        const args = [' : "            '--standalone',";
  ls = exactReplace(ls, argumentAnchor, argumentAnchor + "\n            ...(useHostAuth === true ? ['--use_host_auth=true'] : []),");
  ls = exactReplace(ls,
    '        let spawnCmd = exports.LS_BINARY;',
    '        let spawnCmd = independentNativeProgram || exports.LS_BINARY;');
  const shellEnvAnchor = compatibility.shellEnvironment === 'prefetched'
    ? '        const env = { ...process.env, ...loginShellEnv };'
    : '        const env = { ...process.env, ...(0, shell_env_1.shellEnvSync)() };';
  ls = exactReplace(ls, shellEnvAnchor,
    shellEnvAnchor + '\n        if (useHostAuth === true) {\n            delete env.AICODE_DEV_PROJECT;\n            delete env.USER_INFO_OVERRIDE_URL;\n        }');
  let bridge = sources.get('dist/hostBridgeServer.js');
  bridge = exactReplace(bridge,
    '            async applyUpdate() {\n                return { accepted: await options.applyUpdate() };\n            },',
    '            async applyUpdate() {\n                return { accepted: await options.applyUpdate() };\n            },\n            async getOAuthToken() {\n                try {\n                    if (typeof options.getOAuthToken !== "function") throw new Error();\n                    return { tokenInfo: await options.getOAuthToken() };\n                }\n                catch { throw new Error("Independent account authentication is unavailable."); }\n            },');
  return new Map([
    ['dist/main.js', Buffer.from(main)],
    ['dist/languageServer.js', Buffer.from(ls)],
    ['dist/hostBridgeServer.js', Buffer.from(bridge)],
    ['dist/proto/host_bridge_pb.js', Buffer.from(descriptorSource())],
  ]);
}
