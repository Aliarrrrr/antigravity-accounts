import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { extractNativeFrontend, inspectFrontendBindings } from '../src/frontend-bindings.mjs';
const { nativeBindingsExpression } = createRequire(import.meta.url)('../src/native-view.cjs');

test('unknown embedded archives and JavaScript cannot supply native bindings', () => {
  assert.throws(() => extractNativeFrontend(Buffer.from('not a native archive')), /unsupported_native_frontend_archive/);
  assert.throws(() => inspectFrontendBindings('var schema = {};'), /unsupported_native_frontend_contract/);
});

test('current native bindings survive compiler renaming and unrelated updates but reject ambiguous contracts', {
  skip: !process.env.ANTIGRAVITY_LANGUAGE_SERVER,
}, async () => {
  const source = extractNativeFrontend(await readFile(process.env.ANTIGRAVITY_LANGUAGE_SERVER));
  const bindings = inspectFrontendBindings(source);
  const names = [...new Set(Object.values(bindings.symbols))];
  const renamed = new Map(names.map((name, index) => [name, '_recompiled' + index]));
  const changed = source.replace(/[$A-Z_a-z][$\w]*/g, name => renamed.get(name) || name) + '\nvar unrelatedNewFeature = true;';
  const result = inspectFrontendBindings(changed);
  for (const [key, name] of Object.entries(bindings.symbols)) assert.equal(result.symbols[key], renamed.get(name));
  assert.notEqual(result.frontendSha256, bindings.frontendSha256);
  assert.match(nativeBindingsExpression(result), /_recompiled/);
  assert.throws(() => inspectFrontendBindings(source + '\n' + source), /unsupported_native_frontend_contract/);
  assert.throws(() => inspectFrontendBindings(source.replace('.actions.setUserStatus,', '.actions.setUnknownStatus,')), /unsupported_native_frontend_contract/);
  assert.throws(() => nativeBindingsExpression({ ...result, symbols: { ...result.symbols, decoder: 'kf;fetch("x")' } }), { code: 'native_view_bindings_invalid' });
});
