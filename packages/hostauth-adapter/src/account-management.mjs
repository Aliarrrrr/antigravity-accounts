// Serialized into the isolated model-menu preload. Keep this function self-contained.
export function installAccountManagement({ document, window, operate, getState, isBusy = () => false,
  getError = () => '', onOpen = () => {}, onClose = () => {}, onRefresh = () => {} } = {}) {
  if (!document?.body || !window || typeof operate !== 'function' || typeof getState !== 'function') return null;
  const rows = new Map();
  const buttonClass = 'inline-flex shrink-0 items-center justify-center rounded-md border border-border bg-transparent px-3 py-1.5 text-xs outline-none hover:bg-secondary focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-default disabled:opacity-50';
  const inputClass = 'min-w-0 rounded-md border border-border bg-transparent px-3 py-2 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50';
  let dialog, content, accountList, message, addLabel, addButton, consent, closeButton, empty;
  let stopped = false, opened = false, returnFocus, pendingRemoval;

  function element(tag, className = '', text = '') {
    const node = document.createElement(tag);
    node.className = className; node.textContent = text;
    return node;
  }
  function trusted(event, node) {
    return !stopped && opened && event.isTrusted === true && event.currentTarget === node &&
      node.isConnected && dialog.contains(node);
  }
  function focus(node) { node?.focus?.({ preventScroll: true }); }
  function action(text, name, callback, accountId) {
    const node = element('button', buttonClass, text);
    node.type = 'button'; node.setAttribute('data-account-action', name);
    node.style.fontFamily = 'inherit'; node.style.color = 'inherit';
    if (accountId) node.setAttribute('data-account-id', accountId);
    node.addEventListener('click', event => {
      if (!trusted(event, node) || node.disabled) return;
      event.preventDefault(); event.stopPropagation(); void callback();
    });
    return node;
  }
  function status() { return getState()?.status || {}; }
  function switching() { return ['waiting', 'confirming', 'blocked'].includes(status().switch?.state); }
  function unavailable() { return isBusy() || !status().configured || status().componentError || switching() || status().login?.state === 'pending'; }
  function locked() { return status().selectionLocked || status().mode === 'native-provider' && status().runtimeStarted; }
  function removalReason(account) {
    const s = status();
    if (isBusy() || switching()) return '请等待当前账号操作或切换完成。';
    if (s.login?.state === 'pending') return '请先完成或结束 Google 授权。';
    if (!s.configured || s.componentError) return '本机账号组件尚未就绪。';
    if (locked() && account.id === s.selectedAccountId) return '当前账号正在使用，请先切换到其他账号再删除。';
    if (account.id === s.primaryAccountId) {
      if (locked()) return '主账号不能在运行中删除。';
      if ((getState()?.accounts || []).length > 1) return '请先删除附属账号，再删除主账号。';
    }
    return '';
  }
  async function run(method, params) {
    pendingRemoval = undefined;
    try { await operate(method, params); }
    finally {
      if (opened) {
        render();
        // A focused select must also reflect a rejected write, rather than
        // appearing to have saved the user's unsaved choice.
        if (method === 'setPreferences') consent.value = preferenceValue();
      }
    }
  }
  function close() {
    if (!opened) return;
    opened = false; pendingRemoval = undefined;
    if (typeof dialog.close === 'function') dialog.close();
    else dialog.removeAttribute('open');
    onClose();
    const target = returnFocus?.isConnected ? returnFocus : document.querySelector('[data-testid="model-selector-trigger"]');
    focus(target); returnFocus = undefined;
  }
  function mount() {
    if (dialog) return;
    dialog = element('dialog', 'rounded-lg border border-border bg-background text-foreground shadow-lg');
    dialog.setAttribute('data-testid', 'antigravity-accounts-management');
    dialog.setAttribute('data-antigravity-accounts-management', '');
    dialog.setAttribute('aria-label', '账号设置与管理');
    dialog.setAttribute('aria-modal', 'true');
    dialog.style.cssText = 'padding:0;margin:auto;width:min(42rem,calc(100vw - 2rem));max-width:calc(100vw - 2rem);max-height:calc(100dvh - 2rem);overflow:hidden;pointer-events:auto;font-family:inherit;color:var(--color-foreground,CanvasText);background-color:var(--color-background,Canvas);';
    const theme = element('style');
    theme.textContent = 'dialog[data-antigravity-accounts-management]::backdrop{background:var(--overlay-background,rgba(0,0,0,.45))}';
    const header = element('div', 'flex items-center justify-between gap-4 border-b border-border px-5 py-4');
    const title = element('h2', 'text-sm font-semibold', '账号设置与管理'); title.style.margin = '0';
    closeButton = action('关闭', 'close', close); closeButton.setAttribute('aria-label', '关闭账号设置与管理');
    header.append(title, closeButton);
    content = element('div', 'space-y-6 px-5 py-4');
    content.style.cssText = 'overflow-y:auto;overscroll-behavior:contain;max-height:calc(100dvh - 7rem);';
    message = element('div', 'text-xs text-muted-foreground');
    message.setAttribute('role', 'status'); message.setAttribute('aria-live', 'polite');
    const accountsSection = element('section', 'space-y-3');
    accountsSection.setAttribute('aria-label', '已添加的账号');
    const accountsTitle = element('h3', 'text-sm font-medium', '账号');
    accountList = element('div', 'space-y-3');
    empty = element('div', 'rounded-md border border-border px-3 py-4 text-sm text-muted-foreground', '还没有添加账号。请在下方通过 Google 授权添加。');
    accountsSection.append(accountsTitle, accountList, empty);
    const addSection = element('section', 'space-y-2 border-t border-border pt-4');
    const addTitle = element('h3', 'text-sm font-medium', '添加账号');
    const addRow = element('div', 'flex flex-wrap items-center gap-2');
    addLabel = element('input', inputClass + ' flex-1'); addLabel.type = 'text'; addLabel.maxLength = 80;
    addLabel.placeholder = '账号标签（可选）'; addLabel.setAttribute('aria-label', '新账号标签');
    addLabel.style.cssText = 'min-width:10rem;font-family:inherit;color:inherit;';
    addButton = action('添加账号', 'add', () => run('beginLogin', { label: addLabel.value.trim() }));
    addLabel.addEventListener('keydown', event => {
      if (!trusted(event, addLabel) || event.key !== 'Enter' || event.repeat || event.isComposing || event.keyCode === 229 || addButton.disabled) return;
      event.preventDefault(); event.stopPropagation(); void run('beginLogin', { label: addLabel.value.trim() });
    });
    addRow.append(addLabel, addButton);
    addSection.append(addTitle, addRow, element('p', 'text-xs text-muted-foreground', '在 Google 官方网页完成授权。标签仅用于本机区分账号。'));
    const privacySection = element('section', 'space-y-2 border-t border-border pt-4');
    const consentLabel = element('label', 'block text-sm font-medium', '数据使用授权');
    consent = element('select', inputClass + ' w-full');
    consent.style.cssText = 'width:100%;font-family:inherit;color:inherit;background-color:inherit;';
    consent.setAttribute('aria-label', '数据使用授权'); consent.setAttribute('data-account-action', 'consent');
    for (const [value, label] of [['off', '自动确认，不勾选（默认）'], ['on', '自动确认，并勾选'], ['manual', '每次手动处理']]) {
      const option = element('option', '', label); option.value = value; consent.append(option);
    }
    consentLabel.append(consent);
    consent.addEventListener('change', event => {
      if (!trusted(event, consent) || consent.disabled || !['off', 'on', 'manual'].includes(consent.value)) return;
      void run('setPreferences', { dataUsageConsent: consent.value });
    });
    privacySection.append(consentLabel, element('p', 'text-xs text-muted-foreground', '账号切换后出现数据使用授权时，按此设置处理。修改后用于后续授权；选择手动处理可保留原生弹窗。'));
    content.append(message, accountsSection, addSection, privacySection);
    dialog.append(theme, header, content); document.body.append(dialog);
    dialog.addEventListener('cancel', event => {
      if (!trusted(event, dialog)) return;
      event.preventDefault(); close();
    });
    dialog.addEventListener('close', () => { if (opened) close(); });
    dialog.addEventListener('keydown', event => {
      if (!trusted(event, dialog)) return;
      // Account labels must not reach native menu typeahead or global shortcuts.
      event.stopPropagation();
      if (event.key === 'Escape' && !event.isComposing) { event.preventDefault(); close(); return; }
      if (event.key !== 'Tab') return;
      const available = [...dialog.querySelectorAll('button,input,select,[tabindex]')]
        .filter(node => !node.disabled && node.tabIndex >= 0 && !node.closest('[hidden]'));
      if (!available.length) { event.preventDefault(); focus(dialog); return; }
      const first = available[0], last = available.at(-1);
      if (event.shiftKey && (document.activeElement === first || !dialog.contains(document.activeElement))) {
        event.preventDefault(); focus(last);
      } else if (!event.shiftKey && (document.activeElement === last || !dialog.contains(document.activeElement))) {
        event.preventDefault(); focus(first);
      }
    });
  }
  function accountRow(account) {
    const row = element('div', 'space-y-2 rounded-md border border-border p-3');
    row.setAttribute('data-account-id', account.id);
    const identity = element('div', 'flex flex-wrap items-center justify-between gap-2');
    const name = element('div', 'min-w-0 text-sm font-medium'); name.style.overflowWrap = 'anywhere';
    const badge = element('span', 'text-xs text-muted-foreground');
    const email = element('div', 'text-xs text-muted-foreground'); email.style.overflowWrap = 'anywhere';
    identity.append(name, badge);
    const labelRow = element('div', 'flex flex-wrap items-center gap-2');
    const label = element('input', inputClass + ' flex-1');
    label.type = 'text'; label.maxLength = 80; label.style.cssText = 'min-width:10rem;font-family:inherit;color:inherit;';
    label.setAttribute('aria-label', `账号标签 ${account.email || account.id}`);
    label.setAttribute('data-account-id', account.id); label.setAttribute('data-account-action', 'label');
    let dirty = false;
    label.addEventListener('input', event => { if (trusted(event, label)) dirty = true; });
    const rename = action('保存标签', 'rename', async () => {
      await run('renameAccount', { id: account.id, label: label.value.trim() });
      // Keep unsaved text after a rejected operation; successful state reads
      // are the authority for the persisted label.
      const current = getState()?.accounts?.find(item => item.id === account.id);
      if (current && (current.label === label.value.trim() || !label.value.trim() && current.label === current.email)) dirty = false;
    }, account.id);
    label.addEventListener('keydown', event => {
      if (!trusted(event, label) || event.key !== 'Enter' || event.repeat || event.isComposing || event.keyCode === 229 || rename.disabled) return;
      event.preventDefault(); event.stopPropagation(); void run('renameAccount', { id: account.id, label: label.value.trim() });
    });
    labelRow.append(label, rename);
    const actions = element('div', 'flex flex-wrap items-center gap-2');
    const primary = action('设为主账号', 'primary', () => run('setPrimaryAccount', { id: account.id }), account.id);
    const reauthorize = action('重新授权', 'reauthorize', () => {
      const current = getState()?.accounts?.find(item => item.id === account.id);
      if (current) return run('beginLogin', { label: current.label || current.email || '' });
    }, account.id);
    const remove = action('删除账号', 'remove', () => {
      const current = getState()?.accounts?.find(item => item.id === account.id);
      if (!current || removalReason(current)) return;
      pendingRemoval = { ...current }; render(); focus(cancel);
      // Another window may have changed the selected account since opening.
      void onRefresh();
    }, account.id);
    remove.setAttribute('data-testid', 'antigravity-accounts-remove');
    remove.setAttribute('aria-label', `删除账号 ${account.email || account.label || 'Google 账号'}`);
    actions.append(primary, reauthorize, remove);
    const explanation = element('div', 'text-xs text-muted-foreground');
    const confirmation = element('div', 'space-y-2 border-t border-border pt-2');
    confirmation.setAttribute('data-testid', 'antigravity-accounts-remove-confirmation');
    confirmation.setAttribute('role', 'group'); confirmation.setAttribute('aria-label', '确认删除账号');
    const description = element('div', 'text-xs'); description.style.overflowWrap = 'anywhere';
    const confirmActions = element('div', 'flex flex-wrap items-center gap-2');
    const confirm = action('确认删除', 'remove-confirm', () => {
      const current = getState()?.accounts?.find(item => item.id === account.id);
      if (!current || !pendingRemoval || pendingRemoval.id !== account.id || pendingRemoval.email !== current.email ||
          pendingRemoval.label !== current.label || removalReason(current)) return;
      return run('removeAccount', { id: account.id });
    }, account.id);
    const cancel = action('取消', 'remove-cancel', () => { pendingRemoval = undefined; render(); focus(remove); }, account.id);
    confirmActions.append(cancel, confirm); confirmation.append(description, confirmActions);
    row.append(identity, email, labelRow, actions, explanation, confirmation);
    return { row, update(current) {
      const s = status();
      name.textContent = current.label || current.email || 'Google 账号'; email.textContent = current.email || '';
      badge.textContent = [current.id === s.selectedAccountId ? '当前账号' : '', current.id === s.primaryAccountId ? '主账号' : '', current.disabled ? '需要重新授权' : ''].filter(Boolean).join(' · ');
      if (!dirty && document.activeElement !== label) label.value = current.label || '';
      rename.disabled = Boolean(unavailable());
      label.disabled = rename.disabled;
      reauthorize.disabled = Boolean(unavailable());
      primary.hidden = Boolean(s.primaryAccountId || locked());
      primary.disabled = Boolean(unavailable() || current.disabled);
      const reason = removalReason(current);
      remove.disabled = Boolean(reason); remove.title = reason || '删除此插件在本机保存的账号授权';
      explanation.textContent = reason; explanation.hidden = !reason;
      confirmation.hidden = pendingRemoval?.id !== current.id;
      confirm.disabled = Boolean(reason);
      description.textContent = `删除 ${current.email || current.label || '此账号'}？仅删除本机插件保存的授权，再次使用需要重新添加。${reason}`;
    } };
  }
  function preferenceValue() {
    const value = getState()?.preferences?.dataUsageConsent;
    return ['off', 'on', 'manual'].includes(value) ? value : 'off';
  }
  function render() {
    if (stopped || !opened) return;
    const state = getState(), accounts = state?.accounts || [], s = state?.status || {};
    if (pendingRemoval && !accounts.some(account => account.id === pendingRemoval.id && account.email === pendingRemoval.email && account.label === pendingRemoval.label)) pendingRemoval = undefined;
    const scrollTop = content.scrollTop;
    for (const [id, row] of rows) if (!accounts.some(account => account.id === id)) { row.row.remove(); rows.delete(id); }
    accounts.forEach((account, index) => {
      let row = rows.get(account.id);
      if (!row) { row = accountRow(account); rows.set(account.id, row); }
      row.update(account);
      if (accountList.children[index] !== row.row) accountList.insertBefore(row.row, accountList.children[index] || null);
    });
    empty.hidden = accounts.length !== 0;
    addButton.disabled = Boolean(unavailable()); addLabel.disabled = addButton.disabled;
    addButton.textContent = s.login?.state === 'pending' ? '等待 Google 授权…' : '添加账号';
    consent.disabled = isBusy();
    if (document.activeElement !== consent) consent.value = preferenceValue();
    const messages = { waiting: '正在等待原生任务结束后切换账号。', confirming: '正在确认所选账号、项目与用量。', blocked: '账号切换等待确认，请在模型列表查看状态。' };
    message.textContent = getError() || (state?.preferencesError ? '无法读取数据使用授权设置，暂按手动处理。请重新保存设置。' : '') || messages[s.switch?.state] || (s.componentError ? '本机账号组件尚未就绪。' :
      s.login?.state === 'pending' ? '请在 Google 官方网页完成账号授权。' : s.login?.state === 'failed' ? 'Google 授权未完成，可重新添加账号。' :
      s.activationRequired ? '账号已配置，重新打开 Antigravity 后启用。' : !state ? '正在读取本机账号…' : !s.primaryAccountId ? '添加账号后，选择一个账号设为主账号。' : '');
    message.hidden = !message.textContent; content.scrollTop = scrollTop;
  }
  return Object.freeze({
    open(trigger) {
      if (stopped) return;
      if (opened) { focus(closeButton); return; }
      // Close the host menu through its own trigger before taking focus. Its
      // portal and outside-interaction handlers may then unmount normally.
      const modelTrigger = document.querySelector('[data-testid="model-selector-trigger"]');
      if (modelTrigger?.getAttribute('aria-expanded') === 'true') modelTrigger.click();
      mount(); returnFocus = trigger; opened = true; render();
      if (typeof dialog.showModal === 'function') dialog.showModal(); else dialog.setAttribute('open', '');
      focus(closeButton); onOpen();
    }, render,
    isOpen() { return opened && !stopped; },
    stop() { if (stopped) return; close(); stopped = true; dialog?.remove(); rows.clear(); },
  });
}
