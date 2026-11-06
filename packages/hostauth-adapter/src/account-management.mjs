// Serialized into the isolated model-menu preload. Keep this function self-contained.
export function installAccountManagement({ document, window, operate, getState, isBusy = () => false,
  getError = () => '', onOpen = () => {}, onClose = () => {}, onRefresh = () => {} } = {}) {
  if (!document?.body || !window || typeof operate !== 'function' || typeof getState !== 'function') return null;
  const rows = new Map();
  const buttonClass = 'accounts-button inline-flex shrink-0 items-center justify-center rounded-md text-xs disabled:cursor-default disabled:opacity-50';
  const inputClass = 'accounts-input min-w-0 rounded-md border border-border bg-transparent text-xs disabled:opacity-50';
  let dialog, content, accountList, accountCount, message, addLabel, addButton, cancelLogin, consent, closeButton, empty;
  let stopped = false, opened = false, returnFocus, pendingRemoval;

  function element(tag, className = '', text = '') {
    const node = document.createElement(tag);
    node.className = className; node.textContent = text;
    return node;
  }
  function icon(name, size = 16) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    for (const [key, value] of Object.entries({ width: size, height: size, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor',
      'stroke-width': '1.7', 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true', focusable: 'false' })) svg.setAttribute(key, value);
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', { user: 'M20 21v-2a6 6 0 0 0-6-6h-4a6 6 0 0 0-6 6v2M16 6a4 4 0 1 1-8 0 4 4 0 0 1 8 0',
      close: 'M6 6l12 12M18 6 6 18', check: 'm5 12 4 4L19 6', plus: 'M12 5v14M5 12h14' }[name]);
    svg.append(path); return svg;
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
    dialog.style.cssText = 'padding:0;margin:auto;width:min(40rem,calc(100vw - 2rem));max-width:calc(100vw - 2rem);max-height:calc(100dvh - 2rem);overflow:hidden;pointer-events:auto;font-family:inherit;color:var(--color-foreground,CanvasText);background-color:var(--color-background,Canvas);';
    const theme = element('style');
    const scope = 'dialog[data-antigravity-accounts-management]';
    theme.textContent = `${scope}::backdrop{background:var(--overlay-background,rgba(0,0,0,.45))}
      ${scope} .accounts-button{min-height:30px;gap:6px;padding:5px 10px;border:1px solid transparent;line-height:18px;cursor:pointer;background:transparent}
      ${scope} .accounts-button:hover:not(:disabled){background:var(--color-secondary,ButtonFace)}
      ${scope} .accounts-button:disabled{cursor:default}
      ${scope} .accounts-button[data-variant=secondary]{border-color:var(--color-border,ButtonBorder)}
      ${scope} .accounts-button[data-variant=primary]{background:var(--color-primary,Highlight);color:var(--color-primary-foreground,HighlightText)!important}
      ${scope} .accounts-button[data-variant=primary]:hover:not(:disabled){filter:brightness(.94)}
      ${scope} .accounts-button[data-variant=danger]:not(:disabled){color:var(--color-error,CanvasText)!important}
      ${scope} .accounts-input{height:32px;padding:5px 9px;line-height:20px;font-family:inherit;color:inherit}
      ${scope} .accounts-button:focus-visible,${scope} .accounts-input:focus-visible{outline:2px solid var(--focus-ring-color,var(--color-primary,Highlight));outline-offset:2px}
      ${scope} .accounts-close{width:30px;padding:5px;color:var(--color-muted-foreground,GrayText)!important}
      ${scope} .accounts-list{border:1px solid var(--color-border,ButtonBorder);border-radius:8px;overflow:hidden}
      ${scope} .accounts-row{padding:14px 16px}
      ${scope} .accounts-row+.accounts-row{border-top:1px solid var(--color-border,ButtonBorder)}
      ${scope} .accounts-avatar{display:flex;align-items:center;justify-content:center;flex:none;width:30px;height:30px;border-radius:50%;background:var(--color-secondary,ButtonFace);color:var(--color-muted-foreground,GrayText)}
      ${scope} .accounts-avatar[data-current=true]{color:var(--color-primary,Highlight)}
      ${scope} .accounts-badges{display:flex;flex-wrap:wrap;gap:4px;margin-top:4px}
      ${scope} .accounts-badge{display:inline-flex;align-items:center;gap:3px;padding:1px 6px;border-radius:4px;background:var(--color-secondary,ButtonFace);color:var(--color-muted-foreground,GrayText);font-size:.6875rem;line-height:16px}
      ${scope} .accounts-badge[data-current=true]{color:var(--color-primary,Highlight)}
      ${scope} .accounts-label-row{display:grid;grid-template-columns:auto minmax(0,1fr) auto;align-items:center;gap:8px;margin-top:12px;margin-inline-start:42px}
      ${scope} .accounts-actions{display:flex;flex-wrap:wrap;gap:2px;margin-top:6px;margin-inline-start:32px}
      ${scope} .accounts-explanation{margin:4px 0 0 42px;font-size:.75rem;color:var(--color-muted-foreground,GrayText)}
      ${scope} .accounts-confirm{margin-top:10px;padding:10px 12px;border-radius:6px;background:var(--color-secondary,ButtonFace)}
      ${scope} .accounts-status{padding:10px 12px;border-radius:6px;background:var(--color-secondary,ButtonFace);font-size:.75rem;line-height:1.5}
      ${scope} .accounts-consent-label{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap}
      ${scope} .accounts-consent-label select{width:min(100%,17rem);height:34px;background-color:var(--color-background,Canvas)}
      @media(max-width:480px){${scope} .accounts-row{padding:12px}${scope} .accounts-label-row{margin-inline-start:0}${scope} .accounts-actions,${scope} .accounts-explanation{margin-inline-start:0}${scope} .accounts-consent-label select{width:100%}}
      @media(forced-colors:active){${scope} .accounts-badge{border:1px solid CanvasText}${scope} .accounts-avatar{border:1px solid CanvasText}}`;
    const header = element('div', 'flex items-start justify-between gap-4 border-b border-border px-5 py-4');
    const heading = element('div', 'space-y-1');
    const title = element('h2', 'text-sm font-semibold', '账号设置与管理'); title.style.margin = '0';
    heading.append(title, element('div', 'text-xs text-muted-foreground', '管理已授权的 Google 账号'));
    closeButton = action('关闭', 'close', close); closeButton.setAttribute('aria-label', '关闭账号设置与管理');
    closeButton.classList.add('accounts-close'); closeButton.replaceChildren(icon('close'));
    header.append(heading, closeButton);
    content = element('div', 'space-y-6 px-5 py-4');
    content.style.cssText = 'overflow-y:auto;overscroll-behavior:contain;max-height:calc(100dvh - 8rem);';
    message = element('div', 'accounts-status');
    message.setAttribute('role', 'status'); message.setAttribute('aria-live', 'polite');
    const accountsSection = element('section', 'space-y-3');
    accountsSection.setAttribute('aria-label', '已添加的账号');
    const accountsTitle = element('h3', 'flex items-center gap-2 text-xs font-medium', '已添加账号');
    accountsTitle.style.margin = '0'; accountCount = element('span', 'text-muted-foreground'); accountsTitle.append(accountCount);
    accountList = element('div', 'accounts-list');
    empty = element('div', 'rounded-md border border-border px-3 py-4 text-sm text-muted-foreground', '还没有添加账号。请在下方通过 Google 授权添加。');
    accountsSection.append(accountsTitle, accountList, empty);
    const addSection = element('section', 'space-y-2 border-t border-border pt-4');
    const addTitle = element('h3', 'text-xs font-medium', '添加账号'); addTitle.style.margin = '0';
    const addRow = element('div', 'flex flex-wrap items-center gap-2');
    addLabel = element('input', inputClass + ' flex-1'); addLabel.type = 'text'; addLabel.maxLength = 80;
    addLabel.placeholder = '账号标签（可选）'; addLabel.setAttribute('aria-label', '新账号标签');
    addLabel.style.cssText = 'min-width:10rem;font-family:inherit;color:inherit;';
    addButton = action('添加账号', 'add', () => run('beginLogin', { label: addLabel.value.trim() }));
    addButton.setAttribute('data-variant', 'primary'); addButton.prepend(icon('plus', 14));
    addLabel.addEventListener('keydown', event => {
      if (!trusted(event, addLabel) || event.key !== 'Enter' || event.repeat || event.isComposing || event.keyCode === 229 || addButton.disabled) return;
      event.preventDefault(); event.stopPropagation(); void run('beginLogin', { label: addLabel.value.trim() });
    });
    cancelLogin = action('取消授权', 'cancel-login', () => run('cancelLogin', {}));
    cancelLogin.hidden = true;
    addRow.append(addLabel, addButton, cancelLogin);
    addSection.append(addTitle, addRow, element('p', 'text-xs text-muted-foreground', '在 Google 官方网页完成授权。标签仅用于本机区分账号。'));
    const privacySection = element('section', 'space-y-2 border-t border-border pt-4');
    const consentLabel = element('label', 'accounts-consent-label text-xs font-medium', '数据使用授权');
    consent = element('select', inputClass);
    consent.setAttribute('aria-label', '数据使用授权'); consent.setAttribute('data-account-action', 'consent');
    for (const [value, label] of [['off', '自动确认，不勾选（默认）'], ['on', '自动确认，并勾选'], ['manual', '每次手动处理']]) {
      const option = element('option', '', label); option.value = value; consent.append(option);
    }
    consentLabel.append(consent);
    consent.addEventListener('change', event => {
      if (!trusted(event, consent) || consent.disabled || !['off', 'on', 'manual'].includes(consent.value)) return;
      void run('setPreferences', { dataUsageConsent: consent.value });
    });
    privacySection.append(consentLabel, element('p', 'text-xs text-muted-foreground', '切换后出现授权提示时按此处理。修改用于后续提示，选择手动处理可保留原生弹窗。'));
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
    const row = element('div', 'accounts-row');
    row.setAttribute('data-account-id', account.id);
    const identity = element('div', 'flex items-start gap-3');
    const avatar = element('div', 'accounts-avatar'); avatar.setAttribute('aria-hidden', 'true'); avatar.append(icon('user'));
    const details = element('div', 'min-w-0 flex-1');
    const name = element('div', 'min-w-0 text-sm font-medium'); name.style.overflowWrap = 'anywhere';
    const badge = element('div', 'accounts-badges');
    const email = element('div', 'text-xs text-muted-foreground'); email.style.overflowWrap = 'anywhere';
    details.append(name, email, badge); identity.append(avatar, details);
    const labelRow = element('div', 'accounts-label-row');
    const label = element('input', inputClass);
    label.type = 'text'; label.maxLength = 80; label.style.minWidth = '0';
    label.setAttribute('aria-label', `账号标签 ${account.email || account.id}`);
    label.setAttribute('data-account-id', account.id); label.setAttribute('data-account-action', 'label');
    let dirty = false;
    label.addEventListener('input', event => { if (trusted(event, label)) dirty = true; });
    const saveLabel = async () => {
      await run('renameAccount', { id: account.id, label: label.value.trim() });
      // Keep unsaved text after a rejected operation; successful state reads
      // are the authority for the persisted label.
      const current = getState()?.accounts?.find(item => item.id === account.id);
      if (current && (current.label === label.value.trim() || !label.value.trim() && current.label === current.email)) dirty = false;
    };
    const rename = action('保存标签', 'rename', saveLabel, account.id);
    rename.setAttribute('data-variant', 'secondary');
    label.addEventListener('keydown', event => {
      if (!trusted(event, label) || event.key !== 'Enter' || event.repeat || event.isComposing || event.keyCode === 229 || rename.disabled) return;
      event.preventDefault(); event.stopPropagation(); void saveLabel();
    });
    const labelCaption = element('span', 'text-xs text-muted-foreground', '标签'); labelCaption.setAttribute('aria-hidden', 'true');
    labelRow.append(labelCaption, label, rename);
    const actions = element('div', 'accounts-actions');
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
    remove.setAttribute('data-variant', 'danger');
    remove.setAttribute('aria-label', `删除账号 ${account.email || account.label || 'Google 账号'}`);
    actions.append(primary, reauthorize, remove);
    const explanation = element('div', 'accounts-explanation');
    const confirmation = element('div', 'accounts-confirm space-y-2');
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
    confirm.setAttribute('data-variant', 'danger');
    const cancel = action('取消', 'remove-cancel', () => { pendingRemoval = undefined; render(); focus(remove); }, account.id);
    confirmActions.append(cancel, confirm); confirmation.append(description, confirmActions);
    row.append(identity, labelRow, actions, explanation, confirmation);
    return { row, update(current) {
      const s = status();
      name.textContent = current.label || current.email || 'Google 账号'; email.textContent = current.email || '';
      email.hidden = !current.email || (current.label || '').trim().toLowerCase() === current.email.trim().toLowerCase();
      avatar.setAttribute('data-current', String(current.id === s.selectedAccountId));
      badge.replaceChildren();
      for (const [shown, text, active] of [[current.id === s.selectedAccountId, '当前账号', true], [current.id === s.primaryAccountId, '主账号'], [current.disabled, '需要重新授权']]) {
        if (!shown) continue;
        const tag = element('span', 'accounts-badge', text);
        if (active) { tag.setAttribute('data-current', 'true'); tag.prepend(icon('check', 12)); }
        badge.append(tag);
      }
      badge.hidden = !badge.childElementCount;
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
    let removedFocusIndex;
    for (const [id, row] of rows) if (!accounts.some(account => account.id === id)) {
      if (row.row.contains(document.activeElement)) removedFocusIndex = [...accountList.children].indexOf(row.row);
      row.row.remove(); rows.delete(id);
    }
    accounts.forEach((account, index) => {
      let row = rows.get(account.id);
      if (!row) { row = accountRow(account); rows.set(account.id, row); }
      row.update(account);
      if (accountList.children[index] !== row.row) accountList.insertBefore(row.row, accountList.children[index] || null);
    });
    empty.hidden = accounts.length !== 0;
    accountList.hidden = !accounts.length; accountCount.textContent = String(accounts.length);
    addButton.disabled = Boolean(unavailable()); addLabel.disabled = addButton.disabled;
    addButton.replaceChildren(icon('plus', 14), document.createTextNode(s.login?.state === 'pending' ? '等待 Google 授权…' : '添加账号'));
    const cancellingFocused = document.activeElement === cancelLogin;
    cancelLogin.hidden = s.login?.state !== 'pending';
    cancelLogin.disabled = Boolean(isBusy() || !s.configured || s.componentError);
    if (cancellingFocused && cancelLogin.hidden) focus(!addButton.disabled ? addButton : closeButton);
    consent.disabled = isBusy();
    if (document.activeElement !== consent) consent.value = preferenceValue();
    const messages = { waiting: '正在等待原生任务结束后切换账号。', confirming: '正在确认所选账号、项目与用量。', blocked: '账号切换等待确认，请在模型列表查看状态。' };
    message.textContent = getError() || (state?.preferencesError ? '无法读取数据使用授权设置，暂按手动处理。请重新保存设置。' : '') || messages[s.switch?.state] || (s.componentError ? '本机账号组件尚未就绪。' :
      s.login?.state === 'pending' ? '请在 Google 官方网页完成账号授权，也可取消本次等待。' : s.login?.state === 'cancelled' ? '已取消本次授权，已添加的账号保持不变。' : s.login?.state === 'failed' ? 'Google 授权未完成，可重新添加账号。' :
      s.activationRequired ? '账号已配置，重新打开 Antigravity 后启用。' : !state ? '正在读取本机账号…' : !s.primaryAccountId ? '添加账号后，选择一个账号设为主账号。' : '');
    message.hidden = !message.textContent; content.scrollTop = scrollTop;
    if (removedFocusIndex !== undefined) {
      const neighbor = accountList.children[Math.min(removedFocusIndex, accountList.children.length - 1)];
      focus(neighbor?.querySelector('button:not(:disabled),input:not(:disabled)') || (!addButton.disabled ? addButton : closeButton));
    }
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
