import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawnSync} from 'node:child_process';
import {missingInputs} from './suite-inputs.mjs';

const root=path.dirname(fileURLToPath(import.meta.url));
const selected=[];
for(const name of fs.readdirSync(root).filter(name=>name.endsWith('.test.mjs'))){
  const missing=missingInputs(name);
  if(missing.length)console.log(`SKIP ${name}: original program input unavailable (${missing.join(', ')})`);
  else selected.push(path.join(root,name));
}
const result=spawnSync(process.execPath,['--test',...selected],{stdio:'inherit',env:process.env});
if(result.error)throw result.error;
process.exitCode=result.status??1;
