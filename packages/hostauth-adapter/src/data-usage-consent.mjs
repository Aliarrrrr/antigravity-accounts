// Runs in the native renderer's main world. Do not expose Electron or IPC here.
// Only the post-onboarding data-use component is eligible for this preference.
export function installDataUsageConsent({ document, window, preference = 'off' } = {}) {
  if (!document?.documentElement || !window || typeof window.MutationObserver !== 'function') return null;
  const key = Symbol.for('antigravity.accounts.data-usage-consent.v1');
  const allowed = value => ['off', 'on', 'manual'].includes(value);
  if (!allowed(preference)) return null;
  if (window[key]) { window[key].setPreference(preference); return window[key]; }
  const excluded = 'pre,code,textarea,[contenteditable],[data-message-role],[data-message-id],.markdown,.prose,[data-user-content],iframe,[hidden],[aria-hidden="true"],[data-antigravity-accounts-menu]';
  const titles = new Set(['Security Notice & Data Use', 'Terms of Service & Data Use', 'One last thing before getting started…']);
  const attempts = new WeakMap(), hidden = new Map();
  let stopped = false, queued = false, revision = 0;
  const sourceOf = value => typeof value === 'function' ? Function.prototype.toString.call(value) : '';
  function restore(dialog) {
    const previous = hidden.get(dialog);
    if (!previous) return;
    if (dialog.style.getPropertyValue('visibility') === 'hidden') {
      if (previous.value) dialog.style.setProperty('visibility', previous.value, previous.priority);
      else dialog.style.removeProperty('visibility');
    }
    hidden.delete(dialog);
  }
  function currentFiber(element) {
    const name = Object.keys(element).find(value => value.startsWith('__reactFiber$'));
    const fiber = name && element[name];
    if (!fiber) return undefined;
    const alternate = fiber.alternate;
    if (!alternate) return fiber;
    const rootOf = candidate => {
      let root = candidate;
      const visited = new Set();
      while (root.return && !visited.has(root) && visited.size < 256) { visited.add(root); root = root.return; }
      return root.return ? undefined : root;
    };
    const firstRoot = rootOf(fiber), otherRoot = rootOf(alternate);
    const firstCurrent = Boolean(firstRoot && firstRoot.stateNode?.current === firstRoot);
    const otherCurrent = Boolean(otherRoot && otherRoot.stateNode?.current === otherRoot);
    if (firstCurrent !== otherCurrent) return firstCurrent ? fiber : alternate;
    // Bailouts can leave both alternates' return pointers leading to the same
    // current root. Resolve their membership through parent child/sibling lists,
    // as React does, instead of treating either return chain as authoritative.
    let a = fiber, b = alternate;
    const parents = new Set();
    const contains = (parent, first, second) => {
      const siblings = new Set();
      for (let child = parent.child; child && !siblings.has(child) && siblings.size < 2048; child = child.sibling) {
        if (child === first) return 1;
        if (child === second) return 2;
        siblings.add(child);
      }
      return 0;
    };
    while (a.return && !parents.has(a) && parents.size < 256) {
      parents.add(a);
      const parentA = a.return, parentB = parentA.alternate;
      if (!parentB) {
        if (!parentA.return) return undefined;
        a = b = parentA.return;
        continue;
      }
      if (parentA.child === parentB.child) {
        const found = contains(parentA, a, b);
        return found === 1 ? fiber : found === 2 ? alternate : undefined;
      }
      if (a.return !== b.return) { a = parentA; b = parentB; }
      else {
        const foundA = contains(parentA, a, b);
        if (foundA === 1) { a = parentA; b = parentB; }
        else if (foundA === 2) { a = parentB; b = parentA; }
        else {
          const foundB = contains(parentB, a, b);
          if (foundB === 1) { a = parentB; b = parentA; }
          else if (foundB === 2) { a = parentA; b = parentB; }
          else return undefined;
        }
      }
      if (a.alternate !== b) return undefined;
    }
    if (a.return || !a.stateNode?.current) return undefined;
    return a.stateNode.current === a ? fiber : a.stateNode.current === b ? alternate : undefined;
  }
  function inspect(heading) {
    if (!heading.isConnected || heading.closest(excluded) || !titles.has(heading.textContent.trim())) return undefined;
    const dialog = heading.closest('[role="dialog"]');
    if (!dialog || dialog.closest(excluded) || dialog.querySelectorAll('h2').length !== 1) return undefined;
    let fiber = currentFiber(heading), content, owner;
    const visited = new Set();
    while (fiber && !visited.has(fiber) && visited.size < 128) {
      visited.add(fiber);
      const props = fiber.memoizedProps, source = sourceOf(fiber.type);
      if (!content && props?.embedded === true && typeof props.telemetryEnabled === 'boolean' &&
          typeof props.onTelemetryChange === 'function' && typeof props.userDataCollectionForceDisabled === 'boolean' &&
          typeof props.gcpTos === 'boolean' && source.includes('Terms of Service & Data Use') &&
          source.includes('onTelemetryChange') &&
          (source.includes('Security Notice & Data Use') && source.includes('by allowing Google to collect and use my Interactions data') ||
           source.includes('One last thing before getting started') && source.includes('Allow Google to collect and use your Interactions data'))) content = fiber;
      if (content && typeof props?.isGcpTos === 'boolean' && props.reason === 'account_changed' &&
          typeof props.onAccept === 'function' && typeof fiber.key === 'string' &&
          source.includes('POST_ONBOARDING_TERMS_SHOWN') && source.includes('POST_ONBOARDING_TERMS_ACCEPTED') &&
          source.includes('defaultTelemetryOnError:!0') &&
          (source.includes('Accept and continue') || source.includes('terms_flow:') && source.includes('"Continue"'))) { owner = fiber; break; }
      fiber = fiber.return;
    }
    if (!content || !owner || content.memoizedProps.gcpTos !== owner.memoizedProps.isGcpTos) return undefined;
    const modernTerms = Object.prototype.hasOwnProperty.call(owner.memoizedProps, 'termsFlow');
    if (modernTerms && !['post_onboarding', 'backstop'].includes(owner.memoizedProps.termsFlow)) return undefined;
    const accept = [...dialog.querySelectorAll(modernTerms ? 'button' : 'button[data-autofocus="true"]')]
      .filter(button => button.textContent.trim() === (modernTerms ? 'Continue' : 'Accept and continue'));
    if (accept.length !== 1 || accept[0].disabled) return undefined;
    const callbacks = [], hooks = new Set(), ordered = [];
    for (let hook = owner.memoizedState; hook && !hooks.has(hook) && hooks.size < 192; hook = hook.next) {
      hooks.add(hook); ordered.push(hook);
      const callback = Array.isArray(hook.memoizedState) && hook.memoizedState[0], source = sourceOf(callback);
      if (source.includes('.setUserAccountSettings(') && source.includes('telemetryEnabled:') &&
          source.includes('disableTelemetry:') && source.includes('USER_SETTINGS_UPDATE_FAILED')) {
        const previous = ordered.at(-2), fetchSource = sourceOf(Array.isArray(previous?.memoizedState) && previous.memoizedState[0]);
        const refs = ordered.slice(-9, -2).map(value => value.memoizedState);
        const service = Array.isArray(previous?.memoizedState) && previous.memoizedState[1]?.[0];
        const caches = ordered.slice(0, -9).map(value => value.memoizedState).filter(value => value &&
          Object.keys(value).length === 1 && value.current && typeof value.current === 'object' &&
          ['telemetryEnabled', 'marketingEmailsEnabled', 'userDataCollectionForceDisabled'].every(name => typeof value.current[name] === 'boolean'));
        // Both reviewed native releases keep these seven settings-hook refs
        // immediately before fetchUserSettings and updateTelemetrySettings:
        // confirmed telemetry/mail, committed telemetry/mail, fetch, sent
        // telemetry/mail. A failed setter resolves after its own catch, so a
        // successful native commit counter is required before accepting.
        if (fetchSource.includes('.getUserAccountSettings(') && refs.length === 7 &&
            refs.every(value => value && Object.keys(value).length === 1 && 'current' in value) &&
            refs.slice(0, 2).every(value => typeof value.current === 'boolean') &&
            refs.slice(2).every(value => Number.isSafeInteger(value.current) && value.current >= 0) &&
            caches.length === 1 && typeof service?.getUserAccountSettings === 'function' && typeof service?.setUserAccountSettings === 'function')
          callbacks.push({ callback, confirmed: refs[0], confirmedMail: refs[1], committed: refs[2], sent: refs[5], service, cache: caches[0] });
      }
    }
    if (callbacks.length !== 1) return undefined;
    const stateGroups = [];
    for (let index = 0; index + 4 <= ordered.length; index++) {
      const group = ordered.slice(index, index + 4);
      if (group.every(hook => typeof hook.memoizedState === 'boolean' && typeof hook.queue?.dispatch === 'function')) stateGroups.push(group);
    }
    if (stateGroups.length !== 1) return undefined;
    return { heading, dialog, content, owner, telemetry: { ...callbacks[0], loading: stateGroups[0][3].memoizedState } };
  }
  async function processDialog(candidate) {
    const { heading, dialog, owner } = candidate;
    const selected = preference, startedAt = revision;
    if (selected === 'manual' || attempts.get(dialog) === `${startedAt}:${owner.key}`) return;
    attempts.set(dialog, `${startedAt}:${owner.key}`);
    hidden.set(dialog, { value: dialog.style.getPropertyValue('visibility'), priority: dialog.style.getPropertyPriority('visibility') });
    dialog.style.setProperty('visibility', 'hidden', 'important');
    let expired = false, timeout;
    const cancelled = () => expired || stopped || revision !== startedAt || preference !== selected;
    try {
      await Promise.race([(async () => {
        // The initial native fetch also supplies the existing marketing-mail
        // setting. Wait for it so the telemetry update preserves that choice.
        while (candidate.telemetry.loading) {
          await new Promise(resolve => window.setTimeout(resolve, 30));
          if (cancelled()) return;
          candidate = inspect(heading);
          if (!candidate || candidate.dialog !== dialog || candidate.owner.key !== owner.key) return;
        }
        if (cancelled()) return;
        if (!candidate.content.memoizedProps.userDataCollectionForceDisabled) {
          // Native initial fetch failures are swallowed as well. Read through
          // its existing settings service once so unknown account/mail values
          // are never written back as defaults. Only this modal's hook cache
          // and confirmed values are updated; no shared account object changes.
          const settings = await candidate.telemetry.service.getUserAccountSettings();
          if (cancelled() || !settings || typeof settings !== 'object' || Array.isArray(settings) ||
              ['telemetryEnabled', 'marketingEmailsEnabled', 'userDataCollectionForceDisabled'].some(name =>
                settings[name] !== undefined && typeof settings[name] !== 'boolean')) return;
          const known = { telemetryEnabled: settings.telemetryEnabled ?? false, marketingEmailsEnabled: settings.marketingEmailsEnabled ?? false,
            userDataCollectionForceDisabled: settings.userDataCollectionForceDisabled ?? false };
          if (known.userDataCollectionForceDisabled) return;
          Object.assign(candidate.telemetry.cache.current, known);
          candidate.telemetry.confirmed.current = known.telemetryEnabled;
          candidate.telemetry.confirmedMail.current = known.marketingEmailsEnabled;
          // Always save even when the checkbox is already false: it could be an
          // error fallback. The native commit counter distinguishes swallowed
          // failures from a successfully saved account preference.
          const before = candidate.telemetry.sent.current;
          const pending = candidate.telemetry.callback(selected === 'on');
          if (!pending || typeof pending.then !== 'function') return;
          const sent = candidate.telemetry.sent.current;
          if (sent !== before + 1) return;
          await pending;
          if (cancelled() || candidate.telemetry.sent.current !== sent || candidate.telemetry.committed.current !== sent) return;
          await new Promise(resolve => window.setTimeout(resolve, 0));
        }
        if (cancelled()) return;
        const current = inspect(heading);
        if (!current || current.dialog !== dialog || current.owner.key !== owner.key) return;
        const props = current.content.memoizedProps;
        if (!props.userDataCollectionForceDisabled && props.telemetryEnabled !== (selected === 'on')) return;
        // The native parent stores its account-scoped terms record and dismisses
        // this modal. No synthetic button clicks or arbitrary dialog callbacks.
        current.owner.memoizedProps.onAccept();
      })(), new Promise((resolve, reject) => {
        timeout = window.setTimeout(() => reject(new Error('native_data_usage_timeout')), 10000);
      })]);
    } catch { /* A changed contract or failed native operation leaves the modal available. */ }
    finally { expired = true; window.clearTimeout(timeout); restore(dialog); }
  }
  function check() {
    queued = false;
    if (stopped || preference === 'manual') return;
    for (const heading of document.querySelectorAll('h2')) {
      const candidate = inspect(heading);
      if (candidate) void processDialog(candidate);
    }
  }
  function queue() {
    if (queued || stopped || preference === 'manual') return;
    queued = true; Promise.resolve().then(check);
  }
  const observer = new window.MutationObserver(records => {
    if (records.some(record => {
      if (record.target.closest?.(excluded)) return false;
      return [...record.addedNodes].some(node => node.nodeType === 1 && !node.matches(excluded) &&
        (node.matches('h2,[role="dialog"]') || node.querySelector('h2,[role="dialog"]')));
    })) queue();
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });
  const controller = Object.freeze({
    setPreference(value) {
      if (stopped || !allowed(value)) return false;
      if (value !== preference) { preference = value; revision++; for (const dialog of hidden.keys()) restore(dialog); }
      queue(); return true;
    },
    stop() {
      if (stopped) return;
      stopped = true; revision++; observer.disconnect();
      for (const dialog of hidden.keys()) restore(dialog);
      if (window[key] === controller) delete window[key];
    },
  });
  window[key] = controller; check(); return controller;
}

export function dataUsageConsentSource(preference = 'off') {
  if (!['off', 'on', 'manual'].includes(preference)) throw new Error('invalid_data_usage_preference');
  return `(() => { if (window.top !== window || location.protocol !== 'https:' || location.hostname !== '127.0.0.1') return false; (${installDataUsageConsent.toString()})({document, window, preference:${JSON.stringify(preference)}}); return true; })()`;
}
