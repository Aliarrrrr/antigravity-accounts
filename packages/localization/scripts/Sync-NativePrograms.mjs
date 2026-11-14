import {readFile,writeFile} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import {createRequire} from 'node:module';

// Build-time only: publish an identical generated copy so a standalone Chinese
// package does not depend on Accounts being installed next to its code.
const source=new URL('../../hostauth-adapter/src/native-program-profiles.cjs',import.meta.url);
const target=new URL('../src/native-program-profiles.cjs',import.meta.url);
await writeFile(target,await readFile(source));
const {ADAPTED_NATIVE_DIGESTS}=createRequire(import.meta.url)(fileURLToPath(source));
const launcher=new URL('./Launch-Localized.ps1',import.meta.url);
const text=await readFile(launcher,'utf8');
const match=/\$adaptedNativeDigests = @\([\s\S]*?\n\s*\)/;
if(!match.test(text))throw new Error('The PowerShell native-program rejection list is missing.');
await writeFile(launcher,text.replace(match,'$adaptedNativeDigests = @(\n'+ADAPTED_NATIVE_DIGESTS.map(digest=>"          '"+digest+"'").join(',\n')+'\n        )'));
console.log('Copied the reviewed native-program description into localization.');
