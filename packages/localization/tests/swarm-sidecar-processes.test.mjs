import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {fileURLToPath,pathToFileURL} from 'node:url';

const scripts=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../scripts');
const powershell=path.join(process.env.SystemRoot||'C:\\Windows','System32','WindowsPowerShell','v1.0','powershell.exe');

test('Swarm Board exemption requires its exact external working directory and parent',{skip:process.platform!=='win32'},async t=>{
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'agy-swarm-sidecar-'));
  const knownRoot=await fs.realpath(root);
  t.after(async()=>{
    assert.equal(await fs.realpath(root),knownRoot);
    assert.equal(path.dirname(knownRoot),await fs.realpath(os.tmpdir()));
    await fs.rm(knownRoot,{recursive:true,force:true});
  });
  const profile=path.join(knownRoot,'user'),roaming=path.join(knownRoot,'roaming'),install=path.join(knownRoot,'install');
  const board=path.join(profile,'.gemini','antigravity','builtin','plugins','swarm','sidecars','swarm_board');
  const shim=path.join(roaming,'Antigravity','bin','agy-node.cmd');
  const executable=path.join(install,'Antigravity.exe');
  const cmd=path.join(process.env.SystemRoot,'System32','cmd.exe');
  const loader=pathToFileURL(path.join(profile,'.gemini','antigravity','lib','node_modules','sidecar_sdk','register_loader.js')).href;
  const args='--import='+loader+' main.mjs';
  await fs.mkdir(board,{recursive:true});
  await fs.mkdir(path.dirname(shim),{recursive:true});
  await fs.writeFile(shim,'@echo off\r\nset ELECTRON_RUN_AS_NODE=1\r\n"'+executable+'" %*\r\n');
  const parent={ProcessId:201,ExecutablePath:cmd,CommandLine:'"'+cmd+'" /c ""'+shim+'" '+args+'"'};
  const child={ProcessId:101,ParentProcessId:201,ExecutablePath:executable,CommandLine:'"'+executable+'" '+args};
  const gui={ProcessId:301,ExecutablePath:executable,CommandLine:'"'+executable+'"'};
  const cases=[
    {name:'known board',cwd:board,processes:[parent,child],want:[101]},
    {name:'working directory may use the TEMP alias',cwd:path.join(root,path.relative(knownRoot,board)),processes:[parent,child],want:[101]},
    {name:'board and GUI',cwd:board,processes:[parent,child,gui],want:[101]},
    {name:'another directory',cwd:install,processes:[parent,child],want:[]},
    {name:'unavailable directory',cwd:null,processes:[parent,child],want:[]},
    {name:'parent absent',cwd:board,processes:[child],want:[]},
    {name:'parent arguments differ',cwd:board,processes:[{...parent,CommandLine:parent.CommandLine.replace('main.mjs','other.mjs')},child],want:[]},
    {name:'extra child arguments',cwd:board,processes:[parent,{...child,CommandLine:child.CommandLine+' --type=renderer'}],want:[]},
    {name:'missing main file',cwd:board,processes:[parent,child],missing:true,want:[]},
  ];
  const dataset=path.join(root,'cases.json'),harness=path.join(root,'check.ps1');
  await fs.writeFile(dataset,JSON.stringify(cases));
  await fs.writeFile(harness,`
param([string]$Scripts,[string]$Dataset,[string]$Install,[string]$Board)
$ErrorActionPreference='Stop'
. (Join-Path $Scripts 'Node-Sidecar-State.ps1')
$cases=Get-Content -LiteralPath $Dataset -Raw -Encoding UTF8 | ConvertFrom-Json
$results=@(foreach($case in $cases) {
  $main=Join-Path $Board 'main.mjs'
  if($case.missing) { [IO.File]::Delete($main) } else { [IO.File]::WriteAllText($main,'// test sidecar') }
  $ids=@(Get-NodeSidecarProcessIds -Processes @($case.processes) -InstallRoot $Install -WorkingDirectoryReader {param($ProcessId) $case.cwd})
  [pscustomobject]@{name=$case.name;ids=$ids}
})
ConvertTo-Json -InputObject $results -Depth 4 -Compress
`);
  const result=spawnSync(powershell,['-NoProfile','-File',harness,'-Scripts',scripts,'-Dataset',dataset,'-Install',install,'-Board',board],{
    encoding:'utf8',windowsHide:true,timeout:30000,env:{...process.env,USERPROFILE:profile,APPDATA:roaming},
  });
  assert.equal(result.status,0,result.stderr||result.error?.message);
  const actual=JSON.parse(result.stdout);
  for(const scenario of cases) assert.deepEqual(actual.find(item=>item.name===scenario.name).ids,scenario.want,scenario.name);
});

test('working-directory reader returns only its own path and handles unavailable processes',{skip:process.platform!=='win32'},()=>{
  const source=path.join(scripts,'Process-WorkingDirectory.ps1').replaceAll("'","''");
  const command=". '"+source+"'; @{actual=(Get-ProcessWorkingDirectory $PID);expected=[Environment]::CurrentDirectory;missing=(Get-ProcessWorkingDirectory 2147483647)} | ConvertTo-Json -Compress";
  const result=spawnSync(powershell,['-NoProfile','-Command',command],{encoding:'utf8',windowsHide:true,timeout:30000});
  assert.equal(result.status,0,result.stderr||result.error?.message);
  const actual=JSON.parse(result.stdout);
  assert.equal(path.resolve(actual.actual).toLowerCase(),path.resolve(actual.expected).toLowerCase());
  assert.equal(actual.missing,null);
});
