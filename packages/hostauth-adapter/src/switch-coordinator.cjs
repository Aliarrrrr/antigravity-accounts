'use strict';

const { ComponentError } = require('./component-client.cjs');

const WORK_KINDS = Object.freeze(['generation', 'tools', 'subagents', 'background']);
const delay = (ms, signal) => new Promise((resolve, reject) => {
  if (signal.aborted) return reject(new ComponentError('switch_cancelled'));
  const timer = setTimeout(done, ms);
  function done() { signal.removeEventListener('abort', abort); resolve(); }
  function abort() { clearTimeout(timer); signal.removeEventListener('abort', abort); reject(new ComponentError('switch_cancelled')); }
  signal.addEventListener('abort', abort, { once: true });
});

function coversAllWork(value) {
  return Array.isArray(value) && WORK_KINDS.every(kind => value.includes(kind));
}

// An idle result must come from a native task adapter. A quiet HTTP connection,
// a checked UI box or absence of a visible conversation is never an idle result.
function isFullyIdle(result) {
  return result?.known === true && result?.fullyIdle === true &&
    coversAllWork(result.coverage) && Array.isArray(result.active) && result.active.length === 0;
}

class SwitchCoordinator {
  constructor({ client, taskAdapter, tokenBarrier, pollIntervalMs = 250, sleep = delay, onChange = () => {} }) {
    this.client = client;
    this.taskAdapter = taskAdapter;
    this.tokenBarrier = tokenBarrier;
    this.pollIntervalMs = pollIntervalMs;
    this.sleep = sleep;
    this.onChange = onChange;
    this.operation = undefined;
    this.observedToken = undefined;
    this.state = Object.freeze({ state: 'active', cancellable: false });
    this.closed = false;
  }

  getStatus() { return this.state; }

  setState(state) {
    this.state = Object.freeze(state);
    this.onChange(this.state);
  }

  observeToken({ accountId, generation } = {}) {
    if (typeof accountId === 'string' && Number.isSafeInteger(generation)) {
      this.observedToken = { accountId, generation };
    }
  }

  async selectAccount(id) {
    if (this.closed) throw new ComponentError('component_unavailable');
    if (typeof id !== 'string' || !id) throw new ComponentError('invalid_account');
    if (this.operation) {
      if (this.state.state === 'blocked' && this.operation.targetId === id) return this.recover();
      throw new ComponentError('switch_in_progress');
    }
    const adapter = this.taskAdapter;
    if (!adapter || typeof adapter.pauseSubmissions !== 'function' || typeof adapter.readActivity !== 'function' ||
        typeof adapter.confirmIdentity !== 'function' || typeof adapter.captureModelSelection !== 'function' ||
        typeof adapter.clearModelSelection !== 'function' || adapter.authBoundarySafe !== true || !this.tokenBarrier ||
        !Array.isArray(adapter.confirmationCoverage) || adapter.confirmationCoverage.length === 0) throw new ComponentError('native_switch_unsupported');
    const operation = { targetId: id, controller: new AbortController(), committed: false, committing: false };
    this.operation = operation;
    this.setState({ state: 'waiting', targetAccountId: id, cancellable: true });
    try {
      const before = await this.client.request('getStatus');
      if (!before.primaryAccountId || !Number.isSafeInteger(before.generation)) throw new ComponentError('primary_account_required');
      if (before.selectedAccountId === id) { this.operation = undefined; this.setState({ state: 'active', cancellable: false }); return before; }
      operation.before = before;
      operation.pause = await adapter.pauseSubmissions({ signal: operation.controller.signal });
      if (operation.pause?.closedToFrontendRequests !== true || typeof operation.pause.release !== 'function') {
        throw new ComponentError('native_pause_unsupported');
      }
      while (true) {
        this.throwIfCancelled(operation);
        const activity = await adapter.readActivity({ signal: operation.controller.signal });
        this.throwIfCancelled(operation);
        if (!activity || activity.known !== true || !coversAllWork(activity.coverage)) throw new ComponentError('native_activity_unknown');
        if (isFullyIdle(activity)) {
          operation.tokens = await this.tokenBarrier.acquire();
          this.throwIfCancelled(operation);
          const criticalActivity = await adapter.readActivity({ signal: operation.controller.signal });
          this.throwIfCancelled(operation);
          if (!criticalActivity || criticalActivity.known !== true || !coversAllWork(criticalActivity.coverage)) throw new ComponentError('native_activity_unknown');
          if (isFullyIdle(criticalActivity)) break;
          // A native timer may have started work after the first idle result.
          // Release old-account callbacks so that work can finish, then retry.
          operation.tokens.release();
          operation.tokens = undefined;
        }
        await this.sleep(this.pollIntervalMs, operation.controller.signal);
      }
      this.throwIfCancelled(operation);
      operation.modelCaptureStarted = true;
      const captured = await adapter.captureModelSelection();
      if (captured?.captured !== true) throw new ComponentError('native_model_capture_required');
      this.throwIfCancelled(operation);
      const prepared = await this.client.request('beginSwitch', { targetId: id, expectedGeneration: before.generation });
      if (!prepared || typeof prepared.ticket !== 'string' || !prepared.ticket || prepared.targetAccountId !== id || prepared.expectedGeneration !== before.generation) {
        throw new ComponentError('switch_ticket_invalid');
      }
      operation.ticket = prepared.ticket;
      this.throwIfCancelled(operation);
      // The callback barrier remains held across prepare/commit. The native
      // provider adapter must obtain current auth before reading an account
      // project, so a late native request cannot pair an old project with the
      // account committed below. This is not a global native scheduler pause.
      operation.committing = true;
      this.setState({ state: 'confirming', targetAccountId: id, cancellable: false });
      let committed;
      try { committed = await this.client.request('commitSwitch', { ticket: operation.ticket }); }
      catch (error) { return await this.reconcile(operation, error?.code || 'switch_commit_unconfirmed'); }
      this.acceptCommit(operation, committed);
      return await this.confirm(operation);
    } catch (error) {
      if (operation.committing || operation.committed) {
        this.block(operation, error?.code || 'switch_confirmation_failed');
      } else {
        await this.releaseUncommitted(operation);
      }
      throw new ComponentError(error?.code || 'account_switch_failed');
    }
  }

  throwIfCancelled(operation) {
    if (operation.controller.signal.aborted || this.closed) throw new ComponentError('switch_cancelled');
  }

  acceptCommit(operation, result) {
    if (result?.selectedAccountId !== operation.targetId || result.generation !== operation.before.generation + 1) {
      throw new ComponentError('switch_commit_unconfirmed');
    }
    operation.committed = true;
    operation.generation = result.generation;
    // Native identity confirmation must be able to fetch the committed token.
    operation.tokens?.release();
    operation.tokens = undefined;
  }

  async reconcile(operation, originalCode = 'switch_commit_unconfirmed') {
    const current = await this.client.request('getStatus');
    if (current.selectedAccountId === operation.targetId && current.generation === operation.before.generation + 1 && !current.pendingSwitch) {
      this.acceptCommit(operation, current);
      return this.confirm(operation);
    }
    if (current.selectedAccountId === operation.before.selectedAccountId && current.generation === operation.before.generation) {
      // A prepared token can expire before commit. The authoritative component
      // status proves the original choice is still selected; cancel only a
      // pending ticket which is actually still present.
      if (current.pendingSwitch && current.pendingSwitch.ticket !== operation.ticket) throw new ComponentError('switch_commit_unconfirmed');
      if (!current.pendingSwitch) operation.ticket = undefined;
      operation.committing = false;
      throw new ComponentError(originalCode);
    }
    throw new ComponentError('switch_commit_unconfirmed');
  }

  async confirm(operation) {
    this.setState({ state: 'confirming', targetAccountId: operation.targetId, cancellable: false });
    const result = await this.taskAdapter.confirmIdentity({
      accountId: operation.targetId,
      generation: operation.generation,
      tokenWasObserved: () => this.observedToken?.accountId === operation.targetId && this.observedToken?.generation === operation.generation,
    });
    const cacheCoverage = this.taskAdapter.confirmationCoverage;
    const cachesMatched = Array.isArray(result?.caches) && cacheCoverage.every(name => result.caches.some(cache =>
      cache?.name === name && cache.accountId === operation.targetId && cache.generation === operation.generation && cache.settled === true));
    if (result?.confirmed !== true || result.projectMatched !== true || !cachesMatched || result.accountId !== operation.targetId || result.generation !== operation.generation ||
        this.observedToken?.accountId !== operation.targetId || this.observedToken?.generation !== operation.generation) {
      throw new ComponentError('native_identity_unconfirmed');
    }
    await this.taskAdapter.clearModelSelection();
    operation.modelCaptureStarted = false;
    await operation.pause.release();
    this.operation = undefined;
    this.setState({ state: 'active', accountId: operation.targetId, generation: operation.generation, cancellable: false });
    return { selectedAccountId: operation.targetId, generation: operation.generation };
  }

  block(operation, code) {
    this.operation = operation;
    this.setState({ state: 'blocked', targetAccountId: operation.targetId, errorCode: code, cancellable: false });
  }

  async recover() {
    const operation = this.operation;
    if (!operation || this.state.state !== 'blocked') throw new ComponentError('switch_in_progress');
    try {
      if (!operation.committing && !operation.committed) { await this.releaseUncommitted(operation); return this.client.request('getStatus'); }
      return operation.committed ? await this.confirm(operation) : await this.reconcile(operation);
    }
    catch (error) { this.block(operation, error?.code || 'switch_confirmation_failed'); throw new ComponentError(error?.code || 'switch_confirmation_failed'); }
  }

  async releaseUncommitted(operation) {
    try {
      if (operation.ticket) {
        await this.client.request('cancelSwitch', { ticket: operation.ticket });
        operation.ticket = undefined;
      }
      operation.tokens?.release();
      operation.tokens = undefined;
      if (operation.modelCaptureStarted) {
        await this.taskAdapter.clearModelSelection();
        operation.modelCaptureStarted = false;
      }
      if (operation.pause && typeof operation.pause.release === 'function') await operation.pause.release();
    } catch {
      this.block(operation, 'switch_cancel_unconfirmed');
      throw new ComponentError('switch_cancel_unconfirmed');
    }
    if (this.operation === operation) this.operation = undefined;
    this.setState({ state: 'active', cancellable: false });
  }

  cancelSwitch() {
    if (!this.operation) return { cancelled: false };
    if (!this.state.cancellable) throw new ComponentError('switch_not_cancellable');
    // Aborting this wait never calls CancelCascade or any task cancellation RPC.
    this.operation.controller.abort();
    return { cancelled: true };
  }

  async close() {
    this.closed = true;
    this.operation?.controller.abort();
    // Closing the host does not release a blocked pause while the LS is alive.
    // The normal host shutdown owns LS termination and removes the session.
  }
}

module.exports = { SwitchCoordinator, isFullyIdle, WORK_KINDS };
