import {createHash} from 'node:crypto';
import {parse} from 'acorn';
import {WHITELIST,nativePatchOperations} from './native-patches.mjs';
import revisions from './native-scope-signatures.json' with {type:'json'};

const functionTypes=new Set(['FunctionDeclaration','FunctionExpression','ArrowFunctionExpression']);
const reserved=new Set(['menuTranslationMap','translateMenu','contextMenuTranslationMap','translateContextLabel']);
const omitted=new Set(['start','end','loc','raw']);
const hash=value=>createHash('sha256').update(JSON.stringify(value)).digest('hex');
const fail=message=>{throw new Error(message);};

function normalize(value,stripBodies=false) {
  if(typeof value==='bigint') return {bigint:String(value)};
  if(Array.isArray(value)) return value.map(item=>normalize(item,stripBodies));
  if(!value || typeof value!=='object') return value;
  const result={};
  for(const key of Object.keys(value).sort()) {
    if(omitted.has(key)) continue;
    result[key]=stripBodies && key==='body' && functionTypes.has(value.type)
      ? '[function body]' : normalize(value[key],stripBodies);
  }
  return result;
}

function walk(node,visit,ancestors=[]) {
  if(!node || typeof node!=='object' || typeof node.type!=='string') return;
  visit(node,ancestors);
  for(const value of Object.values(node)) {
    if(Array.isArray(value)) for(const child of value) walk(child,visit,[...ancestors,node]);
    else if(value && typeof value==='object') walk(value,visit,[...ancestors,node]);
  }
}

// Retain the event name, callee, property and conditions that own a callback.
// Sibling statements are outside this function's signature and may change.
function parentContext(ancestors,selected) {
  const result=[];
  let child=selected;
  for(let i=ancestors.length-1;i>=0;i--) {
    const parent=ancestors[i];
    if(parent.type==='Program' || parent.type==='BlockStatement') {child=parent;continue;}
    const shell={};
    for(const key of Object.keys(parent).sort()) {
      if(omitted.has(key)) continue;
      const value=parent[key];
      if(value===child) shell[key]='[protected child]';
      else if(Array.isArray(value) && value.includes(child)) {
        shell[key]=['properties','declarations','consequent'].includes(key)
          ? ['[protected child]']
          : value.map(item=>item===child?'[protected child]':normalize(item,true));
      } else shell[key]=normalize(value,true);
    }
    result.push(shell);
    child=parent;
  }
  return result;
}

function bindingNames(pattern) {
  if(!pattern) return [];
  if(pattern.type==='Identifier') return [pattern.name];
  if(pattern.type==='RestElement') return bindingNames(pattern.argument);
  if(pattern.type==='AssignmentPattern') return bindingNames(pattern.left);
  if(pattern.type==='ArrayPattern') return pattern.elements.flatMap(bindingNames);
  if(pattern.type==='ObjectPattern') return pattern.properties.flatMap(item=>bindingNames(item.value??item.argument));
  return [];
}

function topLevelBindings(ast) {
  const bindings=new Map();
  for(const statement of ast.body) {
    const declarations=statement.type==='VariableDeclaration'
      ? statement.declarations.map(item=>({names:bindingNames(item.id),node:{kind:statement.kind,...item}}))
      : statement.id ? [{names:[statement.id.name],node:statement}] : [];
    for(const declaration of declarations) for(const name of declaration.names) {
      if(bindings.has(name)) fail('duplicate_top_level_binding: '+name);
      bindings.set(name,declaration.node);
    }
  }
  return bindings;
}

// New literal/function definitions cannot execute native APIs merely by being
// declared. Keep existing definitions fixed while allowing independent helpers.
// Calls, property reads, spreads, computed keys and assignment expressions remain
// part of the exact top-level statement sequence.
function definitionOnly(node) {
  if(!node) return true;
  if(node.type==='Literal' || node.type==='FunctionExpression' || node.type==='ArrowFunctionExpression') return true;
  if(node.type==='ArrayExpression') return node.elements.every(definitionOnly);
  if(node.type==='ObjectExpression') return node.properties.every(property=>property.type==='Property'
    && !property.computed && definitionOnly(property.value));
  if(node.type==='UnaryExpression' && ['!','~','+','-','void','typeof'].includes(node.operator)) return definitionOnly(node.argument);
  return false;
}

function topLevelProfile(ast) {
  const definitions={},statements=[];
  for(const statement of ast.body) {
    if(statement.type==='FunctionDeclaration') continue;
    if(statement.type==='VariableDeclaration' && statement.kind==='const'
      && statement.declarations.every(item=>item.id.type==='Identifier' && definitionOnly(item.init))) {
      for(const declaration of statement.declarations) definitions[declaration.id.name]=hash(normalize(declaration,true));
    } else statements.push(hash(normalize(statement,true)));
  }
  return {definitions,statements};
}

// Purely derives a profile for an explicitly reviewed original. Runtime checks use
// only the checked-in profiles below, never archives or backups from the machine.
export function nativeScopeProfile(files,{operations=nativePatchOperations('/* native compatibility analysis */')}={}) {
  if(!(files instanceof Map)) fail('native_files_must_be_map');
  const result={};
  for(const entry of WHITELIST) {
    const bytes=files.get(entry);
    if(!Buffer.isBuffer(bytes)) fail('native_file_missing: '+entry);
    const source=bytes.toString('utf8');
    if(source.includes('ANTIGRAVITY_ZH_CN_NATIVE_V1') || source.includes('__antigravityZhCN') || source.includes('Antigravity 2.0 Chinese Localization Engine')) fail('already_localized: '+entry);
    let ast;
    try {ast=parse(source,{ecmaVersion:2022,sourceType:'script'});}
    catch(error) {fail('native_syntax_invalid: '+entry+': '+error.message);}
    const nodes=[];
    walk(ast,(node,ancestors)=>{
      if(node.type==='Identifier' && reserved.has(node.name)) fail('localization_helper_conflict: '+entry+': '+node.name);
      nodes.push({node,ancestors});
    });
    const bindings=topLevelBindings(ast);
    for(const name of ['Object','Array',...(entry==='dist/preload.js'?['window','document','Promise']:[])]) {
      if(bindings.has(name)) fail('localization_global_shadowed: '+entry+': '+name);
    }
    const functionHeads={};
    for(const node of ast.body) if(node.type==='FunctionDeclaration') functionHeads[node.id.name]=hash(normalize(node,true));
    const scopes=[],dependencies={},contextFunctionNames=new Set();
    for(const [index,operation] of operations.entries()) {
      if(operation.entry!==entry) continue;
      const occurrences=[];
      for(let offset=source.indexOf(operation.old);offset!==-1;offset=source.indexOf(operation.old,offset+operation.old.length)) occurrences.push(offset);
      if(occurrences.length!==operation.expected) fail('native_anchor_count: '+entry+': '+index+': '+occurrences.length);
      for(const [occurrence,start] of occurrences.entries()) {
        // The preload anchor includes a newline outside its ExpressionStatement.
        const end=start+operation.old.trimEnd().length;
        const candidates=nodes.filter(({node})=>node.start<=start&&node.end>=end);
        const selected=candidates.filter(({node})=>functionTypes.has(node.type)).at(-1)
          ?? candidates.find(({ancestors})=>ancestors.length===1);
        if(!selected) fail('native_anchor_outside_code: '+entry+': '+index);
        const {node,ancestors}=selected;
        // Reject text found in a comment or unrelated string instead of the
        // reviewed syntax location, even when the text occurs exactly once.
        const syntaxOwner=candidates.at(-1)?.node;
        if(!syntaxOwner || syntaxOwner.type==='Program') fail('native_anchor_outside_code: '+entry+': '+index);
        const context=parentContext(ancestors,node);
        for(const shell of context) walk(shell,part=>{
          if(part.type==='Identifier' && Object.hasOwn(functionHeads,part.name)) contextFunctionNames.add(part.name);
        });
        scopes.push({operation:index,occurrence,signature:hash({node:normalize(node),context})});
        walk(node,part=>{
          if(part.type!=='Identifier' || !bindings.has(part.name)) return;
          const declaration=bindings.get(part.name);
          dependencies[part.name]=hash(normalize(declaration,declaration.type==='FunctionDeclaration'));
        });
      }
    }
    result[entry]={
      topLevel:hash(ast.body.filter(node=>node.type!=='FunctionDeclaration').map(node=>normalize(node,true))),
      topLevelParts:topLevelProfile(ast),
      functionHeads,dependencies,scopes,contextFunctionNames:[...contextFunctionNames].sort()
    };
  }
  return result;
}

function sameFileProfile(actual,expected) {
    const relatedNames=new Set([...Object.keys(expected?.dependencies||{}),...(actual.contextFunctionNames||[])]);
    // Pure declarations unrelated to a translation site cannot affect the
    // injected code. Executing statements, imports and protected dependencies
    // are still compared; do not require every unrelated helper's signature.
    const topLevelMatches=expected?.topLevelParts
      ? JSON.stringify(actual.topLevelParts.statements)===JSON.stringify(expected.topLevelParts.statements)
        && Object.entries(expected.topLevelParts.definitions).filter(([name])=>Object.hasOwn(expected.dependencies,name))
          .every(([name,signature])=>actual.topLevelParts.definitions[name]===signature)
      : actual.topLevel===expected?.topLevel;
    return expected && topLevelMatches
      && Object.entries(expected.functionHeads).filter(([name])=>relatedNames.has(name))
        .every(([name,signature])=>actual.functionHeads[name]===signature)
      && JSON.stringify(actual.dependencies)===JSON.stringify(expected.dependencies)
      && JSON.stringify(actual.scopes)===JSON.stringify(expected.scopes);
}

export function nativeCompatibility(files,{profiles=revisions,operations}={}) {
  try {
    const profile=nativeScopeProfile(files,{operations});
    const reviewed=profiles.find(revision=>WHITELIST.every(entry=>sameFileProfile(profile[entry],revision.files[entry])));
    if(reviewed) return {compatible:true,mode:'reviewed-scopes',revision:reviewed.name};
    // Translation hooks in separate files are independent. A release may combine
    // already reviewed files without having their exact historical combination.
    const reviewedFiles=Object.fromEntries(WHITELIST.map(entry=>[entry,profiles.find(revision=>sameFileProfile(profile[entry],revision.files[entry]))?.name]));
    const unreviewed=WHITELIST.filter(entry=>!reviewedFiles[entry]);
    if(unreviewed.length) return {compatible:false,mode:'reviewed-scopes',reason:'unreviewed_native_scopes',unreviewed};
    const names=[...new Set(Object.values(reviewedFiles))];
    return {compatible:true,mode:'reviewed-scopes',revision:names.length===1?names[0]:'reviewed-file-combination',reviewedFiles};
  } catch(error) {
    return {compatible:false,mode:'reviewed-scopes',reason:error.message};
  }
}
