import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile,access} from 'node:fs/promises';
import path from 'node:path';
import {readAsar,rewriteAsar} from '../src/asar.mjs';
import {inspectSourceCompatibility,patchSources} from '../src/patch-plan.mjs';
import {sourceScopeProfile,matchesSourceScope} from '../src/source-scope-contracts.mjs';

test('scope contracts follow dependencies while leaving unrelated helper bodies independent',()=>{
  const source="const mode = 'native'; exports.startLanguageServer = startLanguageServer; function startLanguageServer(){return helper(mode)} function helper(value){return value} function unused(){return 1}";
  const expected=sourceScopeProfile(source,'dist/languageServer.js');
  for(const changed of [source+'\n// comment',source.replace('unused(){return 1}','unused(){return 9}'),source+'\nfunction addedUnrelatedHelper(){return 2}']) assert.equal(matchesSourceScope(changed,'dist/languageServer.js',expected),true);
  for(const changed of [source.replace("'native'","'other'"),source.replace('return helper(mode)','return "other"'),source.replace('helper(value){return value}','helper(value){return null}'),source+'\nvoid performSideEffect();',source+'\nfunction mode(){}',source+'\nfunction hostauthSession(){}']) assert.equal(matchesSourceScope(changed,'dist/languageServer.js',expected),false);
});

const program=process.env.ANTIGRAVITY_PROGRAM_ASAR||path.join(process.env.LOCALAPPDATA||'','Programs/antigravity/resources/app.asar');
let available=false;try{await access(program);available=true}catch{}
test('reviewed original archives accept comments and unrelated helpers but reject altered authentication and top-level execution', {skip:!available},async()=>{
  const original=await readFile(program),archive=readAsar(original);
  // Use actual reviewed input. Installed patched programs intentionally fail.
  const baseline=inspectSourceCompatibility(archive);
  const edit=(name,transform)=>readAsar(rewriteAsar(original,new Map([[name,Buffer.from(transform(archive.read(name).toString()))]])));
  for(const updated of [edit('dist/main.js',source=>'// upstream comment\n'+source),edit('dist/languageServer.js',source=>source+'\nfunction unrelatedHelpText(){return "help";}')]){
    assert.equal(inspectSourceCompatibility(updated).match,'reviewed-scopes');
    assert.equal(inspectSourceCompatibility(updated).sourceContractVersion,baseline.sourceContractVersion);
    assert.equal(patchSources(updated).size,4);
  }
  for(const updated of [edit('dist/main.js',source=>source+'\nvoid alteredStartup();'),edit('dist/languageServer.js',source=>source.replace('function startLanguageServer(', 'function alteredStartLanguageServer('))]) assert.throws(()=>inspectSourceCompatibility(updated),/Unsupported exact program source/);
});
