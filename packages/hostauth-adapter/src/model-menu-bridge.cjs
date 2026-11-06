'use strict';

const { ComponentError } = require('./component-client.cjs');
const { loopbackOrigin } = require('./submission-gate.cjs');
const path = require('node:path');
const CHANNEL = 'antigravity-accounts:model-menu';
const ACTIONS = new Set(['getState', 'getPreferences', 'setPreferences', 'renameAccount', 'selectAccount', 'cancelSwitch', 'beginLogin', 'cancelLogin', 'setPrimaryAccount', 'removeAccount', 'confirmModelSelection']);

function publicState(status, accounts) {
  const pick = (source, names) => Object.fromEntries(names.filter(name => source?.[name] !== undefined).map(name => [name, source[name]]));
  return {
    status: {
      ...pick(status, ['configured', 'selectionLocked', 'selectedAccountId', 'primaryAccountId', 'selectedAccountDisabled',
        'generation', 'mode', 'runtimeStarted', 'activationRequired', 'liveSwitchSupported', 'componentError', 'startupAccountError']),
      login: pick(status?.login, ['state']),
      switch: pick(status?.switch, ['state', 'targetAccountId', 'cancellable', 'errorCode']),
    },
    accounts: Array.isArray(accounts) ? accounts.map(account => pick(account, ['id', 'label', 'email', 'selected', 'role', 'primaryAccountId', 'disabled'])) : [],
  };
}

function createModelMenuBridge({ electron, client, preferences, isAccountWindow = () => false }) {
  let origin, closed = false;
  function nativeWindow(event) {
    if (closed || !origin || !event?.sender || !event.senderFrame ||
        event.senderFrame !== event.sender.mainFrame || event.senderFrame.parent) throw new ComponentError('account_menu_unavailable');
    let address;
    try { address = new URL(event.senderFrame.url); } catch { throw new ComponentError('account_menu_unavailable'); }
    const window = electron.BrowserWindow.fromWebContents(event.sender);
    if (address.origin !== origin || !window || window.isDestroyed() || window.webContents !== event.sender || isAccountWindow(window))
      throw new ComponentError('account_menu_unavailable');
    return window;
  }
  const handler = async (event, request, ...extra) => {
    nativeWindow(event);
    if (extra.length || !request || typeof request !== 'object' || Array.isArray(request) ||
        Object.keys(request).some(key => !['method', 'params'].includes(key)) || !ACTIONS.has(request.method))
      throw new ComponentError('method_not_allowed');
    const { method } = request, params = request.params ?? {};
    const fields = method === 'setPreferences' ? ['dataUsageConsent'] : method === 'renameAccount' ? ['id', 'label'] :
      method === 'beginLogin' ? ['label'] : ['selectAccount', 'setPrimaryAccount', 'removeAccount'].includes(method) ? ['id'] : [];
    if (typeof params !== 'object' || Array.isArray(params) || Object.keys(params).some(key => !fields.includes(key)))
      throw new ComponentError('unsupported_parameter');
    if (fields[0] === 'id' && (typeof params.id !== 'string' || !params.id || params.id.length > 128)) throw new ComponentError('invalid_account');
    if ((fields.includes('label') && params.label !== undefined || method === 'renameAccount') &&
        (typeof params.label !== 'string' || params.label.length > 80 || /[\x00-\x1f\x7f-\x9f]/.test(params.label)))
      throw new ComponentError('unsupported_parameter');
    try {
      if (method === 'getPreferences') return preferences ? await preferences.get() : { dataUsageConsent: 'off' };
      if (method === 'setPreferences') {
        if (!preferences) throw new ComponentError('account_preferences_unavailable');
        const next = await preferences.set(params);
        for (const window of electron.BrowserWindow.getAllWindows()) {
          try {
            if (!window.isDestroyed() && new URL(window.webContents.getURL()).origin === origin)
              window.webContents.send?.(CHANNEL + ':preferences', next);
          } catch { /* A closing or navigating window does not undo a saved preference. */ }
        }
        return next;
      }
      if (method === 'getState') {
        const [status, accounts] = await Promise.all([client.request('getStatus', {}), client.request('listAccounts', {})]);
        let settings = { dataUsageConsent: 'off' }, preferencesError;
        try { if (preferences) settings = await preferences.get(); }
        catch { settings = { dataUsageConsent: 'manual' }; preferencesError = 'account_preferences_unavailable'; }
        return { ...publicState(status, accounts), preferences: settings, ...(preferencesError ? { preferencesError } : {}) };
      }
      await client.request(method, params);
      return { accepted: true };
    } catch (error) {
      const code = /^[a-z][a-z0-9_]{0,63}$/.test(error?.code || '') ? error.code : 'account_operation_failed';
      // Electron serializes Error.message, but drops custom properties such as
      // .code. Preserve only the bounded code; never forward a component message.
      throw new ComponentError(code, code);
    }
  };
  electron.ipcMain.handle(CHANNEL, handler);
  const session = electron.session.defaultSession;
  let preloadId;
  try {
    preloadId = session.registerPreloadScript({ type: 'frame', filePath: path.join(__dirname, 'model-menu-preload.cjs') });
  } catch {
    electron.ipcMain.removeHandler(CHANNEL);
    throw new ComponentError('account_menu_unavailable');
  }
  return Object.freeze({
    setEndpoint(baseUrl) {
      const next = loopbackOrigin(baseUrl);
      if (!next.startsWith('https://')) throw new ComponentError('native_endpoint_invalid');
      origin = next;
    },
    async open(parent) {
      if (closed || !origin) throw new ComponentError('account_menu_unavailable');
      const windows = parent ? [parent] : electron.BrowserWindow.getAllWindows();
      const window = windows.find(candidate => !candidate.isDestroyed() && !isAccountWindow(candidate) && (() => {
        try { return new URL(candidate.webContents.getURL()).origin === origin; } catch { return false; }
      })());
      if (!window) throw new ComponentError('account_menu_unavailable');
      window.show(); window.focus();
      // Native tray/application commands only open the existing selector.
      // Account changes still require trusted input in the isolated preload.
      await window.webContents.executeJavaScript(`(() => { const b = document.querySelector('button[data-testid="model-selector-trigger"]'); if (b && b.getAttribute('aria-expanded') !== 'true') b.click(); })()`);
    },
    close() {
      if (closed) return;
      closed = true;
      electron.ipcMain.removeHandler(CHANNEL);
      session.unregisterPreloadScript(preloadId);
    },
  });
}

module.exports = { CHANNEL, createModelMenuBridge, publicState };
