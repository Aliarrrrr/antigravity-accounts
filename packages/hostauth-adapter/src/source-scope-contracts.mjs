import { createHash } from 'node:crypto';
import acorn from './acorn.cjs';

const roots = Object.freeze({
  'dist/main.js': ['closeHostBridgeServer'],
  'dist/languageServer.js': ['startLanguageServer', 'startAndMonitorLanguageServer', 'killLanguageServer', 'prefetchShellEnv'],
  'dist/hostBridgeServer.js': ['startHostBridgeServer', 'buildRoutes'],
  'dist/utils.js': ['getLocalHubWindows', 'getHubWindows', 'getHubWebContents', 'isLocalHubTopFrame', 'resolveHubWindowFromWebContents', 'registerWindowWebContents', 'createWindow'],
  'dist/lsDefaults.js': ['baseLsArgs'],
});
const reserved = new Set(['hostauthAddon', 'hostauthSession', 'accountsUpdateHandoff']);
const hash = value => createHash('sha256').update(JSON.stringify(value)).digest('hex');
function normalize(value) {
  if (typeof value === 'bigint') return { bigint: value.toString() };
  if (Array.isArray(value)) return value.map(normalize);
  if (!value || typeof value !== 'object') return value;
  return Object.fromEntries(Object.keys(value).filter(key => !['start', 'end', 'loc', 'raw'].includes(key)).sort()
    .map(key => [key, normalize(value[key])]));
}
function visit(node, callback) {
  if (!node || typeof node !== 'object') return;
  if (node.type) callback(node);
  for (const value of Object.values(node)) {
    if (Array.isArray(value)) for (const child of value) visit(child, callback);
    else if (value && typeof value === 'object') visit(value, callback);
  }
}
function identifiers(node) {
  const names = new Set();
  visit(node, value => { if (value.type === 'Identifier') names.add(value.name); });
  return names;
}
function exportDeclaration(statement) {
  return statement.type === 'ExpressionStatement' && statement.expression.type === 'AssignmentExpression' &&
    statement.expression.left.type === 'MemberExpression' &&
    (statement.expression.left.object.name === 'exports' || statement.expression.left.object.name === 'module');
}

// Only function declarations can be omitted from the runtime comparison. All
// imports, variable initializers, classes, exported names and top-level calls
// remain exact AST contracts; adding a startup side effect is never accepted.
// Functions referenced by a protected function or executable top-level code
// are followed recursively, including their top-level variable dependencies.
export function sourceScopeProfile(source, filename) {
  if (typeof source !== 'string') throw new Error('source_scope_missing');
  const ast = acorn.parse(source, { ecmaVersion: 2022, sourceType: 'script' });
  const functions = new Map(), bindings = new Map();
  for (const statement of ast.body) {
    if (statement.type === 'FunctionDeclaration') {
      const name = statement.id?.name;
      if (!name || bindings.has(name) || reserved.has(name)) throw new Error('source_scope_binding_conflict');
      bindings.set(name, statement); functions.set(name, statement);
    } else if (statement.type === 'VariableDeclaration') {
      for (const declaration of statement.declarations) {
        if (declaration.id.type !== 'Identifier') continue;
        const name = declaration.id.name;
        if (bindings.has(name) || reserved.has(name)) throw new Error('source_scope_binding_conflict');
        bindings.set(name, declaration);
      }
    } else if (statement.type === 'ClassDeclaration' && statement.id) {
      if (bindings.has(statement.id.name) || reserved.has(statement.id.name)) throw new Error('source_scope_binding_conflict');
      bindings.set(statement.id.name, statement);
    }
  }
  const statements = ast.body.filter(statement => statement.type !== 'FunctionDeclaration');
  const required = new Set((roots[filename] || []).filter(name => functions.has(name)));
  for (const statement of statements) {
    if (exportDeclaration(statement)) continue;
    for (const name of identifiers(statement)) if (functions.has(name)) required.add(name);
  }
  const dependencies = {};
  const queue = [...required];
  for (let index = 0; index < queue.length; index++) {
    const name = queue[index], declaration = bindings.get(name);
    if (!declaration || Object.hasOwn(dependencies, name)) continue;
    dependencies[name] = hash(normalize(declaration));
    for (const reference of identifiers(declaration)) if (bindings.has(reference) && !Object.hasOwn(dependencies, reference)) queue.push(reference);
  }
  return {
    statements: statements.map(statement => hash(normalize(statement))),
    dependencies: Object.fromEntries(Object.entries(dependencies).sort(([left], [right]) => left.localeCompare(right))),
    functionNames: [...functions.keys()].sort(),
    identifiers: [...identifiers(ast)].sort(),
  };
}

export function matchesSourceScope(source, filename, expected) {
  try {
    if (!expected) return false;
    const actual = sourceScopeProfile(source, filename);
    if (JSON.stringify(actual.statements) !== JSON.stringify(expected.statements) ||
        JSON.stringify(actual.dependencies) !== JSON.stringify(expected.dependencies)) return false;
    const expectedNames = new Set(expected.functionNames);
    // A new declaration must not shadow a previously used global/property or
    // one of the adapter's injected bindings, even when its body is uncalled.
    return expected.functionNames.every(name => actual.functionNames.includes(name)) &&
      actual.functionNames.every(name => expectedNames.has(name) || !expected.identifiers.includes(name));
  } catch { return false; }
}
