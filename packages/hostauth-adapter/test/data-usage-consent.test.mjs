import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { installDataUsageConsent, dataUsageConsentSource } from '../src/data-usage-consent.mjs';
import { extractNativeFrontend } from '../src/frontend-bindings.mjs';

const { JSDOM } = createRequire(import.meta.url)('jsdom');
const settle = () => new Promise(resolve => setTimeout(resolve, 25));

function harness({ preference = 'off', initial = true, forceDisabled = false, reason = 'account_changed', mode = 'success', deferred, readFails = false, modern = false, termsFlow = 'backstop' } = {}) {
  const dom = new JSDOM(modern
    ? '<div role="dialog"><h2>One last thing before getting started…</h2><input type="checkbox"><button>Continue</button></div>'
    : '<div role="dialog"><h2>Security Notice &amp; Data Use</h2><input type="checkbox"><button data-autofocus="true">Accept and continue</button></div>',
    { url: 'https://127.0.0.1:43123/', pretendToBeVisual: true });
  const { window } = dom, { document } = window;
  const dialog = document.querySelector('[role="dialog"]'), heading = document.querySelector('h2');
  let accepts = 0;
  const calls = [], reads = [], refs = [initial, false, 0, 0, 0, 0, 0].map(current => ({ current }));
  function NativeContent({ onTelemetryChange }) {
    return ['Security Notice & Data Use', 'Terms of Service & Data Use', 'by allowing Google to collect and use my Interactions data', onTelemetryChange];
  }
  function NativeModal() {
    const marker = { defaultTelemetryOnError:!0 };
    return [marker, 'POST_ONBOARDING_TERMS_SHOWN', 'POST_ONBOARDING_TERMS_ACCEPTED', 'Accept and continue'];
  }
  function ModernContent({ onTelemetryChange }) {
    return ['One last thing before getting started', 'Terms of Service & Data Use', 'Allow Google to collect and use your Interactions data', onTelemetryChange];
  }
  function ModernModal() {
    const marker = { defaultTelemetryOnError:!0, terms_flow:'backstop' };
    return [marker, 'POST_ONBOARDING_TERMS_SHOWN', 'POST_ONBOARDING_TERMS_ACCEPTED', "Continue"];
  }
  const props = { embedded: true, telemetryEnabled: initial, onTelemetryChange() {},
    userDataCollectionForceDisabled: forceDisabled, gcpTos: false };
  const owner = { type: modern ? ModernModal : NativeModal, key: 'synthetic@example.invalid:false', memoizedProps: {
    isGcpTos: false, reason, onAccept() { accepts++; dialog.remove(); },
  } };
  if (modern) owner.memoizedProps.termsFlow = termsFlow;
  const content = { type: modern ? ModernContent : NativeContent, memoizedProps: props, return: owner };
  heading.__reactFiber$synthetic = { memoizedProps: {}, return: content };
  const service = {
    getUserAccountSettings() { reads.push(true); if (readFails) throw Error('synthetic read failure'); return { telemetryEnabled: initial, marketingEmailsEnabled: false, userDataCollectionForceDisabled: false }; },
    async setUserAccountSettings(value) { calls.push(value); if (deferred) await deferred; if (mode === 'reject') throw Error('synthetic failure'); return value; },
  };
  const Ng = { USER_SETTINGS_UPDATE_FAILED: 'failed' };
  const marketing = false;
  async function fetchSettings() { return service.getUserAccountSettings(); }
  async function updateTelemetry(value) {
    const sent = ++refs[5].current;
    props.telemetryEnabled = value;
    try {
      const result = await service.setUserAccountSettings({ telemetryEnabled: value, marketingEmailsEnabled: marketing });
      if (mode !== 'swallow') { refs[2].current = sent; refs[0].current = result.telemetryEnabled; }
      else props.telemetryEnabled = initial;
      const userStatus = { disableTelemetry: !result.telemetryEnabled };
      return userStatus;
    } catch { props.telemetryEnabled = initial; return Ng.USER_SETTINGS_UPDATE_FAILED; }
  }
  const stateHooks = [initial, false, forceDisabled, false].map(memoizedState => ({ memoizedState, queue: { dispatch() {} } }));
  const hooks = [...stateHooks, { memoizedState: { current: { telemetryEnabled: initial, marketingEmailsEnabled: false, userDataCollectionForceDisabled: forceDisabled } } },
    ...refs.map(memoizedState => ({ memoizedState })),
    { memoizedState: [fetchSettings, [service]] }, { memoizedState: [updateTelemetry, []] }];
  hooks.forEach((hook, index) => { hook.next = hooks[index + 1]; }); owner.memoizedState = hooks[0];
  let controller;
  return { dom, window, document, dialog, heading, props, owner, content, refs, stateHooks, calls, reads,
    accepts: () => accepts,
    start() { controller = installDataUsageConsent({ document, window, preference }); return controller; },
    close() { controller?.stop(); window.close(); },
  };
}

test('switch data-use consent defaults to off and confirms only after the native setting is saved', async () => {
  let release; const deferred = new Promise(resolve => { release = resolve; });
  const h = harness({ deferred });
  try {
    h.start();
    assert.equal(h.dialog.style.visibility, 'hidden'); assert.equal(h.accepts(), 0);
    await new Promise(setImmediate);
    assert.deepEqual(h.calls, [{ telemetryEnabled: false, marketingEmailsEnabled: false }]);
    release(); await settle();
    assert.equal(h.accepts(), 1); assert.equal(h.dialog.isConnected, false);
  } finally { release(); h.close(); }
});

test('failed account-settings reads preserve the popup without saving defaults', async () => {
  const h = harness({ initial: false, readFails: true });
  try {
    h.start(); await settle();
    assert.equal(h.reads.length, 1); assert.equal(h.accepts(), 0); assert.deepEqual(h.calls, []);
    assert.equal(h.dialog.style.visibility, '');
  } finally { h.close(); }
});

test('on uses the native account preference while manual leaves the popup unchanged', async () => {
  for (const preference of ['on', 'manual']) {
    const h = harness({ preference, initial: false });
    try {
      h.start(); await settle();
      assert.equal(h.accepts(), preference === 'on' ? 1 : 0);
      assert.deepEqual(h.calls, preference === 'on' ? [{ telemetryEnabled: true, marketingEmailsEnabled: false }] : []);
      assert.equal(h.dialog.style.visibility, '');
    } finally { h.close(); }
  }
});

test('2.31 Continue dialogs preserve preference, account-change reason and recognized terms flow', async () => {
  for (const options of [{}, { termsFlow: 'post_onboarding', preference: 'on' }, { preference: 'manual' },
    { reason: 'no_prior_acceptance' }, { reason: 'tos_type_changed' }, { termsFlow: 'changed_contract' }, { mode: 'reject' }]) {
    const h = harness({ modern: true, ...options });
    try {
      h.start(); await settle();
      const accepted = !options.reason && !options.mode && options.termsFlow !== 'changed_contract' && options.preference !== 'manual';
      assert.equal(h.accepts(), accepted ? 1 : 0);
      assert.equal(h.dialog.style.visibility, '');
      if (accepted) assert.equal(h.calls[0].telemetryEnabled, options.preference === 'on');
      if (options.reason || options.termsFlow === 'changed_contract' || options.preference === 'manual') assert.deepEqual(h.calls, []);
    } finally { h.close(); }
  }
});

test('an unchecked fallback and swallowed native errors cannot count as a saved preference', async () => {
  for (const mode of ['reject', 'swallow']) {
    const h = harness({ mode, initial: false });
    try {
      h.start(); await settle();
      assert.equal(h.calls.length, 1); assert.equal(h.accepts(), 0);
      assert.equal(h.dialog.isConnected, true); assert.equal(h.dialog.style.visibility, '');
      const unrelated = h.document.createElement('div'); unrelated.textContent = 'ordinary update'; h.document.body.append(unrelated);
      await settle(); assert.equal(h.calls.length, 1, 'A failed operation is not repeated on unrelated mutations');
    } finally { h.close(); }
  }
});

test('enterprise-disabled collection remains disabled and no settings request is made', async () => {
  const h = harness({ preference: 'on', forceDisabled: true, initial: false });
  try { h.start(); await settle(); assert.equal(h.accepts(), 1); assert.deepEqual(h.calls, []); }
  finally { h.close(); }
});

test('first use, changed terms, user content and unrecognized native components retain their popup', async () => {
  for (const change of [h => { h.owner.memoizedProps.reason = 'no_prior_acceptance'; },
    h => { h.owner.memoizedProps.reason = 'tos_type_changed'; },
    h => { h.dialog.setAttribute('data-user-content', 'true'); },
    h => { h.content.type = () => 'another component'; },
    h => { h.owner.memoizedState.next = null; }]) {
    const h = harness();
    try { change(h); h.start(); await settle(); assert.equal(h.accepts(), 0); assert.deepEqual(h.calls, []); assert.equal(h.dialog.style.visibility, ''); }
    finally { h.close(); }
  }
});

test('switching to manual or stopping during a native request restores the popup and prevents acceptance', async () => {
  for (const stop of [false, true]) {
    let release; const deferred = new Promise(resolve => { release = resolve; });
    const h = harness({ deferred });
    try {
      const controller = h.start();
      if (stop) controller.stop(); else controller.setPreference('manual');
      assert.equal(h.dialog.style.visibility, ''); release(); await settle();
      assert.equal(h.accepts(), 0); assert.equal(h.dialog.isConnected, true);
    } finally { release(); h.close(); }
  }
});

test('pending native fetches and updates have a ten-second limit and late success cannot accept', async () => {
  for (const fetching of [false, true]) {
    let release; const deferred = new Promise(resolve => { release = resolve; });
    const h = harness({ deferred });
    try {
      h.stateHooks[3].memoizedState = fetching;
      const original = h.window.setTimeout.bind(h.window);
      h.window.setTimeout = (callback, milliseconds, ...args) => original(callback, milliseconds === 10000 ? 2 : milliseconds, ...args);
      h.start(); await settle();
      assert.equal(h.dialog.style.visibility, ''); assert.equal(h.accepts(), 0);
      if (fetching) assert.deepEqual(h.calls, []);
      h.stateHooks[3].memoizedState = false; release(); await settle();
      assert.equal(h.accepts(), 0);
    } finally { release(); h.close(); }
  }
});

test('a preference change reuses the observer and newly rendered switch dialogs are handled', async () => {
  const h = harness({ preference: 'manual' });
  try {
    const controller = h.start(); assert.equal(controller.setPreference('unknown'), false);
    assert.equal(installDataUsageConsent({ document: h.document, window: h.window, preference: 'off' }), controller);
    await settle(); assert.equal(h.accepts(), 1);
    h.document.body.append(h.dialog); await settle();
    assert.equal(h.accepts(), 1, 'The same completed native component cannot be accepted twice');
  } finally { h.close(); }
});

test('a committed React alternate supplies the current native checkbox state', async () => {
  const h = harness();
  try {
    const old = { memoizedProps: {}, return: undefined };
    const committedRoot = { stateNode: undefined }; committedRoot.stateNode = { current: committedRoot };
    h.owner.return = committedRoot;
    old.alternate = h.heading.__reactFiber$synthetic;
    h.heading.__reactFiber$synthetic = old;
    h.start(); await settle(); assert.equal(h.accepts(), 1);
  } finally { h.close(); }
});

test('shared React parent return pointers cannot hide the committed unchecked state after saving', async () => {
  const h = harness();
  try {
    const staleHost = h.heading.__reactFiber$synthetic;
    const currentHost = { memoizedProps: {}, return: h.content, alternate: staleHost };
    const staleOwner = { ...h.owner }, staleContent = { ...h.content, memoizedProps: { ...h.props }, return: staleOwner };
    staleHost.return = staleContent; staleHost.alternate = currentHost;
    staleContent.alternate = h.content; h.content.alternate = staleContent;
    staleOwner.alternate = h.owner; h.owner.alternate = staleOwner;
    staleContent.child = staleHost; h.content.child = currentHost;
    staleOwner.child = staleContent; h.owner.child = h.content;
    const root = {}, previousRoot = {}, sharedParent = {}, previousParent = {};
    root.stateNode = previousRoot.stateNode = { current: root };
    root.alternate = previousRoot; previousRoot.alternate = root;
    root.child = sharedParent; previousRoot.child = previousParent;
    sharedParent.return = root; previousParent.return = previousRoot;
    sharedParent.alternate = previousParent; previousParent.alternate = sharedParent;
    sharedParent.child = h.owner; previousParent.child = staleOwner;
    // React's bailout has reused the parent return pointer for both branches.
    staleOwner.return = h.owner.return = sharedParent;
    h.start(); await settle();
    assert.equal(h.calls.length, 1);
    assert.equal(staleContent.memoizedProps.telemetryEnabled, true);
    assert.equal(h.props.telemetryEnabled, false);
    assert.equal(h.refs[2].current, 1, 'The native settings save committed successfully');
    assert.equal(h.accepts(), 1, 'Acceptance reads the child branch committed by React');
  } finally { h.close(); }
});

test('serialized installer validates policy and only runs in the native loopback main frame', () => {
  for (const preference of ['off', 'on', 'manual']) new vm.Script(dataUsageConsentSource(preference));
  assert.throws(() => dataUsageConsentSource('unknown'), /invalid_data_usage_preference/);
  const source = dataUsageConsentSource('off');
  assert.equal(vm.runInNewContext(source, { window: { top: {} }, location: { protocol: 'https:', hostname: '127.0.0.1' } }), false);
  const window = {}; window.top = window;
  assert.equal(vm.runInNewContext(source, { window, location: { protocol: 'https:', hostname: 'foreign.invalid' } }), false);
});

test('the reviewed native frontend retains the exact modal and settings-hook contracts', {
  skip: !process.env.ANTIGRAVITY_LANGUAGE_SERVER,
}, async () => {
  const source = extractNativeFrontend(await readFile(process.env.ANTIGRAVITY_LANGUAGE_SERVER));
  assert.match(source, /POST_ONBOARDING_TERMS_SHOWN/); assert.match(source, /POST_ONBOARDING_TERMS_ACCEPTED/);
  assert(source.match(/reason:[\w$]+,(?:termsFlow:[\w$]+,)?onAccept:[\w$]+/), 'The native account-change modal retains bounded reason and acceptance props');
  assert(source.includes('defaultTelemetryOnError:!0'));
  const hooks = [...source.matchAll(/(?:[$\w]+)=\(0,[$\w]+\.useRef\)\(0\),(?:[$\w]+)=\(0,[$\w]+\.useRef\)\(0\),(?:[$\w]+)=\(0,[$\w]+\.useRef\)\(0\),(?:[$\w]+)=\(0,[$\w]+\.useRef\)\(0\),(?:[$\w]+)=\(0,[$\w]+\.useRef\)\(0\),(?:[$\w]+)=\(0,[$\w]+\.useCallback\)\(async\(\)=>/g)];
  assert.equal(hooks.filter(match => source.slice(match.index, match.index + 2200).includes('.setUserAccountSettings(')).length, 1);
});

test('actual native settings hooks save off while preserving mail, and native failures keep the dialog', {
  skip: !process.env.ANTIGRAVITY_LANGUAGE_SERVER,
}, async () => {
  const source = extractNativeFrontend(await readFile(process.env.ANTIGRAVITY_LANGUAGE_SERVER));
  const expression = (pattern, boundaries) => {
    const matches = [...source.matchAll(pattern)]; assert.equal(matches.length, 1);
    const start = matches[0].index;
    const end = Math.min(...boundaries.map(boundary => source.indexOf(boundary, start)).filter(index => index > start));
    assert(end > start); return source.slice(source.indexOf('=', start) + 1, end + 1);
  };
  const hookSource = expression(/var ([$\w]+)=\(a=\{\}\)=>\{var \{autoFetch:/g, ['};var ']);
  const contentSource = expression(/var ([$\w]+)=\(\{productName:a,telemetryEnabled:b,onTelemetryChange:c/g, ['};var ', '};\nconst ']);
  const modalSource = expression(/const ([$\w]+)=\(\{isGcpTos:a,reason:b,(?:onAccept:c|termsFlow:c,onAccept:d)\}\)=>/g, ['};const ']);
  for (const failure of [false, true]) {
    const h = harness({ modern: modalSource.includes('termsFlow:') });
    try {
      const hooks = [], effects = [], requests = [], actions = [];
      const service = {
        async getUserAccountSettings() { return { telemetryEnabled: true, marketingEmailsEnabled: true, userDataCollectionForceDisabled: false }; },
        async setUserAccountSettings(settings) { requests.push(settings); if (failure) throw Error('synthetic native save failure'); return settings; },
      };
      const add = hook => { if (hooks.length) hooks.at(-1).next = hook; hooks.push(hook); return hook; };
      let states = 0;
      const React = {
        useState(value) {
          const index = states++, hook = add({ memoizedState: value, queue: { dispatch: undefined } });
          const set = value => { hook.memoizedState = value; if (index === 0) h.props.telemetryEnabled = value; if (index === 2) h.props.userDataCollectionForceDisabled = value; };
          hook.queue.dispatch = set; return [value, set];
        },
        useRef(current) { return add({ memoizedState: { current } }).memoizedState; },
        useCallback(fn, deps) { add({ memoizedState: [fn, deps] }); return fn; },
        useEffect(fn) { add({ memoizedState: { effect: fn } }); effects.push(fn); },
      };
      const identity = value => value;
      const construct = (schema, value) => ({ telemetryEnabled: false, marketingEmailsEnabled: false, userDataCollectionForceDisabled: false, ...value });
      const getSettings = () => ({ get: () => ({}) }), getDispatch = () => value => actions.push(value), getErrorHandler = () => () => {};
      const getStatus = () => ({ email: 'synthetic@example.invalid' });
      const context = { A: React, e: identity,
        dm: () => service, Ql: getStatus, mu: {}, Pl: getDispatch, $b: construct, Oh: {}, tB: getErrorHandler,
        hk: getSettings, D: { UserStatusSchema: {} }, lu: identity, mqb() {}, Ng: {},
        Al: () => service, ll: getStatus, gt: {}, kl: getDispatch, Vb: construct, sh: {}, Qv: getErrorHandler,
        Fj: getSettings, B: { UserStatusSchema: {} }, ft: identity, Kdb() {}, xg: {},
        Bk: () => service, ok: getStatus, Yq: {}, nk: getDispatch, Wb: construct, fha: {}, St: getErrorHandler,
        gj: getSettings, E: { UserStatusSchema: {} }, Xq: identity, WDb() {}, rg: {},
      };
      if (modalSource.includes('termsFlow:')) {
        context.B = React;
      }
      const nativeHook = vm.runInNewContext(`(${hookSource})`, context);
      nativeHook({ defaultTelemetryOnError: true }); effects.forEach(fn => fn());
      await settle();
      h.content.type = vm.runInNewContext(`(${contentSource})`);
      h.owner.type = vm.runInNewContext(`(${modalSource})`);
      h.owner.memoizedState = hooks[0];
      h.start(); await settle();
      assert.equal(requests.length, 1);
      assert.equal(requests[0].telemetryEnabled, false); assert.equal(requests[0].marketingEmailsEnabled, true);
      assert.equal(h.accepts(), failure ? 0 : 1);
      assert.equal(h.dialog.style.visibility, '');
      if (!failure) assert.equal(actions.at(-1).disableTelemetry, true);
    } finally { h.close(); }
  }
});
