'use strict';

const { ComponentError } = require('./component-client.cjs');

// Executed in the inspected native main world. These are the native protobuf decoder,
// Redux action and selectors, rather than a second copy of model definitions.
function updateNativeView(rawStatus, requireExplicitSelection = false, phase = 'refresh', bindings) {
  const picksKey = Symbol.for('antigravity.accounts.native-model-picks.v1');
  const captureStoreKey = Symbol.for('antigravity.accounts.native-model-capture-store.v1');
  if (!['capture', 'refresh', 'clear'].includes(phase)) return { errorCode: 'native_view_phase_invalid' };
  if (phase === 'clear') {
    const capturedStore = document[captureStoreKey];
    if (capturedStore) {
      const capturedPicks = capturedStore[picksKey];
      if (capturedPicks) {
        if (typeof capturedPicks.unsubscribe !== 'function') return { errorCode: 'native_view_listener_unavailable' };
        capturedPicks.unsubscribe();
        delete capturedStore[picksKey];
      }
      delete document[captureStoreKey];
    }
    return { cleared: true };
  }
  const schema = bindings?.schema ?? (typeof B === 'object' ? B : undefined);
  const Ee = bindings?.decoder ?? (typeof Ve === 'function' ? Ve : undefined);
  const Ms = bindings?.setStatus ?? (typeof ft === 'function' ? ft : undefined);
  const CCa = bindings?.modelPicked ?? (typeof cCa === 'function' ? cCa : undefined);
  const Lla = bindings?.addListener ?? (typeof Ula === 'function' ? Ula : undefined);
  const lma = bindings?.filter ?? (typeof Dma === 'function' ? Dma : undefined);
  const kka = bindings?.feature ?? (typeof qka === 'object' ? qka : undefined);
  const redux = bindings?.redux ?? (typeof gl === 'object' ? gl : undefined);
  const Ks = bindings?.catalog ?? (typeof Ys === 'function' ? Ys : undefined);
  const Ls = bindings?.resolveModel ?? (typeof Zs === 'function' ? Zs : undefined);
  const Sk = bindings?.findModel ?? (typeof Cma === 'function' ? Cma : undefined);
  const equals = bindings?.equals;
  if (typeof schema !== 'object' || schema.UserStatusSchema?.typeName !== 'exa.codeium_common_pb.UserStatus' ||
      typeof Ee !== 'function' || typeof Ms !== 'function' || Ms.type !== 'userStatus/setUserStatus' ||
      typeof CCa !== 'function' || CCa.type !== 'modelSelection/modelPicked' ||
      typeof Lla !== 'function' || Lla.type !== 'listenerMiddleware/add' ||
      typeof lma !== 'function' || typeof redux !== 'object' || typeof kka !== 'object' ||
      bindings && typeof equals !== 'function' ||
      typeof Ks !== 'function' || typeof Ls !== 'function' || typeof Sk !== 'function') return { updated: false, errorCode: 'native_view_symbols_changed' };
  const excluded = 'pre,code,textarea,[contenteditable],[data-message-role],[data-message-id],.markdown,.prose,[data-user-content],iframe,[hidden],[aria-hidden="true"]';
  const triggers = [...document.querySelectorAll('button[data-testid="model-selector-trigger"]')]
    .filter(button => button.isConnected && !button.closest(excluded));
  if (triggers.length !== 1) return { updated: false, errorCode: 'native_model_selector_unavailable' };
  const key = Object.keys(triggers[0]).find(name => name.startsWith('__reactFiber$'));
  let fiber = key && triggers[0][key], store, feature;
  const visited = new Set();
  // Read only the model selector's ancestors. Other roots, nested unrelated
  // Providers and conversation content cannot select this view's store.
  while (fiber && (!store || !feature)) {
    if (visited.has(fiber) || visited.size >= 256) return { updated: false, errorCode: 'native_view_provider_unavailable' };
    visited.add(fiber);
    const value = fiber.memoizedProps?.value;
    if (!store && (fiber.type === redux || fiber.type === redux.Provider) && value?.store &&
        typeof value.store.dispatch === 'function' && typeof value.store.getState === 'function') store = value.store;
    if (!feature && (fiber.type === kka || fiber.type === kka.Provider) && typeof value?.get === 'function') feature = value;
    fiber = fiber.return;
  }
  if (!store || !feature) return { updated: false, errorCode: 'native_view_provider_unavailable' };
  const match = location.pathname.match(/^\/c\/([^/]+)\/?$/);
  const conversationId = match?.[1];
  if (conversationId?.length > 256) return { errorCode: 'native_view_context_changed' };
  const selected = state => (conversationId !== undefined && state.modelSelection.byConversation?.[conversationId]) || state.modelSelection.default;
  const copySelection = selection => {
    if (selection?.model !== undefined && !Number.isSafeInteger(selection.model) ||
        selection?.contextWindow !== undefined && (typeof selection.contextWindow !== 'string' || selection.contextWindow.length > 32) ||
        selection?.customLabel !== undefined && (typeof selection.customLabel !== 'string' || selection.customLabel.length > 512)) return undefined;
    return { model: selection?.model, contextWindow: selection?.contextWindow, customLabel: selection?.customLabel };
  };
  const sameConcreteModel = (left, right) => Boolean(left && right && !left.isCustom && !right.isCustom &&
    !left.modelAlias && !right.modelAlias && Number.isSafeInteger(left.value) && left.value !== schema.Model.UNSPECIFIED && left.value === right.value);
  const before = store.getState();
  if (before?.userStatus?.$typeName !== schema.UserStatusSchema.typeName || !before.modelSelection) return { updated: false, errorCode: 'native_view_status_invalid' };
  let picks = store[picksKey];
  if (phase === 'capture') {
    if (picks || document[captureStoreKey]) return { errorCode: 'native_view_capture_in_progress' };
    const selection = copySelection(selected(before));
    if (!selection) return { errorCode: 'native_view_selection_invalid' };
    const previous = Ls(Ks(before), selected(before));
    picks = { count: 0, suppress: false, lastPick: undefined, capture: {
      conversationId, selection,
      model: sameConcreteModel(previous, previous) && selection.model === previous.value &&
        selection.model !== schema.Model.GOOGLE_GEMINI_INTERNAL_BYOM ? previous.value : undefined,
      pickCount: 0, requiredAfter: null,
    } };
    const unsubscribe = store.dispatch(Lla({ type: CCa.type, effect(action) {
      if (picks.suppress || picks.count >= Number.MAX_SAFE_INTEGER) return;
      const payload = action?.payload;
      if (payload?.conversationId !== picks.capture.conversationId) return;
      picks.count++;
      // No trajectory pick data, queued message IDs, user status or account
      // information is retained. Only the actual native model action is kept.
      picks.lastPick = { count: picks.count, conversationId: picks.capture.conversationId,
        selection: copySelection(payload) };
    } }));
    if (typeof unsubscribe !== 'function') return { updated: false, errorCode: 'native_view_listener_unavailable' };
    picks.unsubscribe = unsubscribe;
    try {
      Object.defineProperty(store, picksKey, { value: picks, configurable: true });
      Object.defineProperty(document, captureStoreKey, { value: store, configurable: true });
    } catch {
      unsubscribe(); delete store[picksKey];
      return { errorCode: 'native_view_capture_unavailable' };
    }
    return { captured: true };
  }
  if (!picks?.capture || document[captureStoreKey] !== store) return { updated: false, errorCode: 'native_view_capture_missing' };
  if (!Number.isSafeInteger(picks.count) || picks.count < 0 ||
      picks.capture.requiredAfter !== null && (!Number.isSafeInteger(picks.capture.requiredAfter) || picks.capture.requiredAfter < 0) ||
      typeof picks.unsubscribe !== 'function') return { updated: false, errorCode: 'native_view_listener_unavailable' };
  const captured = picks.capture;
  if (captured.conversationId !== conversationId) return { updated: false, errorCode: 'native_view_context_changed' };
  const userPick = picks.lastPick?.count > captured.pickCount && picks.lastPick.conversationId === conversationId ? picks.lastPick : undefined;
  const expectedSelection = userPick ? userPick.selection : captured.selection;
  const expectedModel = userPick ? userPick.selection?.model : captured.model;
  const expectedContextWindow = expectedSelection?.contextWindow;
  const parsed = lma(Ee(schema.UserStatusSchema, rawStatus), feature.get('models')?.geminiModelsOnly === true);
  if (parsed?.$typeName !== schema.UserStatusSchema.typeName) return { updated: false, errorCode: 'native_view_status_invalid' };
  store.dispatch(Ms(parsed));
  let after = store.getState();
  if (after.userStatus !== parsed && !(equals && equals(schema.UserStatusSchema, after.userStatus, parsed))) return { updated: false, errorCode: 'native_view_update_failed' };
  let catalog = Ks(after), actualSelection = selected(after), next = Ls(catalog, actualSelection);
  const available = Number.isSafeInteger(expectedModel) && expectedModel !== schema.Model.UNSPECIFIED && expectedModel !== schema.Model.GOOGLE_GEMINI_INTERNAL_BYOM
    ? Sk(expectedModel, catalog.visibleModelConfigs) : undefined;
  const expected = available && !available.isCustom && !available.modelAlias ? { value: expectedModel } : undefined;
  // Use the model captured before credentials change, or a later actual native
  // modelPicked. A status poll may already have replaced the Redux catalog and
  // default before this call; its fallback is never the captured selection.
  if (expected && (!sameConcreteModel(expected, next) || actualSelection?.model !== expectedModel || actualSelection?.contextWindow !== expectedContextWindow)) {
    picks.suppress = true;
    try { store.dispatch(CCa({ ...(conversationId !== undefined ? { conversationId } : {}), model: expectedModel,
      contextWindow: expectedContextWindow, customLabel: expectedSelection?.customLabel })); }
    finally { picks.suppress = false; }
    after = store.getState(); catalog = Ks(after); actualSelection = selected(after); next = Ls(catalog, actualSelection);
  }
  // Each window records its own missing selection. The component's aggregate
  // retry flag must not require a model pick in a window that kept its model.
  const needsExplicit = captured.requiredAfter !== null;
  const nativePickObserved = userPick && userPick.count > (captured.requiredAfter ?? captured.pickCount);
  const selectionReady = Boolean(expected && (!needsExplicit || nativePickObserved) && sameConcreteModel(expected, next) &&
    actualSelection?.model === expectedModel && actualSelection?.contextWindow === expectedContextWindow);
  if (!selectionReady && captured.requiredAfter === null) captured.requiredAfter = picks.count;
  return { updated: true, selectionReady };
}

function nativeBindingsExpression(bindings) {
  const keys = ['schema', 'decoder', 'equals', 'setStatus', 'modelPicked', 'addListener', 'filter', 'catalog', 'resolveModel', 'findModel', 'feature', 'redux'];
  if (!bindings || bindings.schemaVersion !== 1 || !bindings.symbols || Object.keys(bindings.symbols).length !== keys.length ||
      keys.some(key => !/^[$A-Z_a-z][$\w]*$/.test(bindings.symbols[key]))) throw new ComponentError('native_view_bindings_invalid');
  return '{' + keys.map(key => JSON.stringify(key) + ':' + bindings.symbols[key]).join(',') + '}';
}

async function refreshNativeViews({ electron, origin, userStatus, requireExplicitSelection = false, phase = 'refresh', bindings }) {
  const windows = electron?.BrowserWindow?.getAllWindows?.().filter(window => {
    if (window.isDestroyed()) return false;
    try { return new URL(window.webContents.getURL()).origin === origin; } catch { return false; }
  }) || [];
  if (!['capture', 'refresh', 'clear'].includes(phase)) throw new ComponentError('native_view_phase_invalid');
  if (!windows.length) {
    if (phase === 'clear') return { cleared: true };
    throw new ComponentError('native_view_unavailable');
  }
  if (phase === 'refresh' && (!userStatus || typeof userStatus !== 'object')) throw new ComponentError('native_view_unavailable');
  const data = JSON.stringify(JSON.stringify(phase === 'refresh' ? userStatus : null));
  const bindingSource = bindings ? nativeBindingsExpression(bindings) : 'undefined';
  const source = `(${updateNativeView.toString()})(JSON.parse(${data}), ${requireExplicitSelection === true}, ${JSON.stringify(phase)}, ${bindingSource})`;
  let results;
  try { results = await Promise.all(windows.map(window => window.webContents.executeJavaScript(source))); }
  catch { throw new ComponentError('native_view_execution_failed'); }
  const successField = phase === 'capture' ? 'captured' : phase === 'clear' ? 'cleared' : 'updated';
  if (results.some(result => result?.[successField] !== true)) {
    const code = results.find(result => result?.[successField] !== true)?.errorCode;
    throw new ComponentError(/^native_[a-z0-9_]{1,56}$/.test(code || '') ? code : 'native_view_unavailable');
  }
  return phase === 'capture' ? { captured: true } : phase === 'clear' ? { cleared: true }
    : { selectionReady: results.every(result => result.selectionReady === true) };
}

module.exports = { updateNativeView, refreshNativeViews, nativeBindingsExpression };
