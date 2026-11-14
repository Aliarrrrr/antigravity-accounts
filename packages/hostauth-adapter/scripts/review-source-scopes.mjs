#!/usr/bin/env node
import path from 'node:path';
import {readFile,writeFile} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import {readAsar,sha256} from '../src/asar.mjs';
import {SOURCE_CONTRACTS} from '../src/patch-plan.mjs';
import {sourceScopeProfile} from '../src/source-scope-contracts.mjs';

// Developer-only generation: first review and record complete file identities
// in SOURCE_CONTRACTS, then derive the AST comparison data from that exact
// original. Runtime recovery never generates or approves its own contracts.
export async function reviewSourceScopes({version,sourceAsar,output}) {
  const contract=SOURCE_CONTRACTS.find(value=>value.version===version);
  if(!contract||!path.isAbsolute(sourceAsar||'')||!path.isAbsolute(output||'')) throw new Error('reviewed_version_and_absolute_paths_required');
  const archive=readAsar(await readFile(sourceAsar));
  const info=JSON.parse(archive.read('package.json'));
  if(info.name!=='antigravity'||info.version!==version) throw new Error('reviewed_original_version_mismatch');
  const files={};
  for(const [filename,expected] of Object.entries(contract.sourceSha256)) {
    const bytes=archive.read(filename);
    if(sha256(bytes)!==expected) throw new Error('unreviewed_source_file: '+filename);
    files[filename]=sourceScopeProfile(bytes.toString('utf8'),filename);
  }
  let existing;
  try {existing=JSON.parse(await readFile(output,'utf8'));}
  catch(error){if(error.code!=='ENOENT')throw error;existing={schemaVersion:1,contracts:[]};}
  if(existing.schemaVersion!==1||!Array.isArray(existing.contracts)) throw new Error('source_scope_profile_invalid');
  const profiles=new Map(existing.contracts.map(value=>[value.version,value]));
  profiles.set(version,{version,files});
  const result={schemaVersion:1,contracts:SOURCE_CONTRACTS.map(value=>profiles.get(value.version)).filter(Boolean)};
  await writeFile(output,JSON.stringify(result,null,2)+'\n');
  return {version,files:Object.keys(files),output};
}

if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)){
  try{
    const args=new Map();
    for(let index=2;index<process.argv.length;index+=2){
      if(!['--version','--asar','--output'].includes(process.argv[index])||!process.argv[index+1]||args.has(process.argv[index])) throw new Error('Usage: review-source-scopes.mjs --version VERSION --asar ABSOLUTE_ORIGINAL --output ABSOLUTE_JSON');
      args.set(process.argv[index],process.argv[index+1]);
    }
    console.log(JSON.stringify(await reviewSourceScopes({version:args.get('--version'),sourceAsar:args.get('--asar'),output:args.get('--output')})));
  }catch(error){console.error(error.message);process.exitCode=1;}
}
