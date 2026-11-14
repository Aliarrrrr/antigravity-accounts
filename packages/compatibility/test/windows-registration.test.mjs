import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {reconcileWindows} from '../windows-registration.mjs';

const run=promisify(execFile);
const quote=value=>"'"+value.replaceAll("'","''")+"'";
test('Windows reconciliation reads UTF-8 paths and preserves Unicode shortcut names',{skip:process.platform!=='win32'},async t=>{
  const parent=await fs.realpath(os.tmpdir()),root=await fs.mkdtemp(path.join(parent,'agy-registration-'));
  t.after(async()=>{assert.equal(path.dirname(await fs.realpath(root)),parent);await fs.rm(root,{recursive:true,force:true});});
  const stateRoot=path.join(root,'中文 🧭 state'),installRoot=path.join(root,'app'),toolkitRoot=path.join(root,'中文 toolkit'),
    shortcuts=[path.join(root,'Antigravity（简体中文）🚀.lnk')];
  await fs.mkdir(stateRoot);await fs.mkdir(installRoot);await fs.mkdir(toolkitRoot);
  const config={schemaVersion:1,stateRoot,installRoot,toolkitRoot,nodePath:process.execPath};
  await fs.writeFile(path.join(stateRoot,'bootstrap.json'),JSON.stringify(config),'utf8');
  await fs.writeFile(path.join(stateRoot,'windows-registration.json'),JSON.stringify({...config,kind:'compatibility',shortcuts}),'utf8');
  await reconcileWindows(stateRoot);
  assert.ok((await fs.stat(shortcuts[0])).size>0);
  assert.deepEqual((await fs.readdir(root)).filter(name=>name.endsWith('.lnk')),[path.basename(shortcuts[0])]);
  const helper=fileURLToPath(new URL('../../localization/scripts/Unicode-Shortcut.ps1',import.meta.url));
  const shell=path.join(process.env.SystemRoot,'System32/WindowsPowerShell/v1.0/powershell.exe');
  const read=`. ${quote(helper)};[Console]::OutputEncoding=[Text.UTF8Encoding]::new();$l=New-UnicodeShortcut -Path ${quote(shortcuts[0])};@{target=$l.TargetPath;arguments=$l.Arguments}|ConvertTo-Json -Compress`;
  const {stdout}=await run(shell,['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(read,'utf16le').toString('base64')],{encoding:'utf8',windowsHide:true});
  const link=JSON.parse(stdout.trim());
  assert.equal(link.target.toLowerCase(),path.join(process.env.SystemRoot,'System32/wscript.exe').toLowerCase());
  assert.ok(link.arguments.includes(stateRoot));assert.ok(link.arguments.includes(path.join(stateRoot,'bootstrap.mjs')));
  await reconcileWindows(stateRoot);
  const replace=`. ${quote(helper)};$l=New-UnicodeShortcut -Path ${quote(shortcuts[0])};$l.TargetPath=${quote(process.execPath)};$l.Arguments='unrelated';$l.Save()`;
  await run(shell,['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(replace,'utf16le').toString('base64')],{windowsHide:true});
  const unrelated=await fs.readFile(shortcuts[0]);
  await assert.rejects(reconcileWindows(stateRoot),/compatibility_shortcut_not_owned/);
  assert.deepEqual(await fs.readFile(shortcuts[0]),unrelated);
});
