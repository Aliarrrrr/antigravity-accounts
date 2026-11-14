import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {extractFile,extractAll,createPackageWithOptions} from '@electron/asar';
import * as toolkit from '../src/toolkit.mjs';
import {WHITELIST} from '../src/native-patches.mjs';

// Read the installer's public receipt, never the account store. Its prepared
// program copy stays unlocalized even after the live installation is repaired.
const receiptRoot=process.env.ANTIGRAVITY_TEST_ACCOUNTS_STATE_ROOT || path.join(process.env.LOCALAPPDATA || '', 'AntigravityAccounts');
const receipt=JSON.parse(await fs.readFile(path.join(receiptRoot,'adapter-installation.json'),'utf8'));
const preparedAsar=process.env.ANTIGRAVITY_TEST_PREPARED_ASAR || path.join(receipt.preparedRoot,'resources','app.asar');
const originalAsar=path.join(receipt.backupRoot,'app.asar');
const sourceInstall=path.dirname(receipt.resourcesRoot);
const readEntry=(archive,name)=>extractFile(archive,name.split('/').join(path.sep));
const originalHash='290E1B7A1CAA430A0C5367C7B433E8D284E2CA5D0B305E91E5CE8EC926F43283';
const mainHashes=new Set(['E1E0D0728C4BDBCDFF9659AD7F6D11A3398CD043696D067C63A9755942295891',
  'F7FDCAE0629FE6E6F1618967CA035CF45A97809BC45D7DBE3E9903A0872393ED']);

async function fixture(t) {
  const temporary=await fs.realpath(os.tmpdir());
  const root=await fs.mkdtemp(path.join(temporary,'agy-2-29-accounts-'));
  t.after(async()=>{
    const resolved=await fs.realpath(root);
    assert.equal(path.dirname(resolved),temporary);
    assert.ok(path.basename(resolved).startsWith('agy-2-29-accounts-'));
    await fs.rm(resolved,{recursive:true,force:true});
  });
  const install=path.join(root,'install');
  await fs.mkdir(path.join(install,'resources'),{recursive:true});
  await fs.copyFile(path.join(sourceInstall,'Antigravity.exe'),path.join(install,'Antigravity.exe'));
  await fs.copyFile(preparedAsar,path.join(install,'resources','app.asar'));
  await fs.cp(path.join(receipt.resourcesRoot,'app.asar.unpacked'),path.join(install,'resources','app.asar.unpacked'),{recursive:true});
  return {root,install};
}

test('reviewed 2.29 Accounts source changes only the whitelisted main script',async t=>{
  assert.equal(toolkit.sha256(await fs.readFile(originalAsar)),originalHash);
  const changed=WHITELIST.filter(name=>!readEntry(preparedAsar,name).equals(readEntry(originalAsar,name)));
  assert.deepEqual(changed,['dist/main.js']);
  assert.ok(mainHashes.has(toolkit.sha256(readEntry(preparedAsar,'dist/main.js'))));
  const {install}=await fixture(t);
  const before=await fs.stat(preparedAsar);
  const info=await toolkit.inspectInstallation(install);
  assert.equal(info.appVersion,'2.29.0');
  assert.equal(info.isLocalized,false);
  assert.equal(info.compatibleNativeSource,true);
  assert.equal((await fs.stat(preparedAsar)).mtimeMs,before.mtimeMs);
});

test('2.29 localization preserves Accounts runtime and rejects unreviewed main behavior',async t=>{
  const {root,install}=await fixture(t);
  const before=await toolkit.inspectInstallation(install);
  const {artifactDir,manifest}=await toolkit.buildInstallation(install,{outputRoot:path.join(root,'artifacts'),compatibleSource:true});
  const candidate=path.join(artifactDir,'app.asar');
  assert.equal(manifest.comparison.changed.length,9);
  const preserved=[...toolkit.archiveEntries(preparedAsar).keys()].filter(name=>name.startsWith('dist/hostauth-addon/') ||
    ['dist/languageServer.js','dist/hostBridgeServer.js','dist/proto/host_bridge_pb.js','dist/webviewBrowser.js','dist/webauthn.js','dist/scriptJail.js'].includes(name));
  assert.ok(preserved.some(name=>name.endsWith('/native-view.cjs')));
  for(const name of preserved) assert.deepEqual(readEntry(candidate,name),readEntry(preparedAsar,name),name+' changed');
  const runtimeLines=source=>source.toString().split('\n').filter(line=>/hostauthAddon|hostauthSession/.test(line));
  assert.deepEqual(runtimeLines(readEntry(candidate,'dist/main.js')),runtimeLines(readEntry(preparedAsar,'dist/main.js')));
  assert.equal((await toolkit.inspectInstallation(install)).asarSha256,before.asarSha256);
  const archive=path.join(install,'resources','app.asar'),extracted=path.join(root,'extracted');
  extractAll(archive,extracted);
  await fs.appendFile(path.join(extracted,'dist','main.js'),'\nprocess.env.UNREVIEWED_ACCOUNTS_TEST = "changed";\n');
  await createPackageWithOptions(extracted,archive,{unpackDir:path.join('node_modules','chrome-devtools-mcp')});
  const changed=await toolkit.inspectInstallation(install);
  assert.equal(changed.compatibleNativeSource,false);
  const rejectedOutput=path.join(root,'rejected');
  await assert.rejects(toolkit.buildInstallation(install,{outputRoot:rejectedOutput,compatibleSource:true}),/incompatible_native_source/);
  await assert.rejects(fs.stat(rejectedOutput),{code:'ENOENT'});
  assert.equal((await toolkit.inspectInstallation(install)).asarSha256,changed.asarSha256);
});
