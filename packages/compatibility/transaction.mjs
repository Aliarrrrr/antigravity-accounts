import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';

const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const same = (a,b) => path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();
const inside = (file,root) => {const r=path.relative(root,file);return r!==''&&r!=='..'&&!r.startsWith('..'+path.sep)&&!path.isAbsolute(r);};
const fail = code => {throw Object.assign(new Error(code),{code});};
async function bytes(file) {try {const s=await fs.lstat(file);if(!s.isFile()||s.isSymbolicLink())fail('transaction_non_regular_file');return await fs.readFile(file);}catch(e){if(e.code==='ENOENT')return null;throw e;}}
const hash = b => b===null?null:digest(b);
async function atomic(file,data) {await fs.mkdir(path.dirname(file),{recursive:true});const stage=file+'.compat-'+randomUUID();await fs.writeFile(stage,data,{flag:'wx'});try{await fs.rename(stage,file);}finally{await fs.rm(stage,{force:true});}}
async function save(root,journal) {await atomic(path.join(root,'transaction.json'),JSON.stringify(journal,null,2)+'\n');}

async function checkedTarget(roots,key,relative) {
  const root=roots[key];
  if(!root||typeof relative!=='string'||!relative||relative.includes('\\')||relative.split('/').some(p=>!p||p==='.'||p==='..'||/[:\0]/.test(p)))fail('transaction_path_invalid');
  if(key==='install'&&!/^resources\/(?:app\.asar|bin\/language_server\.exe|antigravity-zh-cn\.install\.json|hostauth-addon\/(?:config\.json|component\/Antigravity\.Accounts\.exe|native\/language_server\.exe))$/.test(relative))fail('transaction_target_not_owned');
  if(key==='accounts'&&relative!=='adapter-installation.json')fail('transaction_target_not_owned');
  if(key==='localization'&&relative!=='status.json')fail('transaction_target_not_owned');
  const target=path.resolve(root,...relative.split('/'));if(!inside(target,root))fail('transaction_path_invalid');
  let cursor=root;
  for(const part of relative.split('/').slice(0,-1)){cursor=path.join(cursor,part);try{const s=await fs.lstat(cursor);if(s.isSymbolicLink()||!s.isDirectory())fail('transaction_path_invalid');}catch(e){if(e.code==='ENOENT')break;throw e;}}
  return target;
}

export async function prepareTransaction({stateRoot,roots,changes,guards=[],metadata={}}) {
  const checked={};
  for(const [key,value] of Object.entries(roots)){if(!['install','accounts','localization'].includes(key)||!path.isAbsolute(value))fail('transaction_roots_invalid');checked[key]=await fs.realpath(value);}
  stateRoot=await fs.realpath(stateRoot);if(same(stateRoot,checked.install)||inside(stateRoot,checked.install))fail('transaction_state_inside_installation');
  const root=path.join(stateRoot,'transactions',randomUUID());await fs.mkdir(path.join(root,'objects'),{recursive:true});
  const journal={schemaVersion:1,phase:'prepared',roots:checked,metadata,guards:[],changes:[]};const targets=new Set();
  for(const [index,change] of changes.entries()){
    const target=await checkedTarget(checked,change.root,change.path);if(targets.has(target.toLowerCase()))fail('transaction_duplicate_target');targets.add(target.toLowerCase());
    const before=await bytes(target),after=change.bytes===null?null:Buffer.from(change.bytes);
    const item={root:change.root,path:change.path,before:hash(before),after:hash(after)};
    for(const [kind,data] of [['before',before],['after',after]])if(data!==null){item[kind+'Object']=index+'-'+kind;await fs.writeFile(path.join(root,'objects',item[kind+'Object']),data,{flag:'wx'});}
    journal.changes.push(item);
  }
  for(const guard of guards){if(!path.isAbsolute(guard.file)||!inside(guard.file,checked.install))fail('transaction_guard_invalid');journal.guards.push({file:guard.file,sha256:hash(await bytes(guard.file))});}
  await save(root,journal);return {transactionRoot:root,journal};
}

async function load(root) {
  const journal=JSON.parse(await fs.readFile(path.join(root,'transaction.json'),'utf8'));
  if(journal.schemaVersion!==1||!Array.isArray(journal.changes)||!Array.isArray(journal.guards))fail('transaction_invalid');
  for(const [key,value]of Object.entries(journal.roots)){if(!same(value,await fs.realpath(value)))fail('transaction_root_changed');}
  return journal;
}
async function verify(root,journal) {
  for(const g of journal.guards)if(hash(await bytes(g.file))!==g.sha256)fail('transaction_host_changed');
  for(const item of journal.changes){const target=await checkedTarget(journal.roots,item.root,item.path);if(![item.before,item.after].includes(hash(await bytes(target))))fail('transaction_files_changed');
    for(const kind of ['before','after'])if(item[kind]!==null){const object=item[kind+'Object'];if(typeof object!=='string'||!/^\d+-(before|after)$/.test(object)||digest(await fs.readFile(path.join(root,'objects',object)))!==item[kind])fail('transaction_object_changed');}}
}
async function replace(root,journal,item,kind) {const target=await checkedTarget(journal.roots,item.root,item.path);if(hash(await bytes(target))===item[kind])return;
  if(item[kind]===null)await fs.rm(target,{force:true});else await atomic(target,await fs.readFile(path.join(root,'objects',item[kind+'Object'])));
  if(hash(await bytes(target))!==item[kind])fail('transaction_write_failed');}

export async function applyTransaction(transactionRoot,{checkStopped,afterWrite=async()=>{},rollback=false}={}) {
  if(typeof checkStopped!=='function')fail('transaction_shutdown_check_required');await checkStopped();
  const journal=await load(transactionRoot);if(journal.phase==='committed'&&!rollback)return journal;
  await verify(transactionRoot,journal);journal.phase=rollback?'rolling_back':'applying';await save(transactionRoot,journal);
  try {
    const items=rollback?[...journal.changes].reverse():journal.changes;
    for(const [index,item]of items.entries()){await checkStopped();await verify(transactionRoot,journal);await replace(transactionRoot,journal,item,rollback?'before':'after');journal.completed=index+1;await save(transactionRoot,journal);await afterWrite(index,item);}
    journal.phase=rollback?'rolled_back':'committed';await save(transactionRoot,journal);return journal;
  }catch(error){
    if(error.code==='simulated_interruption')throw error;
    try {await checkStopped();await verify(transactionRoot,journal);for(const item of [...journal.changes].reverse())await replace(transactionRoot,journal,item,'before');journal.phase='rolled_back';journal.error=error.code||'transaction_write_failed';await save(transactionRoot,journal);}
    catch(recovery){journal.phase='recovery_required';journal.error=error.code||'transaction_write_failed';await save(transactionRoot,journal);throw Object.assign(new Error('transaction_recovery_required'),{cause:recovery});}
    throw error;
  }
}

export async function recoverTransactions(stateRoot,options) {
  const directory=path.join(stateRoot,'transactions');let entries;try{entries=await fs.readdir(directory,{withFileTypes:true});}catch(e){if(e.code==='ENOENT')return[];throw e;}
  const results=[];for(const entry of entries){if(!entry.isDirectory()||!/^[-a-f0-9]{36}$/.test(entry.name))continue;const root=path.join(directory,entry.name),journal=await load(root);
    if(['applying','rolling_back','recovery_required'].includes(journal.phase)){
      try{results.push(await applyTransaction(root,{...options,rollback:true}));}
      catch(e){if(e.code==='transaction_host_changed'||e.code==='transaction_files_changed'){journal.phase='superseded';journal.error=e.code;await save(root,journal);results.push(journal);}else throw e;}
    }}return results;
}
