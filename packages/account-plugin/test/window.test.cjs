'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { ensureAccountReady, openAccountWindow, installAccountMenu, isAccountWindow } = require('../window.cjs');

function harness() {
  const calls = [], dialogs = [], responses = [];
  let status = { configured: true, primaryAccountId: 'primary', selectedAccountId: 'expired', selectedAccountDisabled: true, login: { state: 'idle' } };
  let accounts = [{ id: 'primary', label: '主账号' }, { id: 'expired', label: '当前账号', selected: true, disabled: true }, { id: 'attached', label: '附属账号' }];
  const electron = {
    BrowserWindow: class { constructor() { assert.fail('A separate account window must not be created'); } },
    dialog: { async showMessageBox(...args) {
      dialogs.push(args); const next = responses.shift();
      if (typeof next === 'function') return next(args.at(-1));
      return { response: next ?? args.at(-1).cancelId };
    } },
  };
  const client = { async request(method, params) {
    calls.push({ method, params });
    if (method === 'getStatus') return status;
    if (method === 'listAccounts') return accounts;
    if (method === 'selectAccount') {
      status = { ...status, selectedAccountId: params.id, selectedAccountDisabled: false };
      accounts = accounts.map(account => ({ ...account, selected: account.id === params.id }));
      return;
    }
    if (method === 'beginLogin') { status = { ...status, login: { state: 'pending' } }; return; }
    assert.fail(`Unexpected method: ${method}`);
  } };
  return { electron, client, calls, dialogs, responses,
    get status() { return status; }, get accounts() { return accounts; },
    setState(nextStatus, nextAccounts = accounts) { status = nextStatus; accounts = nextAccounts; },
  };
}

test('startup recovery selects an available account through a native dialog without obtaining a token', async () => {
  const h = harness(); h.responses.push(1);
  await ensureAccountReady(h);
  assert.deepEqual(h.dialogs[0][0].buttons, ['主账号', '附属账号', '重新授权当前账号', '取消启动']);
  assert(h.calls.some(call => call.method === 'selectAccount' && call.params.id === 'attached'));
  assert(!h.calls.some(call => call.method === 'getToken' || call.method === 'beginLogin'));
});

test('cancel and invalid responses reject startup without changing the selection', async () => {
  for (const response of [3, -1, 99, 0.5]) {
    const h = harness(); h.responses.push(response);
    await assert.rejects(ensureAccountReady(h), /account_selection_cancelled/);
    assert(!h.calls.some(call => call.method === 'selectAccount' || call.method === 'beginLogin'));
  }
});

test('missing setup cancels startup instead of falling back or adding an account', async () => {
  for (const [status, accounts] of [
    [{ configured: false, primaryAccountId: 'primary' }, [{ id: 'primary' }]],
    [{ configured: true }, []],
    [{ configured: true, primaryAccountId: 'missing' }, [{ id: 'other' }]],
  ]) {
    const h = harness(); h.setState(status, accounts);
    await assert.rejects(ensureAccountReady(h), /account_setup_required/);
    assert.deepEqual(h.dialogs[0][0].buttons, ['取消启动']);
    assert(!h.calls.some(call => call.method === 'selectAccount' || call.method === 'beginLogin'));
  }
});

test('startup recovery rechecks the selected account and configuration after a choice', async () => {
  const h = harness(); h.responses.push(0);
  const originalRequest = h.client.request;
  h.client.request = async (method, params) => {
    const result = await originalRequest(method, params);
    if (method === 'selectAccount') h.setState({ ...h.status, configured: false });
    return result;
  };
  await assert.rejects(ensureAccountReady(h), /account_selection_not_ready/);
});

test('native recovery dialog uses only a live optional parent', async () => {
  for (const destroyed of [false, true]) {
    const h = harness(); h.responses.push(0); h.parent = { isDestroyed() { return destroyed; } };
    await ensureAccountReady(h);
    assert.equal(h.dialogs[0].length, destroyed ? 1 : 2);
    if (!destroyed) assert.equal(h.dialogs[0][0], h.parent);
  }
});

test('a changed selection after the requested choice cannot start with another account', async () => {
  const h = harness(); h.responses.push(0);
  const originalRequest = h.client.request;
  h.client.request = async (method, params) => {
    const result = await originalRequest(method, params);
    if (method === 'selectAccount') {
      h.setState({ ...h.status, selectedAccountId: 'attached' },
        h.accounts.map(account => ({ ...account, selected: account.id === 'attached' })));
    }
    return result;
  };
  await assert.rejects(ensureAccountReady(h), /account_selection_not_ready/);
});

test('reauthorization starts once, waits for explicit browser completion, and checks the result', async () => {
  const h = harness(); h.responses.push(2, () => {
    h.setState({ ...h.status, selectedAccountDisabled: false, login: { state: 'succeeded' } },
      h.accounts.map(account => ({ ...account, disabled: false })));
    return { response: 0 };
  });
  await ensureAccountReady(h);
  assert.deepEqual(h.calls.filter(call => call.method === 'beginLogin'), [{ method: 'beginLogin', params: { label: '当前账号' } }]);
  assert.equal(h.dialogs.length, 2);
  assert(!h.calls.some(call => call.method === 'getToken'));
});

test('pending authorization never starts another login and cannot continue startup', async () => {
  for (const existingPending of [false, true]) {
    const h = harness();
    if (existingPending) { h.setState({ ...h.status, login: { state: 'pending' } }); h.responses.push(0); }
    else h.responses.push(2, 0);
    await assert.rejects(ensureAccountReady(h), /login_pending/);
    assert.equal(h.calls.filter(call => call.method === 'beginLogin').length, existingPending ? 0 : 1);
  }
});

test('failed browser authorization and cancel reject startup without token requests', async () => {
  for (const result of ['failed', 'cancelled']) {
    const h = harness(); h.responses.push(2, () => {
      h.setState({ ...h.status, login: { state: 'failed' } });
      return { response: result === 'cancelled' ? 1 : 0 };
    });
    await assert.rejects(ensureAccountReady(h), new RegExp(result === 'cancelled' ? 'account_selection_cancelled' : 'account_login_failed'));
    assert.equal(h.calls.filter(call => call.method === 'beginLogin').length, 1);
    assert(!h.calls.some(call => call.method === 'getToken'));
  }
});

test('selecting another Google account during reauthorization does not silently change the chosen account', async () => {
  const h = harness(); h.responses.push(2, () => {
    h.setState({ ...h.status, login: { state: 'succeeded' } }, [...h.accounts, { id: 'new', label: '其他账号' }]);
    return { response: 0 };
  });
  await assert.rejects(ensureAccountReady(h), /account_selection_not_ready/);
  assert(!h.calls.some(call => call.method === 'selectAccount'));
});

test('daily account entry delegates to the native model menu without contacting the component', async () => {
  const h = harness(); let opened = 0; h.openMenu = () => { opened++; return 'opened'; };
  assert.equal(await openAccountWindow(h), 'opened');
  installAccountMenu(h); installAccountMenu(h);
  assert.equal(opened, 1);
  assert.deepEqual(h.calls, []);
  assert.equal(isAccountWindow({}), false);
  await assert.rejects(openAccountWindow({}), /account_menu_unavailable/);
});

test('component and native dialog errors reject startup', async () => {
  const h = harness(); h.client.request = async () => { throw new Error('component_unavailable'); };
  await assert.rejects(ensureAccountReady(h), /component_unavailable/);
  const missing = harness(); missing.electron.dialog = undefined;
  await assert.rejects(ensureAccountReady(missing), /account_dialog_unavailable/);
});
