import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { SwitchCoordinator, isFullyIdle, WORK_KINDS } = require('../src/switch-coordinator.cjs');
const { TokenBarrier } = require('../src/token-barrier.cjs');

const idle = () => ({ known: true, fullyIdle: true, coverage: [...WORK_KINDS], active: [] });
const busy = () => ({ ...idle(), fullyIdle: false, active: [{ id: 'native-task', kind: 'tools' }] });
function deferred() { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; }
function setup({ activity = [idle(), idle()], confirm, sleep } = {}) {
  const calls = [];
  let selected = 'primary', generation = 1;
  const client = { async request(method, params) {
    calls.push([method, params]);
    if (method === 'getStatus') return { primaryAccountId: 'primary', selectedAccountId: selected, generation };
    if (method === 'beginSwitch') return { ticket: 'ticket', targetAccountId: params.targetId, expectedGeneration: params.expectedGeneration };
    if (method === 'commitSwitch') { selected = 'attached'; generation++; return { selectedAccountId: selected, generation }; }
    if (method === 'cancelSwitch') return { cancelled: true };
  } };
  let coordinator;
  const tokens = new TokenBarrier(async () => { coordinator.observeToken({ accountId: selected, generation }); return selected; });
  const adapter = {
    authBoundarySafe: true,
    confirmationCoverage: ['project', 'userStatus', 'models'],
    async captureModelSelection() { calls.push(['capture']); return { captured: true }; },
    async clearModelSelection() { calls.push(['clear']); return { cleared: true }; },
    async pauseSubmissions() { calls.push(['pause']); return { closedToFrontendRequests: true, release: async () => { calls.push(['release']); } }; },
    async readActivity() { calls.push(['activity']); return activity.length > 1 ? activity.shift() : activity[0]; },
    async confirmIdentity(options) {
      calls.push(['confirm']);
      await tokens.getToken();
      return confirm ? confirm(options) : {
        confirmed: true, projectMatched: true, accountId: options.accountId, generation: options.generation,
        caches: adapter.confirmationCoverage.map(name => ({ name, accountId: options.accountId, generation: options.generation, settled: true })),
      };
    },
  };
  coordinator = new SwitchCoordinator({ client, taskAdapter: adapter, tokenBarrier: tokens, pollIntervalMs: 1, sleep });
  return { coordinator, tokens, client, calls, adapter };
}

test('native idle requires all four work classes and explicit empty active list', () => {
  assert.equal(isFullyIdle(idle()), true);
  for (const change of [{ known: false }, { fullyIdle: false }, { coverage: ['generation', 'tools'] }, { active: undefined }, { active: ['task'] }]) {
    assert.equal(isFullyIdle({ ...idle(), ...change }), false);
  }
  assert.equal(isFullyIdle({ httpRequests: 0 }), false);
});

test('token barrier drains old callbacks and releases queued callbacks against the new account', async () => {
  const started = deferred(), old = deferred();
  let selected = 'primary', calls = 0;
  const barrier = new TokenBarrier(async () => { calls++; if (calls === 1) { started.resolve(); return old.promise; } return selected; });
  const first = barrier.getToken();
  await started.promise;
  let locked = false;
  const acquiring = barrier.acquire().then(value => { locked = true; return value; });
  const next = barrier.getToken();
  await Promise.resolve();
  assert.equal(calls, 1);
  assert.equal(locked, false);
  old.resolve('primary');
  assert.equal(await first, 'primary');
  const lease = await acquiring;
  assert.equal(lease.waiting, 1);
  selected = 'attached';
  lease.release();
  assert.equal(await next, 'attached');
});

test('switch drains native work, rechecks inside token barrier, commits and confirms without restarting', async () => {
  const { coordinator, calls } = setup({ activity: [busy(), idle(), idle()] });
  assert.deepEqual(await coordinator.selectAccount('attached'), { selectedAccountId: 'attached', generation: 2 });
  assert.deepEqual(calls.map(x => x[0]), ['getStatus', 'pause', 'activity', 'activity', 'activity', 'capture', 'beginSwitch', 'commitSwitch', 'confirm', 'clear', 'release']);
  assert.equal(coordinator.getStatus().state, 'active');
  assert.equal(calls.some(x => /restart|cancelcascade/i.test(x[0])), false);
});

test('a native timer race releases old credentials and waits for its task before retrying', async () => {
  const { coordinator, calls } = setup({ activity: [idle(), busy(), busy(), idle(), idle()] });
  await coordinator.selectAccount('attached');
  assert.equal(calls.filter(x => x[0] === 'activity').length, 5);
  assert.equal(calls.filter(x => x[0] === 'beginSwitch').length, 1);
});

test('unknown native activity does not prepare or commit an account and resumes old work', async () => {
  const { coordinator, calls } = setup({ activity: [{ known: false }] });
  await assert.rejects(coordinator.selectAccount('attached'), { code: 'native_activity_unknown' });
  assert.equal(calls.some(x => x[0] === 'beginSwitch'), false);
  assert.equal(calls.at(-1)[0], 'release');
});

test('unsupported auth caching cannot be enabled by a UI selection', async () => {
  const { coordinator, adapter, calls } = setup();
  adapter.authBoundarySafe = false;
  await assert.rejects(coordinator.selectAccount('attached'), { code: 'native_switch_unsupported' });
  assert.equal(calls.length, 0);
});

test('model capture failure or cancellation prevents preparing an account and clears partial records', async () => {
  for (const cancel of [false, true]) {
    const { coordinator, adapter, calls } = setup();
    adapter.captureModelSelection = async () => {
      calls.push(['capture']);
      if (cancel) { coordinator.cancelSwitch(); return { captured: true }; }
      throw Object.assign(new Error(), { code: 'native_view_unavailable' });
    };
    await assert.rejects(coordinator.selectAccount('attached'), { code: cancel ? 'switch_cancelled' : 'native_view_unavailable' });
    assert.equal(calls.some(call => call[0] === 'beginSwitch'), false);
    assert.deepEqual(calls.slice(-2).map(call => call[0]), ['clear', 'release']);
    assert.equal(coordinator.getStatus().state, 'active');
  }
});

test('cancelling idle wait never cancels native work or changes the selected account', async () => {
  const sleeping = deferred();
  const { coordinator, calls } = setup({ activity: [busy()], sleep: async (_, signal) => {
    sleeping.resolve();
    await new Promise((resolve, reject) => signal.addEventListener('abort', () => reject(Object.assign(new Error(), { code: 'switch_cancelled' })), { once: true }));
  } });
  const pending = coordinator.selectAccount('attached');
  await sleeping.promise;
  assert.deepEqual(coordinator.cancelSwitch(), { cancelled: true });
  await assert.rejects(pending, { code: 'switch_cancelled' });
  assert.deepEqual(calls.map(x => x[0]), ['getStatus', 'pause', 'activity', 'release']);
  assert.equal(coordinator.getStatus().state, 'active');
});

test('commit is not reported active until native identity and the host token generation are confirmed', async () => {
  const { coordinator, calls, adapter } = setup({ confirm: () => ({ confirmed: false }) });
  await assert.rejects(coordinator.selectAccount('attached'), { code: 'native_identity_unconfirmed' });
  assert.equal(coordinator.getStatus().state, 'blocked');
  assert.equal(calls.some(x => x[0] === 'release'), false);
  assert.throws(() => coordinator.cancelSwitch(), { code: 'switch_not_cancellable' });
  adapter.confirmIdentity = async options => ({ confirmed: true, projectMatched: true, accountId: options.accountId, generation: options.generation,
    caches: adapter.confirmationCoverage.map(name => ({ name, accountId: options.accountId, generation: options.generation, settled: true })) });
  await coordinator.selectAccount('attached');
  assert.equal(calls.filter(x => x[0] === 'commitSwitch').length, 1);
  assert.equal(calls.at(-1)[0], 'release');
});

test('identity text alone without a post-commit HostAuth callback cannot confirm a switch', async () => {
  const { coordinator, adapter } = setup();
  adapter.confirmIdentity = async options => ({ confirmed: true, projectMatched: true, accountId: options.accountId, generation: options.generation,
    caches: adapter.confirmationCoverage.map(name => ({ name, accountId: options.accountId, generation: options.generation, settled: true })) });
  await assert.rejects(coordinator.selectAccount('attached'), { code: 'native_identity_unconfirmed' });
  assert.equal(coordinator.getStatus().state, 'blocked');
});

test('late old-account metadata cannot release submission pause even with the new token observed', async () => {
  const { coordinator, adapter, calls } = setup({ confirm: options => ({
    confirmed: true, projectMatched: true, accountId: options.accountId, generation: options.generation,
    caches: [
      { name: 'project', accountId: options.accountId, generation: options.generation, settled: true },
      { name: 'userStatus', accountId: 'primary', generation: 1, settled: true },
      { name: 'models', accountId: options.accountId, generation: options.generation, settled: false },
    ],
  }) });
  await assert.rejects(coordinator.selectAccount('attached'), { code: 'native_identity_unconfirmed' });
  assert.equal(coordinator.getStatus().state, 'blocked');
  assert.equal(calls.some(x => x[0] === 'release'), false);
});

test('partially cleared model captures can finish cleanup without repeating identity confirmation', async () => {
  const { coordinator, adapter, calls } = setup();
  const confirm = adapter.confirmIdentity;
  let captured = true, clears = 0;
  adapter.confirmIdentity = async options => {
    if (!captured) throw Object.assign(new Error(), { code: 'native_view_capture_missing' });
    return confirm(options);
  };
  adapter.clearModelSelection = async () => {
    calls.push(['clear']);
    // One native window was cleared before a second window failed.
    captured = false;
    if (++clears === 1) throw Object.assign(new Error(), { code: 'native_view_execution_failed' });
    return { cleared: true };
  };
  await assert.rejects(coordinator.selectAccount('attached'), { code: 'native_view_execution_failed' });
  assert.equal(coordinator.getStatus().state, 'blocked');
  assert.equal(calls.some(call => call[0] === 'release'), false);
  await coordinator.selectAccount('attached');
  assert.equal(coordinator.getStatus().state, 'active');
  assert.equal(clears, 2);
  assert.equal(calls.filter(call => call[0] === 'commitSwitch').length, 1);
  assert.equal(calls.filter(call => call[0] === 'confirm').length, 1);
  assert.equal(calls.at(-1)[0], 'release');
});

test('a failed pause release retries only the unfinished release after captures were cleared', async () => {
  const { coordinator, adapter, calls } = setup();
  let releases = 0;
  adapter.pauseSubmissions = async () => ({
    closedToFrontendRequests: true,
    async release() {
      calls.push(['release']);
      if (++releases === 1) throw Object.assign(new Error(), { code: 'native_submission_gate_unavailable' });
    },
  });
  await assert.rejects(coordinator.selectAccount('attached'), { code: 'native_submission_gate_unavailable' });
  assert.equal(coordinator.getStatus().state, 'blocked');
  await coordinator.selectAccount('attached');
  assert.equal(coordinator.getStatus().state, 'active');
  assert.equal(releases, 2);
  for (const method of ['commitSwitch', 'confirm', 'clear']) {
    assert.equal(calls.filter(call => call[0] === method).length, 1);
  }
});

test('an unavailable model still requires an explicit selection before cleanup can be resumed', async () => {
  const { coordinator, adapter, calls } = setup();
  const confirm = adapter.confirmIdentity;
  let modelSelected = false;
  adapter.confirmIdentity = async options => {
    if (!modelSelected) throw Object.assign(new Error(), { code: 'native_model_selection_required' });
    return confirm(options);
  };
  for (let attempt = 0; attempt < 2; attempt++) {
    await assert.rejects(coordinator.selectAccount('attached'), { code: 'native_model_selection_required' });
    assert.equal(coordinator.getStatus().state, 'blocked');
    assert.equal(calls.some(call => ['clear', 'release'].includes(call[0])), false);
  }
  modelSelected = true;
  await coordinator.selectAccount('attached');
  assert.equal(coordinator.getStatus().state, 'active');
  assert.equal(calls.filter(call => call[0] === 'commitSwitch').length, 1);
  assert.deepEqual(calls.slice(-2).map(call => call[0]), ['clear', 'release']);
});

test('only one native window can retry a failed cancellation cleanup at a time', async () => {
  const { coordinator, client, calls } = setup();
  const request = client.request;
  const retry = deferred();
  let cancellations = 0;
  client.request = async (method, params) => {
    if (method === 'beginSwitch') {
      const result = await request(method, params);
      coordinator.cancelSwitch();
      return result;
    }
    if (method === 'cancelSwitch') {
      calls.push([method, params]);
      if (++cancellations === 1) throw Object.assign(new Error(), { code: 'component_request_failed' });
      await retry.promise;
      return { cancelled: true };
    }
    return request(method, params);
  };
  await assert.rejects(coordinator.selectAccount('attached'), { code: 'switch_cancel_unconfirmed' });
  assert.equal(coordinator.getStatus().state, 'blocked');
  const recovering = coordinator.selectAccount('attached');
  await assert.rejects(coordinator.selectAccount('attached'), { code: 'switch_in_progress' });
  assert.equal(cancellations, 2, 'only the original failed cleanup and one retry own the ticket');
  retry.resolve();
  await recovering;
  assert.equal(coordinator.getStatus().state, 'active');
  assert.equal(calls.filter(call => call[0] === 'release').length, 1);
  assert.equal(calls.some(call => call[0] === 'commitSwitch'), false);
});
