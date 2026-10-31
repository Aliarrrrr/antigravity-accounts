'use strict';

const { spawn } = require('node:child_process');

const METHODS = new Set(['getToken', 'getStatus', 'listAccounts', 'selectAccount', 'setPrimaryAccount', 'beginSwitch', 'commitSwitch', 'cancelSwitch', 'configureOAuth', 'beginLogin', 'removeAccount', 'shutdown']);
const MAX_LINE_BYTES = 128 * 1024;

class ComponentError extends Error {
  constructor(code, message = 'The independent account component could not complete this operation.') {
    super(message);
    this.name = 'ComponentError';
    this.code = code;
  }
}

// This transport never forwards component error messages or stderr. A component
// must not be able to accidentally put credentials in the host's Electron log.
class ComponentClient {
  constructor(executable, { args = ['--stdio'], timeoutMs = 120000, spawnImpl = spawn, onFatal = () => {} } = {}) {
    this.pending = new Map();
    this.sequence = 0;
    this.timeoutMs = timeoutMs;
    this.onFatal = onFatal;
    this.closed = false;
    this.closing = false;
    this.buffer = '';
    this.child = spawnImpl(executable, args, { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true, shell: false });
    this.child.stdout.setEncoding('utf8');
    this.child.stdout.on('data', (chunk) => this.accept(chunk));
    this.child.stderr.resume();
    this.child.stdin.on('error', () => this.fail('component_pipe_failed'));
    this.child.on('error', () => this.fail('component_start_failed'));
    this.child.on('exit', () => this.fail('component_exited'));
  }

  accept(chunk) {
    if (this.closed) return;
    this.buffer += chunk;
    if (Buffer.byteLength(this.buffer, 'utf8') > MAX_LINE_BYTES && !this.buffer.includes('\n')) {
      this.fail('component_response_too_large');
      return;
    }
    let boundary;
    while ((boundary = this.buffer.indexOf('\n')) >= 0) {
      const line = this.buffer.slice(0, boundary).replace(/\r$/, '');
      this.buffer = this.buffer.slice(boundary + 1);
      if (Buffer.byteLength(line, 'utf8') > MAX_LINE_BYTES || line.length === 0) {
        this.fail('component_protocol_error');
        return;
      }
      let response;
      try { response = JSON.parse(line); } catch { this.fail('component_protocol_error'); return; }
      if (!response || !Number.isSafeInteger(response.id) || !this.pending.has(response.id) ||
          (Object.hasOwn(response, 'result') === Object.hasOwn(response, 'error'))) {
        this.fail('component_protocol_error');
        return;
      }
      const pending = this.pending.get(response.id);
      this.pending.delete(response.id);
      clearTimeout(pending.timer);
      if (response.error) {
        const code = typeof response.error.code === 'string' && /^[a-z][a-z0-9_]{0,63}$/.test(response.error.code)
          ? response.error.code : 'component_request_failed';
        pending.reject(new ComponentError(code));
      } else pending.resolve(response.result);
    }
    if (Buffer.byteLength(this.buffer, 'utf8') > MAX_LINE_BYTES) this.fail('component_response_too_large');
  }

  request(method, params = {}, { timeoutMs = this.timeoutMs } = {}) {
    if (!METHODS.has(method)) return Promise.reject(new ComponentError('method_not_allowed'));
    if (this.closed || this.closing && method !== 'shutdown') return Promise.reject(new ComponentError('component_unavailable'));
    const id = ++this.sequence;
    let line;
    try { line = JSON.stringify({ id, method, params }) + '\n'; } catch { return Promise.reject(new ComponentError('invalid_request')); }
    if (Buffer.byteLength(line, 'utf8') > MAX_LINE_BYTES) return Promise.reject(new ComponentError('request_too_large'));
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        // A late response cannot be associated safely after timeout. Terminate
        // this transport instead of accidentally using a later token result.
        this.fail('component_timeout');
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      try { this.child.stdin.write(line); } catch { this.fail('component_pipe_failed'); }
    });
  }

  fail(code) {
    if (this.closed) return;
    this.closed = true;
    this.buffer = '';
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(new ComponentError(code));
    }
    this.pending.clear();
    this.child.kill();
    if (!this.closing) this.onFatal(code);
  }

  async close() {
    if (this.closed) return;
    this.closing = true;
    try { await this.request('shutdown', {}, { timeoutMs: 1500 }); } catch { /* fixed errors, never print transport data */ }
    this.fail('component_closed');
  }
}

module.exports = { ComponentClient, ComponentError };
