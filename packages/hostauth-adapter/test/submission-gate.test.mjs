import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { createSubmissionGate, loopbackOrigin } = require('../src/submission-gate.cjs');

test('submission pause inspects only the URL and never reads an Authorization-bearing header object', () => {
  let filter, listener;
  const gate = createSubmissionGate({ baseUrl: 'http://127.0.0.1:12345', session: { webRequest: {
    onBeforeRequest(value, handler) { filter = value; listener = handler; },
  } } });
  assert.deepEqual(filter, { urls: ['http://127.0.0.1:12345/*'] });
  const request = { url: 'http://127.0.0.1:12345/exa.language_server_pb.LanguageServerService/SendUserCascadeMessage' };
  Object.defineProperty(request, 'requestHeaders', { get() { throw new Error('must never read headers'); } });
  Object.defineProperty(request, 'uploadData', { get() { throw new Error('must never read prompt'); } });
  const decisions = [];
  listener(request, decision => decisions.push(decision));
  const pause = gate.acquire();
  listener(request, decision => decisions.push(decision));
  assert.deepEqual(decisions, [{ cancel: false }], 'a paused submission remains pending rather than being cancelled');
  listener({ url: 'https://accounts.google.com/' }, decision => decisions.push(decision));
  pause.release();
  listener(request, decision => decisions.push(decision));
  assert.deepEqual(decisions, [{ cancel: false }, { cancel: false }, { cancel: false }, { cancel: false }]);
  assert.equal(pause.closedToFrontendRequests, true);
  assert.equal(pause.closedToNewWork, undefined);
  gate.dispose();
  assert.equal(listener, null);
});

test('held submissions resume once in order and an exceeded pending limit never bypasses the pause', () => {
  let listener;
  const gate = createSubmissionGate({ baseUrl: 'http://127.0.0.1:12345', maxPending: 1,
    allowedReadPaths: ['/read-state'], session: { webRequest: { onBeforeRequest(_, value) { listener = value; } } } });
  const lease = gate.acquire();
  const results = [];
  listener({ url: 'http://127.0.0.1:12345/send' }, decision => results.push(['held', decision.cancel]));
  listener({ url: 'http://127.0.0.1:12345/send' }, decision => results.push(['limit', decision.cancel]));
  listener({ url: 'http://127.0.0.1:12345/read-state' }, decision => results.push(['read', decision.cancel]));
  assert.deepEqual(results, [['limit', true], ['read', false]]);
  lease.release(); lease.release();
  assert.deepEqual(results, [['limit', true], ['read', false], ['held', false]]);
  gate.dispose();
});

test('submission gate refuses non-loopback, userinfo and path-bearing endpoints', () => {
  for (const value of ['https://example.com', 'http://localhost:1234', 'http://127.0.0.1', 'http://user@127.0.0.1:1234', 'http://127.0.0.1:1234/path']) {
    assert.throws(() => loopbackOrigin(value), { code: 'native_endpoint_invalid' });
  }
});
