'use strict';

const { randomUUID } = require('node:crypto');
const { ComponentError } = require('./component-client.cjs');
const { loopbackOrigin } = require('./submission-gate.cjs');

const AGENT_STATE_PATH = '/exa.language_server_pb.LanguageServerService/StreamAgentStateUpdates';
const MAX_MESSAGE_BYTES = 4 * 1024 * 1024;

function invalid() { return new ComponentError('native_agent_state_invalid'); }

// Unknown fields, including task bodies, steps, paths and metadata, are skipped
// as opaque byte ranges. No conversation string is decoded from a response.
function visitFields(buffer, visit) {
  let offset = 0;
  function integer() {
    let value = 0, scale = 1;
    for (let i = 0; i < 10; i++) {
      if (offset >= buffer.length) throw invalid();
      const byte = buffer[offset++];
      value += (byte & 127) * scale;
      if (!(byte & 128)) {
        if (!Number.isSafeInteger(value)) throw invalid();
        return value;
      }
      scale *= 128;
    }
    throw invalid();
  }
  while (offset < buffer.length) {
    const tag = integer(), number = Math.floor(tag / 8), wire = tag & 7;
    if (!number || number > 0x1fffffff) throw invalid();
    if (wire === 0) visit(number, wire, integer());
    else if (wire === 2) {
      const length = integer();
      if (length > buffer.length - offset) throw invalid();
      visit(number, wire, buffer.subarray(offset, offset + length));
      offset += length;
    } else if (wire === 1 || wire === 5) {
      const length = wire === 1 ? 8 : 4;
      if (length > buffer.length - offset) throw invalid();
      offset += length;
      visit(number, wire, undefined);
    } else throw invalid();
  }
}

function varint(value) {
  const bytes = [];
  do { const byte = value % 128; value = Math.floor(value / 128); bytes.push(byte | (value ? 128 : 0)); } while (value);
  return Buffer.from(bytes);
}

function bytesField(number, value) {
  const data = Buffer.isBuffer(value) ? value : Buffer.from(value, 'utf8');
  return Buffer.concat([varint(number * 8 + 2), varint(data.length), data]);
}

function connectEnvelope(data, flags = 0) {
  if (!Buffer.isBuffer(data) || data.length > MAX_MESSAGE_BYTES || ![0, 2].includes(flags)) throw invalid();
  const header = Buffer.alloc(5);
  header[0] = flags;
  header.writeUInt32BE(data.length, 1);
  return Buffer.concat([header, data]);
}

function encodeAgentStateRequest(conversationId, subscriberId = `accounts-idle-${randomUUID()}`) {
  for (const value of [conversationId, subscriberId]) {
    if (typeof value !== 'string' || !value || Buffer.byteLength(value, 'utf8') > 256 || /[\x00-\x1f]/.test(value)) throw new ComponentError('native_conversation_id_invalid');
  }
  // Explicit zero-width slices suppress step, generator and executor pages.
  // disable_rehydration prevents this observation from loading stored history.
  const zeroSlice = Buffer.from([8, 0, 16, 0]);
  const body = Buffer.concat([
    bytesField(1, conversationId), bytesField(2, subscriberId),
    bytesField(3, zeroSlice), bytesField(5, zeroSlice), bytesField(6, zeroSlice),
    Buffer.from([56, 1]),
  ]);
  return connectEnvelope(body);
}

function lengthUpdate(buffer) {
  let totalLength = 0, seen = false;
  visitFields(buffer, (number, wire, value) => {
    if (number !== 3) return;
    if (seen || wire !== 0 || value > 0xffffffff) throw invalid();
    totalLength = value; seen = true;
  });
  // A present aggregate message with omitted scalar total_length uses the
  // proto3 default of zero. Whether an absent aggregate means zero depends on
  // the caller knowing that this is the initial full update, not a delta.
  return totalLength;
}

function decodeExecutionStateUpdate(update, initial) {
  let fullyIdle = false, executionActive = false;
  const seen = new Set(), counts = {}, statuses = [];
  const countFields = new Map([[17, 'backgroundTasks'], [20, 'pendingAgentMessages']]);
  visitFields(update, (number, wire, value) => {
    if (![3, 4, 5, 18, 25].includes(number) && !countFields.has(number)) return;
    if (seen.has(number)) throw invalid();
    seen.add(number);
    if ([3, 4, 5].includes(number)) {
      if (wire !== 0 || ![1, 2, 3, 4].includes(value)) throw invalid();
      statuses.push(value);
    } else if (number === 18) {
      if (wire !== 0 || ![0, 1].includes(value)) throw invalid();
      fullyIdle = value === 1;
    } else if (number === 25) {
      if (wire !== 2) throw invalid();
      // Initialization, active requests and retry waits use this message.
      // Any present status, including an unfamiliar variant, blocks switching.
      executionActive = true;
    } else {
      if (wire !== 2) throw invalid();
      counts[countFields.get(number)] = lengthUpdate(value);
    }
  });
  if (statuses.length !== 3) return { known: false, fullyIdle: false, errorCode: 'native_agent_state_incomplete' };
  for (const name of countFields.values()) {
    if (counts[name] !== undefined) continue;
    if (!initial) return { known: false, fullyIdle: false, errorCode: 'native_agent_state_incomplete' };
    counts[name] = 0;
  }
  // 2.31 publishes fully_idle from getActivitySnapshotLocked. That predicate
  // includes the main run status, child activity and pending agent work. The
  // removed queue/command aggregates cannot be reported as observed zeros.
  return { known: true, fullyIdle: fullyIdle && statuses.every(value => value === 1) &&
    !executionActive && Object.values(counts).every(value => value === 0), executionActive, ...counts };
}

function decodeAgentStateUpdate(buffer, { initial = false, nativeVersion = '2.29.0' } = {}) {
  if (typeof initial !== 'boolean') throw invalid();
  if (!['2.28.2', '2.29.0', '2.31.0', '2.31.2', '2.32.1'].includes(nativeVersion)) throw invalid();
  if (!Buffer.isBuffer(buffer) || buffer.length > MAX_MESSAGE_BYTES) throw invalid();
  let update;
  visitFields(buffer, (number, wire, value) => {
    if (number !== 1) return;
    if (update !== undefined || wire !== 2) throw invalid();
    update = value;
  });
  if (update === undefined) throw new ComponentError('native_agent_state_incomplete');
  if (nativeVersion === '2.31.0' || nativeVersion === '2.31.2' || nativeVersion === '2.32.1') return decodeExecutionStateUpdate(update, initial);
  let fullyIdle = false, hasActiveChildren = false;
  const seen = new Set(), counts = {};
  const countFields = new Map([[9, 'queuedSteps'], [12, 'backgroundCommands'], [17, 'backgroundTasks'], [20, 'pendingAgentMessages']]);
  visitFields(update, (number, wire, value) => {
    if (number !== 14 && number !== 18 && !countFields.has(number)) return;
    if (seen.has(number)) throw invalid();
    seen.add(number);
    if (number === 14 || number === 18) {
      if (wire !== 0 || ![0, 1].includes(value)) throw invalid();
      if (number === 18) fullyIdle = value === 1;
      else hasActiveChildren = value === 1;
    } else {
      if (wire !== 2) throw invalid();
      counts[countFields.get(number)] = lengthUpdate(value);
    }
  });
  // In pinned 2.28.2 AgentState.Updates builds a full update while holding the
  // state mutex, then yields its cloned result before reading delta events.
  // buildFullStateUpdateLocked omits these four aggregate messages exactly
  // when their backing slice lengths are zero. Subscription page filtering
  // changes step/generator/executor content, not these aggregate counts.
  // Only the first data frame of a fresh owned subscription has that contract;
  // no field combination can identify an arbitrary sparse delta as initial.
  for (const name of countFields.values()) {
    if (counts[name] !== undefined) continue;
    if (initial) counts[name] = 0;
    else return { known: false, fullyIdle: false, hasActiveChildren, ...counts, errorCode: 'native_agent_state_incomplete' };
  }
  return { known: true, fullyIdle: fullyIdle && !hasActiveChildren && Object.values(counts).every(value => value === 0),
    hasActiveChildren, ...counts };
}

function endStreamError(buffer) {
  let parsed;
  try { parsed = JSON.parse(buffer.toString('utf8')); } catch { throw invalid(); }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw invalid();
  if (parsed.error !== undefined && parsed.error !== null) throw new ComponentError('native_agent_state_failed');
  throw new ComponentError('native_agent_state_incomplete');
}

async function readInitialUpdate(reader, signal, nativeVersion) {
  let buffer = Buffer.alloc(0), totalBytes = 0;
  while (true) {
    if (signal.aborted) throw new ComponentError('native_agent_state_timeout');
    if (buffer.length >= 5) {
      const flags = buffer[0], length = buffer.readUInt32BE(1);
      // We do not advertise compression; compressed/unknown flags are errors.
      if (![0, 2].includes(flags)) throw invalid();
      if (length > MAX_MESSAGE_BYTES) throw new ComponentError('native_agent_state_too_large');
      if (buffer.length >= length + 5) {
        const message = buffer.subarray(5, length + 5);
        if (flags === 2) return endStreamError(message);
        return decodeAgentStateUpdate(message, { initial: true, nativeVersion });
      }
    }
    const result = await reader.read();
    if (result.done) throw new ComponentError(buffer.length ? 'native_agent_state_invalid' : 'native_agent_state_incomplete');
    totalBytes += result.value.byteLength;
    if (totalBytes > MAX_MESSAGE_BYTES + 5) throw new ComponentError('native_agent_state_too_large');
    buffer = Buffer.concat([buffer, Buffer.from(result.value)]);
  }
}

async function readAgentState({ fetchLocal, baseUrl, csrf, conversationId, signal, timeoutMs = 5000, nativeVersion = '2.29.0' }) {
  const origin = loopbackOrigin(baseUrl);
  if (typeof fetchLocal !== 'function') throw new ComponentError('native_request_failed');
  if (typeof csrf !== 'string' || csrf.length < 16 || /[\r\n]/.test(csrf)) throw new ComponentError('native_csrf_invalid');
  if (!Number.isSafeInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 120000) throw new ComponentError('native_timeout_invalid');
  const body = encodeAgentStateRequest(conversationId);
  const controller = new AbortController();
  let timedOut = false, reader;
  const abort = () => { controller.abort(); void reader?.cancel().catch(() => {}); };
  const timer = setTimeout(() => { timedOut = true; abort(); }, timeoutMs);
  signal?.addEventListener('abort', abort, { once: true });
  try {
    if (signal?.aborted) throw new ComponentError('switch_cancelled');
    const response = await fetchLocal(origin + AGENT_STATE_PATH, {
      method: 'POST', redirect: 'error', credentials: 'omit', referrerPolicy: 'no-referrer', cache: 'no-store', signal: controller.signal,
      headers: { 'X-Codeium-Csrf-Token': csrf, 'Content-Type': 'application/connect+proto', 'Connect-Protocol-Version': '1', 'Connect-Timeout-Ms': String(timeoutMs) }, body,
    });
    if (!response.ok || response.status !== 200 || response.redirected) throw new ComponentError('native_agent_state_failed');
    if (!/^application\/connect\+proto(?:;|$)/i.test(response.headers.get('content-type') || '') || !response.body?.getReader) throw invalid();
    reader = response.body.getReader();
    return await readInitialUpdate(reader, controller.signal, nativeVersion);
  } catch (error) {
    if (signal?.aborted) throw new ComponentError('switch_cancelled');
    return { known: false, fullyIdle: false, errorCode: timedOut ? 'native_agent_state_timeout' : error instanceof ComponentError ? error.code : 'native_agent_state_failed' };
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener('abort', abort);
    // Cancel only this short-lived read subscription. No CancelCascade or task
    // cancellation RPC is called, and existing frontend subscriptions persist.
    controller.abort();
    try { await reader?.cancel(); } catch { /* the owned request may already be aborted */ }
    try { reader?.releaseLock(); } catch { /* an aborted stream can have no reader */ }
  }
}

module.exports = { AGENT_STATE_PATH, MAX_MESSAGE_BYTES, connectEnvelope, encodeAgentStateRequest, decodeAgentStateUpdate, readAgentState };
