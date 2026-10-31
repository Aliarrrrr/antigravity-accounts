import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import { installModelMenuEntry, modelMenuEntrySource } from '../src/model-menu-entry.mjs';

let JSDOM;
for (const anchor of [import.meta.url, process.env.ACCOUNTS_DOM_TEST_PACKAGE,
  new URL('../../../../../tools/antigravity-zh-cn/package.json', import.meta.url)]) {
  if (!anchor) continue;
  try { ({ JSDOM } = createRequire(anchor)('jsdom')); break; } catch { /* No download in offline checks. */ }
}
const settle = () => new Promise(setImmediate);
const html = '<button data-testid="model-selector-trigger" aria-expanded="true">Current model</button>' +
  '<div role="menu"><div data-testid="model-selector-panel"><div class="models"><button role="menuitem" data-testid="model-selector-item">Native option</button></div><div class="usage"><button role="menuitem">View Usage</button></div></div></div>';
const initial = () => ({ status: {
  configured: true, mode: 'native-provider', runtimeStarted: true, primaryAccountId: 'a', selectedAccountId: 'a',
  liveSwitchSupported: true, login: { state: 'idle' }, switch: { state: 'active', cancellable: false },
}, accounts: [
  { id: 'a', label: 'Work', email: 'work@example.invalid', selected: true },
  { id: 'b', label: 'Personal', email: 'personal@example.invalid', selected: false },
] });

function harness({ source = html, onRequest } = {}) {
  const dom = new JSDOM(source, { url: 'https://127.0.0.1:43123/', pretendToBeVisual: true });
  const { window } = dom, { document } = window;
  const calls = [], listeners = new WeakMap(), timers = new Map();
  let state = initial(), timerId = 0;
  const original = window.EventTarget.prototype.addEventListener;
  window.EventTarget.prototype.addEventListener = function (type, callback, ...rest) {
    if (!listeners.has(this)) listeners.set(this, new Map());
    const handlers = listeners.get(this);
    handlers.set(type, [...handlers.get(type) || [], callback]);
    return original.call(this, type, callback, ...rest);
  };
  window.setTimeout = callback => { const id = ++timerId; timers.set(id, callback); return id; };
  window.clearTimeout = id => timers.delete(id);
  const request = async (method, params) => {
    calls.push({ method, params });
    if (onRequest) return onRequest(method, params, state);
    if (method === 'getState') return state;
    if (method === 'selectAccount') { state.status.selectedAccountId = params.id; return { selected: true }; }
    return {};
  };
  const controller = installModelMenuEntry({ window, document, request });
  return { dom, window, document, calls, timers, request, controller,
    state: () => state, setState(next) { state = next; },
    // Call the registered listener with a native-event-shaped object. The page
    // cannot construct isTrusted=true DOM events; no production test API exists.
    trustedClick(button) {
      for (const listener of listeners.get(button)?.get('click') || [])
        listener.call(button, { isTrusted: true, currentTarget: button, preventDefault() {}, stopPropagation() {} });
    },
    trustedKey(button, key) {
      for (const listener of listeners.get(button)?.get('keydown') || [])
        listener.call(button, { isTrusted: true, currentTarget: button, key, repeat: false, preventDefault() {}, stopPropagation() {} });
    },
    trustedNativePick(option) {
      for (const listener of listeners.get(document)?.get('click') || [])
        listener.call(document, { type: 'click', isTrusted: true, target: option });
    },
    tick() { const pending = [...timers.values()]; timers.clear(); for (const callback of pending) callback(); },
    close() { controller.stop(); dom.window.close(); },
  };
}
const current = document => document.querySelector('[data-testid="antigravity-accounts-current-group"]');
const groupButton = (document, id = 'b') => document.querySelector(`[data-testid="antigravity-accounts-collapsed-group"] button[data-account-id="${id}"]`);

test('unavailable model requires a trusted choice in the native model list', { skip: !JSDOM }, async () => {
  const h = harness();
  try {
    h.state().status.switch = { state: 'blocked', targetAccountId: 'a', errorCode: 'native_model_selection_required', cancellable: false };
    await settle();
    assert.match(h.document.querySelector('[role="status"]').textContent, /原模型不适用/);
    const option = h.document.querySelector('[data-testid="model-selector-item"]');
    option.click(); await settle();
    assert.equal(h.calls.some(c => c.method === 'confirmModelSelection'), false);
    h.trustedNativePick(option); await settle();
    assert.equal(h.calls.filter(c => c.method === 'confirmModelSelection').length, 1);
  } finally { h.close(); }
});

test('native effort, context-window and custom model choices include portal submenus', { skip: !JSDOM }, async () => {
  for (const id of ['model-selector-effort-group', 'model-selector-effort-option', 'model-selector-context-window-option', 'model-selector-custom-item']) {
    const h = harness();
    try {
      h.state().status.switch = { state: 'blocked', targetAccountId: 'a', errorCode: 'native_model_selection_required', cancellable: false };
      await settle();
      const portal = h.document.createElement('div'); portal.setAttribute('role', 'menu');
      const option = h.document.createElement('span'); option.setAttribute('data-testid', id); option.textContent = 'Native effort choice';
      portal.append(option); h.document.body.append(portal);
      option.click(); await settle();
      assert.equal(h.calls.some(c => c.method === 'confirmModelSelection'), false);
      h.trustedNativePick(option); await settle();
      assert.equal(h.calls.filter(c => c.method === 'confirmModelSelection').length, 1, id);
    } finally { h.close(); }
  }
});

test('serialized preload is frame-restricted and keeps account IPC private to the isolated world', () => {
  const source = modelMenuEntrySource();
  assert.doesNotThrow(() => new vm.Script(source));
  assert(source.includes("ipcRenderer.invoke('antigravity-accounts:model-menu', { method, params })"));
  assert(!/contextBridge|requestHeaders|cookie|localStorage|sessionStorage|openAccounts/.test(source));
  for (const [isMainFrame, protocol, hostname] of [[false, 'https:', '127.0.0.1'], [true, 'http:', '127.0.0.1'], [true, 'https:', 'example.com']]) {
    const window = {}; window.top = window;
    vm.runInNewContext(source, { process: { isMainFrame }, window, location: { protocol, hostname },
      require() { assert.fail('Unrelated pages must not request Electron IPC'); } });
  }
});

test('accounts surround the actual model list without cloning, moving, or replacing native nodes', { skip: !JSDOM }, async () => {
  const h = harness(), { document } = h;
  try {
    const panel = document.querySelector('[data-testid="model-selector-panel"]');
    const models = panel.querySelector('.models'), usage = panel.querySelector('.usage'), native = models.firstElementChild;
    let selected = 0; native.addEventListener('click', () => selected++);
    await settle();
    assert.equal(installModelMenuEntry({ window: h.window, document, request: h.request }), h.controller);
    assert.match(current(document).textContent, /Work · 正在使用/);
    assert.match(groupButton(document).textContent, /Personal/);
    assert.equal(models.parentElement, panel); assert.equal(usage.parentElement, panel);
    assert.equal(native.parentElement, models);
    assert.equal(document.querySelectorAll('[data-testid="model-selector-item"]').length, 1);
    assert.equal(panel.children[0].nextElementSibling, models);
    assert.equal(panel.lastElementChild, usage);
    native.click(); assert.equal(selected, 1, 'Native event handlers remain attached');
    assert.equal(h.timers.size, 0, 'Steady menus do not poll');
    assert.equal(document.querySelector('[data-antigravity-accounts-menu]').style.color, 'inherit');
    h.controller.stop();
    assert.deepEqual([...panel.children], [models, usage]);
  } finally { h.close(); }
});

test('protected content, unrelated menus, hidden panels and collapsed native triggers are ignored', { skip: !JSDOM }, async () => {
  const h = harness({ source: '<button data-testid="model-selector-trigger" aria-expanded="false"></button><div role="menu"><button>Gemini</button></div>' +
    '<article data-message-role="assistant"><div data-testid="model-selector-panel"></div></article>' +
    '<div hidden><div data-testid="model-selector-panel"></div></div><div data-testid="model-selector-panel"></div>' });
  try {
    await settle();
    assert.equal(h.document.querySelectorAll('[data-antigravity-accounts-menu]').length, 0);
    assert.equal(h.calls.length, 0);
  } finally { h.close(); }
});

test('chat streaming and unrelated updates do not rescan closed or open model menus', { skip: !JSDOM }, async () => {
  for (const open of [false, true]) {
    const h = harness({ source: open ? html : '<button data-testid="model-selector-trigger" aria-expanded="false"></button>' });
    try {
      const article = h.document.createElement('article'); article.setAttribute('data-message-role', 'assistant');
      article.innerHTML = '<div class="markdown"></div>';
      const badge = h.document.createElement('div');
      h.document.body.append(article, badge); await settle();
      const original = h.document.querySelectorAll.bind(h.document);
      let menuScans = 0;
      h.document.querySelectorAll = selector => {
        if (['div[data-testid="model-selector-panel"]', 'button[data-testid="model-selector-trigger"]'].includes(selector)) menuScans++;
        return original(selector);
      };
      const calls = h.calls.length;
      for (let i = 0; i < 10; i++) {
        article.firstElementChild.append(h.document.createElement('span'));
        badge.textContent = String(i); badge.setAttribute('aria-hidden', String(i % 2 === 0));
        await settle();
      }
      assert.equal(menuScans, 0, 'Unrelated updates must not search the whole document for a model panel or trigger');
      assert.equal(h.calls.length, calls);
      assert.equal(h.timers.size, 0);
      assert.equal(Boolean(current(h.document)), open);
    } finally { h.close(); }
  }
});

test('portal, trigger and visibility changes still attach and remove account controls', { skip: !JSDOM }, async () => {
  const h = harness({ source: '<button data-testid="model-selector-trigger" aria-expanded="false"></button>' });
  try {
    h.state().status.login = { state: 'pending' };
    const trigger = h.document.querySelector('button');
    const portal = h.document.createElement('div');
    portal.innerHTML = html.slice(html.indexOf('<div role="menu">'));
    const article = h.document.createElement('article'); article.setAttribute('data-message-role', 'assistant');
    h.document.body.append(article, portal); await settle();
    const check = async visible => {
      await settle();
      assert.equal(h.document.querySelectorAll('[data-antigravity-accounts-menu]').length, visible ? 3 : 0);
      assert.equal(h.timers.size, visible ? 1 : 0);
      if (!visible) {
        const calls = h.calls.length; h.tick(); await settle(); assert.equal(h.calls.length, calls);
      }
    };
    await check(false);
    trigger.setAttribute('aria-expanded', 'true'); await check(true);
    for (const attribute of ['hidden', 'aria-hidden']) {
      portal.setAttribute(attribute, attribute === 'hidden' ? '' : 'true'); await check(false);
      portal.removeAttribute(attribute); await check(true);
    }
    trigger.setAttribute('data-testid', 'replaced-trigger'); await check(false);
    trigger.setAttribute('data-testid', 'model-selector-trigger'); await check(true);
    trigger.remove(); await check(false);
    h.document.body.prepend(trigger); await check(true);
    portal.remove(); await check(false);
    h.document.body.append(portal); await check(true);
    article.append(portal); await check(false);
    h.document.body.append(portal); await check(true);
    trigger.setAttribute('aria-expanded', 'false'); await check(false);
  } finally { h.close(); }
});

test('synthetic page clicks and keyboard input cannot change accounts or begin OAuth', { skip: !JSDOM }, async () => {
  const h = harness();
  try {
    await settle();
    for (const button of h.document.querySelectorAll('[data-antigravity-accounts-menu] button')) {
      button.click();
      button.dispatchEvent(new h.window.MouseEvent('click', { bubbles: true }));
      button.dispatchEvent(new h.window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    }
    h.document.querySelector('input').dispatchEvent(new h.window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await settle();
    assert.deepEqual(h.calls.map(c => c.method), ['getState']);
  } finally { h.close(); }
});

test('trusted account selection updates the group label only after backend confirmation', { skip: !JSDOM }, async () => {
  let resolveSelection;
  const h = harness({ onRequest(method, params, state) {
    if (method === 'getState') return structuredClone(state);
    if (method === 'selectAccount') return new Promise(resolve => { resolveSelection = () => { state.status.selectedAccountId = params.id; resolve({ selected: true }); }; });
  } });
  try {
    await settle();
    const models = h.document.querySelector('.models');
    h.trustedClick(groupButton(h.document));
    assert.match(current(h.document).textContent, /Work/);
    assert.equal(h.timers.size, 1);
    resolveSelection(); await settle();
    assert.match(current(h.document).textContent, /Personal · 正在使用/);
    assert.match(groupButton(h.document, 'a').textContent, /Work · 主账号/);
    assert.equal(h.document.querySelector('.models'), models);
    assert.equal(h.timers.size, 0);
    assert.deepEqual(h.calls.filter(c => c.method !== 'getState'), [{ method: 'selectAccount', params: { id: 'b' } }]);
  } finally { h.close(); }
});

test('pending switch displays cancellation and closing the native menu stops status polling', { skip: !JSDOM }, async () => {
  let cancel = false;
  const h = harness({ onRequest(method, params, state) {
    if (method === 'getState') {
      const next = structuredClone(state);
      next.status.switch = cancel ? { state: 'active', cancellable: false } : { state: 'waiting', targetAccountId: 'b', cancellable: true };
      return next;
    }
    if (method === 'cancelSwitch') { cancel = true; state.status.switch = { state: 'active', cancellable: false }; return {}; }
  } });
  try {
    h.state().status.switch = { state: 'waiting', targetAccountId: 'b', cancellable: true };
    await settle();
    assert.match(h.document.querySelector('[role="status"]').textContent, /等待生成/);
    const button = [...h.document.querySelectorAll('[data-antigravity-accounts-menu] button')].find(b => /取消切换/.test(b.textContent));
    h.trustedClick(button); await settle(); assert.equal(cancel, true);
    h.state().status.login = { state: 'pending' };
    const panel = h.document.querySelector('[data-testid="model-selector-panel"]');
    panel.remove(); await settle();
    assert.equal(h.timers.size, 0);
    const count = h.calls.length; h.tick(); await settle(); assert.equal(h.calls.length, count);
    h.document.querySelector('[data-testid="model-selector-trigger"]').setAttribute('aria-expanded', 'false');
    h.document.body.append(panel); await settle();
    assert.equal(panel.querySelectorAll('[data-antigravity-accounts-menu]').length, 0);
  } finally { h.close(); }
});

test('inline login keeps typed label, polls only during OAuth and enables first primary selection', { skip: !JSDOM }, async () => {
  const h = harness({ onRequest(method, params, state) {
    if (method === 'getState') return structuredClone(state);
    if (method === 'beginLogin') { state.status.login = { state: 'pending' }; return { started: true }; }
  } });
  try {
    const registration = initial();
    registration.status.mode = 'registration-only'; registration.status.runtimeStarted = true;
    registration.status.primaryAccountId = null; registration.status.selectedAccountId = null;
    h.setState(registration); await settle();
    const input = h.document.querySelector('input'); input.value = 'Personal label';
    const login = [...h.document.querySelectorAll('[data-antigravity-accounts-menu] button')].find(b => b.textContent === '添加账号');
    h.trustedClick(login); await settle();
    assert.deepEqual(h.calls.find(c => c.method === 'beginLogin').params, { label: 'Personal label' });
    assert.equal(h.document.querySelector('input'), input); assert.equal(input.value, 'Personal label');
    assert.match(h.document.querySelector('[role="status"]').textContent, /官方 Google/);
    assert.equal(h.timers.size, 1);
    h.state().status.login = { state: 'succeeded' };
    h.tick(); await settle();
    assert.equal(h.timers.size, 0);
    assert([...h.document.querySelectorAll('button')].some(b => b.textContent === '设为主账号' && !b.disabled));
  } finally { h.close(); }
});

test('read and switch failures stay visible and do not claim a different selected account', { skip: !JSDOM }, async () => {
  let failRead = true;
  const h = harness({ onRequest(method, params, state) {
    if (method === 'getState') { if (failRead) throw new Error('Component unavailable'); return state; }
    throw new Error('Switch rejected');
  } });
  try {
    await settle();
    assert.match(h.document.querySelector('[role="status"]').textContent, /无法读取本机账号/);
    const panel = h.document.querySelector('[data-testid="model-selector-panel"]');
    panel.remove(); await settle(); failRead = false; h.document.body.append(panel); await settle();
    h.trustedClick(groupButton(h.document)); await settle();
    assert.match(h.document.querySelector('[role="status"]').textContent, /操作未完成/);
    assert.match(current(h.document).textContent, /Work/);
  } finally { h.close(); }
});

test('committed blocked account can be reconfirmed from its current group', { skip: !JSDOM }, async () => {
  const blocked = initial();
  blocked.status.selectedAccountId = 'b';
  blocked.status.switch = { state: 'blocked', targetAccountId: 'b', cancellable: false };
  const h = harness({ onRequest(method, params) {
    if (method === 'getState') return structuredClone(blocked);
    if (method === 'selectAccount') {
      blocked.status.switch = { state: 'active', cancellable: false };
      return { selectedAccountId: params.id };
    }
  } });
  try {
    await settle();
    assert.match(current(h.document).textContent, /Personal · 等待确认/);
    const recover = h.document.querySelector('[data-testid="antigravity-accounts-reconfirm"]');
    assert(recover, 'The committed current account needs a reachable recovery action');
    assert.equal(recover.disabled, false);
    assert.equal(recover.getAttribute('data-account-id'), 'b');
    h.trustedClick(recover); await settle();
    assert.deepEqual(h.calls.filter(c => c.method !== 'getState'), [{ method: 'selectAccount', params: { id: 'b' } }]);
    assert.match(current(h.document).textContent, /Personal · 正在使用/);
    assert.equal(h.document.querySelector('[data-testid="antigravity-accounts-reconfirm"]'), null);
  } finally { h.close(); }
});

test('a failed account operation cannot mask a blocked model choice or bounded failure code', { skip: !JSDOM }, async () => {
  for (const code of ['native_model_selection_required', 'native_view_provider_unavailable', 'PRIVATE: token=secret']) {
    const h = harness({ onRequest(method, params, state) {
      if (method === 'getState') return state;
      state.status.selectedAccountId = params.id;
      state.status.switch = { state: 'blocked', targetAccountId: params.id, errorCode: code };
      throw new Error('PRIVATE: token=secret');
    } });
    try {
      await settle(); h.trustedClick(groupButton(h.document)); await settle();
      const message = h.document.querySelector('[role="status"]').textContent;
      assert(!message.includes('操作未完成')); assert(!message.includes('PRIVATE'));
      assert.match(message, code === 'native_model_selection_required' ? /请选择上方可用模型/ : /新的发送已暂停/);
      if (code === 'native_view_provider_unavailable') assert(message.includes(code));
    } finally { h.close(); }
  }
});

test('completed selection requests another state read after an older in-flight read', { skip: !JSDOM }, async () => {
  let reads = 0, releaseOldRead, finishSelection;
  const h = harness({ onRequest(method, params, state) {
    if (method === 'getState') {
      reads++;
      const snapshot = structuredClone(state);
      if (reads === 2) return new Promise(resolve => { releaseOldRead = () => resolve(snapshot); });
      return snapshot;
    }
    if (method === 'selectAccount') return new Promise(resolve => {
      finishSelection = () => { state.status.selectedAccountId = params.id; resolve({ selectedAccountId: params.id }); };
    });
  } });
  try {
    await settle(); h.trustedClick(groupButton(h.document));
    h.tick(); await settle();
    assert.equal(reads, 2);
    finishSelection(); await settle();
    releaseOldRead(); await settle(); await settle();
    assert.equal(reads, 3, 'The operation completion must not lose its requested refresh');
    assert.match(current(h.document).textContent, /Personal · 正在使用/);
    assert.equal(h.timers.size, 0);
  } finally { h.close(); }
});

test('queued operation refresh is discarded when the native menu closes or the controller stops', { skip: !JSDOM }, async () => {
  for (const stop of [false, true]) {
    let reads = 0, releaseOldRead, finishSelection;
    const h = harness({ onRequest(method, params, state) {
      if (method === 'getState') {
        reads++;
        const snapshot = structuredClone(state);
        if (reads === 2) return new Promise(resolve => { releaseOldRead = () => resolve(snapshot); });
        return snapshot;
      }
      if (method === 'selectAccount') return new Promise(resolve => {
        finishSelection = () => { state.status.selectedAccountId = params.id; resolve({ selectedAccountId: params.id }); };
      });
    } });
    try {
      await settle(); h.trustedClick(groupButton(h.document)); h.tick(); await settle();
      finishSelection(); await settle();
      if (stop) h.controller.stop();
      else { h.document.querySelector('[data-testid="model-selector-panel"]').remove(); await settle(); }
      releaseOldRead(); await settle(); h.tick(); await settle();
      assert.equal(reads, 2, 'Detached menus must not perform the queued read');
      assert.equal(h.timers.size, 0);
    } finally { h.close(); }
  }
});

test('native model replacement is preserved and stopped or detached controls cannot mutate state', { skip: !JSDOM }, async () => {
  const h = harness();
  try {
    await settle();
    const stale = groupButton(h.document), panel = h.document.querySelector('[data-testid="model-selector-panel"]');
    const oldModels = panel.querySelector('.models');
    const nextModels = h.document.createElement('div');
    nextModels.innerHTML = '<button data-testid="model-selector-item">Updated native model</button>';
    oldModels.replaceWith(nextModels); await settle();
    assert.equal(current(h.document).parentElement.nextElementSibling, nextModels);
    assert.equal(h.document.querySelectorAll('[data-testid="model-selector-item"]').length, 1);
    groupButton(h.document).focus(); h.trustedKey(groupButton(h.document), 'End');
    assert.equal(h.document.activeElement.textContent, 'View Usage');
    h.controller.stop(); h.trustedClick(stale); await settle();
    assert.deepEqual(h.calls.map(c => c.method), ['getState']);
  } finally { h.close(); }
});
