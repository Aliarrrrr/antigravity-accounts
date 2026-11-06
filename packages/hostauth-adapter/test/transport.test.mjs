import test from 'node:test';
import assert from 'node:assert/strict';
import { access, mkdtemp, mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { ComponentClient } = require('../src/component-client.cjs');
const { prepare, tokenInfo, componentPath, PROVIDER_NATIVE_PATH } = require('../src/runtime.cjs');
const { PROGRAM_SHA256, PATCHED_PROGRAM_SHA256, PROVIDER_PROGRAM_SHA256, NATIVE_PROGRAM_PROFILES } = require('../src/native-task-adapter.cjs');
const fixture = fileURLToPath(new URL('./fake-component.cjs', import.meta.url));
const makeClient = (options = {}) => new ComponentClient(process.execPath, { args: [fixture, '--stdio'], ...options });
const validToken = () => ({ accessToken: 'fake-access', tokenType: 'Bearer', refreshToken: 'fake-refresh', expiry: new Date(Date.now() + 3600000).toISOString(), isGcpTos: false });
const runtimeConfig = config => JSON.stringify({ nativeProgram: NATIVE_PROGRAM_PROFILES[0], ...config });

test('stdio multiplexes independent requests and returns only matched results', async () => {
  const client = makeClient();
  try {
    const [status, token] = await Promise.all([client.request('getStatus'), client.request('getToken')]);
    assert.equal(status.configured, true);
    assert.equal(token.accessToken, 'independent-fake-access');
    assert.equal((await client.request('renameAccount', { id: 'fake', label: 'Work' })).method, 'renameAccount');
    assert.equal((await client.request('cancelLogin')).method, 'cancelLogin');
    await assert.rejects(client.request('inventedMethod'), { code: 'method_not_allowed' });
  } finally { await client.close(); }
});

test('component errors cannot echo its message or stderr into an Error', async () => {
  const client = makeClient();
  try {
    await assert.rejects(client.request('getStatus', { scenario: 'error' }), (error) => !String(error).includes('DO_NOT_LOG_FAKE_TOKEN'));
  } finally { await client.close(); }
});

for (const scenario of ['malformed', 'huge', 'wrong-id', 'exit', 'hang']) {
  test(`transport fails closed on ${scenario}`, async () => {
    let fatal = 0;
    const client = makeClient({ timeoutMs: scenario === 'hang' ? 200 : 3000, onFatal: () => { fatal++; } });
    await assert.rejects(client.request('getStatus', { scenario }));
    await assert.rejects(client.request('getToken'), { code: 'component_unavailable' });
    assert.equal(fatal, 1);
    await client.close();
  });
}

test('token conversion requires independent refresh token, correct OAuth client mode and useful expiry', () => {
  const input = validToken();
  const actual = tokenInfo(input);
  assert.equal(typeof actual.expiry.seconds, 'bigint');
  assert.equal(actual.refreshToken, 'fake-refresh');
  for (const changed of [{ refreshToken: '' }, { expiry: new Date().toISOString() }, { accessToken: '' }, { isGcpTos: true }, { tokenType: 'Basic' }]) {
    assert.throws(() => tokenInfo({ ...input, ...changed }));
  }
});

test('component path is constrained to addon or an explicitly absolute override', () => {
  assert.throws(() => componentPath('/app/resources', { component: '../../outside' }, ''));
  assert.throws(() => componentPath('/app/resources', { component: 'component/app.exe' }, 'relative.exe'));
  assert.match(componentPath('/app/resources', { component: 'component/app.exe' }, ''), /hostauth-addon/);
});

test('configured independent account is checked before token release and runtime selection uses a native coordinator', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-runtime-'));
  const calls = [];
  let uiClient;
  let installs = 0;
  await mkdir(path.join(root, 'hostauth-addon'));
  await writeFile(path.join(root, 'hostauth-addon/config.json'), runtimeConfig({ component: 'component/fake.exe', oauthClient: { clientId: 'public-fake-client' } }));
  const client = {
    async request(method) { calls.push(method); return method === 'getToken' ? validToken() : { configured: true, primaryAccountId: 'fake', selectedAccountId: 'fake' }; },
    async close() { calls.push('close'); },
  };
  try {
    const session = await prepare({ resourcesPath: root, activationStatePath: path.join(root, 'plugin/host-provider.json'), clientFactory: () => client, nativeProgramVerifier: async ({ programPath, acceptedSha256 }) => {
      assert.equal(programPath, path.join(root, PROVIDER_NATIVE_PATH));
      assert.deepEqual(acceptedSha256, [PROVIDER_PROGRAM_SHA256]);
      return PROVIDER_PROGRAM_SHA256;
    }, accountUI: {
      async ensureAccountReady() { assert.fail('A configured account does not need a startup selection dialog'); },
      installAccountMenu({ client }) { uiClient = client; installs++; },
    } });
    assert.deepEqual(calls, ['getStatus', 'configureOAuth', 'getToken']);
    assert.equal(session.usesHostAuth, true);
    assert.equal(session.mode, 'native-provider');
    assert.equal(session.nativeProgramPath, PROVIDER_NATIVE_PATH);
    session.markLanguageServerStarted();
    await assert.rejects(uiClient.request('getToken'), { code: 'method_not_allowed' });
    assert.equal(installs, 1);
    await uiClient.request('renameAccount', { id: 'fake', label: 'Work' });
    assert.equal(calls.at(-1), 'renameAccount');
    await uiClient.request('cancelLogin', {});
    assert.equal(calls.at(-1), 'cancelLogin');
    await assert.rejects(uiClient.request('selectAccount', { id: 'attached' }), { code: 'native_switch_unsupported' });
    await assert.rejects(uiClient.request('configureOAuth'), { code: 'method_not_allowed' });
    await assert.rejects(uiClient.request('removeAccount', { id: 'fake' }), { code: 'account_locked' });
    await assert.rejects(uiClient.request('setPrimaryAccount', { id: 'fake' }), { code: 'runtime_configuration_locked' });
    assert.equal((await uiClient.request('getStatus')).switch.state, 'active');
    assert.equal((await session.getOAuthToken()).accessToken, 'fake-access');
    await session.close();
    await assert.rejects(session.getOAuthToken(), { code: 'component_unavailable' });
  } finally { await rm(root, { recursive: true, force: true }); }
});

async function removalRuntime({ taskAdapter, onStatus = status => status } = {}) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-remove-'));
  await mkdir(path.join(root, 'hostauth-addon'));
  await writeFile(path.join(root, 'hostauth-addon/config.json'), runtimeConfig({ component: 'component/fake.exe', oauthClient: { clientId: 'public-fake-client' } }));
  const status = { configured: true, selectionLocked: true, primaryAccountId: 'primary', selectedAccountId: 'current', generation: 1, login: { state: 'idle' } };
  const accounts = ['primary', 'current', 'unused', 'target'];
  const calls = [];
  let uiClient, pendingTarget;
  const client = {
    async request(method, params) {
      calls.push({ method, params });
      if (method === 'getStatus') return onStatus(structuredClone(status));
      if (method === 'getToken') return { ...validToken(), accountId: status.selectedAccountId, generation: status.generation };
      if (method === 'removeAccount') { accounts.splice(accounts.indexOf(params.id), 1); return { removed: true }; }
      if (method === 'beginSwitch') { pendingTarget = params.targetId; return { ticket: 'synthetic-ticket', targetAccountId: pendingTarget, expectedGeneration: status.generation }; }
      if (method === 'commitSwitch') { status.selectedAccountId = pendingTarget; status.generation++; return { selectedAccountId: pendingTarget, generation: status.generation }; }
      return {};
    }, async close() {},
  };
  const session = await prepare({ resourcesPath: root, activationStatePath: path.join(root, 'plugin/host-provider.json'), clientFactory: () => client,
    nativeProgramVerifier: async () => PROVIDER_PROGRAM_SHA256, taskAdapter,
    accountUI: { installAccountMenu({ client }) { uiClient = client; } } });
  session.markLanguageServerStarted();
  return { session, client: uiClient, status, accounts, calls, async close() { await session.close(); await rm(root, { recursive: true, force: true }); } };
}

test('running provider can delete an inactive attached account while preserving current and primary selection', async () => {
  const h = await removalRuntime();
  try {
    h.calls.length = 0;
    assert.deepEqual(await h.client.request('removeAccount', { id: 'unused' }), { removed: true });
    assert.deepEqual(h.accounts, ['primary', 'current', 'target']);
    assert.equal(h.status.primaryAccountId, 'primary');
    assert.equal(h.status.selectedAccountId, 'current');
    assert.equal(h.status.generation, 1);
    assert.deepEqual(h.calls, [{ method: 'getStatus', params: undefined }, { method: 'removeAccount', params: { id: 'unused' } }]);
    for (const id of ['current', 'primary']) await assert.rejects(h.client.request('removeAccount', { id }), { code: 'account_locked' });
    h.status.login.state = 'pending';
    await assert.rejects(h.client.request('removeAccount', { id: 'target' }), { code: 'login_pending' });
    await assert.rejects(h.client.request('setPrimaryAccount', { id: 'target' }), { code: 'runtime_configuration_locked' });
    await assert.rejects(h.client.request('configureOAuth'), { code: 'method_not_allowed' });
    assert.equal(h.calls.filter(call => call.method === 'removeAccount').length, 1);
  } finally { await h.close(); }
});

test('waiting, confirming and blocked switches reject deletion without forwarding it to the component', async () => {
  let enteredRead, releaseRead, enteredConfirm, rejectConfirm;
  const reading = new Promise(resolve => { enteredRead = resolve; });
  const readResult = new Promise(resolve => { releaseRead = resolve; });
  const confirming = new Promise(resolve => { enteredConfirm = resolve; });
  const confirmResult = new Promise((_, reject) => { rejectConfirm = reject; });
  const taskAdapter = {
    authBoundarySafe: true, confirmationCoverage: ['synthetic-cache'],
    async pauseSubmissions() { return { closedToFrontendRequests: true, async release() {} }; },
    async readActivity() { enteredRead(); return readResult; },
    async captureModelSelection() { return { captured: true }; },
    async clearModelSelection() {},
    async confirmIdentity() { enteredConfirm(); return confirmResult; },
  };
  const h = await removalRuntime({ taskAdapter });
  const switching = h.client.request('selectAccount', { id: 'target' });
  const rejected = assert.rejects(switching, { code: 'native_identity_unconfirmed' });
  try {
    await reading;
    assert.equal((await h.client.request('getStatus')).switch.state, 'waiting');
    await assert.rejects(h.client.request('removeAccount', { id: 'unused' }), { code: 'switch_in_progress' });
    releaseRead({ known: true, fullyIdle: true, coverage: ['generation', 'tools', 'subagents', 'background'], active: [] });
    await confirming;
    assert.equal((await h.client.request('getStatus')).switch.state, 'confirming');
    await assert.rejects(h.client.request('removeAccount', { id: 'unused' }), { code: 'switch_in_progress' });
    rejectConfirm(Object.assign(new Error('synthetic confirmation failure'), { code: 'native_identity_unconfirmed' }));
    await rejected;
    assert.equal((await h.client.request('getStatus')).switch.state, 'blocked');
    await assert.rejects(h.client.request('removeAccount', { id: 'unused' }), { code: 'switch_in_progress' });
    assert.equal(h.calls.some(call => call.method === 'removeAccount'), false);
  } finally { await h.close(); }
});

test('a switch beginning during the removal status read prevents forwarding deletion', async () => {
  let holdStatus = false, enteredStatus, releaseStatus, enteredPause, releasePause;
  const statusRead = new Promise(resolve => { enteredStatus = resolve; });
  const statusResult = new Promise(resolve => { releaseStatus = resolve; });
  const paused = new Promise(resolve => { enteredPause = resolve; });
  const pauseResult = new Promise(resolve => { releasePause = resolve; });
  const taskAdapter = {
    authBoundarySafe: true, confirmationCoverage: ['synthetic-cache'],
    async pauseSubmissions() { enteredPause(); return pauseResult; },
    async readActivity() { assert.fail('The cancelled switch must stop before any native activity read'); },
    async captureModelSelection() {}, async clearModelSelection() {}, async confirmIdentity() {},
  };
  const h = await removalRuntime({ taskAdapter, onStatus(status) {
    if (holdStatus) { holdStatus = false; enteredStatus(); return statusResult; }
    return status;
  } });
  try {
    holdStatus = true;
    const deleting = h.client.request('removeAccount', { id: 'unused' });
    const rejectedDelete = assert.rejects(deleting, { code: 'switch_in_progress' });
    await statusRead;
    const switching = h.client.request('selectAccount', { id: 'target' });
    const rejectedSwitch = assert.rejects(switching, { code: 'switch_cancelled' });
    await paused;
    releaseStatus(structuredClone(h.status));
    await rejectedDelete;
    await h.client.request('cancelSwitch');
    releasePause({ closedToFrontendRequests: true, async release() {} });
    await rejectedSwitch;
    assert.equal(h.calls.some(call => call.method === 'removeAccount'), false);
  } finally { await h.close(); }
});

test('cancelled setup closes component without exposing the original exception', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-cancel-'));
  await mkdir(path.join(root, 'hostauth-addon'));
  await writeFile(path.join(root, 'hostauth-addon/config.json'), '{}');
  let closed = false;
  try {
    await assert.rejects(prepare({ resourcesPath: root, activationStatePath: path.join(root, 'plugin/host-provider.json'), clientFactory: () => ({ request: async () => ({ configured: true, primaryAccountId: 'fake' }), close: async () => { closed = true; } }), accountUI: {
      async ensureAccountReady() { throw new Error('DO_NOT_LOG_FAKE_TOKEN'); },
    } }), (error) => error.code === 'account_setup_failed' && !String(error).includes('DO_NOT_LOG_FAKE_TOKEN'));
    assert.equal(closed, true);
    await assert.rejects(prepare({ wsl: { distro: 'fake' } }), { code: 'host_mode_unsupported' });
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('registration-only startup neither opens OAuth setup nor asks for an account token', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-register-'));
  const calls = [];
  let uiClient;
  let primaryAccountId;
  let selectedAccountId;
  await mkdir(path.join(root, 'hostauth-addon'));
  await writeFile(path.join(root, 'hostauth-addon/config.json'), runtimeConfig({ component: 'component/fake.exe', oauthClient: { clientId: 'public-fake-client' } }));
  const client = {
    async request(method, params) {
      calls.push(method);
      if (method === 'getToken') assert.fail('Registration must not use credentials or refresh tokens');
      if (method === 'getStatus') return { configured: true, primaryAccountId, selectedAccountId, selectionLocked: false };
      if (method === 'setPrimaryAccount') { primaryAccountId = selectedAccountId = params.id; return {}; }
      if (method === 'selectAccount') { selectedAccountId = params.id; return {}; }
      if (method === 'listAccounts') return [];
      return {};
    }, async close() {},
  };
  try {
    const session = await prepare({ resourcesPath: root, activationStatePath: path.join(root, 'plugin/host-provider.json'), clientFactory: () => client,
      nativeProgramVerifier: async ({ programPath, acceptedSha256 }) => {
        assert.equal(programPath, path.join(root, 'bin/language_server.exe'));
        assert.deepEqual(acceptedSha256, [PROGRAM_SHA256, PATCHED_PROGRAM_SHA256]);
        return PATCHED_PROGRAM_SHA256;
      }, accountUI: {
        async ensureAccountReady() { assert.fail('Registration-only startup must preserve the existing work window'); },
        installAccountMenu({ client }) { uiClient = client; },
      } });
    assert.deepEqual(calls, ['getStatus', 'configureOAuth']);
    assert.equal(session.mode, 'registration-only');
    assert.equal(session.usesHostAuth, false);
    assert.equal(session.nativeProgramPath, undefined);
    session.markLanguageServerStarted({ baseUrl: 'https://127.0.0.1:12345', csrf: 'local-value-that-is-not-google' });
    assert.equal((await uiClient.request('getStatus')).runtimeStarted, true);
    await uiClient.request('setPrimaryAccount', { id: 'new-independent-primary' });
    await uiClient.request('selectAccount', { id: 'new-independent-attached' });
    const status = await uiClient.request('getStatus');
    assert.equal(status.mode, 'registration-only');
    assert.equal(status.activationRequired, true);
    assert.equal(status.selectionLocked, false);
    assert.equal(status.liveSwitchSupported, false);
    await assert.rejects(session.getOAuthToken(), { code: 'provider_not_active' });
    assert.equal(calls.includes('getToken'), false);
    await session.close();
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('registration component failure keeps the manager and original native auth available', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-register-fail-'));
  let uiClient;
  let fatal = 0;
  await mkdir(path.join(root, 'hostauth-addon'));
  await writeFile(path.join(root, 'hostauth-addon/config.json'), runtimeConfig({ component: 'component/fake.exe' }));
  try {
    const session = await prepare({ resourcesPath: root, activationStatePath: path.join(root, 'plugin/host-provider.json'),
      onFatal() { fatal++; }, clientFactory({ onFatal }) { return {
        async request() { onFatal('component_start_failed'); throw Object.assign(new Error('DO_NOT_LOG_FAKE_TOKEN'), { code: 'component_start_failed' }); },
        async close() {},
      }; }, accountUI: { installAccountMenu({ client }) { uiClient = client; } } });
    assert.equal(session.usesHostAuth, false);
    assert.equal(fatal, 0);
    session.markLanguageServerStarted();
    const status = await uiClient.request('getStatus');
    assert.equal(status.mode, 'registration-only');
    assert.equal(status.componentError, 'component_start_failed');
    assert.equal(JSON.stringify(status).includes('DO_NOT_LOG'), false);
    assert.deepEqual(await uiClient.request('listAccounts'), []);
    await assert.rejects(uiClient.request('beginLogin'), { code: 'component_start_failed' });
    await session.close();
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('previous provider activation prevents original-auth fallback when the component cannot start', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-provider-fail-'));
  const state = path.join(root, 'plugin/host-provider.json');
  await mkdir(path.join(root, 'hostauth-addon'));
  await writeFile(path.join(root, 'hostauth-addon/config.json'), runtimeConfig({ component: 'component/fake.exe', oauthClient: { clientId: 'public-fake-client' } }));
  const args = { resourcesPath: root, activationStatePath: state, nativeProgramVerifier: async () => PROVIDER_PROGRAM_SHA256, accountUI: {} };
  try {
    const session = await prepare({ ...args, clientFactory: () => ({ request: async method => method === 'getToken' ? validToken() : { configured: true, primaryAccountId: 'primary', selectedAccountId: 'primary' }, close: async () => {} }) });
    await session.close();
    await assert.rejects(prepare({ ...args, clientFactory: () => ({ request: async () => { throw new Error('DO_NOT_LOG_FAKE_TOKEN'); }, close: async () => {} }) }),
      error => error.code === 'account_setup_failed' && !String(error).includes('DO_NOT_LOG'));
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('an explicit account directory supplies identical component arguments and provider marker across LOCALAPPDATA environments', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-explicit-state-'));
  const resourcesPath = path.join(root, 'installation/resources');
  const accountsStateRoot = path.join(root, 'physical-account-state');
  const previousLocal = process.env.LOCALAPPDATA;
  const componentOptions = [];
  await mkdir(path.join(resourcesPath, 'hostauth-addon'), { recursive: true });
  await mkdir(accountsStateRoot);
  await writeFile(path.join(resourcesPath, 'hostauth-addon/config.json'), runtimeConfig({ component: 'component/fake.exe',
    oauthClient: { clientId: 'public-fake-client' }, accountsStateRoot }));
  try {
    for (const name of ['desktop-environment', 'packaged-environment']) {
      const environmentRoot = path.join(root, name);
      await mkdir(environmentRoot);
      process.env.LOCALAPPDATA = environmentRoot;
      const session = await prepare({ resourcesPath, nativeProgramVerifier: async () => PROVIDER_PROGRAM_SHA256,
        activationStatePath: path.join(environmentRoot, 'different-marker.json'),
        clientFactory(options) {
          componentOptions.push(options.args);
          return { async request(method) {
            return method === 'getToken' ? validToken() : { configured: true, primaryAccountId: 'primary', selectedAccountId: 'attached' };
          }, async close() {} };
        }, accountUI: {} });
      assert.equal(session.usesHostAuth, true);
      await session.close();
      assert.deepEqual(JSON.parse(await readFile(path.join(accountsStateRoot, 'host-provider.json'), 'utf8')), { schema: 1, enabled: true });
      await assert.rejects(access(path.join(environmentRoot, 'different-marker.json')), /ENOENT/);
      await assert.rejects(access(path.join(environmentRoot, 'AntigravityAccounts/host-provider.json')), /ENOENT/);
    }
    assert.deepEqual(componentOptions, [['--stdio', '--data-root', accountsStateRoot], ['--stdio', '--data-root', accountsStateRoot]]);
    for (const failedStatus of [true, false]) {
      let closed = false;
      await assert.rejects(prepare({ resourcesPath, clientFactory(options) {
        assert.deepEqual(options.args, ['--stdio', '--data-root', accountsStateRoot]);
        return { async request() {
          if (failedStatus) throw new Error('synthetic component failure');
          return { configured: true, primaryAccountId: null, selectedAccountId: null };
        }, async close() { closed = true; } };
      }, accountUI: {} }), { code: 'account_setup_failed' });
      assert.equal(closed, true);
    }
  } finally {
    if (previousLocal === undefined) delete process.env.LOCALAPPDATA; else process.env.LOCALAPPDATA = previousLocal;
    await rm(root, { recursive: true, force: true });
  }
});

test('a fresh explicit account directory without provider activation retains registration-only startup', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-fresh-state-'));
  const resourcesPath = path.join(root, 'installation/resources'), accountsStateRoot = path.join(root, 'empty-account-state');
  await mkdir(path.join(resourcesPath, 'hostauth-addon'), { recursive: true });
  await mkdir(accountsStateRoot);
  await writeFile(path.join(resourcesPath, 'hostauth-addon/config.json'), runtimeConfig({ component: 'component/fake.exe', oauthClient: { clientId: 'public-fake-client' }, accountsStateRoot }));
  const calls = [];
  try {
    const session = await prepare({ resourcesPath, nativeProgramVerifier: async () => PATCHED_PROGRAM_SHA256,
      clientFactory(options) {
        assert.deepEqual(options.args, ['--stdio', '--data-root', accountsStateRoot]);
        return { async request(method) { calls.push(method); return { configured: true }; }, async close() {} };
      }, accountUI: {} });
    assert.equal(session.mode, 'registration-only');
    assert.equal(session.usesHostAuth, false);
    assert.deepEqual(calls, ['getStatus', 'configureOAuth']);
    await session.close();
    await assert.rejects(access(path.join(accountsStateRoot, 'host-provider.json')), /ENOENT/);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('invalid or unreadable explicit storage configuration never starts a component or falls back to original auth', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-invalid-state-'));
  const resourcesPath = path.join(root, 'installation/resources');
  const legacyMarker = path.join(root, 'legacy/host-provider.json');
  await mkdir(path.join(resourcesPath, 'hostauth-addon'), { recursive: true });
  await mkdir(path.dirname(legacyMarker));
  await writeFile(legacyMarker, '{"schema":1,"enabled":true}\n');
  const fileRoot = path.join(root, 'not-a-directory');
  await writeFile(fileRoot, 'synthetic public path');
  const configPath = path.join(resourcesPath, 'hostauth-addon/config.json');
  const base = { component: 'component/fake.exe', oauthClient: { clientId: 'public-fake-client' } };
  const configurations = [null, '', 'relative-state', path.join(root, 'missing'), resourcesPath, path.dirname(resourcesPath), fileRoot]
    .map(accountsStateRoot => runtimeConfig({ ...base, accountsStateRoot }));
  configurations.push('{', 'null', '[]');
  try {
    for (const value of configurations) {
      await writeFile(configPath, value);
      await assert.rejects(prepare({ resourcesPath, activationStatePath: legacyMarker,
        clientFactory() { assert.fail('Invalid configuration must not invoke a default-root component'); }, accountUI: {} }), { code: 'account_setup_failed' });
    }
    await rm(configPath);
    await assert.rejects(prepare({ resourcesPath, activationStatePath: legacyMarker,
      clientFactory() { assert.fail('Missing configuration must not invoke a default-root component'); }, accountUI: {} }), { code: 'account_setup_failed' });
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('provider startup refuses original or diagnostics-only native program and never falls back', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-provider-native-'));
  const calls = [];
  await mkdir(path.join(root, 'hostauth-addon'));
  await writeFile(path.join(root, 'hostauth-addon/config.json'), runtimeConfig({ component: 'component/fake.exe', oauthClient: { clientId: 'public-fake-client' } }));
  const clientFactory = () => ({
    async request(method) { calls.push(method); return { configured: true, primaryAccountId: 'primary', selectedAccountId: 'primary' }; },
    async close() { calls.push('close'); },
  });
  const args = { resourcesPath: root, activationStatePath: path.join(root, 'plugin/host-provider.json'), clientFactory, accountUI: {} };
  try {
    for (const digest of [PROGRAM_SHA256, PATCHED_PROGRAM_SHA256, 'unknown-program']) {
      calls.length = 0;
      await assert.rejects(prepare({ ...args, nativeProgramVerifier: async () => digest }), { code: 'account_setup_failed' });
      assert.deepEqual(calls, ['getStatus', 'close']);
    }
    calls.length = 0;
    // No verifier override: a missing provider copy is a fatal provider setup
    // failure even though the ordinary executable may still be installed.
    await mkdir(path.join(root, 'bin'));
    await writeFile(path.join(root, 'bin/language_server.exe'), 'ordinary native program must not be selected');
    await assert.rejects(prepare(args), { code: 'account_setup_failed' });
    assert.deepEqual(calls, ['getStatus', 'close']);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('startup selects one complete native program set and rejects another version before token release', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-program-set-'));
  const calls = [];
  await mkdir(path.join(root, 'hostauth-addon'));
  const configFile = path.join(root, 'hostauth-addon/config.json');
  const clientFactory = () => ({
    async request(method) {
      calls.push(method);
      return method === 'getToken' ? validToken() : { configured: true, primaryAccountId: 'primary', selectedAccountId: 'primary' };
    }, async close() { calls.push('close'); },
  });
  const args = { resourcesPath: root, activationStatePath: path.join(root, 'plugin/host-provider.json'), clientFactory, accountUI: {} };
  try {
    for (const profile of NATIVE_PROGRAM_PROFILES) {
      await writeFile(configFile, runtimeConfig({ nativeProgram: profile, component: 'component/fake.exe', oauthClient: { clientId: 'public-fake-client' } }));
      let session;
      try {
        session = await prepare({ ...args, nativeProgramVerifier: async ({ acceptedSha256 }) => {
          assert.deepEqual(acceptedSha256, [profile.providerSha256]); return profile.providerSha256;
        } });
        assert.equal(session.usesHostAuth, true);
      } finally { await session?.close(); }
      const other = NATIVE_PROGRAM_PROFILES.find(value => value !== profile);
      calls.length = 0;
      await assert.rejects(prepare({ ...args, nativeProgramVerifier: async () => other.providerSha256 }), { code: 'account_setup_failed' });
      assert.deepEqual(calls, ['getStatus', 'close'], 'A different reviewed provider still belongs to a different program set');
      await writeFile(configFile, runtimeConfig({ nativeProgram: { ...profile, originalSha256: other.originalSha256 },
        component: 'component/fake.exe', oauthClient: { clientId: 'public-fake-client' } }));
      calls.length = 0;
      await assert.rejects(prepare({ ...args, nativeProgramVerifier: async () => assert.fail('Mixed configuration must be rejected before program verification') }), { code: 'account_setup_failed' });
      assert.deepEqual(calls, ['getStatus', 'close']);
    }
    await writeFile(configFile, JSON.stringify({ component: 'component/fake.exe', oauthClient: { clientId: 'public-fake-client' } }));
    calls.length = 0;
    await assert.rejects(prepare({ ...args, nativeProgramVerifier: async () => assert.fail('Missing program configuration cannot authorize a provider') }), { code: 'account_setup_failed' });
    assert.deepEqual(calls, ['getStatus', 'close']);
  } finally { await rm(root, { recursive: true, force: true }); }
});

for (const scenario of ['disabled', 'account_reauth_required', 'account_operation_failed']) {
  test(`startup offers another independent account after ${scenario}`, async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-startup-repair-'));
    await mkdir(path.join(root, 'hostauth-addon'));
    await writeFile(path.join(root, 'hostauth-addon/config.json'), runtimeConfig({ component: 'component/fake.exe', oauthClient: { clientId: 'public-fake-client' } }));
    let selected = 'primary', dialogs = 0, closed = false;
    const tokenAccounts = [];
    const client = {
      async request(method, params) {
        if (method === 'getStatus') return { configured: true, primaryAccountId: 'primary', selectedAccountId: selected,
          selectedAccountDisabled: scenario === 'disabled' && selected === 'primary', selectionLocked: false };
        if (method === 'selectAccount') { selected = params.id; return { selected: true }; }
        if (method === 'getToken') {
          tokenAccounts.push(selected);
          if (selected === 'primary') throw Object.assign(new Error('DO_NOT_LOG_TOKEN'), { code: scenario });
          return { ...validToken(), accountId: selected, generation: 2 };
        }
        return {};
      }, async close() { closed = true; },
    };
    try {
      const session = await prepare({ resourcesPath: root, activationStatePath: path.join(root, 'plugin/host-provider.json'),
        clientFactory: () => client, nativeProgramVerifier: async () => PROVIDER_PROGRAM_SHA256, accountUI: {
          async ensureAccountReady({ client: manager }) {
            dialogs++;
            const status = await manager.request('getStatus');
            assert.equal(status.runtimeStarted, false);
            assert.equal(status.startupAccountError, scenario === 'disabled' ? 'account_reauth_required' : scenario);
            assert.equal(status.componentError, undefined);
            assert.equal(JSON.stringify(status).includes('DO_NOT_LOG'), false);
            await manager.request('selectAccount', { id: 'attached' });
          },
          installAccountMenu({ client: manager }) { assert.equal(closed, false); this.manager = manager; },
        } });
      assert.equal(session.usesHostAuth, true);
      assert.equal(dialogs, 1);
      assert.deepEqual(tokenAccounts, scenario === 'disabled' ? ['attached'] : ['primary', 'attached']);
      await session.close();
      assert.equal(closed, true);
    } finally { await rm(root, { recursive: true, force: true }); }
  });
}

test('startup credential failure waits for the manager before retrying and cancellation never uses original auth', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-startup-cancel-'));
  await mkdir(path.join(root, 'hostauth-addon'));
  await writeFile(path.join(root, 'hostauth-addon/config.json'), runtimeConfig({ component: 'component/fake.exe', oauthClient: { clientId: 'public-fake-client' } }));
  let opened, cancel, requests = 0, closed = false;
  const shown = new Promise(resolve => { opened = resolve; });
  const dialog = new Promise((_, reject) => { cancel = reject; });
  try {
    const starting = prepare({ resourcesPath: root, activationStatePath: path.join(root, 'plugin/host-provider.json'),
      nativeProgramVerifier: async () => PROVIDER_PROGRAM_SHA256,
      clientFactory: () => ({
        async request(method) {
          if (method === 'getToken') { requests++; throw Object.assign(new Error('DO_NOT_LOG_TOKEN'), { code: 'oauth_request_failed' }); }
          return { configured: true, primaryAccountId: 'primary', selectedAccountId: 'primary' };
        }, async close() { closed = true; },
      }), accountUI: { async ensureAccountReady() { opened(); await dialog; } },
    });
    const rejected = assert.rejects(starting, error => error.code === 'account_setup_failed' && !String(error).includes('DO_NOT_LOG'));
    await shown;
    assert.equal(requests, 1);
    assert.equal(closed, false);
    cancel(new Error('account_selection_cancelled'));
    await rejected;
    assert.equal(requests, 1);
    assert.equal(closed, true);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('invalid component token is fatal without offering an account retry', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'hostauth-startup-protocol-'));
  await mkdir(path.join(root, 'hostauth-addon'));
  await writeFile(path.join(root, 'hostauth-addon/config.json'), runtimeConfig({ component: 'component/fake.exe', oauthClient: { clientId: 'public-fake-client' } }));
  let closed = false;
  try {
    await assert.rejects(prepare({ resourcesPath: root, activationStatePath: path.join(root, 'plugin/host-provider.json'),
      nativeProgramVerifier: async () => PROVIDER_PROGRAM_SHA256,
      clientFactory: () => ({ async request(method) {
        return method === 'getToken' ? { ...validToken(), refreshToken: '' } : { configured: true, primaryAccountId: 'primary', selectedAccountId: 'primary' };
      }, async close() { closed = true; } }), accountUI: { async ensureAccountReady() { assert.fail('A malformed token is a protocol failure'); } },
    }), { code: 'account_setup_failed' });
    assert.equal(closed, true);
  } finally { await rm(root, { recursive: true, force: true }); }
});
