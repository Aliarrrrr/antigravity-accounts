'use strict';

const { ComponentError } = require('./component-client.cjs');

// Only the independently added account provider is called here. This barrier
// never receives an incoming HTTP request or any official Authorization header.
class TokenBarrier {
  constructor(provider, { maxWaiting = 256 } = {}) {
    this.provider = provider;
    this.maxWaiting = maxWaiting;
    this.inflight = new Set();
    this.waiting = [];
    this.held = undefined;
    this.closed = false;
  }

  getToken() {
    if (this.closed) return Promise.reject(new ComponentError('component_unavailable'));
    if (this.held) {
      if (this.waiting.length >= this.maxWaiting) return Promise.reject(new ComponentError('token_wait_limit'));
      return new Promise((resolve, reject) => this.waiting.push({ resolve, reject }));
    }
    const pending = Promise.resolve().then(() => this.provider());
    this.inflight.add(pending);
    // Attach both branches so a provider failure cannot create an unhandled
    // rejection in a detached finally() promise.
    pending.then(() => this.inflight.delete(pending), () => this.inflight.delete(pending));
    return pending;
  }

  async acquire() {
    if (this.closed) throw new ComponentError('component_unavailable');
    if (this.held) throw new ComponentError('token_barrier_held');
    const owner = {};
    this.held = owner;
    // No new provider invocation can start after held was set. These promises
    // represent callbacks already started under the old account generation.
    await Promise.allSettled([...this.inflight]);
    if (this.closed) throw new ComponentError('component_unavailable');
    let released = false;
    const barrier = this;
    return Object.freeze({
      get waiting() { return barrier.waiting.length; },
      release: () => {
        if (released) return;
        released = true;
        if (this.held !== owner) throw new ComponentError('token_barrier_owner_invalid');
        this.held = undefined;
        const queued = this.waiting.splice(0);
        for (const item of queued) this.getToken().then(item.resolve, item.reject);
      },
    });
  }

  close() {
    this.closed = true;
    for (const item of this.waiting.splice(0)) item.reject(new ComponentError('component_unavailable'));
  }
}

module.exports = { TokenBarrier };
