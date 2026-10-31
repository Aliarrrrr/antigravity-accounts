'use strict';

function fail(code) { throw new Error(code); }

async function showMessage(options, message) {
  const dialog = options.electron?.dialog;
  if (typeof dialog?.showMessageBox !== 'function') fail('account_dialog_unavailable');
  const parent = options.parent;
  const parameters = { title: 'Antigravity', noLink: true, ...message };
  return parent && typeof parent.isDestroyed === 'function' && !parent.isDestroyed()
    ? dialog.showMessageBox(parent, parameters)
    : dialog.showMessageBox(parameters);
}

async function readAccounts(client) {
  const [status, accounts] = await Promise.all([
    client.request('getStatus', {}), client.request('listAccounts', {}),
  ]);
  if (!status || !Array.isArray(accounts)) fail('account_state_invalid');
  return { status, accounts };
}

function ready({ status, accounts }, expectedAccountId) {
  return status.configured && status.primaryAccountId &&
    accounts.some(account => account.id === status.primaryAccountId) &&
    status.selectedAccountId === expectedAccountId &&
    accounts.some(account => account.id === status.selectedAccountId && account.selected && !account.disabled) &&
    !status.selectedAccountDisabled && status.login?.state !== 'pending';
}

async function finishLogin(options, expectedAccountId) {
  const result = await showMessage(options, {
    type: 'info', message: '请在 Google 网页完成账号授权',
    detail: '完成后点击继续。尚未完成时可取消启动；不会重新发起授权。',
    buttons: ['继续', '取消启动'], defaultId: 0, cancelId: 1,
  });
  if (result.response !== 0) fail('account_selection_cancelled');
  const state = await readAccounts(options.client);
  if (state.status.login?.state === 'pending') fail('login_pending');
  if (state.status.login?.state !== 'succeeded') fail('account_login_failed');
  if (!ready(state, expectedAccountId)) fail('account_selection_not_ready');
}

async function ensureAccountReady(options) {
  // This recovery runs before the independent provider starts. Daily account
  // actions live in the native model menu; no separate renderer is created.
  const state = await readAccounts(options.client);
  const { status, accounts } = state;
  if (!status.configured || !status.primaryAccountId ||
      !accounts.some(account => account.id === status.primaryAccountId)) {
    await showMessage(options, {
      type: 'warning', message: '账号尚未配置完成，无法启动独立账号用量',
      detail: '请先在模型列表中添加账号并设置主账号，然后重新启动 Antigravity。',
      buttons: ['取消启动'], defaultId: 0, cancelId: 0,
    });
    fail('account_setup_required');
  }
  if (status.login?.state === 'pending') return finishLogin(options, status.selectedAccountId);

  const available = accounts.filter(account => !account.disabled);
  const selected = accounts.find(account => account.id === status.selectedAccountId);
  const buttons = available.map(account =>
    String(account.label || account.email || 'Google 账号') +
    (account.id === status.selectedAccountId ? '（当前）' : ''));
  const reauthorizeId = selected ? buttons.push('重新授权当前账号') - 1 : -1;
  const cancelId = buttons.push('取消启动') - 1;
  const result = await showMessage(options, {
    type: 'warning', message: '选择启动时使用的额度账号',
    detail: '所选账号尚未就绪。可选择已添加的可用账号，或重新授权当前账号。',
    buttons, defaultId: cancelId, cancelId,
  });
  if (Number.isInteger(result.response) && result.response >= 0 && result.response < available.length) {
    const id = available[result.response].id;
    await options.client.request('selectAccount', { id });
    if (!ready(await readAccounts(options.client), id)) fail('account_selection_not_ready');
    return;
  }
  if (selected && result.response === reauthorizeId) {
    await options.client.request('beginLogin', { label: selected.label || selected.email || '' });
    return finishLogin(options, selected.id);
  }
  fail('account_selection_cancelled');
}

function openAccountWindow(options) {
  if (typeof options?.openMenu !== 'function') return Promise.reject(new Error('account_menu_unavailable'));
  return Promise.resolve().then(() => options.openMenu());
}

// Kept for older host adapters. The model menu owns the account controls.
function installAccountMenu() {}
function isAccountWindow() { return false; }

module.exports = { ensureAccountReady, installAccountMenu, openAccountWindow, isAccountWindow };
