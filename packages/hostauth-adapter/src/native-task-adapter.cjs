'use strict';

const { ComponentError } = require('./component-client.cjs');
const { createSubmissionGate, loopbackOrigin } = require('./submission-gate.cjs');
const { WORK_KINDS } = require('./switch-coordinator.cjs');
const { readAgentState, AGENT_STATE_PATH } = require('./native-agent-state.cjs');

const SERVICE = '/exa.language_server_pb.LanguageServerService/';
const NATIVE_PROGRAM_PROFILES = Object.freeze([
  Object.freeze({ version: '2.29.0',
    originalSha256: 'c98e7c331da5bcc37002a550e7d625b4c636f775fa2804f73777c6a500e1c3d5',
    patchedSha256: '169355a713b8199f6fe91dd5eaf81bc7f366544b2808014425fc820ccdd04bfc',
    providerSha256: '9a58d7ec73af4da1f60b9682d6632ed54a9f62dd2752372c8f53f8cbe116eb85' }),
  Object.freeze({ version: '2.28.2',
    originalSha256: 'a51a80eb1b355dc523dd9a6d7cec098ad450e3c9fb30d6001806cac86814f37b',
    patchedSha256: '684a9ea0bbba226335feac69f9fc45d27980522fe172979fd58de07f35c52441',
    providerSha256: 'c132ea0fc1688dfd614cae5ac5c47b89c6c36753375981a0600170cef896dfea' }),
]);
const { originalSha256: PROGRAM_SHA256, patchedSha256: PATCHED_PROGRAM_SHA256,
  providerSha256: PROVIDER_PROGRAM_SHA256 } = NATIVE_PROGRAM_PROFILES[0];

function resolveNativeProgram(nativeProgram) {
  const profile = NATIVE_PROGRAM_PROFILES.find(value => nativeProgram &&
    ['version', 'originalSha256', 'patchedSha256', 'providerSha256'].every(key => value[key] === nativeProgram[key]));
  if (!profile) throw new ComponentError('native_program_unsupported');
  return profile;
}
const CACHE_NAMES = Object.freeze(['peopleAPIUserInfo', 'availableModels', 'userInfo', 'loadCodeAssist', 'adminControls', 'businessAdminControls', 'quotaSummary']);
// The pinned provider copy refreshes its project through HostAuth before returning
// it to request construction. Native local A->B tests include a late request
// behind the callback barrier, live state counts, and a real RunCommand child.
// This verifies the local switch mechanism, not Google OAuth or quota ownership.
const AUTH_BOUNDARY_VERIFIED = true;
const READ_METHODS = Object.freeze(['GetRemoteControlInfo', 'GetAllCascadeTrajectories', 'WaitForConversationFullyIdle', 'GetLoadCodeAssist', 'GetUserStatus', 'GetAuthStatus', 'GetAvailableModels', 'RetrieveUserQuotaSummary']);
const MAX_BODY = 8 * 1024 * 1024;
const MAX_ERROR_BODY = 16 * 1024;

function fields(buffer) {
  let offset = 0;
  const result = [];
  function integer() {
    let value = 0, scale = 1;
    for (let i = 0; i < 10; i++) {
      if (offset >= buffer.length) throw new ComponentError('native_protocol_invalid');
      const byte = buffer[offset++];
      value += (byte & 127) * scale;
      if (!(byte & 128)) { if (!Number.isSafeInteger(value)) throw new ComponentError('native_protocol_invalid'); return value; }
      scale *= 128;
    }
    throw new ComponentError('native_protocol_invalid');
  }
  while (offset < buffer.length) {
    const tag = integer(), number = Math.floor(tag / 8), wire = tag & 7;
    if (number < 1) throw new ComponentError('native_protocol_invalid');
    if (wire === 0) result.push({ number, wire, value: integer() });
    else if (wire === 2) {
      const length = integer();
      if (offset + length > buffer.length) throw new ComponentError('native_protocol_invalid');
      result.push({ number, wire, value: buffer.subarray(offset, offset += length) });
    } else if (wire === 1 || wire === 5) {
      const length = wire === 1 ? 8 : 4;
      if (offset + length > buffer.length) throw new ComponentError('native_protocol_invalid');
      offset += length;
    } else throw new ComponentError('native_protocol_invalid');
  }
  return result;
}

function one(items, number, fallback) {
  const found = items.filter(item => item.number === number);
  if (found.length > 1) throw new ComponentError('native_protocol_invalid');
  return found.length ? found[0].value : fallback;
}

function encodeInteger(value) {
  const bytes = [];
  do { const byte = value % 128; value = Math.floor(value / 128); bytes.push(byte | (value ? 128 : 0)); } while (value);
  return Buffer.from(bytes);
}

function waitRequest(id) {
  const key = Buffer.from(id, 'utf8');
  return Buffer.concat([Buffer.from([10]), encodeInteger(key.length), key, Buffer.from([16, 1, 24, 0, 32, 0])]);
}

// Do not decode summary, waiting-step content, workspace paths or conversation
// bodies. Only map keys and native execution-state fields are inspected.
function decodeActivity(buffer) {
  const items = fields(buffer);
  const seen = new Set(), summaries = [];
  for (const item of items) {
    if (item.number !== 1) continue;
    if (item.wire !== 2) throw new ComponentError('native_protocol_invalid');
    const entry = fields(item.value);
    const key = one(entry, 1), raw = one(entry, 2);
    if (!Buffer.isBuffer(key) || !Buffer.isBuffer(raw) || key.length === 0 || key.length > 256) throw new ComponentError('native_protocol_invalid');
    const id = key.toString('utf8');
    if (seen.has(id)) throw new ComponentError('native_protocol_invalid');
    seen.add(id);
    const summary = fields(raw);
    const status = one(summary, 5, 0);
    if (![1, 2, 3, 4].includes(status)) throw new ComponentError('native_activity_unknown');
    for (const number of [18, 21, 26, 27, 30]) if (![0, 1].includes(one(summary, number, 0))) throw new ComponentError('native_protocol_invalid');
    const backgroundTaskCount = one(summary, 29, 0);
    if (!Number.isSafeInteger(backgroundTaskCount) || backgroundTaskCount < 0 || backgroundTaskCount > 0xffffffff) throw new ComponentError('native_protocol_invalid');
    summaries.push({ id, status, waiting: summary.filter(field => field.number === 8).length,
      hasActiveChildren: one(summary, 18, 0) === 1, notFullyIdle: one(summary, 21, 0) === 1,
      hasActivity: one(summary, 26, 0) === 1, cloud: one(summary, 27, 0) === 1,
      backgroundTaskCount, hasRunningSubagents: one(summary, 30, 0) === 1 });
  }
  return summaries;
}

function decodeWait(buffer) {
  const timedOut = one(fields(buffer), 1, 0);
  if (![0, 1].includes(timedOut)) throw new ComponentError('native_protocol_invalid');
  return { timedOut: timedOut === 1 };
}

function decodeRemoteControl(buffer) {
  const enabled = one(fields(buffer), 1, 0);
  if (![0, 1].includes(enabled)) throw new ComponentError('native_protocol_invalid');
  return { enabled: enabled === 1 };
}

function refreshActivity(text) {
  if (typeof text !== 'string' || !/^goroutine profile: total [1-9][0-9]*\r?\n/.test(text) ||
      !/^[1-9][0-9]* @ 0x[0-9a-f]+/m.test(text)) throw new ComponentError('native_refresh_observation_unavailable');
  let count = 0;
  for (const block of text.split(/\r?\n\r?\n/)) {
    const match = block.match(/(?:^|\n)([1-9][0-9]*) @ /);
    const cacheRefresh = /\/cache\.\(\*Cache[^\n]*\)\.(?:forceRefresh|backgroundRefresh|ForceRefresh)/.test(block);
    // Match the complete client package, including tab/image/video/unary and
    // stream methods, rather than a list which could miss an active model path.
    // Persistent goroutines in this package deliberately prevent a switch until
    // their behavior is understood; they must never be guessed to be idle.
    const cloudRequest = /\/codeassistclient\./.test(block);
    // A sleeping scheduled task can be fully idle according to AgentState but
    // still resume later. Likewise remote control can submit directly inside
    // the native process without passing through Electron's request listener.
    const scheduledWork = /\(\*(?:ScheduleTool|ScheduleSubHandler)\)\.(?:runTimer|runCron|handleOneShot|handleCron)/.test(block);
    const remoteAdmission = /\/(?:remotecontrol|remote_control)\./.test(block);
    // RunCommand waits for an external process without registering AgentState
    // background work. Its handler must drain independently of cascade state.
    const nativeCommand = /\/language_server\.\(\*Server\)\.RunCommand(?:\+|\.|\s)/.test(block);
    if (match && (cacheRefresh || cloudRequest || scheduledWork || remoteAdmission || nativeCommand)) count += Number(match[1]);
  }
  return { known: true, count };
}

async function readBody(response, max = MAX_BODY, allowFailure = false) {
  if (response.redirected || (!allowFailure && (!response.ok || response.status !== 200))) throw new ComponentError('native_request_failed');
  if (!response.body?.getReader) throw new ComponentError('native_protocol_invalid');
  const reader = response.body.getReader(), chunks = [];
  let bytes = 0;
  try {
    while (true) {
      const item = await reader.read();
      if (item.done) break;
      bytes += item.value.byteLength;
      if (bytes > max) {
        reader.cancel().catch(() => {});
        throw new ComponentError('native_response_too_large');
      }
      chunks.push(Buffer.from(item.value));
    }
  } finally { reader.releaseLock(); }
  return Buffer.concat(chunks, bytes);
}

function createNativeTaskAdapter({ electron, baseUrl, csrf, client, nativeProgramSha256, nativeProgram, fetchImpl, refreshNativeView, observationTimeoutMs = 5000, confirmationTimeoutMs = 120000 }) {
  const origin = loopbackOrigin(baseUrl);
  if (typeof csrf !== 'string' || csrf.length < 16 || /[\r\n]/.test(csrf)) throw new ComponentError('native_csrf_invalid');
  const session = electron?.session?.defaultSession;
  const fetchLocal = fetchImpl || ((url, options) => session.fetch(url, options));
  const gate = createSubmissionGate({ session, baseUrl: origin, allowedReadPaths: [...READ_METHODS.map(name => SERVICE + name), AGENT_STATE_PATH, '/debug/pprof/goroutine'] });
  let profile;
  try { profile = resolveNativeProgram(nativeProgram); } catch { /* Unknown or mixed program sets cannot switch accounts. */ }
  const authBoundarySafe = AUTH_BOUNDARY_VERIFIED && Boolean(profile) && nativeProgramSha256 === profile.providerSha256;
  let modelSelectionPending = false, modelSelectionAcknowledged = false, modelSelectionCaptured = false;

  async function request(path, { body, proto = false, profile = false, classifyReadError = false, signal, timeoutMs = observationTimeoutMs } = {}) {
    if (!path.startsWith('/') || path.startsWith('//')) throw new ComponentError('native_endpoint_invalid');
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    const abort = () => controller.abort();
    signal?.addEventListener('abort', abort, { once: true });
    try {
      if (signal?.aborted) throw new ComponentError('switch_cancelled');
      const response = await fetchLocal(origin + path, {
        method: body === undefined ? 'GET' : 'POST', redirect: 'error', credentials: 'omit', referrerPolicy: 'no-referrer', cache: 'no-store', signal: controller.signal,
        headers: { 'X-Codeium-Csrf-Token': csrf, ...(body !== undefined ? { 'Content-Type': proto ? 'application/proto' : 'application/json', 'Connect-Protocol-Version': '1' } : {}) },
        ...(body !== undefined ? { body: proto ? body : JSON.stringify(body) } : {}),
      });
      const contentType = response.headers.get('content-type') || '';
      if (proto && !/^application\/proto(?:;|$)/i.test(contentType)) throw new ComponentError('native_protocol_invalid');
      if (profile && !/^text\/plain(?:;|$)/i.test(contentType)) throw new ComponentError('native_refresh_observation_unavailable');
      if (!proto && !profile && !/^application\/json(?:;|$)/i.test(contentType)) throw new ComponentError('native_protocol_invalid');
      if (classifyReadError && !proto && !profile && !response.redirected && response.status >= 400 && response.status <= 599) {
        let verificationRequired = false;
        try {
          const failure = JSON.parse((await readBody(response, MAX_ERROR_BODY, true)).toString('utf8'));
          // Inspect only the bounded Connect error message from a failed read
          // RPC. Native wrappers may prefix this exact official 403 message;
          // other fields, URLs and response text never reach ComponentError.
          verificationRequired = failure?.code === 'internal' && typeof failure.message === 'string' &&
            /^(?:[A-Za-z0-9_./() -]{1,160}: ){0,4}request failed \(code 403\): Verify your account to continue\.$/.test(failure.message.trim());
        } catch { /* Invalid or oversized failure bodies remain a generic failure. */ }
        if (signal?.aborted) throw new ComponentError('switch_cancelled');
        if (verificationRequired) throw new ComponentError('native_account_verification_required');
        throw new ComponentError('native_request_failed');
      }
      const bytes = await readBody(response);
      if (proto) return bytes;
      return bytes.toString('utf8');
    } catch (error) {
      if (error instanceof ComponentError) throw error;
      throw new ComponentError(signal?.aborted ? 'switch_cancelled' : 'native_request_failed');
    } finally { clearTimeout(timer); signal?.removeEventListener('abort', abort); }
  }

  async function rpc(name, body, options = {}) {
    if (!READ_METHODS.includes(name)) throw new ComponentError('method_not_allowed');
    let text;
    try { text = await request(SERVICE + name, { ...options, body, classifyReadError: true }); }
    catch (error) {
      if (name === 'RetrieveUserQuotaSummary' && error?.code === 'native_request_failed')
        throw new ComponentError('native_quota_refresh_failed');
      throw error;
    }
    try { return JSON.parse(text); } catch { throw new ComponentError('native_protocol_invalid'); }
  }

  async function readRefresh(signal, timeoutMs = observationTimeoutMs) {
    return refreshActivity(await request('/debug/pprof/goroutine?debug=1', { signal, profile: true, timeoutMs }));
  }

  async function waitRefreshSettled(signal, deadline) {
    while (Date.now() < deadline) {
      if ((await readRefresh(signal, Math.min(observationTimeoutMs, deadline - Date.now()))).count === 0) return;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new ComponentError('native_cache_refresh_pending');
  }

  return Object.freeze({
    authBoundarySafe,
    confirmationCoverage: CACHE_NAMES,
    async pauseSubmissions() { return gate.acquire(); },
    async captureModelSelection() {
      if (typeof refreshNativeView !== 'function') throw new ComponentError('native_view_unavailable');
      const result = await refreshNativeView(undefined, { phase: 'capture' });
      if (result?.captured !== true) throw new ComponentError('native_model_capture_required');
      modelSelectionCaptured = true;
      modelSelectionPending = false; modelSelectionAcknowledged = false;
      return { captured: true };
    },
    async clearModelSelection() {
      if (typeof refreshNativeView !== 'function') throw new ComponentError('native_view_unavailable');
      const result = await refreshNativeView(undefined, { phase: 'clear' });
      if (result?.cleared !== true) throw new ComponentError('native_model_release_failed');
      modelSelectionCaptured = false; modelSelectionPending = false; modelSelectionAcknowledged = false;
      return { cleared: true };
    },
    async readActivity({ signal } = {}) {
      const coverage = [...WORK_KINDS];
      try {
        const remote = decodeRemoteControl(await request(SERVICE + 'GetRemoteControlInfo', { body: Buffer.alloc(0), proto: true, signal }));
        if (remote.enabled) return { known: true, fullyIdle: false, coverage, active: [{ kind: 'remoteControl' }] };
        const summaries = decodeActivity(await request(SERVICE + 'GetAllCascadeTrajectories', { body: Buffer.alloc(0), proto: true, signal }));
        const active = summaries.filter(summary => summary.status !== 1 || summary.waiting || summary.hasActiveChildren || summary.notFullyIdle || summary.hasActivity || summary.backgroundTaskCount || summary.hasRunningSubagents)
          .map(summary => ({ id: summary.id, kind: summary.cloud ? 'cloud' : 'native' }));
        if (active.length) return { known: true, fullyIdle: false, coverage, active };
        const refresh = await readRefresh(signal);
        if (refresh.count) return { known: true, fullyIdle: false, coverage, active: [{ kind: 'nativeRequestOrBackgroundWork', count: refresh.count }] };
        // Historical cloud summaries do not use this local host's provider.
        // Active cloud work above blocks; no remote-idle RPC is sent here.
        for (const summary of summaries.filter(value => !value.cloud)) {
          const response = decodeWait(await request(SERVICE + 'WaitForConversationFullyIdle', { body: waitRequest(summary.id), proto: true, signal }));
          if (response.timedOut) return { known: true, fullyIdle: false, coverage, active: [{ id: summary.id, kind: 'native' }] };
          // GetAllCascadeTrajectories omits live background aggregates, and
          // IsFullyIdle alone does not include pending background tasks. Read
          // their initial counts from a dedicated native state subscription.
          const state = await readAgentState({ fetchLocal, baseUrl: origin, csrf, conversationId: summary.id, signal, timeoutMs: observationTimeoutMs });
          if (!state.known) return { known: false, fullyIdle: false, coverage, active: [], errorCode: state.errorCode || 'native_agent_state_incomplete' };
          if (!state.fullyIdle) return { known: true, fullyIdle: false, coverage, active: [{ id: summary.id, kind: 'nativeBackgroundWork',
            backgroundTasks: state.backgroundTasks, backgroundCommands: state.backgroundCommands, queuedSteps: state.queuedSteps,
            pendingAgentMessages: state.pendingAgentMessages, hasActiveChildren: state.hasActiveChildren }] };
        }
        const after = await readRefresh(signal);
        const remoteAfter = decodeRemoteControl(await request(SERVICE + 'GetRemoteControlInfo', { body: Buffer.alloc(0), proto: true, signal }));
        return { known: true, fullyIdle: after.count === 0 && !remoteAfter.enabled, coverage,
          active: [...(after.count ? [{ kind: 'nativeRequestOrBackgroundWork', count: after.count }] : []), ...(remoteAfter.enabled ? [{ kind: 'remoteControl' }] : [])] };
      } catch (error) {
        if (error?.code === 'switch_cancelled') throw error;
        return { known: false, fullyIdle: false, coverage, active: [], errorCode: error?.code || 'native_activity_unknown' };
      }
    },
    async confirmIdentity({ accountId, generation, tokenWasObserved }) {
      if (authBoundarySafe && !modelSelectionCaptured) throw new ComponentError('native_model_capture_required');
      if (modelSelectionPending && !modelSelectionAcknowledged) throw new ComponentError('native_model_selection_required');
      const accounts = await client.request('listAccounts');
      const target = accounts.find(account => account.id === accountId && !account.disabled);
      if (!target?.email) throw new ComponentError('native_identity_unconfirmed');
      const deadline = Date.now() + confirmationTimeoutMs;
      const remaining = () => {
        const timeoutMs = deadline - Date.now();
        if (timeoutMs <= 0) throw new ComponentError('native_identity_timeout');
        return { timeoutMs };
      };
      // Force a token-dependent backend call. It must observe the committed
      // HostAuth generation and invalidate the seven native metadata caches.
      const firstLoad = await rpc('GetLoadCodeAssist', { forceRefresh: true }, remaining());
      if (!tokenWasObserved()) throw new ComponentError('native_token_not_observed');
      await waitRefreshSettled(undefined, deadline);
      // The old generation's refreshes were drained before commit. These force
      // calls are sequential so a previous singleflight cannot be mistaken for
      // a second completed request for the newly selected account.
      await rpc('GetAvailableModels', { forceRefresh: true }, remaining());
      await rpc('RetrieveUserQuotaSummary', { forceRefresh: true }, remaining());
      await waitRefreshSettled(undefined, deadline);
      const secondLoad = await rpc('GetLoadCodeAssist', { forceRefresh: true }, remaining());
      await waitRefreshSettled(undefined, deadline);
      const user = await rpc('GetUserStatus', {}, remaining());
      const auth = await rpc('GetAuthStatus', {}, remaining());
      await waitRefreshSettled(undefined, deadline);
      if (!tokenWasObserved()) throw new ComponentError('native_token_not_observed');
      // 2.28 GetAuthStatus exposes hasValidAuth, not a project ID. Compare two
      // completed force-refresh results across the metadata refreshes instead.
      // The native email must match the independently added selected account.
      const firstProject = firstLoad?.response?.cloudaicompanionProject;
      const secondProject = secondLoad?.response?.cloudaicompanionProject;
      const projectMatched = typeof firstProject === 'string' && firstProject.length > 0 && firstProject === secondProject;
      const confirmed = user?.userStatus?.email?.toLowerCase() === target.email.toLowerCase() && auth?.authResult?.hasValidAuth === true && projectMatched;
      if (confirmed && authBoundarySafe) {
        if (typeof refreshNativeView !== 'function') throw new ComponentError('native_view_unavailable');
        const view = await refreshNativeView(user.userStatus, { requireExplicitSelection: modelSelectionPending });
        if (view?.selectionReady !== true) {
          modelSelectionPending = true; modelSelectionAcknowledged = false;
          throw new ComponentError('native_model_selection_required');
        }
        modelSelectionPending = false; modelSelectionAcknowledged = false;
      }
      return { confirmed, projectMatched, accountId, generation,
        caches: confirmed ? CACHE_NAMES.map(name => ({ name, accountId, generation, settled: true })) : [] };
    },
    acknowledgeModelSelection() { if (modelSelectionPending) modelSelectionAcknowledged = true; },
    close() { gate.dispose(); },
  });
}

module.exports = { createNativeTaskAdapter, decodeActivity, decodeWait, decodeRemoteControl, refreshActivity, waitRequest, PROGRAM_SHA256, PATCHED_PROGRAM_SHA256, PROVIDER_PROGRAM_SHA256, NATIVE_PROGRAM_PROFILES, resolveNativeProgram, CACHE_NAMES, AUTH_BOUNDARY_VERIFIED };
