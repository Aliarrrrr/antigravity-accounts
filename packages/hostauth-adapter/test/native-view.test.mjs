import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { createRequire } from 'node:module';
const { updateNativeView, refreshNativeViews } = createRequire(import.meta.url)('../src/native-view.cjs');

const config = (value, label = `Model ${value}`) => ({ value, label });
function harness({ selected = 2, path = '/c/conversation', geminiOnly = false, overrideModel, contextWindow, ignoreRestore = false } = {}) {
  const schema = { typeName: 'exa.codeium_common_pb.UserStatus' };
  let state = { userStatus: { $typeName: schema.typeName, models: [config(1), config(2)] },
    modelSelection: { default: { model: 1, contextWindow }, byConversation: { conversation: { model: selected, contextWindow } } },
    draft: 'Keep the existing draft', navigation: { scroll: 456 } };
  const actions = [], decoded = [], modelListeners = new Set();
  const store = { getState: () => state, dispatch(action) {
    if (action.type === 'listenerMiddleware/add') {
      assert.equal(action.payload.type, 'modelSelection/modelPicked');
      modelListeners.add(action.payload.effect);
      return () => modelListeners.delete(action.payload.effect);
    }
    actions.push(action);
    if (action.type === 'userStatus/setUserStatus') {
      state = { ...state, userStatus: action.payload };
      // Native RCa may synchronously dispatch defaultOverrideApplied when the
      // incoming account supplies a new default model configuration version.
      if (overrideModel !== undefined) state = { ...state, modelSelection: { ...state.modelSelection, default: { model: overrideModel } } };
    } else if (action.type === 'modelSelection/modelPicked' && !ignoreRestore) {
      const { conversationId, model, customLabel, contextWindow: window } = action.payload;
      const selection = { model, customLabel, contextWindow: window };
      state = { ...state, modelSelection: { ...state.modelSelection, default: selection,
        byConversation: conversationId === undefined ? state.modelSelection.byConversation : { ...state.modelSelection.byConversation, [conversationId]: selection } } };
    }
    if (action.type === 'modelSelection/modelPicked') for (const listener of modelListeners) listener(action);
  } };
  const gl = {}, kka = {}; gl.Provider = gl; kka.Provider = kka;
  const features = { get(name) { assert.equal(name, 'models'); return { geminiModelsOnly: geminiOnly }; } };
  const provider = { type: gl, memoizedProps: { value: { store } } };
  const featureProvider = { type: kka, memoizedProps: { value: features }, return: provider };
  const fiber = { type: 'button', return: featureProvider };
  const trigger = { isConnected: true, closest: () => null, '__reactFiber$native': fiber };
  const Ms = payload => ({ type: 'userStatus/setUserStatus', payload }); Ms.type = 'userStatus/setUserStatus';
  const CCa = payload => ({ type: 'modelSelection/modelPicked', payload }); CCa.type = 'modelSelection/modelPicked';
  const Lla = payload => ({ type: 'listenerMiddleware/add', payload }); Lla.type = 'listenerMiddleware/add';
  const context = {
    B: { UserStatusSchema: schema, Model: { UNSPECIFIED: 0, GOOGLE_GEMINI_INTERNAL_BYOM: 99 } },
    Ve(s, raw) { assert.equal(s, schema); decoded.push(raw); return { ...raw, $typeName: s.typeName }; },
    ft: Ms, cCa: CCa, Ula: Lla, gl, qka: kka, document: { querySelectorAll(selector) {
      assert.equal(selector, 'button[data-testid="model-selector-trigger"]'); return [trigger];
    } }, location: { pathname: path },
    Dma(value, only) { return only ? { ...value, models: value.models.filter(m => m.label.includes('Gemini')) } : value; },
    Ys(value) { return { visibleModelConfigs: value.userStatus.models, customModels: [] }; },
    Cma(model, options) { return options.find(option => option.value === model); },
    Zs(catalog, selection) { return catalog.visibleModelConfigs.find(option => option.value === selection?.model) || catalog.visibleModelConfigs[0]; },
  };
  const execute = (raw, explicit, phase) => vm.runInNewContext(`(${updateNativeView.toString()})(raw, explicit, phase)`, { ...context, raw, explicit, phase });
  return { context, trigger, fiber, provider, featureProvider, actions, decoded, store,
    capture() { return execute(null, false, 'capture'); },
    clear() { return execute(null, false, 'clear'); },
    refresh(raw, explicit = false) { return execute(raw, explicit, 'refresh'); },
    run(raw, explicit = false) {
      if (!store[Symbol.for('antigravity.accounts.native-model-picks.v1')]?.capture) {
        const captured = execute(null, false, 'capture');
        if (captured.captured !== true) return { updated: false, errorCode: captured.errorCode };
      }
      return execute(raw, explicit, 'refresh');
    }, choose(model) { store.dispatch(CCa({ ...(path === '/' ? {} : { conversationId: 'conversation' }), model })); },
    overwriteSelection(model) { state.modelSelection.byConversation.conversation.model = model; },
    listenerCount() { return modelListeners.size; } };
}

test('native action updates model options with the native decoder and leaves drafts and navigation intact', () => {
  const h = harness(), before = h.store.getState();
  const result = h.run({ email: 'fake@example.invalid', models: [config(2), config(3)] });
  assert.equal(result.updated, true); assert.equal(result.selectionReady, true);
  assert.equal(h.actions[0].type, 'userStatus/setUserStatus'); assert.equal(h.decoded.length, 1);
  assert.deepEqual(h.store.getState().userStatus.models.map(m => m.value), [2, 3]);
  assert.equal(h.store.getState().modelSelection, before.modelSelection);
  assert.equal(h.store.getState().navigation, before.navigation);
  assert.equal(h.store.getState().draft, before.draft);
});

test('an unavailable original model cannot silently become the native fallback', () => {
  const h = harness();
  const next = { models: [config(1), config(3)] };
  assert.equal(h.run(next).selectionReady, false);
  assert.equal(h.run(next, true).selectionReady, false, 'reconfirming without a native choice is still blocked');
  h.choose(3);
  assert.equal(h.run(next, true).selectionReady, true);
});

test('an actual native modelPicked action is required after the view becomes blocked', () => {
  const h = harness();
  const next = { models: [config(1), config(3)] };
  assert.equal(h.run(next).selectionReady, false);
  h.overwriteSelection(3);
  assert.equal(h.run(next, true).selectionReady, false, 'Opening a submenu or a state overwrite is not a native model pick');
  h.choose(3);
  assert.equal(h.run(next, true).selectionReady, true);
  assert.equal(h.listenerCount(), 1, 'Repeated refreshes reuse the same native action listener');
});

test('a native account default override cannot silently replace the actual new-conversation model', () => {
  const h = harness({ path: '/', overrideModel: 3, contextWindow: '1M' });
  const result = h.run({ models: [config(1), config(3)] });
  assert.equal(result.selectionReady, true);
  assert.equal(h.store.getState().modelSelection.default.model, 1, 'The real post-dispatch model must remain the previous model');
  assert.equal(h.store.getState().modelSelection.default.contextWindow, '1M');
  assert.deepEqual(h.actions.map(action => action.type), ['userStatus/setUserStatus', 'modelSelection/modelPicked']);
  assert.equal(h.store[Symbol.for('antigravity.accounts.native-model-picks.v1')].count, 0, 'Adapter restoration is not counted as user model selection');
});

test('the view stays paused if the native restoration action does not preserve the actual model', () => {
  const h = harness({ path: '/', overrideModel: 3, ignoreRestore: true });
  assert.equal(h.run({ models: [config(1), config(3)] }).selectionReady, false);
  assert.equal(h.store.getState().modelSelection.default.model, 3);
});

test('new conversations use the existing default and native Gemini-only filtering', () => {
  const h = harness({ path: '/', geminiOnly: true });
  const result = h.run({ models: [config(1, 'Gemini'), config(2, 'Other')] });
  assert.equal(result.selectionReady, true);
  assert.deepEqual(h.store.getState().userStatus.models.map(m => m.label), ['Gemini']);
});

test('an unresolvable custom model is not accepted through its normal-model fallback', () => {
  const h = harness({ selected: 99 });
  assert.equal(h.run({ models: [config(1), config(2)] }).selectionReady, false);
  assert.deepEqual(h.actions.map(action => action.type), ['userStatus/setUserStatus']);
});

test('an already missing native selection cannot be captured as the resolver fallback', () => {
  const h = harness({ selected: 88 });
  assert.equal(h.capture().captured, true);
  assert.equal(h.refresh({ models: [config(1), config(2)] }).selectionReady, false);
  assert.equal(h.store[Symbol.for('antigravity.accounts.native-model-picks.v1')].capture.model, undefined);
  h.choose(2);
  assert.equal(h.refresh({ models: [config(1), config(2)] }, true).selectionReady, true);
});

test('unknown native globals, schema or provider trees do not dispatch', () => {
  for (const mutate of [
    h => { h.context.B.UserStatusSchema.typeName = 'unknown'; },
    h => { h.context.ft.type = 'unknown'; },
    h => { h.context.cCa.type = 'unknown'; },
    h => { h.context.Ula.type = 'unknown'; },
    h => { delete h.context.Ve; },
    h => { delete h.trigger.__reactFiber$native; },
  ]) {
    const h = harness(); mutate(h);
    assert.equal(h.run({ models: [] }).updated, false); assert.equal(h.actions.length, 0);
  }
});

test('only the selector ancestor providers are used, with unrelated branches and outer providers ignored', () => {
  const h = harness();
  const otherStore = { dispatch() { assert.fail('Unrelated store'); }, getState() { assert.fail('Unrelated state'); } };
  h.provider.return = { type: h.context.gl, memoizedProps: { value: { store: otherStore } } };
  Object.defineProperty(h.fiber, 'child', { get() { assert.fail('Do not traverse model or conversation children'); } });
  Object.defineProperty(h.fiber, 'sibling', { get() { assert.fail('Do not traverse unrelated providers'); } });
  assert.equal(h.run({ models: [config(2)] }).selectionReady, true);
});

test('missing, ambiguous, protected or cyclic selector providers fail without dispatch', () => {
  for (const mutate of [
    h => { h.context.document.querySelectorAll = () => []; },
    h => { h.context.document.querySelectorAll = () => [h.trigger, h.trigger]; },
    h => { h.trigger.closest = () => ({}); },
    h => { h.featureProvider.return = h.fiber; },
  ]) {
    const h = harness(); mutate(h);
    const result = h.run({ models: [] });
    assert.equal(result.updated, false); assert.match(result.errorCode, /^native_(model_selector|view_provider)_unavailable$/);
    assert.equal(h.actions.length, 0);
  }
});

test('only native windows receive public status; payload strings remain data', async () => {
  const h = harness();
  const calls = [], raw = { models: [config(2)], email: 'fake@example.invalid', label: '";throw new Error("unsafe");//' };
  const window = { isDestroyed: () => false, webContents: {
    getURL: () => 'https://127.0.0.1:12345/c/conversation',
    async executeJavaScript(source) { calls.push(source); return vm.runInNewContext(source, h.context); },
  } };
  const other = { isDestroyed: () => false, webContents: { getURL: () => 'https://example.invalid/', executeJavaScript() { assert.fail('Foreign window'); } } };
  const electron = { BrowserWindow: { getAllWindows: () => [other, window] } };
  assert.equal((await refreshNativeViews({ electron, origin: 'https://127.0.0.1:12345', phase: 'capture' })).captured, true);
  assert.equal((await refreshNativeViews({ electron, origin: 'https://127.0.0.1:12345', userStatus: raw })).selectionReady, true);
  assert.equal(calls.length, 2);
  assert.equal(h.store.getState().userStatus.label, raw.label);
  await assert.rejects(refreshNativeViews({ electron, origin: 'https://127.0.0.1:34567', userStatus: raw }), { code: 'native_view_unavailable' });
  assert.equal((await refreshNativeViews({ electron, origin: 'https://127.0.0.1:12345', phase: 'clear' })).cleared, true);
});

test('a native status poll before confirmation cannot turn the missing old model into an accepted fallback', () => {
  const h = harness();
  assert.equal(h.capture().captured, true);
  const raw = { models: [config(3)] };
  h.store.dispatch(h.context.ft({ ...raw, $typeName: h.context.B.UserStatusSchema.typeName }));
  assert.equal(h.store.getState().modelSelection.byConversation.conversation.model, 2);
  assert.equal(h.refresh(raw).selectionReady, false);
  assert.equal(h.refresh(raw, true).selectionReady, false);
  assert.equal(h.actions.filter(action => action.type === 'modelSelection/modelPicked').length, 0);
  h.choose(3);
  assert.equal(h.refresh(raw, true).selectionReady, true);
});

test('a native status poll may override the default before confirmation but the captured available model is restored', () => {
  const h = harness({ path: '/', overrideModel: 3 });
  assert.equal(h.capture().captured, true);
  const raw = { models: [config(1), config(3)] };
  h.store.dispatch(h.context.ft({ ...raw, $typeName: h.context.B.UserStatusSchema.typeName }));
  assert.equal(h.store.getState().modelSelection.default.model, 3);
  assert.equal(h.refresh(raw).selectionReady, true);
  assert.equal(h.store.getState().modelSelection.default.model, 1);
  assert.equal(h.store[Symbol.for('antigravity.accounts.native-model-picks.v1')].count, 0);
});

test('a real model pick after capture takes precedence over the old model and a later automatic default', () => {
  const h = harness({ path: '/', overrideModel: 4 });
  assert.equal(h.capture().captured, true);
  const raw = { models: [config(1), config(3), config(4)] };
  h.store.dispatch(h.context.ft({ ...raw, $typeName: h.context.B.UserStatusSchema.typeName }));
  h.choose(3);
  assert.equal(h.refresh(raw).selectionReady, true);
  assert.equal(h.store.getState().modelSelection.default.model, 3);
  assert.equal(h.store[Symbol.for('antigravity.accounts.native-model-picks.v1')].count, 1);
});

test('a different conversation model pick cannot acknowledge a blocked selection', () => {
  const h = harness();
  const raw = { models: [config(3)] };
  assert.equal(h.run(raw).selectionReady, false);
  h.store.dispatch(h.context.cCa({ conversationId: 'different-conversation', model: 3 }));
  assert.equal(h.refresh(raw, true).selectionReady, false);
  assert.equal(h.store[Symbol.for('antigravity.accounts.native-model-picks.v1')].count, 0);
});

test('refresh requires a capture, refuses a changed conversation, and clear removes only its listener and model record', () => {
  const h = harness();
  assert.equal(h.refresh({ models: [config(2)] }).errorCode, 'native_view_capture_missing');
  assert.equal(h.capture().captured, true);
  assert.equal(h.capture().errorCode, 'native_view_capture_in_progress');
  h.context.location.pathname = '/c/other';
  assert.equal(h.refresh({ models: [config(2)] }).errorCode, 'native_view_context_changed');
  h.context.document.querySelectorAll = () => [];
  assert.equal(h.clear().cleared, true, 'A hidden or removed selector must not prevent clearing the owned capture');
  assert.equal(h.listenerCount(), 0);
  assert.equal(h.store[Symbol.for('antigravity.accounts.native-model-picks.v1')], undefined);
  assert.equal(h.clear().cleared, true);
});

test('a partially failed multi-window capture can be cleared without retaining model records', async () => {
  const one = harness(), two = harness();
  two.context.document.querySelectorAll = () => [];
  const windows = [one, two].map(h => ({ isDestroyed: () => false, webContents: {
    getURL: () => 'https://127.0.0.1:12345/',
    executeJavaScript: async source => vm.runInNewContext(source, h.context),
  } }));
  const electron = { BrowserWindow: { getAllWindows: () => windows } };
  await assert.rejects(refreshNativeViews({ electron, origin: 'https://127.0.0.1:12345', phase: 'capture' }), { code: 'native_model_selector_unavailable' });
  assert.equal(one.listenerCount(), 1);
  assert.equal((await refreshNativeViews({ electron, origin: 'https://127.0.0.1:12345', phase: 'clear' })).cleared, true);
  assert.equal(one.listenerCount(), 0); assert.equal(two.listenerCount(), 0);
});

test('reconfirming one window does not demand a new model pick in another window whose model remains available', async () => {
  const kept = harness({ selected: 2 }), missing = harness({ selected: 1 });
  const windows = [kept, missing].map(h => ({ isDestroyed: () => false, webContents: {
    getURL: () => 'https://127.0.0.1:12345/c/conversation',
    executeJavaScript: async source => vm.runInNewContext(source, h.context),
  } }));
  const electron = { BrowserWindow: { getAllWindows: () => windows } };
  const options = { electron, origin: 'https://127.0.0.1:12345' };
  await refreshNativeViews({ ...options, phase: 'capture' });
  const userStatus = { models: [config(2), config(3)] };
  assert.equal((await refreshNativeViews({ ...options, userStatus })).selectionReady, false);
  missing.choose(3);
  assert.equal((await refreshNativeViews({ ...options, userStatus, requireExplicitSelection: true })).selectionReady, true);
  assert.equal(kept.store[Symbol.for('antigravity.accounts.native-model-picks.v1')].count, 0);
  assert.equal(missing.store[Symbol.for('antigravity.accounts.native-model-picks.v1')].count, 1);
  await refreshNativeViews({ ...options, phase: 'clear' });
});
