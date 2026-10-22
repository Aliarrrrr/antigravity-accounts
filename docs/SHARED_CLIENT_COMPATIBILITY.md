# Shared client compatibility: Antigravity 2.24.0

The account-switch adapter remains **unsupported**. These findings come from the installed 2.24.0 program files, inspected on 2026-10-20 and rechecked on 2026-10-22. The static script opens no Profile, credential, conversation or project file, starts no client or language service, and sends no request. Separate working-data copy checks are described below. Declarations and decoded instructions do not establish successful account switching.

Hub keeps user-entered labels, shared groups and working content. It does not read, copy, encrypt or save account credentials. The official client's existing login storage can remain in place, but selecting it without browser confirmation has not been established.

## Reproduce the static report

Run from the repository root with Python 3:

```powershell
python -X utf8 scripts/Inspect-SharedClient.py --install-dir "$env:LOCALAPPDATA\Programs\antigravity"
python -X utf8 scripts/Inspect-SharedClient.py --self-test
```

The script reads `resources/app.asar` and `resources/bin/language_server.exe` in memory and emits JSON. It has no endpoint or credential-file argument. Basic inspection uses the standard library; optional, already-installed Capstone enables bounded x64 instruction decoding. Without it, instruction-derived findings remain unavailable rather than being inferred from strings. No package is installed by the script. The current inspection used Capstone 6.0.0. `--self-test` uses in-memory report inputs without reading installed files. `runtimeValidated=false` and `adapterSupported=false` remain false even when extraction succeeds. Incomplete descriptor extraction exits with code 1.

The current installation contains complete `language_server.proto`, `cortex.proto`, `project.proto`, `jetski_cortex.proto` and `codeium_common.proto` descriptors. The report gives their binary offsets and the selected message fields, numbers, ProtoJSON names, enum values and RPC paths. It also distinguishes a declaration from an actual `language_server.(*Server)` symbol. ProtoJSON field names do not prove that an HTTP JSON transport is accepted.

## Authentication and identity

All paths below have the prefix `/exa.language_server_pb.LanguageServerService/`. Requests and responses use the field names recorded in the installed descriptors.

| Method | Request | Relevant response |
| --- | --- | --- |
| `GetAuthStatus` | Empty | `authResult.hasValidAuth`, `authMode`, login/eligibility errors; no account ID or email |
| `HasAuthToken` | Empty | `hasToken`, `isGcpTos`, `projectId`, `location`, `wifProvider` |
| `Login` | `isGcpTos`, `additionalScopes[]`, `enableBusinessLogin`, `wifLoginInfo` | `authResult` |
| `LoginWithBrowser` | `isGcpTos`, `additionalScopes[]` | `authResult` |
| `AuthLogout` | Empty | Empty |

Identity-related request schemas are inspectable with `--method`, but the product does not call them or persist returned personal fields. Method declarations alone do not confirm which account subsequent work would use.

There is no `SwitchAccount`, `SetAuthToken`, `ReloadAuth` or `ListAccounts` method in the decoded LanguageServerService. The `SetAuthToken` strings found earlier include generated field setters in other services and unrelated clients; they are not a running-session account replacement API.

The binary contains `fileTokenStorage`, `KeyringTokenStorage`, `compositeTokenStorage` and `StoredToken` implementations. The report keeps byte-level relative-call candidates separate from Capstone-decoded instruction findings.

- `codeassistclient.NewFileTokenStorage` calls `os.UserHomeDir`, builds three path components, and calls `filepath.join`. The decoded components are the returned home, `.gemini` and `jetski-standalone-oauth-token` (`0x141e43432` through `0x141e434f3` in this installation). This constructor does not take `gemini_dir` or `config_dir`; its file path is separate from those work/configuration overrides. No token file was opened by the inspection.
- `KeyringTokenStorage.LoadStoredToken` supplies the literal service `gemini` and the receiver's account string to its keyring interface. `authclient.NewAuthClient` stores the literal account string `antigravity` before passing the storage object to `NewCompositeTokenStorage` (`0x142629b24`–`0x142629be6`). These instructions identify the constructed storage path, not the selected backend at runtime.
- The Windows keyring implementation concatenates service, `:` and account before calling `wincred.GetGenericCredential` (`0x14116d7ea`–`0x14116d80c`). The above path therefore uses `gemini:antigravity`, rather than a key derived from HOME or Hub Profile ID. Changing HOME cannot establish a separate Windows credential namespace for this path.
- `NewCompositeTokenStorage` has branches for caller-requested file storage, `shouldBypassKeyring` and a recent keyring timeout. The report extracts the associated log-format arguments and direct calls. The caller options, detection results, fallback behavior during reads/refresh, and which backend the existing official session would select remain unconfirmed.

No credential replacement is implemented or planned as a workaround. The complete StoredToken representation and refresh behavior were not inspected. Login/logout declarations do not establish a browser-free switch or preservation of active work.

## Directory parameters and Electron startup

Capstone resolves three calls from `main.init.0` to `google3/base/go/flag.(*FlagSet).declareFlag`, including literal name and help arguments. These are actual compiled registrations, not matches for isolated strings:

| Flag | Registration call | Registered help meaning |
| --- | --- | --- |
| `app_data_dir` | `0x142c1518f` | Application-data path relative to GeminiDir |
| `gemini_dir` | `0x142c151f4` | Gemini files path; absolute paths are used directly, relative paths resolve against HomeDir |
| `config_dir` | `0x142c15259` | User-configuration path relative to GeminiDir |

The report records the exact help text and call addresses. This establishes registration and the stated path meanings, not complete runtime path use, every dependent file, account separation or safe concurrent sharing.

In installed `app.asar!dist/languageServer.js:200-261`, `startLanguageServer` accepts only `headless`, `hostBridgeUrl`, `hostBridgeToken` and `wsl` options. Its fixed arguments include `--standalone`, `--app_data_dir` (the app name `antigravity`), port/transport settings and official service endpoints. It does not pass `--gemini_dir` or `--config_dir` and does not forward arbitrary extra arguments.

The child environment is `{ ...process.env, ...shellEnvSync() }` (line 246). The bundled `shell-env` returns `process.env` on Windows. There is no existing Profile/HOME override option or Profile-switch IPC channel. Crash restart reuses the original options (`dist/languageServer.js:358-409`). The WSL switch relaunches and quits the whole app (`dist/menu.js:163-176`).

The native window can survive a backend restart: `dist/main.js:291-300` reloads each existing BrowserWindow at `https://127.0.0.1:<new-port>/`. This reloads the page; it does not show recovery of the current conversation, draft or view state. The `DEV_URL` startup branch skips language-service startup and creates a window for that URL, but supplies no runtime Profile-switch interface.

`dist/paths.js:23-27` derives the main-process work directory and configuration file from its own home (`~/.gemini/antigravity` and `~/.gemini/config/config.json`). A future child-only environment change would not automatically change those parent-process paths. `dist/languageServer.js:323-326` opens an `ANTIGRAVITY_OPEN_URL` request in the external browser; the host has no existing policy that guarantees silent failure instead of login UI.

## Tasks, project references and navigation

| Method | Request | Relevant response |
| --- | --- | --- |
| `GetAllCascadeTrajectories` | `excludeSubtrajectories` | `trajectorySummaries` map keyed by string |
| `GetCascadeTrajectory` | `cascadeId`, verbosity fields, `disableRehydration` | `trajectory`, `status`, step/metadata counts |
| `WaitForConversationFullyIdle` | `conversationId`, `inactivityTimeoutSeconds`, `stabilizationDurationSeconds`, `returnOnExecutorError` | `timedOut` |
| `ListProjects` | Empty | `projectIds[]` |
| `ReadProject` | `id` | `project`, `notFoundOnDisk`, `errorMessage` |
| `GetBrowserOpenConversation` | Empty | `cascadeId` |
| `SetBrowserOpenConversation` | `cascadeId`, `expiresAt` | Empty |
| `SmartFocusConversation` | `cascadeId` | Empty |

`CascadeRunStatus` values are `UNSPECIFIED=0`, `IDLE=1`, `RUNNING=2`, `CANCELING=3`, `BUSY=4`. Summaries also have `hasActiveChildren`, `notFullyIdle`, `hasActivity`, `killed` and `interrupted` fields. A missing or zero status cannot be treated as idle. It is still necessary to establish which background work the list includes and how the flags behave during tool execution, child conversations and cancellation.

`Project.projectConversations` is field 8. Its `conversations[]` elements contain `conversationId` (field 1) and `environmentId` (field 2). Project also references resources, environments, permission grants and settings. Therefore a project record can depend on conversation and environment records outside a directory named for the project. This establishes the protobuf relationship, not the disk JSON spelling, file extension or a complete import set.

The binary contains a `conversation_summaries.db` literal, SQLite runtime data, GORM trajectory types and both database-trajectory and protobuf-file-store implementations. Those coexistence facts do not identify the format of every `.db` file. The initial header-only check was followed by a separate working-data inspection: Python SQLite backup created an isolated copy of one local conversation database and its summary index; no authentication file was included.

The conversation copy has `user_version=1` and tables `battle_mode_infos`, `executor_metadata`, `gen_metadata`, `parent_references`, `steps`, `trajectory_meta`, and `trajectory_metadata_blob`. Its `cascade_id` matches its filename; its `trajectory_id` differs. The summary copy has `user_version=3`; `conversation_summaries` contains `conversation_id`, `project_id`, `parent_conversation_id`, `raw_summary` and further status fields, alongside full-text search tables containing conversation bodies. The importer reads supported project references but does not copy or merge this index. One annotation copy exposed the field name `title`. Only schema, field names, types, counts and match results were printed; conversation bodies and project values were not printed.

An isolated CLI trial imported the conversation file group, compared all records across the seven tables, skipped an identical second import and a conflicting annotation, and removed the imported files through its restore operation. The source copy was unchanged. This establishes file retention and removal for the trial; account binding, native client restoration and cross-account continuation remain unconfirmed.

A separate project metadata check returned field names and types only: `id`, `name`, `projectResources`, `settings`, and `isWorkspaceOnly` in one JSON record. No field values were printed. This confirms one on-disk shape, not a complete project migration contract.

The Electron host loads `https://127.0.0.1:<language-server-port>/`. After a backend port change it reloads existing windows at that root URL. Host IPC has directory pickers but no project/conversation navigation method. The compiled language-server methods above are navigation candidates; their names do not prove that they move the visible window. The official conversation/project route structures were not recovered. The `/c/<uuid>` pattern in the separately installed localization patch is not an independent source for an official navigation API.

## Current compatibility decision

`adapterDecision.status` is always `unsupported`. User-entered account labels and shared working-content management do not grant the runtime account-switch adapter any additional capability.

The remaining gaps are concrete:

- Confirm that official authentication can select an existing Profile without Hub reading or saving credentials, including composite file/keyring choice and its Windows namespace.
- Establish the actual runtime directory behavior and complete project/conversation import set. Registered flags do not prove that every work file follows the override.
- Establish a restart interface that accepts an explicit child environment while leaving the visible window alive; none exists in the observed host.
- Confirm idle coverage for active tools, child tasks and cancellation before interrupting a backend.
- Restore the same conversation, unsent draft and view after restart, including a return to the original backend on failure.
- Demonstrate that expired or unusable official login state leaves the current session usable without opening a browser or requiring confirmation.

The removed connection-check script is not replaced by an endpoint or secret-file workflow. This report performs installed-file inspection only. No client patch, service start, account switch or browser interaction has been performed.
