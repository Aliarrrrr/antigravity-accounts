// Serialized into an isolated Electron preload. Keep dependencies in this body.
export function installModelMenuEntry({ document, window, request } = {}) {
  if (!document?.documentElement || !window || typeof request !== 'function' ||
      typeof window.MutationObserver !== 'function') return null;
  const key = '__antigravityAccountsModelMenuEntryV2';
  if (window[key]) return window[key];
  const triggerSelector = 'button[data-testid="model-selector-trigger"]';
  const panelSelector = 'div[data-testid="model-selector-panel"]';
  const menuSelector = `${triggerSelector},${panelSelector}`;
  const nativeOptionSelector = ['model-selector-item', 'model-selector-effort-group', 'model-selector-effort-option',
    'model-selector-context-window-option', 'model-selector-custom-item'].map(id => `[data-testid="${id}"]`).join(',');
  const ownSelector = '[data-antigravity-accounts-menu]';
  const excluded = 'pre,code,textarea,[contenteditable],[data-message-role],[data-message-id],.markdown,.prose,[data-user-content],iframe';
  const entries = new Map();
  const buttonClass = 'flex w-full shrink-0 cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-left text-xs outline-none hover:bg-secondary focus-visible:bg-secondary disabled:cursor-default disabled:opacity-50';
  let stopped = false, queued = false, busy = false, loading = false, pendingRefresh = false, timer;
  let state, lastState = '', errorText = '', confirmingNativeSelection = false;
  const validPanel = panel => panel.isConnected && panel.matches(panelSelector) &&
    !panel.closest(excluded + ',[hidden],[aria-hidden="true"]') &&
    [...document.querySelectorAll(triggerSelector)].some(trigger => !trigger.closest(excluded) &&
      trigger.getAttribute('aria-expanded') !== 'false');
  const running = () => !stopped && [...entries.keys()].some(validPanel);
  const activeSwitch = () => ['waiting', 'confirming', 'blocked'].includes(state?.status?.switch?.state);

  function element(tag, className = '', text = '') {
    const node = document.createElement(tag);
    node.className = className; node.textContent = text; return node;
  }
  function trusted(event, button, entry) {
    return !stopped && event.isTrusted === true && event.currentTarget === button &&
      button.isConnected && entry.nodes.some(node => node.contains(button)) && validPanel(entry.panel);
  }
  function actionButton(entry, text, action, { disabled = false, id, role } = {}) {
    const button = element('button', buttonClass, text);
    button.type = 'button'; button.style.color = 'inherit'; button.style.font = 'inherit';
    button.disabled = disabled;
    if (id) button.setAttribute('data-account-id', id);
    if (role) button.setAttribute('role', role);
    button.addEventListener('click', event => {
      if (!trusted(event, button, entry) || button.disabled) return;
      event.preventDefault(); event.stopPropagation(); void action();
    });
    button.addEventListener('keydown', event => {
      if (!trusted(event, button, entry) || !['ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)) return;
      const buttons = [...entry.panel.querySelectorAll('button:not(:disabled),input:not(:disabled)')]
        .filter(node => !node.closest('[hidden],[aria-hidden="true"]'));
      const index = buttons.indexOf(button);
      if (index < 0 || !buttons.length) return;
      event.preventDefault(); event.stopPropagation();
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1 :
        (index + (event.key === 'ArrowDown' ? 1 : buttons.length - 1)) % buttons.length;
      buttons[next].focus();
    });
    return button;
  }
  function schedule() {
    window.clearTimeout(timer); timer = undefined;
    // Idle, closed menus do no work. Pending login/switch status is local IPC.
    if (running() && (busy || state?.status?.login?.state === 'pending' || activeSwitch()))
      timer = window.setTimeout(() => { void refresh(); }, 750);
  }
  async function operate(method, params = {}) {
    if (busy && method !== 'cancelSwitch') return;
    if (method === 'cancelSwitch') {
      try { await request(method, params); errorText = ''; }
      catch { errorText = '此阶段已不能取消，请等待账号确认。'; }
      await refresh(); return;
    }
    busy = true; errorText = ''; render(); schedule();
    try { await request(method, params); }
    catch { errorText = '操作未完成，请查看账号与切换状态后重试。'; }
    finally { busy = false; await refresh(); }
  }
  async function refresh() {
    if (!running()) return;
    if (loading) { pendingRefresh = true; return; }
    loading = true;
    try {
      const next = await request('getState');
      if (!next || !next.status || !Array.isArray(next.accounts)) throw new Error('Invalid account state');
      // Copy only public presentation fields; never retain arbitrary IPC data.
      const status = next.status;
      state = { status: {
        configured: status.configured === true, mode: status.mode, runtimeStarted: status.runtimeStarted === true,
        primaryAccountId: status.primaryAccountId, selectedAccountId: status.selectedAccountId,
        liveSwitchSupported: status.liveSwitchSupported === true, activationRequired: status.activationRequired === true,
        componentError: Boolean(status.componentError), selectedAccountDisabled: status.selectedAccountDisabled === true,
        login: { state: status.login?.state },
        switch: { state: status.switch?.state, targetAccountId: status.switch?.targetAccountId, cancellable: status.switch?.cancellable === true, errorCode: status.switch?.errorCode },
      }, accounts: next.accounts.slice(0, 100).filter(a => typeof a?.id === 'string' && a.id.length <= 100).map(a => ({
        id: a.id, label: typeof a.label === 'string' ? a.label : '', email: typeof a.email === 'string' ? a.email : '',
        disabled: a.disabled === true,
      })) };
      if (errorText === '无法读取本机账号。可关闭并重新打开模型菜单重试。') errorText = '';
    } catch { errorText = '无法读取本机账号。可关闭并重新打开模型菜单重试。'; }
    finally {
      loading = false; render();
      if (pendingRefresh) {
        pendingRefresh = false;
        if (running()) void refresh();
      } else schedule();
    }
  }
  function createEntry(panel) {
    const before = element('div', 'shrink-0');
    const after = element('div', 'shrink-0');
    const controls = element('div', 'shrink-0 border-t border-border px-2 py-2 text-xs');
    const message = element('div', 'text-muted-foreground py-1');
    message.setAttribute('role', 'status'); message.setAttribute('aria-live', 'polite');
    const entry = { panel, before, after, controls, message, nodes: [before, after, controls] };
    for (const node of entry.nodes) {
      node.setAttribute('data-antigravity-accounts-menu', '');
      node.style.color = 'inherit'; node.style.font = 'inherit';
    }
    const loginRow = element('div', 'flex items-center gap-1');
    const label = element('input', 'min-w-0 flex-1 rounded-md border border-border bg-transparent px-2 py-1 text-xs outline-none focus-visible:ring-1 focus-visible:ring-ring');
    label.type = 'text'; label.maxLength = 80; label.placeholder = '账号标签（可选）';
    label.setAttribute('aria-label', '新账号标签');
    label.style.width = '1px'; label.style.color = 'inherit'; label.style.font = 'inherit';
    const login = actionButton(entry, '添加账号', () => operate('beginLogin', { label: label.value.trim() }));
    label.addEventListener('keydown', event => {
      if (event.isTrusted !== true || event.key !== 'Enter' || event.repeat || busy || login.disabled || !validPanel(panel)) return;
      event.preventDefault(); event.stopPropagation(); void operate('beginLogin', { label: label.value.trim() });
    });
    // Keep text-entry keys out of the native menu's typeahead handler.
    label.addEventListener('keydown', event => { if (event.isTrusted === true) event.stopPropagation(); });
    login.style.width = 'auto';
    loginRow.append(label, login); controls.append(message, loginRow);
    entry.login = login; entry.label = label;
    entry.remove = () => { for (const node of entry.nodes) node.remove(); };
    return entry;
  }
  function render() {
    const signature = JSON.stringify({ state, busy, errorText });
    if (lastState === signature) return;
    lastState = signature;
    for (const entry of entries.values()) renderEntry(entry);
  }
  function renderEntry(entry) {
    const { before, after, controls, message } = entry;
    const focused = document.activeElement;
    const focusedId = focused?.getAttribute('data-account-id');
    before.replaceChildren(); after.replaceChildren();
    const status = state?.status, accounts = state?.accounts || [];
    const providerActive = status?.mode === 'native-provider' && status.runtimeStarted;
    const locked = providerActive;
    const switching = activeSwitch();
    const selected = accounts.find(a => a.id === status?.selectedAccountId);
    const primary = accounts.find(a => a.id === status?.primaryAccountId);
    before.append(element('div', 'px-2 pt-2 pb-1 text-xs text-muted-foreground', '账号与模型'));
    const current = element('div', 'px-2 py-1.5 text-xs font-medium');
    current.setAttribute('data-testid', 'antigravity-accounts-current-group');
    const selectionState = status?.switch?.state === 'confirming' ? '正在确认' : status?.switch?.state === 'blocked' ? '等待确认' : providerActive ? '正在使用' : '待启用';
    current.textContent = selected ? `${selected.label || selected.email || 'Google 账号'} · ${selectionState}` : 'Antigravity 当前登录';
    current.setAttribute('role', 'heading'); current.setAttribute('aria-level', '3');
    before.append(current);
    if (selected && status.switch?.state === 'blocked' && status.switch.targetAccountId === selected.id) {
      const recover = actionButton(entry, '重新确认此账号', () => operate('selectAccount', { id: selected.id }), {
        disabled: busy || !status.liveSwitchSupported, id: selected.id,
      });
      recover.setAttribute('data-testid', 'antigravity-accounts-reconfirm');
      before.append(recover);
    }
    for (const account of accounts) {
      if (account === selected) continue;
      const recovering = status.switch?.state === 'blocked' && status.switch.targetAccountId === account.id;
      const selectDisabled = busy || account.disabled || !primary || (providerActive && !status.liveSwitchSupported) || (switching && !recovering);
      const group = element('div', 'border-t border-border py-1');
      group.setAttribute('role', 'group'); group.setAttribute('aria-label', account.label || account.email || 'Google 账号');
      group.setAttribute('data-testid', 'antigravity-accounts-collapsed-group');
      const text = `${account.label || account.email || 'Google 账号'}${account.id === status.primaryAccountId ? ' · 主账号' : ''}${account.disabled ? ' · 需要重新授权' : recovering ? ' · 重新确认' : ' ▸'}`;
      const choose = actionButton(entry, text, () => operate('selectAccount', { id: account.id }), { disabled: selectDisabled, id: account.id, role: 'menuitemradio' });
      choose.setAttribute('aria-checked', 'false'); choose.setAttribute('aria-expanded', 'false');
      choose.title = account.disabled ? '此账号需要通过 Google 重新授权' : '选择此账号后显示其原生可用模型';
      group.append(choose);
      if (!primary && !locked) group.append(actionButton(entry, '设为主账号', () => operate('setPrimaryAccount', { id: account.id }), { disabled: busy || account.disabled, id: account.id }));
      if (!locked && account.id !== status.primaryAccountId) group.append(actionButton(entry, '移除账号', () => operate('removeAccount', { id: account.id }), { disabled: busy || switching, id: account.id }));
      after.append(group);
    }
    if (selected && !primary && !locked) after.prepend(actionButton(entry, '将当前账号设为主账号', () => operate('setPrimaryAccount', { id: selected.id }), { disabled: busy || selected.disabled, id: selected.id }));
    const failureCode = /^[a-z][a-z0-9_]{0,63}$/.test(status?.switch?.errorCode || '') ? status.switch.errorCode : '';
    const messages = {
      waiting: '等待生成、工具和后台任务结束；原任务继续执行。',
      confirming: '正在确认所选账号、项目与用量；新的发送暂时等待。',
      blocked: status?.switch?.errorCode === 'native_model_selection_required'
        ? '原模型不适用于此账号。请选择上方可用模型，确认后恢复发送。'
        : `账号确认尚未完成，新的发送已暂停。${failureCode ? `错误代码：${failureCode}。` : ''}`,
    };
    // A completed state read identifies a blocked switch more precisely than
    // the rejected IPC operation. Do not hide its recovery action or code.
    message.textContent = messages[status?.switch?.state] || errorText ||
      (status?.componentError ? '本机账号组件尚未就绪。' : status?.activationRequired ? '账号已配置，重新打开 Antigravity 后启用。' :
        status?.login?.state === 'pending' ? '请在官方 Google 页面完成授权。' :
        status?.login?.state === 'failed' ? 'Google 授权未完成，可重新添加账号。' :
        !state ? '正在读取本机账号…' : !primary ? '添加账号后，将一个账号设为主账号。' : '');
    entry.login.disabled = busy || !status?.configured || status.componentError || status.login?.state === 'pending' || switching;
    entry.login.textContent = status?.login?.state === 'pending' ? '等待授权…' : '添加账号';
    entry.label.disabled = entry.login.disabled;
    if (entry.cancel) { entry.cancel.remove(); entry.cancel = undefined; }
    if (status?.switch?.cancellable) {
      entry.cancel = actionButton(entry, '取消切换，继续原任务', () => operate('cancelSwitch'));
      controls.insertBefore(entry.cancel, message.nextSibling);
    }
    if (focusedId) [...entry.panel.querySelectorAll('button[data-account-id]')].find(b => b.getAttribute('data-account-id') === focusedId)?.focus();
  }
  function place(entry) {
    const { panel, before, after, controls } = entry;
    let native = panel.querySelector('[data-testid="model-selector-item"]');
    while (native && native.parentElement !== panel) native = native.parentElement;
    native ||= [...panel.children].find(node => !node.matches(ownSelector));
    // Only our siblings move. React retains ownership and parentage of every
    // native model option, its scroll container and the View Usage footer.
    if (native) {
      if (before.nextSibling !== native) panel.insertBefore(before, native);
      if (native.nextSibling !== after) native.after(after);
      if (after.nextSibling !== controls) after.after(controls);
    } else {
      for (const node of entry.nodes) if (node.parentElement !== panel) panel.append(node);
    }
  }
  function update() {
    queued = false;
    if (stopped) return;
    for (const [panel, entry] of entries) {
      if (validPanel(panel)) continue;
      entry.remove(); entries.delete(panel);
    }
    let added = false;
    for (const panel of document.querySelectorAll(panelSelector)) {
      if (!validPanel(panel)) continue;
      let entry = entries.get(panel);
      if (!entry) { entry = createEntry(panel); entries.set(panel, entry); renderEntry(entry); added = true; }
      place(entry);
    }
    if (!running()) { window.clearTimeout(timer); timer = undefined; pendingRefresh = false; }
    else if (added) void refresh();
  }
  function nativeModelSelection(event) {
    if (stopped || event.isTrusted !== true || confirmingNativeSelection ||
        state?.status?.switch?.state !== 'blocked' || state.status.switch.errorCode !== 'native_model_selection_required' ||
        event.type === 'keydown' && (event.repeat || !['Enter', ' '].includes(event.key))) return;
    const target = event.target;
    const option = target?.closest?.(nativeOptionSelector) || target?.closest?.('[role^="menuitem"],button')?.querySelector(nativeOptionSelector);
    if (!option?.isConnected || option.closest(excluded + ',[hidden],[aria-hidden="true"]') ||
        ![...document.querySelectorAll(triggerSelector)].some(trigger => !trigger.closest(excluded))) return;
    confirmingNativeSelection = true;
    // Let the native React handler finish its modelPicked action first.
    void Promise.resolve().then(() => request('confirmModelSelection')).catch(() => {
      errorText = '请选择该账号的可用模型，确认后恢复发送。';
    }).finally(() => { confirmingNativeSelection = false; void refresh(); });
  }
  document.addEventListener('click', nativeModelSelection, true);
  document.addEventListener('keydown', nativeModelSelection, true);
  function affectsMenu(record) {
    const target = record.target;
    if (target.closest?.(ownSelector)) return false;
    const panels = [...entries.keys()];
    const containsPanel = node => panels.some(panel => node === panel || node.contains?.(panel));
    if (record.type === 'attributes') {
      // A tracked panel can become hidden, retagged or moved into protected
      // content. Its controls must still be removed in those cases.
      if (containsPanel(target) || panels.some(panel => panel.contains(target))) return true;
      if (target.closest?.(excluded)) return false;
      return (record.attributeName === 'data-testid' &&
        ['model-selector-trigger', 'model-selector-panel'].includes(record.oldValue)) ||
        target.matches?.(menuSelector) || Boolean(target.querySelector?.(menuSelector));
    }
    const changed = [...record.addedNodes, ...record.removedNodes];
    if (changed.some(containsPanel)) return true;
    if (target.closest?.(excluded)) return false;
    if (panels.some(panel => panel === target || panel.contains(target))) return true;
    // Chat streaming and unrelated application updates do not rescan the
    // document. Portals may add or remove the whole selector subtree.
    return changed.some(node => node.nodeType === 1 &&
      !node.matches(`${ownSelector},${excluded}`) &&
      (node.matches(menuSelector) || node.querySelector(menuSelector)));
  }
  const observer = new window.MutationObserver(records => {
    if (queued || stopped) return;
    if (!records.some(affectsMenu)) return;
    queued = true; Promise.resolve().then(update);
  });
  observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true,
    attributeOldValue: true,
    attributeFilter: ['data-testid', 'hidden', 'aria-hidden', 'aria-expanded'] });
  const controller = Object.freeze({ stop() {
    if (stopped) return;
    stopped = true; pendingRefresh = false; observer.disconnect(); window.clearTimeout(timer);
    document.removeEventListener('click', nativeModelSelection, true);
    document.removeEventListener('keydown', nativeModelSelection, true);
    for (const entry of entries.values()) entry.remove(); entries.clear();
    state = undefined;
    if (window[key] === controller) delete window[key];
  } });
  window[key] = controller; update(); return controller;
}

export function modelMenuEntrySource() {
  return `\n// ANTIGRAVITY_ACCOUNTS_MODEL_MENU_V2\n(() => {
    if (process.isMainFrame !== true || window.top !== window ||
        location.protocol !== 'https:' || location.hostname !== '127.0.0.1') return;
    const { ipcRenderer } = require('electron');
    const start = () => (${installModelMenuEntry.toString()})({
      document, window,
      request: (method, params = {}) => ipcRenderer.invoke('antigravity-accounts:model-menu', { method, params }),
    });
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
  })();\n`;
}
