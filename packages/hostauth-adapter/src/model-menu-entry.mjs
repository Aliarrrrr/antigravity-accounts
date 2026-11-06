import { installAccountManagement } from './account-management.mjs';
import { installDataUsageConsent } from './data-usage-consent.mjs';
// Serialized into an isolated Electron preload. Keep dependencies in this body.
export function installModelMenuEntry({ document, window, request, createManagement = installAccountManagement } = {}) {
  if (!document?.documentElement || !window || typeof request !== 'function' ||
      typeof window.MutationObserver !== 'function') return null;
  const key = '__antigravityAccountsModelMenuEntryV2';
  if (window[key]) return window[key];
  const triggerSelector = '[data-testid="model-selector-trigger"]';
  const panelSelector = '[data-testid="model-selector-panel"]';
  const menuSelector = `${triggerSelector},${panelSelector}`;
  const nativeOptionSelector = ['model-selector-item', 'model-selector-effort-group', 'model-selector-effort-option',
    'model-selector-context-window-option', 'model-selector-custom-item'].map(id => `[data-testid="${id}"]`).join(',');
  const ownSelector = '[data-antigravity-accounts-menu]';
  const excluded = 'pre,code,textarea,[contenteditable],[data-message-role],[data-message-id],.markdown,.prose,[data-user-content],iframe';
  const entries = new Map();
  const buttonClass = 'flex w-full shrink-0 cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-left text-xs outline-none hover:bg-secondary focus-visible:bg-secondary disabled:cursor-default disabled:opacity-50';
  let stopped = false, queued = false, busy = false, loading = false, pendingRefresh = false, timer;
  let state, lastState = '', errorText = '', confirmingNativeSelection = false, management;
  const visibleTrigger = trigger => !trigger.closest(excluded + ',[hidden],[aria-hidden="true"]') &&
    trigger.getAttribute('aria-expanded') !== 'false';
  const validPanel = panel => panel.isConnected && panel.matches(panelSelector) &&
    !panel.closest(excluded + ',[hidden],[aria-hidden="true"]') &&
    [...document.querySelectorAll(triggerSelector)].some(visibleTrigger);
  const running = () => !stopped && (management?.isOpen() || [...entries.keys()].some(validPanel));
  const activeSwitch = () => ['waiting', 'confirming', 'blocked'].includes(state?.status?.switch?.state);

  function element(tag, className = '', text = '') {
    const node = document.createElement(tag);
    node.className = className; node.textContent = text; return node;
  }
  function trusted(event, button, entry) {
    return !stopped && event.isTrusted === true && event.currentTarget === button &&
      button.isConnected && entry.nodes.some(node => node.contains(button)) && validPanel(entry.panel);
  }
  function actionButton(entry, text, action, { disabled = false, id, role, actionId } = {}) {
    const button = element('button', buttonClass, text);
    button.type = 'button'; button.style.color = 'inherit'; button.style.fontFamily = 'inherit';
    button.disabled = disabled;
    if (id) button.setAttribute('data-account-id', id);
    if (actionId) button.setAttribute('data-account-action', actionId);
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
      buttons[next].focus({ preventScroll: true });
      buttons[next].scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
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
    catch { errorText = method === 'removeAccount'
      ? '删除未完成。请确认此账号未在使用或切换，再重新操作。'
      : method === 'setPreferences' ? '设置未保存，请重试。' : method === 'renameAccount' ? '标签未保存。请使用不超过 80 字的标签，并等待切换完成后重试。' : '操作未完成，请查看账号与切换状态后重试。'; }
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
        selectionLocked: status.selectionLocked === true,
        primaryAccountId: status.primaryAccountId, selectedAccountId: status.selectedAccountId,
        liveSwitchSupported: status.liveSwitchSupported === true, activationRequired: status.activationRequired === true,
        componentError: Boolean(status.componentError), selectedAccountDisabled: status.selectedAccountDisabled === true,
        login: { state: status.login?.state },
        switch: { state: status.switch?.state, targetAccountId: status.switch?.targetAccountId, cancellable: status.switch?.cancellable === true, errorCode: status.switch?.errorCode },
      }, preferences: { dataUsageConsent: ['off', 'on', 'manual'].includes(next.preferences?.dataUsageConsent) ? next.preferences.dataUsageConsent : 'off' },
      preferencesError: Boolean(next.preferencesError), accounts: next.accounts.slice(0, 100).filter(a => typeof a?.id === 'string' && a.id.length <= 100).map(a => ({
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
    const after = element('div', 'min-h-0 shrink-0 overflow-y-auto');
    // Account rows have their own bounded area, so a long account list cannot
    // push the native model options, add-account row or usage footer offscreen.
    after.style.maxHeight = 'min(32vh, 12rem)'; after.style.overscrollBehavior = 'contain';
    const controls = element('div', 'shrink-0 border-t border-border px-2 py-2 text-xs');
    const message = element('div', 'text-muted-foreground py-1');
    message.setAttribute('role', 'status'); message.setAttribute('aria-live', 'polite');
    const entry = { panel, before, after, controls, message, nodes: [before, after, controls],
      nativeMaxHeight: window.getComputedStyle?.(panel).maxHeight,
      panelStyles: Object.fromEntries(['max-height', 'display', 'overflow-y'].map(name => [name, {
        original: panel.style.getPropertyValue(name), priority: panel.style.getPropertyPriority(name),
      }])) };
    for (const node of entry.nodes) {
      node.setAttribute('data-antigravity-accounts-menu', '');
      node.style.color = 'inherit'; node.style.fontFamily = 'inherit';
    }
    const manage = actionButton(entry, '账号设置与管理…', () => management.open(manage), { role: 'menuitem', actionId: 'manage' });
    manage.setAttribute('data-testid', 'antigravity-accounts-manage');
    controls.append(message, manage);
    entry.remove = () => {
      for (const node of entry.nodes) node.remove();
      for (const name of Object.keys(entry.panelStyles)) restorePanelStyle(entry, name);
    };
    return entry;
  }
  function restorePanelStyle(entry, name) {
    const { panel } = entry, saved = entry.panelStyles[name];
    if (saved.applied !== undefined && panel.style.getPropertyValue(name) === saved.applied &&
        panel.style.getPropertyPriority(name) === saved.priority) {
      if (saved.original) panel.style.setProperty(name, saved.original, saved.priority);
      else panel.style.removeProperty(name);
    }
    delete saved.applied; delete saved.requested;
  }
  function setPanelStyle(entry, name, value) {
    const { panel } = entry, saved = entry.panelStyles[name];
    if (saved.requested !== value || panel.style.getPropertyValue(name) !== saved.applied ||
        panel.style.getPropertyPriority(name) !== saved.priority)
      panel.style.setProperty(name, value, saved.priority);
    saved.requested = value; saved.applied = panel.style.getPropertyValue(name);
  }
  function fitPanel(entry) {
    const { panel, before, after, controls, nativeMaxHeight, native } = entry;
    if (!panel.isConnected || !native?.isConnected || !nativeMaxHeight || nativeMaxHeight === 'none') return;
    // The host caps just its model list and footer at 20rem. Preserve that
    // allowance when inserting account sections, while using the host popup's
    // collision bounds on short windows. Do not change any native child style.
    const height = node => node.getBoundingClientRect().height;
    const rootFontSize = parseFloat(window.getComputedStyle(document.documentElement).fontSize) || 16;
    const accountLimit = Math.min(window.innerHeight * .32, rootFontSize * 12);
    const naturalAccounts = Math.min(Math.max(after.scrollHeight, height(after)), accountLimit);
    const hostAvailable = window.getComputedStyle(panel).getPropertyValue('--available-height').trim();
    const available = Math.max(0, Math.min(window.innerHeight - 16,
      /^\d+(?:\.\d+)?px$/.test(hostAvailable) ? parseFloat(hostAvailable) - 8 : Infinity));
    const firstOption = native.querySelector(nativeOptionSelector);
    const firstRow = firstOption?.closest('[role^="menuitem"],button') || firstOption;
    const header = native.querySelector('[data-testid="model-selector-header"]');
    let scroll = firstRow?.parentElement || native;
    while (scroll !== native && !/auto|scroll/.test(window.getComputedStyle(scroll).overflowY)) scroll = scroll.parentElement || native;
    const scrollStyle = window.getComputedStyle(scroll);
    const rowAllowance = (header ? height(header) : 0) + (firstRow ? height(firstRow) : 0) +
      (parseFloat(scrollStyle.paddingTop) || 0) + (parseFloat(scrollStyle.paddingBottom) || 0);
    const reservedModels = Math.min(scroll.scrollHeight || rowAllowance, rowAllowance);
    const fixedHeight = height(before) + height(controls) + [...panel.children]
      .filter(node => node !== native && !entry.nodes.includes(node)).reduce((sum, node) => sum + height(node), 0);
    const remainingAccounts = Math.max(0, available - fixedHeight - reservedModels);
    const firstAccountHeight = after.firstElementChild ? height(after.firstElementChild) : 0;
    const outerScroll = remainingAccounts < Math.min(naturalAccounts, firstAccountHeight);
    const accountsHeight = outerScroll ? naturalAccounts : Math.min(naturalAccounts, remainingAccounts);
    const accountMax = outerScroll || remainingAccounts >= accountLimit
      ? 'min(32vh, 12rem)' : `min(32vh, 12rem, ${Math.floor(remainingAccounts)}px)`;
    if (after.style.maxHeight !== accountMax) after.style.maxHeight = accountMax;
    if (outerScroll) {
      // At the smallest heights, keep every control reachable with one outer
      // scroll rather than collapsing the native model list to zero height.
      setPanelStyle(entry, 'display', 'block'); setPanelStyle(entry, 'overflow-y', 'auto');
    } else {
      restorePanelStyle(entry, 'display'); restorePanelStyle(entry, 'overflow-y');
    }
    const extraHeight = Math.ceil(height(before) + accountsHeight + height(controls));
    const maxHeight = `min(calc(${nativeMaxHeight} + ${extraHeight}px), calc(100dvh - 16px), max(0px, calc(var(--available-height, 100dvh) - 8px)))`;
    setPanelStyle(entry, 'max-height', maxHeight);
  }
  function render() {
    const signature = JSON.stringify({ state, busy, errorText });
    if (lastState === signature) return;
    lastState = signature;
    for (const entry of entries.values()) renderEntry(entry);
    management?.render();
  }
  function renderEntry(entry) {
    const { before, after, controls, message } = entry;
    const accountScrollTop = after.scrollTop;
    const focused = document.activeElement;
    const ownsFocus = entry.nodes.some(node => node.contains(focused));
    const focusedId = ownsFocus ? focused?.getAttribute('data-account-id') : null;
    const focusedAction = ownsFocus ? focused?.getAttribute('data-account-action') : null;
    before.replaceChildren(); after.replaceChildren();
    const status = state?.status, accounts = state?.accounts || [];
    const providerActive = status?.mode === 'native-provider' && status.runtimeStarted;
    const switching = activeSwitch();
    const selected = accounts.find(a => a.id === status?.selectedAccountId);
    const primary = accounts.find(a => a.id === status?.primaryAccountId);
    before.append(element('div', 'px-2 pt-2 pb-1 text-xs text-muted-foreground', '账号与模型'));
    const current = element('div', 'px-2 py-1.5 text-xs font-medium');
    current.setAttribute('data-testid', 'antigravity-accounts-current-group');
    current.style.overflow = 'hidden'; current.style.textOverflow = 'ellipsis'; current.style.whiteSpace = 'nowrap';
    const selectionState = status?.switch?.state === 'confirming' ? '正在确认' : status?.switch?.state === 'blocked' ? '等待确认' : providerActive ? '正在使用' : '待启用';
    current.textContent = selected ? `${selected.label || selected.email || 'Google 账号'} · ${selectionState}` : 'Antigravity 当前登录';
    current.title = current.textContent;
    current.setAttribute('role', 'heading'); current.setAttribute('aria-level', '3');
    const currentRow = element('div', 'flex items-center gap-1');
    current.style.minWidth = '0'; current.style.flex = '1';
    currentRow.append(current); before.append(currentRow);
    if (selected && status.switch?.state === 'blocked' && status.switch.targetAccountId === selected.id) {
      const recover = actionButton(entry, '重新确认此账号', () => operate('selectAccount', { id: selected.id }), {
        disabled: busy || !status.liveSwitchSupported, id: selected.id, actionId: 'reconfirm',
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
      const choose = actionButton(entry, text, () => operate('selectAccount', { id: account.id }), { disabled: selectDisabled, id: account.id, role: 'menuitemradio', actionId: 'select' });
      choose.style.minWidth = '0';
      choose.style.flex = '1';
      const accountLabel = element('span', 'min-w-0 flex-1 truncate', text);
      accountLabel.style.overflow = 'hidden'; accountLabel.style.textOverflow = 'ellipsis'; accountLabel.style.whiteSpace = 'nowrap';
      choose.replaceChildren(accountLabel);
      choose.setAttribute('aria-checked', 'false'); choose.setAttribute('aria-expanded', 'false');
      choose.title = `${account.label || account.email || 'Google 账号'} — ${account.disabled ? '此账号需要通过 Google 重新授权' : '选择此账号后显示其原生可用模型'}`;
      const accountRow = element('div', 'flex items-center gap-1');
      accountRow.append(choose); group.append(accountRow);
      after.append(group);
    }
    const failureCode = /^[a-z][a-z0-9_]{0,63}$/.test(status?.switch?.errorCode || '') ? status.switch.errorCode : '';
    const messages = {
      waiting: '等待生成、工具和后台任务结束；原任务继续执行。',
      confirming: '正在确认所选账号、项目与用量；新的发送暂时等待。',
      blocked: status?.switch?.errorCode === 'native_model_selection_required'
        ? '原模型不适用于此账号。请选择上方可用模型，确认后恢复发送。'
        : status?.switch?.errorCode === 'native_account_verification_required'
        ? 'Google 要求此账号完成身份验证，新的发送已暂停。请亲自在 Google 官方页面完成验证，再重新确认此账号。'
        : status?.switch?.errorCode === 'native_quota_refresh_failed'
        ? '此账号的额度信息刷新失败，新的发送已暂停。可重新确认此账号；持续失败时需检查该账号的服务权限。'
        : `账号确认尚未完成，新的发送已暂停。${failureCode ? `错误代码：${failureCode}。` : ''}`,
    };
    // A completed state read identifies a blocked switch more precisely than
    // the rejected IPC operation. Do not hide its recovery action or code.
    message.textContent = messages[status?.switch?.state] || errorText ||
      (status?.componentError ? '本机账号组件尚未就绪。' : status?.activationRequired ? '账号已配置，重新打开 Antigravity 后启用。' :
        status?.login?.state === 'pending' ? '请在官方 Google 页面完成授权。' :
        status?.login?.state === 'failed' ? 'Google 授权未完成，可重新添加账号。' :
        !state ? '正在读取本机账号…' : !primary ? '在账号设置与管理中添加账号并设定主账号。' : '');
    if (entry.cancel) { entry.cancel.remove(); entry.cancel = undefined; }
    if (status?.switch?.cancellable) {
      entry.cancel = actionButton(entry, '取消切换，继续原任务', () => operate('cancelSwitch'));
      controls.insertBefore(entry.cancel, message.nextSibling);
    }
    after.scrollTop = accountScrollTop;
    if (focusedId) [...entry.panel.querySelectorAll('button[data-account-id]')].find(b =>
      b.getAttribute('data-account-id') === focusedId && b.getAttribute('data-account-action') === focusedAction &&
      !b.disabled)?.focus({ preventScroll: true });
    fitPanel(entry);
  }
  function place(entry) {
    const { panel, before, after, controls } = entry;
    let native = panel.querySelector('[data-testid="model-selector-item"]');
    while (native && native.parentElement !== panel) native = native.parentElement;
    native ||= [...panel.children].find(node => !node.matches(ownSelector));
    entry.native = native;
    // Only our siblings move. React retains ownership and parentage of every
    // native model option, its scroll container and the View Usage footer.
    if (native) {
      if (before.nextSibling !== native) panel.insertBefore(before, native);
      if (native.nextSibling !== after) native.after(after);
      if (after.nextSibling !== controls) after.after(controls);
    } else {
      for (const node of entry.nodes) if (node.parentElement !== panel) panel.append(node);
    }
    fitPanel(entry);
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
        ![...document.querySelectorAll(triggerSelector)].some(visibleTrigger)) return;
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
  const resized = () => {
    if (stopped || queued || !running()) return;
    queued = true; Promise.resolve().then(update);
  };
  window.addEventListener('resize', resized);
  const controller = Object.freeze({ stop() {
    if (stopped) return;
    stopped = true; pendingRefresh = false; observer.disconnect(); window.clearTimeout(timer); management?.stop();
    document.removeEventListener('click', nativeModelSelection, true);
    document.removeEventListener('keydown', nativeModelSelection, true);
    window.removeEventListener('resize', resized);
    for (const entry of entries.values()) entry.remove(); entries.clear();
    state = undefined;
    if (window[key] === controller) delete window[key];
  } });
  management = createManagement({ document, window, operate, getState: () => state,
    isBusy: () => busy, getError: () => errorText,
    onOpen: () => { void refresh(); }, onRefresh: () => { void refresh(); }, onClose: () => { schedule(); },
  });
  window[key] = controller; update(); return controller;
}

export function modelMenuEntrySource() {
  return `\n// ANTIGRAVITY_ACCOUNTS_MODEL_MENU_V2\n(() => {
    if (process.isMainFrame !== true || window.top !== window ||
        location.protocol !== 'https:' || location.hostname !== '127.0.0.1') return;
    const { ipcRenderer, webFrame } = require('electron');
    const consentInstaller = ${JSON.stringify(installDataUsageConsent.toString())};
    const syncConsent = preferences => {
      const value = ['off', 'on', 'manual'].includes(preferences?.dataUsageConsent) ? preferences.dataUsageConsent : 'manual';
      void webFrame.executeJavaScript('(' + consentInstaller + ')({document,window,preference:' + JSON.stringify(value) + '})').catch(() => {});
    };
    ipcRenderer.on('antigravity-accounts:model-menu:preferences', (_event, preferences) => syncConsent(preferences));
    const start = () => {
      void ipcRenderer.invoke('antigravity-accounts:model-menu', { method: 'getPreferences', params: {} })
        .then(syncConsent).catch(() => syncConsent({ dataUsageConsent: 'manual' }));
      return (${installModelMenuEntry.toString()})({
        document, window, createManagement: (${installAccountManagement.toString()}),
        request: async (method, params = {}) => {
          const result = await ipcRenderer.invoke('antigravity-accounts:model-menu', { method, params });
          if (method === 'getState') syncConsent(result.preferences);
          if (method === 'setPreferences') syncConsent(result);
          return result;
        },
      });
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
  })();\n`;
}
