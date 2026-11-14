import test from 'node:test';
import assert from 'node:assert/strict';
import {WHITELIST} from '../src/native-patches.mjs';
import {nativeCompatibility,nativeScopeProfile} from '../src/native-compatibility.mjs';

const entry='dist/menu.js';
const source='"use strict";\nconst dependency=require("native");\nconst unrelatedVersion=1;\nfunction helper(value){ return value; }\nfunction translationOwner(){ return dependency.open({title:"English",value:helper(1)}); }\nfunction unrelated(version){ return version; }\n';
const files=new Map(WHITELIST.map(name=>[name,Buffer.from(name===entry?source:'"use strict";\n')]));
const operations=[{entry,old:'title:"English"',expected:1}];
const profiles=[{name:'reviewed-test',files:nativeScopeProfile(files,{operations})}];
function mutate(from,to){const input=new Map(files);assert.ok(source.includes(from));input.set(entry,Buffer.from(source.replace(from,to)));return nativeCompatibility(input,{profiles,operations});}

test('unrelated function parameters and pure constants can change without widening translation dependencies',()=>{
  assert.equal(mutate('function unrelated(version)','function unrelated(version,fetchImpl=fetch)').compatible,true);
  assert.equal(mutate('const unrelatedVersion=1','const unrelatedVersion=2').compatible,true);
  assert.equal(mutate('function unrelated(version){ return version; }','').compatible,true);
});

test('translation owner, referenced declarations, imports and top-level execution stay protected',()=>{
  for(const [from,to] of [
    ['dependency.open({','dependency.other({'],
    ['function helper(value)','function helper(value,options)'],
    ['require("native")','require("other")'],
    ['const unrelatedVersion=1','const unrelatedVersion=dependency.initialize()'],
  ])assert.equal(mutate(from,to).compatible,false,from);
});

test('a wrapper owning a translation callback retains its declaration and event condition',()=>{
  const wrapped='const native=require("native");\nfunction localOnly(channel,callback){return native.handle(channel,callback);}\nlocalOnly("dialog:open",()=>({title:"English"}));';
  const input=new Map(files);input.set(entry,Buffer.from(wrapped));
  const reviewed=[{name:'reviewed-wrapper',files:nativeScopeProfile(input,{operations})}];
  for(const [from,to] of [['localOnly("dialog:open"','localOnly("different:open"'],['function localOnly(channel,callback)','function localOnly(channel,callback,extra)']]){
    const changed=new Map(input);changed.set(entry,Buffer.from(wrapped.replace(from,to)));
    assert.equal(nativeCompatibility(changed,{profiles:reviewed,operations}).compatible,false,from);
  }
});
