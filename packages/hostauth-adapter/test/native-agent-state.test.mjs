import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { AGENT_STATE_PATH, MAX_MESSAGE_BYTES, connectEnvelope, encodeAgentStateRequest, decodeAgentStateUpdate, readAgentState } = require('../src/native-agent-state.cjs');

const scalar = (number, value) => Buffer.concat([vint(number * 8), vint(value)]);
const bytes = (number, value) => Buffer.concat([vint(number * 8 + 2), vint(value.length), Buffer.from(value)]);
function vint(value) {
  const result = [];
  do { const part = value % 128; value = Math.floor(value / 128); result.push(part | (value ? 128 : 0)); } while (value);
  return Buffer.from(result);
}
function state({ fullyIdle = 1, children = 0, tasks = 0, commands = 0, queued = 0, messages = 0, missing = [], extra = [] } = {}) {
  const update = [scalar(18, fullyIdle), scalar(14, children), ...extra];
  for (const [number, count] of [[17, tasks], [12, commands], [9, queued], [20, messages]]) {
    if (!missing.includes(number)) update.push(bytes(number, count ? scalar(3, count) : Buffer.alloc(0)));
  }
  return bytes(1, Buffer.concat(update));
}
function stream(chunks, { contentType = 'application/connect+proto', keepOpen = false, onCancel = () => {} } = {}) {
  return new Response(new ReadableStream({
    start(controller) { for (const chunk of chunks) controller.enqueue(chunk); if (!keepOpen) controller.close(); },
    cancel() { onCancel(); },
  }), { headers: { 'Content-Type': contentType } });
}
const options = { baseUrl: 'https://127.0.0.1:12345', csrf: 'only-native-csrf-generated-for-this-process', conversationId: 'conversation-id' };

test('initial aggregate decoding ignores task and conversation contents and counts pending background work', () => {
  const opaque = Buffer.from([255, 255, 255, 255]); // Deliberately not a protobuf message.
  const decoded = decodeAgentStateUpdate(state({ tasks: 2, commands: 1, queued: 3, messages: 4,
    extra: [bytes(1, Buffer.from('DO_NOT_READ_CONVERSATION')), bytes(7, opaque), bytes(6, opaque)] }));
  assert.deepEqual(decoded, { known: true, fullyIdle: false, hasActiveChildren: false, backgroundTasks: 2, backgroundCommands: 1, queuedSteps: 3, pendingAgentMessages: 4 });
  assert.equal(JSON.stringify(decoded).includes('DO_NOT_READ'), false);
  assert.deepEqual(decodeAgentStateUpdate(state()), { known: true, fullyIdle: true, hasActiveChildren: false,
    backgroundTasks: 0, backgroundCommands: 0, queuedSteps: 0, pendingAgentMessages: 0 });
  for (const change of [{ tasks: 1 }, { commands: 1 }, { queued: 1 }, { messages: 1 }, { children: 1 }, { fullyIdle: 0 }]) {
    assert.equal(decodeAgentStateUpdate(state(change)).fullyIdle, false);
  }
});

test('only a fresh subscription initial full update can default omitted aggregates to zero', () => {
  for (const number of [9, 12, 17, 20]) {
    const decoded = decodeAgentStateUpdate(state({ missing: [number] }));
    assert.equal(decoded.known, false);
    assert.equal(decoded.fullyIdle, false);
    assert.equal(decoded.errorCode, 'native_agent_state_incomplete');
    const initial = decodeAgentStateUpdate(state({ missing: [number] }), { initial: true });
    assert.equal(initial.known, true);
    assert.equal(initial.fullyIdle, true);
  }
  // Actual 2.25 empty-cascade first response omits all four aggregates. Frame
  // position on the fresh subscription supplies the full-update contract;
  // the same shape passed as a delta cannot establish any omitted count.
  const nativeEmptyInitialShape = bytes(1, Buffer.concat([
    bytes(1, Buffer.from('native-id')), bytes(2, Buffer.from('native-trajectory')),
    scalar(3, 1), scalar(4, 1), scalar(5, 1), bytes(7, Buffer.alloc(0)), scalar(18, 1), bytes(27, Buffer.alloc(0)),
  ]));
  assert.equal(decodeAgentStateUpdate(nativeEmptyInitialShape).known, false);
  assert.deepEqual(decodeAgentStateUpdate(nativeEmptyInitialShape, { initial: true }), {
    known: true, fullyIdle: true, hasActiveChildren: false, queuedSteps: 0,
    backgroundCommands: 0, backgroundTasks: 0, pendingAgentMessages: 0,
  });
  for (const [change, missing] of [
    [{ tasks: 2 }, [9, 12, 20]], [{ commands: 1 }, [9, 17, 20]],
    [{ queued: 3 }, [12, 17, 20]], [{ messages: 4 }, [9, 12, 17]],
    [{ children: 1 }, [9, 12, 17, 20]], [{ fullyIdle: 0 }, [9, 12, 17, 20]],
  ]) {
    assert.equal(decodeAgentStateUpdate(state({ ...change, missing }), { initial: true }).fullyIdle, false);
  }
  assert.throws(() => decodeAgentStateUpdate(Buffer.alloc(0)), { code: 'native_agent_state_incomplete' });
});

test('initial empty counts do not make absent fully_idle or child activity idle', () => {
  for (const update of [Buffer.alloc(0), scalar(14, 1), scalar(18, 0), Buffer.concat([scalar(18, 1), scalar(14, 1)])]) {
    const actual = decodeAgentStateUpdate(bytes(1, update), { initial: true });
    assert.equal(actual.known, true);
    assert.equal(actual.fullyIdle, false);
  }
  assert.throws(() => decodeAgentStateUpdate(state(), { initial: 'true' }), { code: 'native_agent_state_invalid' });
});

test('duplicate, wrong-wire, invalid boolean and truncated protobuf values fail closed', () => {
  for (const raw of [
    bytes(1, Buffer.concat([scalar(18, 1), scalar(18, 1)])),
    bytes(1, scalar(17, 0)),
    bytes(1, scalar(18, 2)),
    bytes(1, bytes(17, Buffer.concat([scalar(3, 1), scalar(3, 2)]))),
    Buffer.from([10, 255]),
    Buffer.concat([state(), state()]),
  ]) assert.throws(() => decodeAgentStateUpdate(raw), { code: 'native_agent_state_invalid' });
});

for (const nativeVersion of ['2.31.0', '2.31.2']) {
test(`${nativeVersion} uses published run states and execution status with its remaining aggregate counts`, () => {
  const current = ({ status = 1, executable = 1, executor = 1, execution, tasks = 0, messages = 0 } = {}) => bytes(1, Buffer.concat([
    scalar(3, status), scalar(4, executable), scalar(5, executor), scalar(18, 1),
    bytes(17, scalar(3, tasks)), bytes(20, scalar(3, messages)),
    ...(execution === undefined ? [] : [bytes(25, execution)]),
  ]));
  const decode = buffer => decodeAgentStateUpdate(buffer, { nativeVersion, initial: true });
  assert.deepEqual(decode(current()), { known: true, fullyIdle: true, executionActive: false, backgroundTasks: 0, pendingAgentMessages: 0 });
  for (const change of [{ status: 2 }, { executable: 3 }, { executor: 4 }, { tasks: 1 }, { messages: 1 },
    { execution: bytes(1, Buffer.from('DO_NOT_READ_INITIALIZATION')) },
    { execution: bytes(2, scalar(1, 2)) }, { execution: bytes(100, Buffer.alloc(0)) }, { execution: Buffer.alloc(0) }]) {
    const result = decode(current(change));
    assert.equal(result.known, true);
    assert.equal(result.fullyIdle, false);
    assert.equal(JSON.stringify(result).includes('DO_NOT_READ'), false);
  }
  // Removed queue/command/child fields are never invented in a 2.31 result.
  assert.equal('queuedSteps' in decode(current()), false);
  assert.equal('backgroundCommands' in decode(current()), false);
  assert.equal('hasActiveChildren' in decode(current()), false);
  assert.throws(() => decode(current({ executor: 9 })), { code: 'native_agent_state_invalid' });
  assert.throws(() => decodeAgentStateUpdate(current(), { nativeVersion: 'future' }), { code: 'native_agent_state_invalid' });
  const sparse = bytes(1, Buffer.concat([scalar(3, 1), scalar(4, 1), scalar(5, 1), scalar(18, 1)]));
  assert.equal(decode(sparse).fullyIdle, true);
  assert.equal(decodeAgentStateUpdate(sparse, { nativeVersion }).known, false);
  assert.equal(decode(bytes(1, scalar(18, 1))).known, false);
  assert.throws(() => decode(bytes(1, Buffer.concat([scalar(3, 1), scalar(4, 1), scalar(5, 1), scalar(25, 0)]))), { code: 'native_agent_state_invalid' });
});

test(`${nativeVersion} stream reads apply the pinned native protocol instead of legacy omitted counters`, async () => {
  const update = bytes(1, Buffer.concat([scalar(3, 1), scalar(4, 1), scalar(5, 1), scalar(18, 1), bytes(25, bytes(2, scalar(1, 1)))]));
  const result = await readAgentState({ ...options, nativeVersion, fetchLocal: async () => stream([connectEnvelope(update)]) });
  assert.equal(result.known, true);
  assert.equal(result.fullyIdle, false);
  assert.equal(result.executionActive, true);
});
}

test('request uses Connect streaming envelope, explicit zero pages and disables native history rehydration', () => {
  const actual = encodeAgentStateRequest('native-id', 'owned-observer');
  const expectedMessage = Buffer.concat([
    bytes(1, Buffer.from('native-id')), bytes(2, Buffer.from('owned-observer')),
    bytes(3, Buffer.from([8, 0, 16, 0])), bytes(5, Buffer.from([8, 0, 16, 0])), bytes(6, Buffer.from([8, 0, 16, 0])), scalar(7, 1),
  ]);
  assert.equal(actual[0], 0);
  assert.equal(actual.readUInt32BE(1), expectedMessage.length);
  assert.deepEqual(actual.subarray(5), expectedMessage);
  for (const id of ['', 'a\0b', 'x'.repeat(257)]) assert.throws(() => encodeAgentStateRequest(id), { code: 'native_conversation_id_invalid' });
});

test('Connect frames can arrive a byte at a time; observer releases its subscription after initial aggregate', async () => {
  // Count aggregates omitted by native buildFullStateUpdateLocked at length 0.
  // A later delta saying background work started must not overwrite this
  // single observation or be treated as another full update.
  const wire = Buffer.concat([connectEnvelope(state({ missing: [9, 12, 17, 20] })), connectEnvelope(state({ tasks: 1 }))]);
  let cancelled = 0, requests = 0;
  const actual = await readAgentState({ ...options, fetchLocal: async (url, request) => {
    requests++;
    assert.equal(url, options.baseUrl + AGENT_STATE_PATH);
    assert.equal(request.method, 'POST');
    assert.equal(request.credentials, 'omit');
    assert.equal(request.redirect, 'error');
    assert.deepEqual(Object.keys(request.headers).sort(), ['Connect-Protocol-Version', 'Connect-Timeout-Ms', 'Content-Type', 'X-Codeium-Csrf-Token']);
    assert.equal(request.headers['Content-Type'], 'application/connect+proto');
    assert.equal(request.headers['X-Codeium-Csrf-Token'], options.csrf);
    assert.equal(request.body[0], 0);
    return stream([...wire].map(byte => Uint8Array.from([byte])), { keepOpen: true, onCancel: () => { cancelled++; } });
  } });
  assert.equal(actual.known, true);
  assert.equal(actual.fullyIdle, true);
  assert.equal(requests, 1);
  assert.equal(cancelled, 1);
});

test('Connect end-stream JSON errors, compression and malformed frames are unknown without content disclosure', async () => {
  const hugeHeader = Buffer.alloc(5); hugeHeader.writeUInt32BE(MAX_MESSAGE_BYTES + 1, 1);
  for (const wire of [
    connectEnvelope(Buffer.from('{"error":{"code":"unknown","message":"DO_NOT_RELAY_TASK_CONTENT"}}'), 2),
    connectEnvelope(Buffer.from('{}'), 2),
    Buffer.from([1, 0, 0, 0, 0]), Buffer.from([128, 0, 0, 0, 0]),
    Buffer.from([0, 0, 0, 0]), Buffer.from([0, 0, 0, 0, 4, 10]), hugeHeader,
  ]) {
    const actual = await readAgentState({ ...options, fetchLocal: async () => stream([wire]) });
    assert.equal(actual.known, false);
    assert.equal(actual.fullyIdle, false);
    assert.equal(JSON.stringify(actual).includes('DO_NOT_RELAY'), false);
  }
  const html = await readAgentState({ ...options, fetchLocal: async () => stream([Buffer.from('<!doctype html>')], { contentType: 'text/html' }) });
  assert.equal(html.known, false);
});

test('bounded observation timeout and user cancellation stop only the owned reader', async () => {
  let cancelled = 0;
  const result = await readAgentState({ ...options, timeoutMs: 20, fetchLocal: async () => stream([], { keepOpen: true, onCancel: () => { cancelled++; } }) });
  assert.deepEqual(result, { known: false, fullyIdle: false, errorCode: 'native_agent_state_timeout' });
  assert.equal(cancelled, 1);
  const controller = new AbortController();
  const promise = readAgentState({ ...options, signal: controller.signal, fetchLocal: async () => stream([], { keepOpen: true }) });
  controller.abort();
  await assert.rejects(promise, { code: 'switch_cancelled' });
  await assert.rejects(readAgentState({ ...options, baseUrl: 'https://example.invalid:12345', fetchLocal: async () => assert.fail('external requests must never be made') }), { code: 'native_endpoint_invalid' });
});
