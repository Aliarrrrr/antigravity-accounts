'use strict';

function installedHostWindowAccess() {
  // In the installed addon this resolves the host's already loaded utility
  // module, including its private HubWindow/WebContentsView registrations.
  let filename;
  try { filename = require.resolve('../utils'); }
  catch (error) {
    if (error.code === 'MODULE_NOT_FOUND') return undefined;
    throw error;
  }
  return require(filename);
}

function getLocalHubViews({ electron, origin, hostWindowAccess = installedHostWindowAccess() }) {
  const windows = typeof hostWindowAccess?.getLocalHubWindows === 'function'
    ? hostWindowAccess.getLocalHubWindows()
    : typeof hostWindowAccess?.getHubWindows === 'function'
      ? hostWindowAccess.getHubWindows()
      : electron?.BrowserWindow?.getAllWindows?.() || [];
  const session = electron?.session?.defaultSession;
  const seen = new Set(), views = [];
  for (const window of windows) {
    try {
      if (!window || window.isDestroyed?.()) continue;
      const contents = typeof hostWindowAccess?.getHubWebContents === 'function'
        ? hostWindowAccess.getHubWebContents(window) : window.webContents;
      if (!contents || seen.has(contents) || contents.isDestroyed?.() || contents.getType?.() === 'webview' ||
          session && contents.session !== session || contents.mainFrame?.parent || new URL(contents.getURL()).origin !== origin) continue;
      if (typeof hostWindowAccess?.isLocalHubTopFrame === 'function' &&
          !hostWindowAccess.isLocalHubTopFrame({ sender: contents, senderFrame: contents.mainFrame })) continue;
      seen.add(contents);
      views.push({ window, contents });
    } catch { /* A window which closed or navigated cannot receive account data. */ }
  }
  return views;
}

module.exports = { getLocalHubViews };
