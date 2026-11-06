import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { installAccountManagement } from '../src/account-management.mjs';

const { JSDOM } = createRequire(process.env.ACCOUNTS_DOM_TEST_PACKAGE || import.meta.url)('jsdom');
const settle = () => new Promise(setImmediate);
function harness({ failPreferences = false, collapseHostMenu = false } = {}) {
  const dom = new JSDOM('<button data-testid="model-selector-trigger">Model</button><button id="entry">Manage</button>', { pretendToBeVisual: true });
  const { window } = dom, { document } = window;
  const handlers = new WeakMap(), calls = [];
  const original = window.EventTarget.prototype.addEventListener;
  window.EventTarget.prototype.addEventListener = function (type, callback, ...rest) {
    if (!handlers.has(this)) handlers.set(this, new Map());
    const map = handlers.get(this); map.set(type, [...map.get(type) || [], callback]);
    return original.call(this, type, callback, ...rest);
  };
  const state = { status: { configured: true, mode: 'native-provider', runtimeStarted: true,
    selectedAccountId: 'a', primaryAccountId: 'a', login: { state: 'idle' }, switch: { state: 'active' } },
  accounts: [{ id: 'a', label: 'Work', email: 'work@example.invalid' }, { id: 'b', label: 'Personal', email: 'personal@example.invalid' }],
  preferences: { dataUsageConsent: 'off' } };
  let busy = false, error = '', opens = 0, closes = 0, refreshes = 0;
  const controller = installAccountManagement({ document, window, getState: () => state,
    isBusy: () => busy, getError: () => error, onOpen() { opens++; }, onClose() { closes++; }, onRefresh() { refreshes++; },
    async operate(method, params) {
      calls.push({ method, params });
      if (method === 'removeAccount') state.accounts = state.accounts.filter(a => a.id !== params.id);
      if (method === 'renameAccount') state.accounts.find(a => a.id === params.id).label = params.label;
      if (method === 'setPreferences') {
        if (failPreferences) error = '设置保存失败';
        else state.preferences = { ...params };
      }
      controller.render();
    } });
  const entry = document.querySelector('#entry');
  if (collapseHostMenu) {
    const trigger = document.querySelector('[data-testid="model-selector-trigger"]');
    trigger.setAttribute('aria-expanded', 'true');
    trigger.addEventListener('click', () => { trigger.setAttribute('aria-expanded', 'false'); entry.remove(); });
  }
  controller.open(entry);
  function trusted(node, type, extra = {}) {
    assert(node, `Missing ${type} target`);
    const event = { isTrusted: true, currentTarget: node, target: node,
      preventDefault() {}, stopPropagation() {}, ...extra };
    for (const fn of handlers.get(node)?.get(type) || []) fn.call(node, event);
  }
  return { dom, document, state, controller, calls, entry, trusted,
    action(name, id) { return document.querySelector(`[data-account-action="${name}"]${id ? `[data-account-id="${id}"]` : ''}`); },
    busy(value) { busy = value; controller.render(); }, error(value) { error = value; controller.render(); },
    opened: () => opens, closed: () => closes,
    refreshes: () => refreshes,
    close() { controller.stop(); dom.window.close(); } };
}

test('one themed dialog owns account management, and only trusted input invokes operations', async () => {
  const h = harness();
  try {
    const dialog = h.document.querySelector('dialog');
    assert.equal(dialog.getAttribute('aria-label'), '账号设置与管理');
    assert(dialog.classList.contains('bg-background'));
    assert.equal(h.document.activeElement, h.action('close'));
    assert.equal(h.opened(), 1);
    h.controller.open(h.entry); assert.equal(h.opened(), 1);
    const add = h.action('add'), input = h.document.querySelector('[aria-label="新账号标签"]');
    input.value = 'Research'; add.click(); await settle();
    assert.deepEqual(h.calls, []);
    h.trusted(add, 'click'); await settle();
    assert.deepEqual(h.calls, [{ method: 'beginLogin', params: { label: 'Research' } }]);
    h.trusted(h.action('close'), 'click');
    assert.equal(h.controller.isOpen(), false); assert.equal(h.document.activeElement, h.entry); assert.equal(h.closed(), 1);
  } finally { h.close(); }
});

test('editing labels keeps the input node and selection while local state refreshes', async () => {
  const h = harness();
  try {
    const input = h.action('label', 'b'); input.focus(); input.value = 'Research notes';
    input.setSelectionRange(4, 9); h.trusted(input, 'input');
    h.state.accounts[1].label = 'Other window'; h.controller.render();
    assert.equal(h.action('label', 'b'), input); assert.equal(input.value, 'Research notes');
    assert.equal(input.selectionStart, 4); assert.equal(input.selectionEnd, 9);
    h.trusted(h.action('rename', 'b'), 'click'); await settle();
    assert.deepEqual(h.calls, [{ method: 'renameAccount', params: { id: 'b', label: 'Research notes' } }]);
    assert.equal(h.state.accounts[1].label, 'Research notes');
    h.state.status.switch.state = 'waiting'; h.controller.render();
    assert.equal(h.action('rename', 'b').disabled, true);
  } finally { h.close(); }
});

test('deleting an attached account requires confirmation, defaults to cancel and preserves the active identity', async () => {
  const h = harness();
  try {
    assert.equal(h.action('remove', 'a').disabled, true);
    h.trusted(h.action('remove', 'b'), 'click');
    assert.equal(h.refreshes(), 1);
    assert.equal(h.document.activeElement, h.action('remove-cancel', 'b'));
    assert.equal(h.action('remove-confirm', 'b').closest('[role="group"]').hidden, false);
    h.action('remove-confirm', 'b').click(); await settle(); assert.deepEqual(h.calls, []);
    h.trusted(h.action('remove-cancel', 'b'), 'click');
    assert.equal(h.document.activeElement, h.action('remove', 'b'));
    h.trusted(h.action('remove', 'b'), 'click');
    h.trusted(h.action('remove-confirm', 'b'), 'click'); await settle();
    assert.deepEqual(h.calls, [{ method: 'removeAccount', params: { id: 'b' } }]);
    assert.deepEqual(h.state.accounts.map(a => a.id), ['a']);
    assert.equal(h.state.status.selectedAccountId, 'a'); assert.equal(h.state.status.primaryAccountId, 'a');
  } finally { h.close(); }
});

test('confirmation rechecks changed account identity and switching or login state', async () => {
  for (const change of [
    h => { h.state.status.switch.state = 'waiting'; },
    h => { h.state.status.login.state = 'pending'; },
    h => { h.state.status.selectedAccountId = 'b'; },
    h => { h.state.accounts[1].email = 'changed@example.invalid'; },
  ]) {
    const h = harness();
    try {
      h.trusted(h.action('remove', 'b'), 'click'); change(h);
      h.trusted(h.action('remove-confirm', 'b'), 'click'); await settle(); assert.deepEqual(h.calls, []);
      h.controller.render();
      if (h.state.accounts[1].email !== 'changed@example.invalid') assert.equal(h.action('remove', 'b').disabled, true);
      else assert.equal(h.action('remove-confirm', 'b').closest('[role="group"]').hidden, true);
    } finally { h.close(); }
  }
});

test('data usage defaults to unchecked, supports all preferences and displays save errors', async () => {
  const h = harness();
  try {
    const select = h.action('consent'); assert.equal(select.value, 'off');
    assert.deepEqual([...select.options].map(o => o.value), ['off', 'on', 'manual']);
    select.value = 'on'; select.dispatchEvent(new h.dom.window.Event('change')); await settle(); assert.deepEqual(h.calls, []);
    h.trusted(select, 'change'); await settle();
    select.value = 'manual'; h.trusted(select, 'change'); await settle();
    assert.deepEqual(h.calls, [
      { method: 'setPreferences', params: { dataUsageConsent: 'on' } },
      { method: 'setPreferences', params: { dataUsageConsent: 'manual' } },
    ]);
    h.error('设置保存失败'); assert.equal(h.document.querySelector('[role="status"]').textContent, '设置保存失败');
  } finally { h.close(); }
});

test('Tab wraps within the dialog and Escape returns focus to the remaining model trigger', () => {
  const h = harness();
  try {
    const dialog = h.document.querySelector('dialog'), select = h.action('consent');
    select.focus(); h.trusted(dialog, 'keydown', { key: 'Tab' });
    assert.equal(h.document.activeElement, h.action('close'));
    h.trusted(dialog, 'keydown', { key: 'Tab', shiftKey: true }); assert.equal(h.document.activeElement, select);
    h.entry.remove(); h.trusted(dialog, 'keydown', { key: 'Escape' });
    assert.equal(h.controller.isOpen(), false);
    assert.equal(h.document.activeElement, h.document.querySelector('[data-testid="model-selector-trigger"]'));
  } finally { h.close(); }
});

test('preferences read and save failures are visible and never leave an unsaved choice selected', async () => {
  const h = harness({ failPreferences: true });
  try {
    const select = h.action('consent'); select.focus(); select.value = 'on';
    h.trusted(select, 'change'); await settle();
    assert.equal(select.value, 'off'); assert.equal(h.state.preferences.dataUsageConsent, 'off');
    assert.equal(h.document.querySelector('[role="status"]').textContent, '设置保存失败');
    h.error(''); h.state.preferencesError = true; h.state.preferences.dataUsageConsent = 'manual';
    h.document.activeElement.blur(); h.controller.render();
    assert.equal(select.value, 'manual');
    assert.match(h.document.querySelector('[role="status"]').textContent, /无法读取.*手动处理/);
  } finally { h.close(); }
});

test('closing the native menu portal while opening management does not remove its controls', async () => {
  const h = harness({ collapseHostMenu: true });
  try {
    assert.equal(h.entry.isConnected, false);
    assert.equal(h.document.querySelector('[data-testid="model-selector-trigger"]').getAttribute('aria-expanded'), 'false');
    assert.equal(h.controller.isOpen(), true);
    const input = h.document.querySelector('[aria-label="新账号标签"]'); input.value = 'Offline';
    h.trusted(h.action('add'), 'click'); await settle();
    assert.deepEqual(h.calls, [{ method: 'beginLogin', params: { label: 'Offline' } }]);
    h.trusted(h.action('close'), 'click');
    assert.equal(h.document.activeElement, h.document.querySelector('[data-testid="model-selector-trigger"]'));
  } finally { h.close(); }
});
