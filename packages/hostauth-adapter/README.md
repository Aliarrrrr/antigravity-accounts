# Antigravity 2.28.2 HostAuth adapter

Version-bound Electron adaptation for the Accounts plugin. The original LS
contacts Google directly; original Google Authorization headers and official
saved credentials never pass through the plugin. The HostBridge uses a fresh
local capability and supplies only independently added accounts.

`patch-plan.mjs` accepts four exact inspected 2.28.2 source digests and preserves
the new Webview PAC initialization. `native-diagnostics-patch.mjs` accepts one exact LS
image and changes four instruction regions to register only
`/debug/pprof/goroutine`. It does not enable dev mode or other pprof routes.
Source drift is rejected before installation.

`native-project-refresh-patch.mjs` changes the existing getCAICProject cache call
to its ABI-compatible native forceRefresh implementation. The combined provider
copy is generated under `hostauth-addon/native/` and used only with independent
HostAuth. Registration keeps the original LS path. Native assets and Node wrapper
paths remain rooted in the official resources directory.

## Preparing, installing and restoring

```text
node scripts/package-copy.mjs --source-asar <installed-resources/app.asar> --output <new-directory> --component <published-accounts/Accounts.exe> --ui <account-plugin-directory> --language-server <installed-language-server.exe>
```

`buildCopy` creates a local adapted archive, native program, component and their
original program copies. These official binaries are never part of the release
ZIP. `applyPrepared` checks both current original files and creates a separate
recovery directory before replacing them; a failed second replacement restores
the first. `restorePrepared` checks both installed files before overwriting
either and rejects later updater modifications.

The main `account-plugin/manage-install.mjs` owns the installation receipt.
`assertApplicationStopped({installRoot})` blocks live desktop, LS, updater and
account component processes. Only its own Electron Node helper and an external
sidecar whose wrapper, parent process, import and entrypoint all match the known
Node launcher are exempt. Only relevant process command lines and public code
are read for that decision; no credential store is inspected.

## Runtime behavior

Before an independent provider has been activated, an unconfigured plugin registers
its model-menu account groups and continues ordinary native startup without
`--use_host_auth`. It does not request a token or open an authorization dialog.
The `--accounts` App argument opens the native model selector after startup.
Daily account selection and Google authorization live inside that selector.
The addon has no separate BrowserWindow, HTML page, stylesheet or exposed
renderer credential API. Startup-only credential recovery uses native dialogs.

When an independently configured primary is activated, startup verifies that
account before supplying `--use_host_auth=true` and a live bridge. A separate
local boolean marker prevents a later component failure from silently selecting
the original official identity. The marker contains no credentials.

The switch coordinator holds new frontend submissions, waits for native work,
drains token callbacks, commits the broker transaction and confirms the native
identity/project. Native read subscriptions inspect live pending counts without
decoding task content. RunCommand execution is also observed independently of
AgentState. Remote control and sleeping schedules prevent a switch while active.

The pinned native frontend decoder and Redux action also update the account's
model list. A concrete model is preserved across native default overrides when
it remains available. Otherwise submissions stay paused until an actual native
modelPicked action is observed. Unknown frontend contracts, custom models and
ambiguous aliases stay paused. The 2.28.2 bindings have been checked with extracted
native definitions and synthetic account/model state in an offline browser.

The exact provider image enables the local switch mechanism. Earlier program-copy
runs covered invented-account A->B changes, a delayed metadata request and
cancelling a wait while an owned command child continued. Preview.5 on 2.28.1 also
completed two real metadata-only account changes. These are version-specific
results, not a completed 2.28.2 account-switch test.

Preview.7 has been installed on 2.28.2. Its native menu showed three account groups,
scrollable native model options and View Usage. The upgrade restored the original
project, conversation, model and tray preference. No account switch or model
request was initiated during this check. New Google sign-in, expiry renewal,
quota charging and conversation generation have not been verified for this version.
Other native images remain unsupported.

`npm test` runs local transport, program-copy, protocol and simulated task tests.
Program-copy checks require the reviewed unmodified official files; an already
adapted or localized installation is not a valid input. Set
`ANTIGRAVITY_PROGRAM_ASAR`, `ANTIGRAVITY_LANGUAGE_SERVER`, `ACCOUNTS_TEST_INSTALL`
and `ACCOUNTS_ELECTRON_EXECUTABLE` to the matching original-program copy, as
described in the release's top-level `VALIDATION.md` (repository:
`docs/ACCOUNTS_VALIDATION.md`). Missing program inputs can cause skips, which do
not count as passing. Earlier separate native diagnostics runs used invented data
and loopback endpoints; they do not establish current Google quota behavior.
Program-copy manifests retain `runtimeVerified:false`.
