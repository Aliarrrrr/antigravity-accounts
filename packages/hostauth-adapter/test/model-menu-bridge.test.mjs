import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { CHANNEL, createModelMenuBridge } = require('../src/model-menu-bridge.cjs');

function harness({ fail, preferences } = {}) {
  const handlers = new Map(), calls = [], opened = [];
  const preloads = new Map();
  const sender = { getURL: () => frame.url, async executeJavaScript(source) { opened.push(source); } }, frame = { url: 'https://127.0.0.1:12345/?local=fake', parent: null };
  sender.mainFrame = frame;
  let destroyed = false, accountWindow = false;
  const window = { webContents: sender, isDestroyed: () => destroyed, show() {}, focus() {} };
  const bridge = createModelMenuBridge({ electron: {
    BrowserWindow: { fromWebContents: value => value === sender ? window : undefined, getAllWindows: () => [window] },
    ipcMain: { handle(name, callback) { assert.equal(handlers.has(name), false); handlers.set(name, callback); }, removeHandler(name) { handlers.delete(name); } },
    session: { defaultSession: {
      registerPreloadScript(script) { assert.equal(script.type, 'frame'); assert.equal(script.filePath.endsWith('model-menu-preload.cjs'), true); preloads.set('model-menu', script); return 'model-menu'; },
      unregisterPreloadScript(id) { preloads.delete(id); },
    } },
  }, client: { async request(method, params) {
    if (fail) throw fail;
    calls.push({ method, params });
    if (method === 'getStatus') return { configured: true, selectedAccountId: 'a', login: { state: 'idle', token: 'PRIVATE' }, switch: { state: 'active' }, accessToken: 'PRIVATE' };
    if (method === 'listAccounts') return [{ id: 'a', label: 'Work', selected: true, token: 'PRIVATE' }];
    return { refreshToken: 'PRIVATE' };
  } }, preferences, isAccountWindow: () => accountWindow });
  return { handlers, opened, calls, preloads, frame, sender, window, bridge, event: { sender, senderFrame: frame },
    destroy() { destroyed = true; }, manager() { accountWindow = true; } };
}

test('model menu reads only public state and accepts bounded account actions', async () => {
  const h = harness(), handle = h.handlers.get(CHANNEL);
  await assert.rejects(handle(h.event, { method: 'getState' }), { code: 'account_menu_unavailable' });
  h.bridge.setEndpoint('https://127.0.0.1:12345');
  const state = await handle(h.event, { method: 'getState' });
  assert.equal(state.status.selectedAccountId, 'a');
  assert.equal(state.accounts[0].label, 'Work');
  assert(!JSON.stringify(state).includes('PRIVATE'));
  assert.deepEqual(await handle(h.event, { method: 'selectAccount', params: { id: 'b' } }), { accepted: true });
  assert.deepEqual(h.calls.at(-1), { method: 'selectAccount', params: { id: 'b' } });
  assert.equal(h.preloads.size, 1);
  for (const request of [{ method: 'getToken' }, { method: 'commitSwitch' }, { method: 'getState', params: { token: 'x' } }, { method: 'selectAccount', params: {} }, { method: 'beginLogin', params: { label: 'x'.repeat(121) } }])
    await assert.rejects(handle(h.event, request));
  await assert.rejects(handle(h.event, { method: 'getState' }, 'unexpected'));
  h.bridge.setEndpoint('https://127.0.0.1:23456');
  await assert.rejects(handle(h.event, { method: 'getState' }), { code: 'account_menu_unavailable' });
  h.frame.url = 'https://127.0.0.1:23456/';
  assert.equal((await handle(h.event, { method: 'getState' })).accounts.length, 1);
  await h.bridge.open();
  assert.equal(h.opened.length, 1);
  assert(h.opened[0].includes('model-selector-trigger'));
  assert(!h.opened[0].includes('selectAccount'));
  h.bridge.close(); h.bridge.close();
  assert.equal(h.handlers.has(CHANNEL), false);
  assert.equal(h.preloads.size, 0);
  await assert.rejects(handle(h.event, { method: 'getState' }), { code: 'account_menu_unavailable' });
});

test('IPC preserves only a bounded error code in Electron serialized messages', async () => {
  for (const code of ['native_view_provider_unavailable', 'PRIVATE: token=secret', undefined]) {
    const h = harness({ fail: Object.assign(new Error('PRIVATE: token=secret'), { code }) });
    h.bridge.setEndpoint('https://127.0.0.1:12345');
    const expected = code === 'native_view_provider_unavailable' ? code : 'account_operation_failed';
    await assert.rejects(h.handlers.get(CHANNEL)(h.event, { method: 'getState' }), error => {
      assert.equal(error.code, expected); assert.equal(error.message, expected);
      assert(!error.message.includes('PRIVATE')); return true;
    });
    h.bridge.close();
  }
});

test('label updates and consent preferences stay bounded and preferences never call the account broker', async () => {
  let saved = { dataUsageConsent: 'off' }, unavailable = false;
  const preferences = {
    async get() { if (unavailable) throw new Error('private path'); return saved; },
    async set(value) {
      if (!['off', 'on', 'manual'].includes(value.dataUsageConsent)) throw Object.assign(new Error(), { code: 'unsupported_parameter' });
      saved = { ...value }; return saved;
    },
  };
  const h = harness({ preferences }); h.bridge.setEndpoint('https://127.0.0.1:12345');
  const handle = request => h.handlers.get(CHANNEL)(h.event, request);
  try {
    assert.deepEqual(await handle({ method: 'getPreferences' }), { dataUsageConsent: 'off' });
    for (const dataUsageConsent of ['on', 'manual', 'off']) {
      assert.deepEqual(await handle({ method: 'setPreferences', params: { dataUsageConsent } }), { dataUsageConsent });
      assert.equal((await handle({ method: 'getState' })).preferences.dataUsageConsent, dataUsageConsent);
    }
    assert(h.calls.every(call => ['getStatus', 'listAccounts'].includes(call.method)));
    await handle({ method: 'renameAccount', params: { id: 'a', label: 'Work label' } });
    assert.deepEqual(h.calls.at(-1), { method: 'renameAccount', params: { id: 'a', label: 'Work label' } });
    for (const request of [
      { method: 'renameAccount', params: { label: 'Work' } },
      { method: 'renameAccount', params: { id: 'a' } },
      { method: 'renameAccount', params: { id: 'a', label: 'a\n' } },
      { method: 'renameAccount', params: { id: 'a', label: 'a'.repeat(81) } },
      { method: 'setPreferences', params: { dataUsageConsent: true } },
      { method: 'setPreferences', params: { dataUsageConsent: 'off', accessToken: 'private' } },
    ]) await assert.rejects(handle(request));
    unavailable = true;
    const state = await handle({ method: 'getState' });
    assert.equal(state.preferences.dataUsageConsent, 'manual');
    assert.equal(state.preferencesError, 'account_preferences_unavailable');
    assert(!JSON.stringify(state).includes('private'));
    await assert.rejects(handle({ method: 'getPreferences' }), { code: 'account_operation_failed' });
  } finally { h.bridge.close(); }
});

test('model-menu IPC rejects foreign frames, other origins, manager pages and destroyed windows', async () => {
  for (const change of [
    h => { h.event.senderFrame = { ...h.frame }; },
    h => { h.frame.parent = {}; },
    h => { h.frame.url = 'https://example.invalid/'; },
    h => { h.frame.url = 'file:///local/accounts/index.html'; },
    h => { h.frame.url = 'https://localhost:12345/'; },
    h => { h.event.sender = { mainFrame: h.frame }; },
    h => { h.destroy(); },
    h => { h.manager(); },
  ]) {
    const h = harness(); h.bridge.setEndpoint('https://127.0.0.1:12345'); change(h);
    await assert.rejects(h.handlers.get(CHANNEL)(h.event, { method: 'getState' }), { code: 'account_menu_unavailable' });
    assert.equal(h.calls.length, 0);
    assert.equal(h.opened.length, 0); h.bridge.close();
  }
  const h = harness();
  for (const url of ['http://127.0.0.1:12345', 'https://example.invalid:12345', 'https://127.0.0.1:12345/path'])
    assert.throws(() => h.bridge.setEndpoint(url), { code: 'native_endpoint_invalid' });
  h.bridge.close();
});
