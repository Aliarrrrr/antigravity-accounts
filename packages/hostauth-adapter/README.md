# Antigravity 2.29 / 2.28.2 HostAuth adapter

Version-bound Electron adaptation for the Accounts plugin. The original LS
contacts Google directly; original Google Authorization headers and official
saved credentials never pass through the plugin. The HostBridge uses a fresh
local capability and supplies only independently added accounts.

`patch-plan.mjs` accepts complete inspected 2.29 / 2.28.2 source contracts and RPC
dependencies, including a changed package version with unchanged contract files.
It preserves async shell-environment prefetch and Webview PAC initialization.
`native-diagnostics-patch.mjs` accepts the two inspected exact LS
image and changes four instruction regions to register only
`/debug/pprof/goroutine`. It does not enable dev mode or other pprof routes.
Unknown source/native changes are rejected before installation. Frontend bindings
are extracted by action/schema/Redux contracts instead of fixed compiler names.

`native-project-refresh-patch.mjs` changes the existing getCAICProject cache call
to its ABI-compatible native forceRefresh implementation. The combined provider
copy is generated under `hostauth-addon/native/` and used only with independent
HostAuth. Registration keeps the original LS path. Native assets and Node wrapper
paths remain rooted in the official resources directory.

## Preparing, installing and restoring

```text
node scripts/package-copy.mjs --source-asar <installed-resources/app.asar> --output <new-directory> --component <published-accounts/Antigravity.Accounts.exe> --ui <account-plugin-directory> --language-server <installed-language-server.exe>
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
Daily account selection stays in that selector. Its single management entry opens
a themed dialog in the same window for Google authorization, labels, reauthorization,
primary-account configuration, removal and data-use preferences.
The addon has no separate BrowserWindow, HTML page, standalone theme or exposed
renderer credential API. Startup-only credential recovery uses native dialogs.

After an account change, data-use consent defaults to saving collection as unchecked
and confirming only after the native save succeeds. The same panel also offers
checked-and-confirm or manual handling. First-use terms, changed terms types,
unrecognized dialogs and failed reads or saves retain the native prompt. Preview.10
resolves the committed React fiber through parent child/sibling membership when
return pointers are shared; this avoids reading the stale checkbox state after saving.

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

Preview.8 on installed Antigravity 2.29.0 completed a real metadata-only
Account A -> Account B -> Account A round trip while retaining the same App,
language-service processes, window, workspace and available concrete model.
No model requests were initiated. This does not verify generation, quota charging,
cross-account generation continuation or an actual NSIS upgrade. See the
[version-specific validation record](https://github.com/Aliarrrrr/antigravity-accounts/blob/master/docs/ACCOUNTS_VALIDATION.md).
Preview.10 on installed 2.29.0 was observed to uncheck the data-use option and
close the prompt automatically while switching back to the original account. The
original account/model were retained and another normal restart loaded successfully.
No model request was sent. This does not establish real-account switching on 2.28.2.
Other native images remain unsupported.

`npm ci --ignore-scripts --no-audit --no-fund` installs locked test dependencies;
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
