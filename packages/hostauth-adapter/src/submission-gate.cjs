'use strict';

const { ComponentError } = require('./component-client.cjs');

function loopbackOrigin(baseUrl) {
  let url;
  try { url = new URL(baseUrl); } catch { throw new ComponentError('native_endpoint_invalid'); }
  if (!['http:', 'https:'].includes(url.protocol) || url.hostname !== '127.0.0.1' || !url.port || url.username || url.password ||
      url.pathname !== '/' || url.search || url.hash) throw new ComponentError('native_endpoint_invalid');
  return url.origin;
}

// Electron onBeforeRequest has no request-header subscription. In particular,
// this code must not migrate to onBeforeSendHeaders or a forwarding proxy.
// Hold new frontend requests in Electron until the account transaction releases
// them. Native schedulers need the separate project/auth boundary and native
// idle checks; this gate makes no claim to pause those schedulers.
function createSubmissionGate({ session, baseUrl, allowedReadPaths = [], maxPending = 256 }) {
  const origin = loopbackOrigin(baseUrl);
  if (!session?.webRequest?.onBeforeRequest) throw new ComponentError('native_submission_gate_unavailable');
  let owner;
  const pending = [];
  let disposed = false;
  if (!Number.isSafeInteger(maxPending) || maxPending < 1 || maxPending > 4096) throw new ComponentError('native_submission_limit_invalid');
  if (!Array.isArray(allowedReadPaths) || allowedReadPaths.some(value => typeof value !== 'string' || !value.startsWith('/') || value.includes('?'))) throw new ComponentError('native_read_paths_invalid');
  const reads = new Set(allowedReadPaths);
  const handler = ({ url }, callback) => {
    let matches = false;
    try { const parsed = new URL(url); matches = parsed.origin === origin && !reads.has(parsed.pathname); } catch { /* invalid URLs are not owned by this gate */ }
    if (owner && matches) {
      if (pending.length >= maxPending) callback({ cancel: true });
      else pending.push(callback);
    } else callback({ cancel: false });
  };
  session.webRequest.onBeforeRequest({ urls: [origin + '/*'] }, handler);
  return Object.freeze({
    origin,
    acquire() {
      if (disposed) throw new ComponentError('native_submission_gate_unavailable');
      if (owner) throw new ComponentError('switch_in_progress');
      const lease = {};
      owner = lease;
      let released = false;
      return Object.freeze({
        source: 'electron-network',
        closedToFrontendRequests: true,
        release() {
          if (released) return;
          released = true;
          if (owner !== lease) throw new ComponentError('native_submission_gate_owner_invalid');
          owner = undefined;
          for (const callback of pending.splice(0)) {
            try { callback({ cancel: false }); }
            catch { /* Electron may have already closed this owned request. */ }
          }
        },
      });
    },
    dispose() {
      if (owner) throw new ComponentError('switch_in_progress');
      disposed = true;
      session.webRequest.onBeforeRequest({ urls: [origin + '/*'] }, null);
    },
  });
}

module.exports = { createSubmissionGate, loopbackOrigin };
