'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { ComponentClient, ComponentError } = require('./component-client.cjs');
const { SwitchCoordinator } = require('./switch-coordinator.cjs');
const { TokenBarrier } = require('./token-barrier.cjs');
const { createModelMenuBridge } = require('./model-menu-bridge.cjs');
const { refreshNativeViews } = require('./native-view.cjs');
const { createNativeTaskAdapter, PROGRAM_SHA256, PATCHED_PROGRAM_SHA256, PROVIDER_PROGRAM_SHA256 } = require('./native-task-adapter.cjs');

const PROVIDER_NATIVE_PATH = 'hostauth-addon/native/language_server.exe';

const PUBLIC_METHODS = new Set(['getStatus', 'listAccounts', 'selectAccount', 'setPrimaryAccount', 'cancelSwitch', 'beginLogin', 'removeAccount', 'confirmModelSelection']);
const LOCKED_METHODS = new Set(['setPrimaryAccount', 'configureOAuth', 'removeAccount']);
const STARTUP_ACCOUNT_ERRORS = new Set(['account_reauth_required', 'account_not_selected', 'oauth_request_failed',
  'oauth_response_invalid', 'oauth_refresh_token_missing', 'operation_cancelled', 'account_operation_failed']);

function tokenInfo(result, now = Date.now()) {
  if (!result || typeof result.accessToken !== 'string' || !result.accessToken ||
      typeof result.tokenType !== 'string' || result.tokenType.toLowerCase() !== 'bearer' ||
      typeof result.refreshToken !== 'string' || !result.refreshToken ||
      typeof result.expiry !== 'string' || result.isGcpTos !== false) {
    throw new ComponentError('invalid_token_response', 'The independent account component returned an invalid OAuth token.');
  }
  const millis = Date.parse(result.expiry);
  if (!Number.isFinite(millis) || millis <= now + 30000) throw new ComponentError('token_expiry_invalid', 'The selected account does not have a usable OAuth token.');
  const seconds = Math.floor(millis / 1000);
  return {
    accessToken: result.accessToken,
    tokenType: result.tokenType,
    refreshToken: result.refreshToken,
    expiry: { seconds: BigInt(seconds), nanos: (millis - seconds * 1000) * 1000000 },
    isGcpTos: result.isGcpTos,
  };
}

function componentPath(resourcesPath, config, override = process.env.ANTIGRAVITY_ACCOUNTS_COMPONENT) {
  if (override) {
    if (!path.isAbsolute(override)) throw new ComponentError('component_path_invalid');
    return override;
  }
  if (!config || typeof config.component !== 'string' || path.isAbsolute(config.component)) throw new ComponentError('component_path_invalid');
  const root = path.resolve(resourcesPath, 'hostauth-addon');
  const resolved = path.resolve(root, config.component);
  if (!resolved.startsWith(root + path.sep)) throw new ComponentError('component_path_invalid');
  return resolved;
}

function providerStatePath(electron) {
  const root = process.env.LOCALAPPDATA || electron?.app?.getPath('appData');
  if (!root || !path.isAbsolute(root)) throw new ComponentError('provider_state_invalid');
  return path.join(root, 'AntigravityAccounts', 'host-provider.json');
}

function readProviderState(filename) {
  try {
    const value = JSON.parse(fs.readFileSync(filename, 'utf8'));
    if (value?.schema !== 1 || value.enabled !== true || Object.keys(value).length !== 2) throw new Error();
    return true;
  } catch (error) {
    if (error.code === 'ENOENT') return false;
    throw new ComponentError('provider_state_invalid');
  }
}

function rememberProvider(filename) {
  fs.mkdirSync(path.dirname(filename), { recursive: true });
  const pending = filename + `.${process.pid}.tmp`;
  try {
    fs.writeFileSync(pending, '{"schema":1,"enabled":true}\n', { mode: 0o600 });
    fs.renameSync(pending, filename);
  } catch {
    try { fs.unlinkSync(pending); } catch { /* no credentials are stored here */ }
    throw new ComponentError('provider_state_write_failed');
  }
}

async function prepare({ electron, parent, resourcesPath = process.resourcesPath, wsl, headless, onFatal = () => {}, clientFactory, accountUI, taskAdapter, nativeProgramVerifier, activationStatePath } = {}) {
  if (wsl || headless) throw new ComponentError('host_mode_unsupported', 'This account adapter requires the local Windows desktop host.');
  if (activationStatePath === undefined) activationStatePath = providerStatePath(electron);
  // This file belongs to the plugin and contains only an enabled flag. Once an
  // independent provider has been used, a failed broker cannot silently switch
  // the native process back to the original account on a subsequent launch.
  if (!path.isAbsolute(activationStatePath)) throw new ComponentError('provider_state_invalid');
  const providerWasEnabled = readProviderState(activationStatePath);
  const ui = accountUI || require('./account-plugin/window.cjs');
  let client;
  let mode = providerWasEnabled ? 'native-provider' : 'registration-only';
  let componentError;
  let startupAccountError;
  let componentReady = false;
  let locked = false;
  let ended = false;
  let nativeProgramSha256;
  let selectedNativeProgramPath;
  let nativeEndpoint;
  let coordinator;
  const componentFailed = (code) => {
    componentReady = false;
    componentError = /^[a-z][a-z0-9_]{0,63}$/.test(code || '') ? code : 'component_unavailable';
    if (mode === 'native-provider' && !ended) onFatal(componentError);
  };
  const tokenBarrier = new TokenBarrier(async () => {
    if (mode !== 'native-provider') throw new ComponentError('provider_not_active');
    const result = await client.request('getToken');
    const token = tokenInfo(result);
    coordinator.observeToken(result);
    return token;
  });
  coordinator = new SwitchCoordinator({ client: { request: (...args) => client.request(...args) }, taskAdapter, tokenBarrier });
  const publicClient = Object.freeze({
    async request(method, params) {
      if (!PUBLIC_METHODS.has(method)) return Promise.reject(new ComponentError('method_not_allowed'));
      if (ended) throw new ComponentError('component_unavailable');
      if (locked && mode === 'native-provider' && LOCKED_METHODS.has(method)) throw new ComponentError('runtime_configuration_locked');
      if (method === 'cancelSwitch') return coordinator.cancelSwitch();
      if (method === 'confirmModelSelection') {
        if (coordinator.getStatus().state !== 'blocked' || coordinator.getStatus().errorCode !== 'native_model_selection_required')
          return { accepted: false };
        coordinator.taskAdapter?.acknowledgeModelSelection?.();
        return coordinator.recover();
      }
      if (method === 'getStatus') {
        const status = componentReady ? await client.request(method, params) : { configured: false, selectionLocked: false, selectedAccountId: null, primaryAccountId: null };
        return { ...status, mode, runtimeStarted: locked, activationRequired: mode === 'registration-only' && Boolean(status.primaryAccountId && status.selectedAccountId && status.configured && !status.selectedAccountDisabled),
          liveSwitchSupported: mode === 'native-provider' && coordinator.taskAdapter?.authBoundarySafe === true,
          ...(startupAccountError ? { startupAccountError } : {}),
          ...(componentError ? { componentError } : {}), switch: coordinator.getStatus() };
      }
      if (!componentReady) {
        if (method === 'listAccounts') return [];
        throw new ComponentError(componentError || 'component_unavailable');
      }
      if (locked && mode === 'native-provider' && method === 'selectAccount') return coordinator.selectAccount(params?.id);
      return client.request(method, params);
    },
  });
  const modelMenuBridge = electron?.ipcMain ? createModelMenuBridge({ electron,
    client: publicClient,
    isAccountWindow: window => ui.isAccountWindow(window),
  }) : undefined;
  try {
    let config;
    try { config = JSON.parse(fs.readFileSync(path.join(resourcesPath, 'hostauth-addon', 'config.json'), 'utf8')); }
    catch { throw new ComponentError('adapter_config_invalid'); }
    client = clientFactory ? clientFactory({ onFatal: componentFailed }) : new ComponentClient(componentPath(resourcesPath, config), { onFatal: componentFailed });
    const initialStatus = await client.request('getStatus');
    componentReady = true;
    if (initialStatus?.primaryAccountId) {
      mode = 'native-provider';
      rememberProvider(activationStatePath);
    } else if (providerWasEnabled) throw new ComponentError('primary_account_required');
    if (!config.oauthClient) throw new ComponentError('oauth_configuration_missing');
    // The project refresh adaptation is confined to independently authorized
    // provider mode. Registration keeps the original native executable and its
    // authentication path; it must never use the provider-specific copy.
    selectedNativeProgramPath = mode === 'native-provider' ? PROVIDER_NATIVE_PATH : undefined;
    const programPath = path.join(resourcesPath, selectedNativeProgramPath || 'bin/language_server.exe');
    const acceptedSha256 = mode === 'native-provider' ? [PROVIDER_PROGRAM_SHA256] : [PROGRAM_SHA256, PATCHED_PROGRAM_SHA256];
    nativeProgramSha256 = nativeProgramVerifier ? await nativeProgramVerifier({ programPath, acceptedSha256: [...acceptedSha256] }) : createHash('sha256').update(await fs.promises.readFile(programPath)).digest('hex');
    if (!nativeProgramSha256 || !acceptedSha256.includes(nativeProgramSha256)) throw new ComponentError('native_program_unsupported');
    // Reapply the program-matched public OAuth client on every start. A vault
    // created under another client must be rejected by the component.
    await client.request('configureOAuth', config.oauthClient);
    // Confirm independently supplied credentials before creating the LS. Neither
    // this code nor the component receives an official account Authorization.
    if (mode === 'native-provider') {
      let chooseAccount = !initialStatus.selectedAccountId || initialStatus.selectedAccountDisabled === true;
      if (initialStatus.selectedAccountDisabled) startupAccountError = 'account_reauth_required';
      while (true) {
        if (chooseAccount) await ui.ensureAccountReady({ electron, client: publicClient, parent });
        try {
          const initialToken = await client.request('getToken');
          tokenInfo(initialToken);
          coordinator.observeToken(initialToken);
          startupAccountError = undefined;
          break;
        } catch (error) {
          if (!componentReady || !STARTUP_ACCOUNT_ERRORS.has(error?.code)) throw error;
          // No native service has received a token yet. Let the user repair or
          // change this explicit selection; retry only after they continue.
          startupAccountError = error.code;
          chooseAccount = true;
        }
      }
    }
  } catch (error) {
    componentReady = false;
    componentError = /^[a-z][a-z0-9_]{0,63}$/.test(error?.code || '') ? error.code : 'component_unavailable';
    await client?.close();
    if (mode === 'native-provider') {
      modelMenuBridge?.close();
      throw new ComponentError('account_setup_failed', 'Independent account setup did not finish; the language server was not started.');
    }
  }
  function setLanguageServerEndpoint(options = {}) {
    if (options.baseUrl) modelMenuBridge?.setEndpoint(options.baseUrl);
    if (mode !== 'native-provider') return;
    if (ended || coordinator.operation) throw new ComponentError('switch_in_progress');
    if (options.taskAdapter) { coordinator.taskAdapter = options.taskAdapter; return; }
    if (!options.baseUrl || !options.csrf) throw new ComponentError('native_endpoint_invalid');
    if (nativeEndpoint === options.baseUrl && coordinator.taskAdapter) return;
    coordinator.taskAdapter?.close?.();
    coordinator.taskAdapter = createNativeTaskAdapter({ electron, baseUrl: options.baseUrl, csrf: options.csrf, client, nativeProgramSha256,
      refreshNativeView: (userStatus, viewOptions) => refreshNativeViews({ electron, origin: options.baseUrl, userStatus, ...viewOptions }) });
    nativeEndpoint = options.baseUrl;
  }
  return Object.freeze({
    mode,
    usesHostAuth: mode === 'native-provider',
    nativeProgramPath: selectedNativeProgramPath,
    async getOAuthToken() {
      if (ended) throw new ComponentError('component_unavailable');
      if (mode !== 'native-provider') throw new ComponentError('provider_not_active');
      return tokenBarrier.getToken();
    },
    markLanguageServerStarted(options = {}) {
      locked = true;
      if (options.taskAdapter || options.baseUrl) setLanguageServerEndpoint(options);
      ui.installAccountMenu({ electron, client: publicClient, parent, openMenu: () => modelMenuBridge?.open(parent) });
    },
    setLanguageServerEndpoint,
    attachTaskAdapter(adapter) {
      if (ended || coordinator.operation) throw new ComponentError('switch_in_progress');
      coordinator.taskAdapter = adapter;
    },
    showAccounts() {
      if (ended) return;
      return modelMenuBridge?.open(parent);
    },
    isAccountWindow(window) {
      return ui.isAccountWindow(window);
    },
    async close() {
      ended = true;
      modelMenuBridge?.close();
      tokenBarrier.close();
      await coordinator.close();
      // A blocked submission gate is retained until native LS shutdown. It is
      // safer to keep this Electron session paused than to release wrong-identity
      // work while the parent is closing the application.
      if (!coordinator.operation) coordinator.taskAdapter?.close?.();
      await client?.close();
    },
  });
}

module.exports = { prepare, tokenInfo, componentPath, PROVIDER_NATIVE_PATH };
