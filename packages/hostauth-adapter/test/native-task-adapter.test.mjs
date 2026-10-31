import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { connectEnvelope } = require('../src/native-agent-state.cjs');
const { createNativeTaskAdapter, decodeActivity, decodeWait, decodeRemoteControl, refreshActivity, PROGRAM_SHA256, PATCHED_PROGRAM_SHA256, PROVIDER_PROGRAM_SHA256 } = require('../src/native-task-adapter.cjs');
import { ORIGINAL_NATIVE_SHA256, PATCHED_NATIVE_SHA256 } from '../src/native-diagnostics-patch.mjs';
import { PROVIDER_NATIVE_SHA256 } from '../src/native-project-refresh-patch.mjs';

function vint(value) { const bytes = []; do { const byte = value % 128; value = Math.floor(value / 128); bytes.push(byte | (value ? 128 : 0)); } while (value); return Buffer.from(bytes); }
const scalar = (number, value) => Buffer.concat([vint(number * 8), vint(value)]);
const bytes = (number, value) => Buffer.concat([vint(number * 8 + 2), vint(value.length), Buffer.from(value)]);
function mapSummary(id, status, extra = []) {
  const summary = Buffer.concat([bytes(1, Buffer.from('DO_NOT_DECODE_CONVERSATION_SUMMARY')), scalar(5, status), ...extra]);
  return bytes(1, Buffer.concat([bytes(1, Buffer.from(id)), bytes(2, summary)]));
}
const pprofIdle = 'goroutine profile: total 2\n1 @ 0x1 0x2\n#\t0x1\truntime.gopark+0x1\tfile.go:1\n\n1 @ 0x3\n#\t0x3\tnet/http.(*conn).serve+0x1\tfile.go:2\n';
const pprofRefreshing = pprofIdle + '\n3 @ 0x4\n#\t0x4\tgoogle3/third_party/jetski/go_utils/cache/cache.(*Cache[go.shape.*uint8]).forceRefresh.func1+0x1\tcache.go:1\n';
function nativeState({ tasks = 0, incomplete = false } = {}) {
  if (incomplete) return Buffer.from([0, 0, 0, 0, 4, 10]);
  return connectEnvelope(bytes(1, Buffer.concat([scalar(18, 1), bytes(9, Buffer.alloc(0)), bytes(12, Buffer.alloc(0)),
    ...(incomplete ? [] : [bytes(17, tasks ? scalar(3, tasks) : Buffer.alloc(0))]), bytes(20, Buffer.alloc(0))])));
}

function adapterWith(fetchImpl, options = {}) {
  let listener;
  return createNativeTaskAdapter({
    electron: { session: { defaultSession: { webRequest: { onBeforeRequest(_, handler) { listener = handler; } } } } },
    baseUrl: 'https://127.0.0.1:12345', csrf: 'new-local-process-csrf-value', nativeProgramSha256: PROGRAM_SHA256,
    client: { request: async () => [] }, fetchImpl, ...options,
  });
}

test('activity protobuf decoding skips summary and waiting-step contents', () => {
  const result = decodeActivity(Buffer.concat([mapSummary('id-one', 1), mapSummary('id-two', 2, [bytes(8, Buffer.from('DO_NOT_DECODE_TOOL_CONTENT')), scalar(18, 1)])]));
  assert.deepEqual(result.map(item => ({ id: item.id, status: item.status, waiting: item.waiting, hasActiveChildren: item.hasActiveChildren })), [
    { id: 'id-one', status: 1, waiting: 0, hasActiveChildren: false },
    { id: 'id-two', status: 2, waiting: 1, hasActiveChildren: true },
  ]);
  assert.equal(JSON.stringify(result).includes('DO_NOT_DECODE'), false);
  assert.throws(() => decodeActivity(mapSummary('id', 0)), { code: 'native_activity_unknown' });
  assert.throws(() => decodeActivity(Buffer.concat([mapSummary('id', 1), mapSummary('id', 1)])), { code: 'native_protocol_invalid' });
  assert.deepEqual(decodeWait(Buffer.alloc(0)), { timedOut: false });
  assert.deepEqual(decodeWait(scalar(1, 1)), { timedOut: true });
  assert.deepEqual(decodeRemoteControl(Buffer.alloc(0)), { enabled: false });
  assert.deepEqual(decodeRemoteControl(scalar(1, 1)), { enabled: true });
  assert.throws(() => decodeRemoteControl(scalar(1, 2)), { code: 'native_protocol_invalid' });
});

test('HTML SPA fallback and malformed pprof can never mean zero refresh goroutines', () => {
  assert.deepEqual(refreshActivity(pprofIdle), { known: true, count: 0 });
  assert.deepEqual(refreshActivity(pprofRefreshing), { known: true, count: 3 });
  for (const text of ['<!doctype html><body>goroutine profile</body>', 'goroutine profile: total 2\n', '']) {
    assert.throws(() => refreshActivity(text), { code: 'native_refresh_observation_unavailable' });
  }
});

test('all native CodeAssistClient frames block account changes, including model paths outside the old method list', () => {
  for (const method of ['StreamGenerateContent', 'postImageGenerationRequest', 'newlyAddedUnaryMethod']) {
    const body = pprofIdle + `\n2 @ 0x4\n#\t0x4\tgoogle3/third_party/jetski/language_server/code_assist_client/codeassistclient.(*CodeAssistClient).${method}+0x1\tclient.go:2\n`;
    assert.deepEqual(refreshActivity(body), { known: true, count: 2 });
  }
  assert.equal(PROGRAM_SHA256, ORIGINAL_NATIVE_SHA256);
  assert.equal(PATCHED_PROGRAM_SHA256, PATCHED_NATIVE_SHA256);
  assert.equal(PROVIDER_PROGRAM_SHA256, PROVIDER_NATIVE_SHA256);
});

test('runtime switching is supported only by the exact independently authorized provider program', () => {
  for (const sha256 of [PROGRAM_SHA256, PATCHED_PROGRAM_SHA256, PROVIDER_PROGRAM_SHA256, 'unrecognized']) {
    const adapter = adapterWith(async () => assert.fail('capability checks do not call a service'), { nativeProgramSha256: sha256 });
    assert.equal(adapter.authBoundarySafe, sha256 === PROVIDER_PROGRAM_SHA256);
    adapter.close();
  }
});

test('sleeping schedules and remote admission workers remain active across native idle responses', () => {
  for (const name of [
    'google3/third_party/jetski/cortex/core/tools/tools.(*ScheduleTool).runTimer',
    'google3/third_party/jetski/cortex/core/tools/tools.(*ScheduleTool).runCron',
    'google3/third_party/jetski/cortex/handlers/handlers.(*ScheduleSubHandler).handleOneShot',
    'google3/third_party/jetski/cortex/handlers/handlers.(*ScheduleSubHandler).handleCron',
    'google3/third_party/jetski/language_server/remotecontrol/remotecontrol.(*webChannelHandlerV2).executeRequest',
    'google3/third_party/jetski/language_server/remotecontrol/remotecontrol.maintainRemoteControlConnectionV2',
    'google3/third_party/jetski/language_server/remotecontrol/remotecontrol.(*meshSignaling).pollSignalingLoop',
    'google3/third_party/jetski/language_server/language_server.(*Server).RunCommand',
  ]) {
    const body = pprofIdle + `\n1 @ 0x4\n#\t0x4\t${name}+0x1\tnative.go:2\n`;
    assert.deepEqual(refreshActivity(body), { known: true, count: 1 });
  }
});

test('real task adapter uses only local CSRF and protobuf state RPCs before it reports native idle', async () => {
  const calls = [];
  const adapter = adapterWith(async (url, options) => {
    calls.push({ url, options });
    assert.equal(new URL(url).origin, 'https://127.0.0.1:12345');
    assert.equal(Object.keys(options.headers).some(key => key.toLowerCase() === 'authorization'), false);
    if (url.endsWith('/GetRemoteControlInfo')) return new Response(Buffer.alloc(0), { headers: { 'Content-Type': 'application/proto' } });
    if (url.endsWith('/GetAllCascadeTrajectories')) return new Response(mapSummary('local-id', 1), { headers: { 'Content-Type': 'application/proto' } });
    if (url.endsWith('/WaitForConversationFullyIdle')) return new Response(Buffer.alloc(0), { headers: { 'Content-Type': 'application/proto' } });
    if (url.endsWith('/StreamAgentStateUpdates')) return new Response(nativeState(), { headers: { 'Content-Type': 'application/connect+proto' } });
    if (url.includes('/debug/pprof/goroutine')) return new Response(pprofIdle, { headers: { 'Content-Type': 'text/plain' } });
    throw new Error('unexpected URL');
  });
  const result = await adapter.readActivity();
  assert.equal(result.known, true);
  assert.equal(result.fullyIdle, true);
  assert.deepEqual(result.active, []);
  assert.deepEqual(calls.map(call => new URL(call.url).pathname.split('/').at(-1)), ['GetRemoteControlInfo', 'GetAllCascadeTrajectories', 'goroutine', 'WaitForConversationFullyIdle', 'StreamAgentStateUpdates', 'goroutine', 'GetRemoteControlInfo']);
  assert.equal(adapter.authBoundarySafe, false);
  adapter.close();
});

test('native pending background work blocks a switch even when the cascade summary and idle RPC appear idle', async () => {
  for (const incomplete of [false, true]) {
    const adapter = adapterWith(async url => {
      if (url.endsWith('/GetRemoteControlInfo') || url.endsWith('/WaitForConversationFullyIdle')) return new Response(Buffer.alloc(0), { headers: { 'Content-Type': 'application/proto' } });
      if (url.endsWith('/GetAllCascadeTrajectories')) return new Response(mapSummary('sleeping-native-task', 1), { headers: { 'Content-Type': 'application/proto' } });
      if (url.endsWith('/StreamAgentStateUpdates')) return new Response(nativeState({ tasks: 1, incomplete }), { headers: { 'Content-Type': 'application/connect+proto' } });
      if (url.includes('/debug/pprof/')) return new Response(pprofIdle, { headers: { 'Content-Type': 'text/plain' } });
      throw Error('unexpected request');
    });
    try {
      const result = await adapter.readActivity();
      assert.equal(result.fullyIdle, false);
      assert.equal(result.known, !incomplete);
      if (!incomplete) assert.equal(result.active[0].backgroundTasks, 1);
      else assert.equal(result.errorCode, 'native_agent_state_invalid');
    } finally { adapter.close(); }
  }
});

test('2.26 summary background tasks and running subagents block before further native reads', async () => {
  for (const extra of [[scalar(29, 2)], [scalar(30, 1)]]) {
    const calls = [];
    const adapter = adapterWith(async url => {
      calls.push(new URL(url).pathname.split('/').at(-1));
      if (url.endsWith('/GetRemoteControlInfo')) return new Response(Buffer.alloc(0), { headers: { 'Content-Type': 'application/proto' } });
      if (url.endsWith('/GetAllCascadeTrajectories')) return new Response(mapSummary('pending-2.26-work', 1, extra), { headers: { 'Content-Type': 'application/proto' } });
      assert.fail('summary work must block before pprof or idle observations');
    });
    try {
      const result = await adapter.readActivity();
      assert.equal(result.known, true);
      assert.equal(result.fullyIdle, false);
      assert.deepEqual(result.active, [{ id: 'pending-2.26-work', kind: 'native' }]);
      assert.deepEqual(calls, ['GetRemoteControlInfo', 'GetAllCascadeTrajectories']);
    } finally { adapter.close(); }
  }
  assert.throws(() => decodeActivity(mapSummary('invalid-count', 1, [scalar(29, 0x100000000)])), { code: 'native_protocol_invalid' });
  assert.throws(() => decodeActivity(mapSummary('invalid-subagents', 1, [scalar(30, 2)])), { code: 'native_protocol_invalid' });
});

test('running native tools prevent metadata observation and every account change', async () => {
  const calls = [];
  const adapter = adapterWith(async url => {
    calls.push(url);
    if (url.endsWith('/GetRemoteControlInfo')) return new Response(Buffer.alloc(0), { headers: { 'Content-Type': 'application/proto' } });
    return new Response(mapSummary('tool-task', 4, [scalar(21, 1)]), { headers: { 'Content-Type': 'application/proto' } });
  });
  const result = await adapter.readActivity();
  assert.equal(result.known, true);
  assert.equal(result.fullyIdle, false);
  assert.equal(calls.length, 2);
  adapter.close();
});

test('enabled remote control blocks switching without stopping its service or cancelling tasks', async () => {
  const calls = [];
  const adapter = adapterWith(async (url, options) => {
    calls.push({ url, options });
    assert.equal(url.endsWith('/GetRemoteControlInfo'), true);
    return new Response(scalar(1, 1), { headers: { 'Content-Type': 'application/proto' } });
  });
  try {
    const result = await adapter.readActivity();
    assert.equal(result.known, true);
    assert.equal(result.fullyIdle, false);
    assert.deepEqual(result.active, [{ kind: 'remoteControl' }]);
    assert.equal(calls.length, 1);
    assert.equal(calls.some(({ url }) => /Cancel|Disable|Set/.test(url)), false);
  } finally { adapter.close(); }
});

test('remote admission appearing during an idle check prevents the final idle result', async () => {
  let remoteReads = 0;
  const adapter = adapterWith(async url => {
    if (url.endsWith('/GetRemoteControlInfo')) return new Response(++remoteReads === 1 ? Buffer.alloc(0) : scalar(1, 1), { headers: { 'Content-Type': 'application/proto' } });
    if (url.endsWith('/GetAllCascadeTrajectories')) return new Response(Buffer.alloc(0), { headers: { 'Content-Type': 'application/proto' } });
    if (url.includes('/debug/pprof/')) return new Response(pprofIdle, { headers: { 'Content-Type': 'text/plain' } });
    throw new Error('unexpected request');
  });
  try {
    const result = await adapter.readActivity();
    assert.equal(result.known, true);
    assert.equal(result.fullyIdle, false);
    assert.deepEqual(result.active, [{ kind: 'remoteControl' }]);
    assert.equal(remoteReads, 2);
  } finally { adapter.close(); }
});

test('unavailable refresh observability is unknown instead of successful idle', async () => {
  const adapter = adapterWith(async url => new Response(url.includes('/debug/pprof/') ? '<html>App</html>' : Buffer.alloc(0),
    { headers: { 'Content-Type': url.includes('/debug/pprof/') ? 'text/html' : 'application/proto' } }));
  const result = await adapter.readActivity();
  assert.equal(result.known, false);
  assert.equal(result.fullyIdle, false);
  assert.equal(result.errorCode, 'native_refresh_observation_unavailable');
  adapter.close();
});

test('a profile-shaped response with an HTML content type remains unknown', async () => {
  const adapter = adapterWith(async url => new Response(url.includes('/debug/pprof/') ? pprofIdle : Buffer.alloc(0),
    { headers: { 'Content-Type': url.includes('/debug/pprof/') ? 'text/html' : 'application/proto' } }));
  const result = await adapter.readActivity();
  assert.equal(result.known, false);
  assert.equal(result.errorCode, 'native_refresh_observation_unavailable');
  adapter.close();
});

function confirmationAdapter({ projects = ['project-b', 'project-b'], email = 'b@example.invalid', hasValidAuth = true, onRequest = () => {}, adapterOptions = {} } = {}) {
  const calls = [];
  let loads = 0;
  const adapter = adapterWith(async (url, options) => {
    const method = new URL(url).pathname.split('/').at(-1);
    calls.push(method);
    onRequest(method, options);
    assert.equal(Object.keys(options.headers).some(key => key.toLowerCase() === 'authorization'), false);
    if (method === 'goroutine') return new Response(pprofIdle, { headers: { 'Content-Type': 'text/plain' } });
    const body = JSON.parse(options.body);
    if (['GetLoadCodeAssist', 'GetAvailableModels', 'RetrieveUserQuotaSummary'].includes(method)) assert.deepEqual(body, { forceRefresh: true });
    let result = {};
    if (method === 'GetLoadCodeAssist') result = { response: { cloudaicompanionProject: projects[loads++ % projects.length] } };
    if (method === 'GetUserStatus') result = { userStatus: { email } };
    if (method === 'GetAuthStatus') result = { authResult: { hasValidAuth } };
    return Response.json(result);
  }, { client: { async request(method) { assert.equal(method, 'listAccounts'); return [{ id: 'account-b', email: 'b@example.invalid', disabled: false }]; } }, ...adapterOptions });
  return { adapter, calls };
}

test('identity confirmation uses two matching native load projects and email without inventing authResult.projectId', async () => {
  const { adapter, calls } = confirmationAdapter();
  try {
    const result = await adapter.confirmIdentity({ accountId: 'account-b', generation: 2, tokenWasObserved: () => true });
    assert.equal(result.confirmed, true);
    assert.equal(result.projectMatched, true);
    assert.equal(result.accountId, 'account-b');
    assert.equal(result.generation, 2);
    assert.equal(result.caches.length, 7);
    assert.deepEqual(calls, ['GetLoadCodeAssist', 'goroutine', 'GetAvailableModels', 'RetrieveUserQuotaSummary', 'goroutine', 'GetLoadCodeAssist', 'goroutine', 'GetUserStatus', 'GetAuthStatus', 'goroutine']);
    assert.equal(adapter.authBoundarySafe, false);
  } finally { adapter.close(); }
});

test('provider confirmation also updates the native view and waits for an explicit unavailable-model choice', async () => {
  const views = [];
  let ready = false;
  const { adapter, calls } = confirmationAdapter({ adapterOptions: {
    nativeProgramSha256: PROVIDER_PROGRAM_SHA256,
    async refreshNativeView(user, options) {
      if (options.phase) return { captured: options.phase === 'capture', cleared: options.phase === 'clear' };
      views.push({ user, options }); return { selectionReady: ready };
    },
  } });
  const confirm = () => adapter.confirmIdentity({ accountId: 'account-b', generation: 2, tokenWasObserved: () => true });
  try {
    await assert.rejects(confirm(), { code: 'native_model_capture_required' });
    assert.equal(calls.length, 0, 'Capture is required before any metadata calls');
    await adapter.captureModelSelection();
    await assert.rejects(confirm(), { code: 'native_model_selection_required' });
    assert.equal(views[0].user.email, 'b@example.invalid');
    assert.equal(views[0].options.requireExplicitSelection, false);
    const count = calls.length;
    await assert.rejects(confirm(), { code: 'native_model_selection_required' });
    assert.equal(calls.length, count, 'reconfirmation alone cannot replace the explicit model choice');
    adapter.acknowledgeModelSelection(); ready = true;
    assert.equal((await confirm()).confirmed, true);
    assert.equal(views[1].options.requireExplicitSelection, true);
    await adapter.clearModelSelection();
    await assert.rejects(confirm(), { code: 'native_model_capture_required' });
  } finally { adapter.close(); }
});

test('a provider cannot finish a switch without a native view updater', async () => {
  const { adapter } = confirmationAdapter({ adapterOptions: { nativeProgramSha256: PROVIDER_PROGRAM_SHA256 } });
  try {
    await assert.rejects(adapter.captureModelSelection(), { code: 'native_view_unavailable' });
  } finally { adapter.close(); }
});

test('mismatched or missing projects, old email and invalid auth never confirm an account switch', async () => {
  for (const options of [{ projects: ['project-a', 'project-b'] }, { projects: ['', ''] }, { projects: [undefined, undefined] }, { email: 'a@example.invalid' }, { hasValidAuth: false }]) {
    const { adapter } = confirmationAdapter(options);
    try {
      const result = await adapter.confirmIdentity({ accountId: 'account-b', generation: 2, tokenWasObserved: () => true });
      assert.equal(result.confirmed, false);
      assert.deepEqual(result.caches, []);
    } finally { adapter.close(); }
  }
});

test('post-refresh identity text cannot replace observing the selected HostAuth generation', async () => {
  const { adapter, calls } = confirmationAdapter();
  try {
    await assert.rejects(adapter.confirmIdentity({ accountId: 'account-b', generation: 2, tokenWasObserved: () => false }), { code: 'native_token_not_observed' });
    assert.deepEqual(calls, ['GetLoadCodeAssist']);
  } finally { adapter.close(); }
  let observed = true;
  const late = confirmationAdapter({ onRequest(method) { if (method === 'GetAuthStatus') observed = false; } });
  try {
    await assert.rejects(late.adapter.confirmIdentity({ accountId: 'account-b', generation: 2, tokenWasObserved: () => observed }), { code: 'native_token_not_observed' });
  } finally { late.adapter.close(); }
});
