import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { getLocalHubViews } = require('../src/local-hub-windows.cjs');

const origin = 'https://127.0.0.1:12345';

function hub(session, url = origin + '/') {
  const contents = { session, getURL: () => url, isDestroyed: () => false, mainFrame: { parent: null } };
  return { window: { isDestroyed: () => false }, contents };
}

test('official Hub access resolves BaseWindow views without visiting shell contents or child views', () => {
  const session = {}, one = hub(session), two = hub(session), ssh = hub({});
  const views = [one, two, ssh];
  const hostWindowAccess = {
    getLocalHubWindows: () => [one.window, two.window, one.window],
    getHubWebContents: window => views.find(view => view.window === window).contents,
    isLocalHubTopFrame: event => views.some(view => view.contents === event.sender && view.contents.mainFrame === event.senderFrame),
  };
  Object.defineProperty(one.window, 'webContents', { get() { assert.fail('Do not read the base shell'); } });
  Object.defineProperty(two.window, 'contentView', { get() { assert.fail('Do not scan arbitrary child views'); } });
  const electron = { BrowserWindow: { getAllWindows() { assert.fail('The host owns the Hub window registry'); } }, session: { defaultSession: session } };
  assert.deepEqual(getLocalHubViews({ electron, origin, hostWindowAccess }), [one, two]);
});

test('destroyed, remote-session, webview, nested-frame and foreign-origin contents cannot receive account state', () => {
  const session = {}, alive = hub(session), destroyedWindow = hub(session), destroyedContents = hub(session),
    remote = hub({}), webview = hub(session), iframe = hub(session), foreign = hub(session, 'https://example.invalid/'),
    wrongPort = hub(session, 'https://127.0.0.1:23456/'), wrongHost = hub(session, 'https://localhost:12345/'), unregistered = hub(session);
  destroyedWindow.window.isDestroyed = () => true;
  destroyedContents.contents.isDestroyed = () => true;
  webview.contents.getType = () => 'webview';
  iframe.contents.mainFrame.parent = {};
  const views = [alive, destroyedWindow, destroyedContents, remote, webview, iframe, foreign, wrongPort, wrongHost, unregistered];
  const hostWindowAccess = {
    getLocalHubWindows: () => views.map(view => view.window),
    getHubWebContents: window => views.find(view => view.window === window).contents,
    isLocalHubTopFrame: event => event.sender !== unregistered.contents,
  };
  assert.deepEqual(getLocalHubViews({ electron: { session: { defaultSession: session } }, origin, hostWindowAccess }), [alive]);
});

test('previous hosts retain registered Hub getters and the older direct BrowserWindow path', () => {
  const session = {}, view = hub(session);
  const electron = { session: { defaultSession: session }, BrowserWindow: { getAllWindows: () => [view.window] } };
  assert.deepEqual(getLocalHubViews({ electron, origin, hostWindowAccess: {
    getHubWindows: () => [view.window], getHubWebContents: () => view.contents,
  } }), [view]);
  view.window.webContents = view.contents;
  assert.deepEqual(getLocalHubViews({ electron, origin, hostWindowAccess: { getHubWindows: () => [view.window] } }), [view]);
  assert.deepEqual(getLocalHubViews({ electron, origin, hostWindowAccess: null }), [view]);
});
