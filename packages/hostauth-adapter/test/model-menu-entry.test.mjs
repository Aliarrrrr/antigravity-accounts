import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import { installModelMenuEntry, modelMenuEntrySource } from '../src/model-menu-entry.mjs';

const { JSDOM } = createRequire(process.env.ACCOUNTS_DOM_TEST_PACKAGE || import.meta.url)('jsdom');
const settle = () => new Promise(setImmediate);
const html = '<button data-testid="model-selector-trigger" aria-expanded="true">Current model</button>' +
  '<div role="menu"><div data-testid="model-selector-panel"><div class="models"><button role="menuitem" data-testid="model-selector-item">Native option</button></div><div class="usage"><button role="menuitem">View Usage</button></div></div></div>';
const initial = () => ({ status: {
  configured: true, mode: 'native-provider', runtimeStarted: true, primaryAccountId: 'a', selectedAccountId: 'a',
  liveSwitchSupported: true, login: { state: 'idle' }, switch: { state: 'active', cancellable: false },
}, preferences: { dataUsageConsent: 'off' }, accounts: [
  { id: 'a', label: 'Work', email: 'work@example.invalid', selected: true },
  { id: 'b', label: 'Personal', email: 'personal@example.invalid', selected: false },
] });

function harness({ source = html, onRequest, ownedSectionHeight, serialized = false } = {}) {
  const dom = new JSDOM(source, { url: 'https://127.0.0.1:43123/', pretendToBeVisual: true });
  const { window } = dom, { document } = window;
  if (ownedSectionHeight !== undefined) window.HTMLElement.prototype.getBoundingClientRect = function () {
    return { height: this.matches('[data-antigravity-accounts-menu]') ? ownedSectionHeight : 0 };
  };
  const calls = [], scripts = [], ipcListeners = new Map(), listeners = new WeakMap(), timers = new Map();
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
    if (method === 'getPreferences') return state.preferences;
    if (method === 'setPreferences') { state.preferences = { ...params }; return state.preferences; }
    if (method === 'selectAccount') { state.status.selectedAccountId = params.id; return { selected: true }; }
    return {};
  };
  let controller;
  if (serialized) {
    vm.runInNewContext(modelMenuEntrySource(), { process: { isMainFrame: true }, window, document,
      location: window.location, require(name) {
        assert.equal(name, 'electron');
        return { ipcRenderer: {
          invoke(channel, { method, params }) {
            assert.equal(channel, 'antigravity-accounts:model-menu'); return request(method, params);
          },
          on(channel, callback) {
            ipcListeners.set(channel, [...ipcListeners.get(channel) || [], callback]);
          },
        }, webFrame: { async executeJavaScript(script) { scripts.push(script); return true; } } };
      } });
    document.dispatchEvent(new window.Event('DOMContentLoaded'));
    controller = window.__antigravityAccountsModelMenuEntryV2;
    assert(controller, 'The serialized preload starts in a native main frame');
  } else controller = installModelMenuEntry({ window, document, request });
  return { dom, window, document, calls, scripts, timers, request, controller,
    state: () => state, setState(next) { state = next; },
    emitPreferences(value) {
      for (const callback of ipcListeners.get('antigravity-accounts:model-menu:preferences') || []) callback({}, value);
    },
    // Call the registered listener with a native-event-shaped object. The page
    // cannot construct isTrusted=true DOM events; no production test API exists.
    trustedClick(button) {
      for (const listener of listeners.get(button)?.get('click') || [])
        listener.call(button, { isTrusted: true, currentTarget: button, preventDefault() {}, stopPropagation() {} });
    },
    trustedKey(button, key, extra = {}) {
      for (const listener of listeners.get(button)?.get('keydown') || [])
        listener.call(button, { isTrusted: true, currentTarget: button, key, repeat: false, preventDefault() {}, stopPropagation() {}, ...extra });
    },
    trustedChange(node) {
      for (const listener of listeners.get(node)?.get('change') || [])
        listener.call(node, { isTrusted: true, currentTarget: node, preventDefault() {}, stopPropagation() {} });
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
const accountAction = (document, id, action) => [...document.querySelectorAll('button[data-account-id]')].find(button =>
  button.getAttribute('data-account-id') === id && button.getAttribute('data-account-action') === action);
const management = document => document.querySelector('[data-testid="antigravity-accounts-management"]');
const confirmation = document => document.querySelector('[data-testid="antigravity-accounts-remove-confirmation"]:not([hidden])');
const openManagement = async h => {
  const entry = h.document.querySelector('[data-testid="antigravity-accounts-manage"]');
  assert(entry && !entry.disabled, 'The native menu exposes account management');
  h.trustedClick(entry); await settle();
  assert(management(h.document)?.hasAttribute('open'), 'A trusted menu action opens the management dialog');
  return management(h.document);
};

test('an inactive attached account can be removed during native use only after explicit confirmation', { skip: !JSDOM }, async () => {
  const h = harness({ onRequest(method, params, state) {
    if (method === 'getState') return state;
    assert.equal(method, 'removeAccount'); assert.deepEqual(params, { id: 'b' });
    state.accounts = state.accounts.filter(account => account.id !== params.id);
    return {};
  } });
  h.state().accounts.push({ id: 'c', label: 'Other attached', email: 'other@example.invalid' });
  try {
    await settle();
    const native = h.document.querySelector('[data-testid="model-selector-item"]');
    await openManagement(h);
    const remove = accountAction(h.document, 'b', 'remove');
    assert.equal(remove.disabled, false);
    h.trustedClick(remove); await settle();
    assert.match(confirmation(h.document).textContent, /personal@example.invalid.*仅删除本机插件保存的授权/);
    assert.equal(h.document.activeElement, accountAction(h.document, 'b', 'remove-cancel'));
    assert.deepEqual(h.calls.filter(call => call.method !== 'getState'), []);
    accountAction(h.document, 'b', 'remove-confirm').click(); await settle();
    assert.deepEqual(h.calls.filter(call => call.method !== 'getState'), [], 'Synthetic page clicks cannot confirm deletion');
    h.trustedClick(accountAction(h.document, 'b', 'remove-cancel')); await settle();
    assert.equal(confirmation(h.document), null);
    assert.equal(h.document.activeElement, accountAction(h.document, 'b', 'remove'));
    h.trustedClick(accountAction(h.document, 'b', 'remove')); await settle();
    h.trustedClick(accountAction(h.document, 'b', 'remove-confirm')); await settle();
    assert.deepEqual(h.calls.filter(call => call.method !== 'getState'), [{ method: 'removeAccount', params: { id: 'b' } }]);
    assert.deepEqual(h.state().accounts.map(account => account.id), ['a', 'c']);
    assert.equal(h.state().status.selectedAccountId, 'a'); assert.equal(h.state().status.primaryAccountId, 'a');
    assert.equal(accountAction(h.document, 'b', 'select'), undefined);
    assert.equal(h.document.querySelector('[data-testid="model-selector-item"]'), native);
    assert.match(h.document.querySelector('.usage').textContent, /View Usage/);
  } finally { h.close(); }
});

test('current, primary, switching and pending-login accounts explain why deletion is unavailable', { skip: !JSDOM }, async () => {
  for (const mode of ['active', 'waiting', 'confirming', 'blocked', 'login']) {
    const h = harness();
    h.state().status.selectedAccountId = 'b';
    h.state().accounts.push({ id: 'c', email: 'other@example.invalid' });
    if (mode === 'login') h.state().status.login.state = 'pending';
    else h.state().status.switch.state = mode;
    try {
      await settle();
      await openManagement(h);
      const currentRemove = accountAction(h.document, 'b', 'remove');
      const primaryRemove = accountAction(h.document, 'a', 'remove');
      assert.equal(currentRemove.disabled, true); assert.equal(primaryRemove.disabled, true);
      assert.equal(accountAction(h.document, 'c', 'remove').disabled, mode !== 'active');
      if (mode === 'active') {
        assert.match(currentRemove.title, /先切换到其他账号/);
        assert.match(primaryRemove.title, /主账号不能在运行中删除/);
      }
      h.trustedClick(currentRemove); h.trustedClick(primaryRemove);
      assert.deepEqual(h.calls.filter(call => call.method !== 'getState'), []);
      assert.equal(confirmation(h.document), null);
    } finally { h.close(); }
  }
});

test('opening removal confirmation refreshes state when another window has selected that account', { skip: !JSDOM }, async () => {
  const h = harness();
  try {
    await settle();
    await openManagement(h);
    const remove = accountAction(h.document, 'b', 'remove');
    h.state().status.selectedAccountId = 'b';
    h.trustedClick(remove);
    const staleConfirm = accountAction(h.document, 'b', 'remove-confirm');
    await settle();
    const currentConfirm = accountAction(h.document, 'b', 'remove-confirm');
    assert.equal(currentConfirm.disabled, true);
    assert.match(confirmation(h.document).textContent, /先切换到其他账号/);
    h.trustedClick(staleConfirm); h.trustedClick(currentConfirm); await settle();
    assert.deepEqual(h.calls.filter(call => call.method !== 'getState'), []);
  } finally { h.close(); }
});

test('failed or detached removal confirmation cannot hide an account or silently repeat deletion', { skip: !JSDOM }, async () => {
  const h = harness({ onRequest(method, params, state) {
    if (method === 'getState') return state;
    assert.equal(method, 'removeAccount'); throw Error('account_locked');
  } });
  try {
    await settle();
    await openManagement(h);
    h.trustedClick(accountAction(h.document, 'b', 'remove')); await settle();
    h.trustedClick(accountAction(h.document, 'b', 'remove-confirm')); await settle();
    assert.match(management(h.document).querySelector('[role="status"]').textContent, /删除未完成/);
    assert(accountAction(h.document, 'b', 'select'));
    h.trustedClick(accountAction(h.document, 'b', 'remove')); await settle();
    const confirm = accountAction(h.document, 'b', 'remove-confirm');
    h.trustedClick(management(h.document).querySelector('[data-account-action="close"]')); await settle();
    h.trustedClick(confirm); await settle();
    assert.equal(h.calls.filter(call => call.method === 'removeAccount').length, 1);
  } finally { h.close(); }
});

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

test('serialized preload opens management with its embedded factory and routes trusted actions to private IPC', { skip: !JSDOM }, async () => {
  const h = harness({ serialized: true });
  try {
    await settle();
    assert.match(current(h.document).textContent, /Work/);
    assert(h.calls.some(call => call.method === 'getPreferences'));
    assert(h.scripts.length > 0);
    assert(h.scripts.every(script => script.endsWith('preference:"off"})')));
    for (const mode of ['on', 'manual', 'off']) {
      h.emitPreferences({ dataUsageConsent: mode }); await settle();
      assert(h.scripts.at(-1).endsWith(`preference:${JSON.stringify(mode)}})`));
      assert.doesNotThrow(() => new vm.Script(h.scripts.at(-1)));
    }
    const dialog = await openManagement(h);
    const input = dialog.querySelector('[aria-label="新账号标签"]'); input.value = 'Embedded factory';
    h.trustedClick(dialog.querySelector('[data-account-action="add"]')); await settle();
    const calls = h.calls.filter(call => !['getState', 'getPreferences'].includes(call.method));
    assert.equal(calls.length, 1); assert.equal(calls[0].method, 'beginLogin');
    assert.equal(calls[0].params.label, 'Embedded factory');
    const consent = dialog.querySelector('[data-account-action="consent"]'); consent.value = 'on';
    h.trustedChange(consent); await settle();
    assert.equal(h.calls.at(-2).method, 'setPreferences');
    assert(h.scripts.at(-1).endsWith('preference:"on"})'), 'Saving the preference updates the native renderer installer');
    assert.equal(h.window.request, undefined, 'The page world does not receive the request function');
  } finally { h.close(); }
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
    assert.equal(panel.querySelectorAll('[data-testid="antigravity-accounts-manage"]').length, 1);
    assert.equal(panel.querySelectorAll('input,[data-account-action="remove"],[data-account-action="primary"],[data-account-action="rename"],[data-account-action="add"]').length, 0,
      'The native dropdown only exposes account selection and the management entry');
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
        if (['[data-testid="model-selector-panel"]', '[data-testid="model-selector-trigger"]'].includes(selector)) menuScans++;
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
    await settle();
    assert.deepEqual(h.calls.map(c => c.method), ['getState']);
    assert.equal(management(h.document), null, 'Synthetic clicks cannot open management');
    const dialog = await openManagement(h);
    for (const button of dialog.querySelectorAll('button')) button.click();
    dialog.querySelector('[aria-label="新账号标签"]').dispatchEvent(new h.window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await settle();
    assert.deepEqual(h.calls.filter(call => call.method !== 'getState'), []);
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

test('management login keeps typed label, polls only during OAuth and enables first primary selection', { skip: !JSDOM }, async () => {
  const h = harness({ onRequest(method, params, state) {
    if (method === 'getState') return structuredClone(state);
    if (method === 'beginLogin') { state.status.login = { state: 'pending' }; return { started: true }; }
  } });
  try {
    const registration = initial();
    registration.status.mode = 'registration-only'; registration.status.runtimeStarted = true;
    registration.status.primaryAccountId = null; registration.status.selectedAccountId = null;
    h.setState(registration); await settle();
    const dialog = await openManagement(h);
    const input = dialog.querySelector('[aria-label="新账号标签"]'); input.value = 'Personal label';
    const login = dialog.querySelector('[data-account-action="add"]');
    h.trustedClick(login); await settle();
    assert.deepEqual(h.calls.find(c => c.method === 'beginLogin').params, { label: 'Personal label' });
    assert.equal(dialog.querySelector('[aria-label="新账号标签"]'), input); assert.equal(input.value, 'Personal label');
    assert.match(dialog.querySelector('[role="status"]').textContent, /Google 官方/);
    assert.equal(h.timers.size, 1);
    h.state().status.login = { state: 'succeeded' };
    h.tick(); await settle();
    assert.equal(h.timers.size, 0);
    assert([...dialog.querySelectorAll('[data-account-action="primary"]')].some(b => !b.hidden && !b.disabled));
  } finally { h.close(); }
});

test('management remains open and polls pending login after the native dropdown closes', { skip: !JSDOM }, async () => {
  const h = harness();
  try {
    h.state().status.login = { state: 'pending' }; await settle();
    const dialog = await openManagement(h);
    const panel = h.document.querySelector('[data-testid="model-selector-panel"]');
    panel.remove(); await settle();
    assert.equal(dialog.hasAttribute('open'), true);
    assert.equal(h.timers.size, 1, 'Pending authorization is still visible in management');
    const reads = h.calls.length; h.tick(); await settle();
    assert.equal(h.calls.length, reads + 1);
    h.trustedClick(dialog.querySelector('[data-account-action="close"]')); await settle();
    assert.equal(dialog.hasAttribute('open'), false);
    assert.equal(h.timers.size, 0, 'Closing both views stops local status polling');
    const closedReads = h.calls.length; h.tick(); await settle();
    assert.equal(h.calls.length, closedReads);
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
  for (const code of ['native_model_selection_required', 'native_account_verification_required', 'native_quota_refresh_failed', 'native_view_provider_unavailable', 'PRIVATE: token=secret']) {
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
      if (code === 'native_account_verification_required') assert.match(message, /Google.*身份验证.*新的发送已暂停.*亲自.*官方页面.*重新确认/);
      if (code === 'native_quota_refresh_failed') assert.match(message, /额度信息刷新失败.*服务权限/);
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
    assert.equal(current(h.document).closest('[data-antigravity-accounts-menu]').nextElementSibling, nextModels);
    assert.equal(h.document.querySelectorAll('[data-testid="model-selector-item"]').length, 1);
    groupButton(h.document).focus(); h.trustedKey(groupButton(h.document), 'End');
    assert.equal(h.document.activeElement.textContent, 'View Usage');
    h.controller.stop(); h.trustedClick(stale); await settle();
    assert.deepEqual(h.calls.map(c => c.method), ['getState']);
  } finally { h.close(); }
});

test('native test IDs survive host element tag changes while hidden triggers cannot activate a panel', { skip: !JSDOM }, async () => {
  const h = harness({ source: html.replace('<button data-testid="model-selector-trigger" aria-expanded="true">Current model</button>',
    '<div role="button" data-testid="model-selector-trigger" aria-expanded="true">Current model</div>')
    .replace('<div data-testid="model-selector-panel">', '<section data-testid="model-selector-panel">')
    .replace('</div></div></div>', '</div></section></div>') });
  try {
    await settle();
    assert.match(current(h.document).textContent, /Work/);
    const trigger = h.document.querySelector('[data-testid="model-selector-trigger"]');
    trigger.hidden = true; await settle();
    assert.equal(current(h.document), null);
    trigger.hidden = false; await settle();
    assert.match(current(h.document).textContent, /Work/);
  } finally { h.close(); }
});

test('Chinese input confirmation does not start account authorization before composition ends', { skip: !JSDOM }, async () => {
  const h = harness();
  try {
    await settle();
    const dialog = await openManagement(h);
    const input = dialog.querySelector('[aria-label="新账号标签"]'); input.value = '工作';
    h.trustedKey(input, 'Enter', { isComposing: true });
    h.trustedKey(input, 'Enter', { keyCode: 229 });
    await settle();
    assert.equal(h.calls.some(call => call.method === 'beginLogin'), false);
    h.trustedKey(input, 'Enter'); await settle();
    assert.deepEqual(h.calls.find(call => call.method === 'beginLogin').params, { label: '工作' });
  } finally { h.close(); }
});

test('status updates keep the same account selection focused and preserve account list scroll', { skip: !JSDOM }, async () => {
  let reads = 0, finishRead;
  const h = harness({ onRequest(method, params, state) {
    assert.equal(method, 'getState');
    if (++reads === 2) return new Promise(resolve => { finishRead = () => resolve(state); });
    return state;
  } });
  try {
    const state = h.state();
    state.status.login = { state: 'pending' };
    await settle();
    const select = groupButton(h.document, 'b');
    assert(select && !select.disabled);
    const scroller = select.closest('[data-antigravity-accounts-menu]'); scroller.scrollTop = 72;
    select.focus(); h.tick(); await settle();
    state.accounts[0].label = 'Renamed Work'; finishRead(); await settle();
    assert.equal(h.document.activeElement.getAttribute('data-account-action'), 'select');
    assert.equal(h.document.activeElement.getAttribute('data-account-id'), 'b');
    assert.equal(scroller.scrollTop, 72);
    assert.equal(scroller.style.maxHeight, 'min(32vh, 12rem)');
    assert(scroller.classList.contains('overflow-y-auto'));
  } finally { h.close(); }
});

test('account sections retain native text sizing and extend only the owned panel height allowance', { skip: !JSDOM }, async () => {
  const source = '<style>body{font:16px/24px Arial}.text-xs,.native-option{font-size:12px;line-height:16px}</style>' +
    html.replace('data-testid="model-selector-panel"', 'style="max-height:320px!important" data-testid="model-selector-panel"')
      .replace('role="menuitem" data-testid="model-selector-item"', 'class="native-option" role="menuitem" data-testid="model-selector-item"');
  const h = harness({ source, ownedSectionHeight: 30 });
  try {
    h.state().status.login.state = 'pending';
    h.state().accounts.push({ id: 'c', label: 'Another account', email: 'another@example.invalid' });
    await settle();
    const panel = h.document.querySelector('[data-testid="model-selector-panel"]');
    const native = panel.querySelector('[data-testid="model-selector-item"]');
    const button = groupButton(h.document), manage = panel.querySelector('[data-testid="antigravity-accounts-manage"]');
    assert.equal(h.window.getComputedStyle(button).fontSize, h.window.getComputedStyle(native).fontSize);
    assert.equal(h.window.getComputedStyle(manage).fontSize, h.window.getComputedStyle(native).fontSize);
    assert.equal(button.style.fontSize, ''); assert.equal(manage.style.fontSize, '');
    assert.equal(button.style.lineHeight, ''); assert.equal(manage.style.lineHeight, '');
    const accounts = button.closest('[data-antigravity-accounts-menu]');
    assert(accounts.classList.contains('shrink-0'), 'A small account list must not shrink to hide one of its rows');
    assert.equal(accounts.style.maxHeight, 'min(32vh, 12rem)', 'A long list keeps its own scroll limit');
    const fitted = panel.style.maxHeight;
    assert.match(fitted, /320px/); assert.match(fitted, /90px/); assert.match(fitted, /--available-height/);
    assert.equal(panel.style.getPropertyPriority('max-height'), 'important');
    h.state().accounts[1].label = 'Updated account'; h.tick(); await settle();
    assert.equal(panel.style.maxHeight, fitted, 'Repeated status updates cannot add the account height twice');
    assert.equal(native.style.cssText, '', 'Native model styles stay untouched');
    h.controller.stop();
    assert.equal(panel.style.maxHeight, '320px');
    assert.equal(panel.style.getPropertyPriority('max-height'), 'important');
  } finally { h.close(); }
});

test('removing account controls preserves a later native panel height change', { skip: !JSDOM }, async () => {
  const h = harness({ source: html.replace('data-testid="model-selector-panel"', 'style="max-height:320px" data-testid="model-selector-panel"'),
    ownedSectionHeight: 20 });
  try {
    await settle();
    const panel = h.document.querySelector('[data-testid="model-selector-panel"]');
    assert.match(panel.style.maxHeight, /--available-height/);
    panel.style.setProperty('max-height', '500px', 'important');
    h.controller.stop();
    assert.equal(panel.style.maxHeight, '500px');
    assert.equal(panel.style.getPropertyPriority('max-height'), 'important');
  } finally { h.close(); }
});
