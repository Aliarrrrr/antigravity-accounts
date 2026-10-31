import { sha256 } from './asar.mjs';
import { descriptorSource } from './descriptor.mjs';

export const SUPPORTED_VERSION = '2.28.2';
export const SOURCE_SHA256 = Object.freeze({
  'dist/main.js': '66fa884c96dee5dd6a536711d35fc90fee853e9a2eede40fb4d1ab576e4ea50f',
  'dist/languageServer.js': '5c781d05050506d7643a88b1eeadc9bb4f320d7133f84b075e6338a6ce158d26',
  'dist/hostBridgeServer.js': 'db4d7dc0e356828ac26ecf461c2c5d34c96612d69c7a9a765221513351378784',
  'dist/proto/host_bridge_pb.js': 'f80259dd94c70ce6befb46d6d9a08617e7ebee97e1e2975d60b657603bab417d',
});

export function exactReplace(source, before, after) {
  const first = source.indexOf(before);
  if (first < 0 || source.indexOf(before, first + before.length) >= 0) throw new Error('Expected exactly one complete source anchor');
  return source.slice(0, first) + after + source.slice(first + before.length);
}

export function patchSources(archive) {
  const packageInfo = JSON.parse(archive.read('package.json').toString('utf8'));
  if (packageInfo.name !== 'antigravity' || packageInfo.version !== SUPPORTED_VERSION || packageInfo.main !== 'dist/main.js') throw new Error('Unsupported Antigravity version or entry point');
  const sources = new Map();
  for (const [name, expected] of Object.entries(SOURCE_SHA256)) {
    const bytes = archive.read(name);
    if (sha256(bytes) !== expected) throw new Error(`Unsupported exact program source: ${name}`);
    sources.set(name, bytes.toString('utf8'));
  }
  let main = sources.get('dist/main.js');
  main = exactReplace(main, 'const hostBridgeServer_1 = require("./hostBridgeServer");', 'const hostBridgeServer_1 = require("./hostBridgeServer");\nconst hostauthAddon = require("./hostauth-addon/runtime.cjs");');
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
  let ls = sources.get('dist/languageServer.js');
  ls = exactReplace(ls, 'const { headless, hostBridgeUrl, hostBridgeToken, wsl } = options;', 'const { headless, hostBridgeUrl, hostBridgeToken, wsl, useHostAuth, nativeProgramPath } = options;');
  ls = exactReplace(ls,
    "        const logStream = fs.createWriteStream((0, paths_1.getLsLogPath)(), { flags: 'w' });",
    "        if (useHostAuth === true && (!hostBridgeUrl || !hostBridgeToken || wsl)) {\n            reject(new Error('Independent host authentication is required; startup was stopped.'));\n            return;\n        }\n        let independentNativeProgram;\n        if (useHostAuth === true) {\n            if (nativeProgramPath !== 'hostauth-addon/native/language_server.exe' || !process.resourcesPath || !path_1.default.isAbsolute(process.resourcesPath)) {\n                reject(new Error('Independent native program path is invalid; startup was stopped.'));\n                return;\n            }\n            independentNativeProgram = path_1.default.join(process.resourcesPath, nativeProgramPath);\n            try {\n                if (!fs.statSync(independentNativeProgram).isFile()) throw new Error();\n            }\n            catch {\n                reject(new Error('Independent native program is unavailable; startup was stopped.'));\n                return;\n            }\n        }\n        else if (nativeProgramPath !== undefined) {\n            reject(new Error('Independent native program requires host authentication; startup was stopped.'));\n            return;\n        }\n        const logStream = fs.createWriteStream((0, paths_1.getLsLogPath)(), { flags: 'w' });");
  ls = exactReplace(ls, "            '--standalone',", "            '--standalone',\n            ...(useHostAuth === true ? ['--use_host_auth=true'] : []),");
  ls = exactReplace(ls,
    '        let spawnCmd = exports.LS_BINARY;',
    '        let spawnCmd = independentNativeProgram || exports.LS_BINARY;');
  ls = exactReplace(ls,
    '        const env = { ...process.env, ...(0, shell_env_1.shellEnvSync)() };',
    '        const env = { ...process.env, ...(0, shell_env_1.shellEnvSync)() };\n        if (useHostAuth === true) {\n            delete env.AICODE_DEV_PROJECT;\n            delete env.USER_INFO_OVERRIDE_URL;\n        }');
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
