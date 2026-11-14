# Accounts 0.4.0-preview.18 validation scope

## Preview.18 Windows compatibility follow-up — 14 November 2026

The preview.17 candidate below was built from commit `9d0eee3354da9df1271d2c17b1b5c4b2cfd748d4`, installed on the existing Antigravity 2.32.1 application through the new coordinator, and launched in Chinese through the stable bootstrap. The two saved accounts completed A to B to A: each confirmation finished, the welcome identity updated, the original primary account was selected again, and the selected project and Gemini 3.8 Flash High remained. The account component and language-service PIDs did not change. The management panel retained both accounts and the existing preference. There were no model submissions, account edits or conversation-body inspections. Normal background account/project/quota requests were not counted.

The pushed preview.17 tag did not publish release assets. Its English-Windows CI run exposed non-Unicode shortcut handling and failures with the runner's 8.3 temporary-directory alias. Local testing on a Chinese Windows installation had not exposed these differences. Preview.18 uses `IShellLinkW` and `IPersistFile`, reads UTF-8 metadata explicitly, normalizes both sides of the Swarm working-directory comparison, and constructs test receipts and mutexes with the same physical directories used in production. All 59 affected Windows cases passed with both normal TEMP and a real `AGYCAN~2` alias, including Chinese and emoji shortcut names. The preview.17 copied-program and installed-window results below retain their original scope. Neither release performs a real vendor-installer upgrade or establishes quota attribution, generation, nonempty-draft preservation or long-context continuation.

## Preview.17 combined compatibility updates — 14 November 2026

Accounts, Chinese localization and a shared prepare/apply coordinator are now distributed together, alongside the standalone Accounts ZIP. Official updates take priority; unsupported native programs are retained, and independent authorizations are not moved. This does not establish arbitrary future native programs or real vendor-installer upgrades.

The complete Accounts Node suites passed 274 tests without skips using separate original 2.32.1 program inputs, including a real Electron check of raw ASAR metadata through `original-fs`. The .NET component passed its 44 synthetic-account checks. The full Chinese suite initially passed 252 of 253 cases; the remaining test supplied an obsolete synthetic manifest without its installation directory. Adding that required identity field made the modified-candidate rejection case pass without weakening deployment validation. Two additional launch cases confirm disabled components are not repaired by the legacy launcher and incomplete coordinated installations refuse launch.

On a separate copy of the official 2.32.1 installation, the coordinator constructed Accounts and then Chinese localization before modifying the destination. Applying both layers succeeded; rolling back that same transaction restored the original program bytes. A synthetic authorization sentinel remained unchanged. The transaction covered seven changed files and guarded the executable plus 347 unpacked files. This was an actual copied-program installation and restoration, not a run of the vendor installer. Interruption, unknown-file refusal, program-change refusal, partial component compatibility, active-application deferral and settings changes are covered by the coordinator tests.

All 26 compatibility tests passed without skips. They cover fixed-repository selection, checksums and release commits, preparation retry after corruption, downgrade refusal, deferred activation, engine fallback, operation refusals, persistent notice deduplication, and preservation of enabled-component settings and redirected account-state roots. Windows ZIP extraction rejects parent traversal, case-insensitive duplicate paths and links before writing any archive entry. Package inspection covers both directory contents and ZIP payloads; the combined bundle contains 429 payload files, including the 55-file Accounts distribution. No official program, authorization file or conversation database is included.

The preview.17 installed-window A to B to A result is recorded in the follow-up section above. No model generation or quota attribution was added by these automated and copied-program checks.

## Preview.16 native-window switching repair — 14 November 2026

The installed preview.15 check below observed startup and account-management UI on Antigravity 2.32.1 without switching a saved account. A subsequent switching failure exposed that the adapter enumerated only `BrowserWindow` instances while the host's Hub used `BaseWindow` with a `WebContentsView`. Rendering account controls in a preload did not exercise native-view capture, model refresh or the later confirmation path.

The preview.16 repair targets the local Hub page's actual webContents for those operations and preserves support for earlier BrowserWindow hosts. It also includes the 2.32.1 source, native-program and OAuth compatibility changes from the unreleased local preview.15 build. SSH remote sessions remain outside independent-account switching support.

The final complete local Node suites passed 269 tests without skips using separate original 2.32.1 program inputs. A first run accidentally used the modified installed archive for three installation-helper cases; those failed on program or state-root checks. Re-running with the original-program copy and an explicit Electron executable passed all cases. The preview.16 self-contained package passed the existing directory and ZIP checks with 48 payload files. No native-process copy was started for these window changes. Earlier isolated-program, installed-menu and 2.29.0 generation results retain their original scope; they do not establish preview.16 quota attribution or generation on 2.32.1.

The preview.16 build was installed on the actual Antigravity 2.32.1 application and Chinese localization was reapplied. In that installed BaseWindow/WebContentsView UI, the two saved accounts completed A to B to A switching: each confirmation finished, the selected account and welcome identity changed, and the original primary account was selected again at the end. The project and Gemini 3.8 Flash High selection remained. The account component and language-service process IDs were unchanged across the return switch. No model submissions or account edits were made, and no conversation body was opened. The App was left running on the original primary account. This confirms the previously failing installed-window switching path; it does not establish generation, exact quota charging, nonempty draft preservation or long-context continuation on 2.32.1.

## Preview.15 local compatibility work — 14 November 2026

This local repair adds the reviewed Antigravity 2.32.1 main process and language-service launcher, including the new `lsDefaults.js` and `loginShellEnvReads.js` dependencies. Default arguments now come from a shared helper, so the adapter inserts HostAuth arguments at the new array construction point. Existing source contracts retain their previous file requirements. SSH remote sessions are outside this independent-account support scope.

The native review identified relocated goroutine-route, project-refresh and public OAuth configuration addresses, while also finding new HostAuth, cache-backoff and conditional license-evaluation code. The activity messages, RPCs and fully-idle predicates examined remain unchanged from 2.31.2. Version-specific original and adapted program digests and original instruction bytes are required. Native fallback also rejects all three newly adapted 2.32.1 program digests.

The first Node run found an incomplete adapted-program rejection list and a test-only unmapped identifier in the recompiled frontend settings hook. Both were corrected. The final complete Node suites passed 264 tests without skips using the separate original 2.32.1 programs. The preview.15 self-contained component build succeeded, and its 44 synthetic-account checks passed under .NET SDK 10.0.400 with no real authentication or model requests.

Isolated copies of the 2.32.1 native program accepted the goroutine route and rejected the other five checked diagnostics routes. A synthetic A-to-B HostAuth change made the next model-list metadata request use both the B identity and `local-project-b`. When the B project refresh failed, that metadata request was not issued. A real initial activity update from an empty local conversation decoded as known and fully idle, the wait RPC returned 200, and the normal adapter confirmed both synthetic identities. These processes used temporary user directories, loopback services, constructed tokens and a rejecting proxy; model-generation requests and denied proxy hosts were zero. There was no OS-level network isolation and no saved-account switch or cloud quota check. This build is local work and has not been published as a GitHub release.

The local preview.15 package was then installed on the actual 2.32.1 application, Chinese localization was rebuilt on that installation, and the registered launcher returned `ready`. The installed window showed Chinese navigation, native account groups and the management panel with both saved accounts, current/primary badges and the existing unchecked data-use preference. Escape returned to the home page. This check made zero saved-account switches, account edits or model submissions; the App was left open. It does not establish quota attribution or cross-account generation on 2.32.1. The packaged validation file was created before this final installed-window observation.

## Preview.14 update recovery changes — 12 November 2026

Unsupported adaptations can now return a native-login choice when the current official EXE, ASAR, unpacked content and language-service PE image are complete and contain no Accounts hooks. The EXE version must match the ASAR version. The selected identity can differ from the saved independent Accounts identity, so launch asks for confirmation and rechecks the program identities while retaining the installation mutex. Declining confirmation leaves the application closed. Account authorizations, provider enablement and the existing installation receipt are retained so a later compatible package can repair the update.

Local tests use synthetic program images and account-state placeholders. They cover unsupported complete updates, partial replacements, corrupt or missing files, known adapted native programs, obsolete localization records, localized native archives, changed files while the prompt is open, an active updater, cancellation and later repair. The manual CMD now uses the same recovery-and-launch entry point and forwards registered Chinese installations to their existing guarded launcher. These checks do not establish the behavior of every future official binary or a real-account identity change during fallback. Unknown language-service binaries have no reliable product-version field, so structural completeness and change checks cannot prove their semantic compatibility with an unknown frontend.

The final complete local Node suites passed 259 checks without skips using separate original 2.31.2 program inputs, including the updater change that continues official installation but disables automatic direct-EXE restart when handoff preparation fails. The component passed its 44 synthetic checks. Windows helper cases use controlled installer and sidecar processes. No real official update, saved-account switch or model generation was performed for these recovery changes.

The final preview.14 package was installed on the existing Antigravity 2.31.2 application. Startup through the registered Chinese launcher, native account groups and the management panel were observed, with both saved accounts present and the default data-use option unchecked. This installed-client check made zero account switches and zero model submissions. It does not establish a live identity change in native fallback mode or the behavior of a real official installer.

Chinese localization uses a separately maintained external toolkit. The Accounts release does not include that toolkit. Its guarded startup path needs an updated launcher that supports preview.14 native-login confirmation. An older Chinese launcher can continue to refuse an unsupported update. The current local launcher was updated and checked, but the Chinese toolkit has not been published separately from this workspace.

This document distinguishes installed-client observations from checks with synthetic accounts, controlled processes and official program copies. The 7 November development-package check predates the final preview.11 visual changes. Earlier preview.10 and preview.8 results retain their original scope.

## Preview.13 local compatibility work — 12 November 2026

Antigravity 2.31.2 retains the seven complete host scripts and four RPC dependency versions used by 2.31.0. Matching source contracts now select the reviewed application version before considering an unchanged-source fallback, so the identical 2.31.0 and 2.31.2 JavaScript sources still pair with their own native programs.

The native review compared the diagnostics route registration, project-cache Get/forceRefresh, CodeAssist construction, HostAuth methods, public OAuth configuration and agent-state update functions. Their normalized instructions and function sizes match 2.31.0 after address relocation. Public OAuth configuration digests, the activity messages and the native fully-idle predicates also match. The native programs are accepted through their complete version-specific image digests and original instruction bytes.

The existing frontend resolver accepts the recompiled native bindings. The complete Node suites passed 239 checks without skips using separate original 2.31.2 program inputs, including account management, model menus, native settings hooks, version pairing and new/previous activity decoding. The component Release build passed 44 synthetic checks with zero real authentication or model requests.

Isolated copies of the 2.31.2 native program returned the goroutine route while rejecting the remaining diagnostics routes. A local empty conversation produced a known, fully-idle initial activity state through both the stream reader and the normal activity adapter. A synthetic HostAuth A-to-B change made the next model-list metadata request use the B token and B project. These processes used temporary home/app-data directories, loopback replacements for configured endpoints and a rejecting proxy, with zero model-generation requests and no denied proxy hosts. This setup did not use OS-level network isolation. These results do not establish a live-account switch or model generation on 2.31.2. Preview.13 was a local build and was not released separately.

## Preview.12 local compatibility work — 11 November 2026

This local build adds the reviewed Antigravity 2.31.0 source combination and native program while retaining 2.29.0 and 2.28.2. The reviewed main-process changes preserve the existing HostAuth startup, restart, updater and shutdown insertion points. RPC source files, dependencies and language-server launcher source match 2.29.0.

The 2.31 native activity stream removes queued-step, background-command and active-child fields and adds execution status. The new reader uses the known native program version, requires all three execution statuses to be idle, the native fully-idle predicate to be true, and background tasks and pending messages to be empty. Any execution-status message, unknown status or incomplete read keeps switching blocked. Old native versions retain their existing aggregate decoding.

The revised data-use modal is recognized through its native component, account-change reason, known terms-flow value and settings hooks. Checks execute the extracted 2.31 native settings hook with synthetic settings, verify that unchecked telemetry is saved while the marketing choice is preserved, and verify that failed saves leave the modal available. Synthetic cases also cover manual policy, first use, changed terms and unknown flows. The native model-menu identifiers and extracted frontend bindings remain supported.

The component Release build and its 44 checks passed with synthetic accounts and zero real authentication or model requests. The initial full Node suites passed 232 checks without skips. After adding a normal-entry execution-status regression, the final suites passed 233 checks without skips against separate original 2.31.0 program inputs. The local ZIP's validation record was written after the initial run. Isolated native processes use temporary user directories, loopback endpoints and synthetic tokens. They confirm the project metadata refresh, the restricted goroutine route and the initial 2.31 activity stream. These checks do not establish a live-account switch, quota charging or model generation on 2.31.0. Preview.12 was a local build and was not released separately.

## Preview.11 changes and verification boundaries — 7 November 2026

Preview.11 combines login cancellation, account-store write limits, switch-recovery and keyboard-focus fixes with compact native-menu rows, separate account names and status, dedicated icons, account-status labels and unified management-panel controls. Versioned package metadata and the release workflow identify the exact committed build; they do not establish a repeat of installed-client generation behavior.

The three-submission text-context check below used a development package before these visual changes. It must not be represented as a fresh check of every final tagged package. Native plugin-list integration remains a feasibility result: Accounts HostAuth is not yet controlled by the generic enabled switch.

## Preview.11 installed UI and synthetic layouts — 7 November 2026

The preview.11 UI build was installed on the existing Antigravity 2.29.0 application, preserving its account-state directory and reapplying Chinese localization. The installed preload/runtime and account component were compared with the packaged files and matched. The native menu, host theme, management dialog, current/primary badges and removal of a repeated identity email were observed. Escape closed the dialog, and the focus ring returned to the native model trigger.

This visual check made **zero new model submissions, zero live-account switches and zero live-account edits**. Startup background metadata and OAuth-renewal requests were not counted. It does not repeat the earlier three-request context check.

Separate synthetic browser pages covered dark/light themes, a 390 × 620 viewport without horizontal overflow, long account labels, Enter-to-save, login cancellation and Tab wrapping. The light/narrow results describe these pages, not an observed change of theme or window size in a real-account App window. The local result is `output/playwright/preview11-installed-ui.json`; related screenshots remain local and are not bundled account data.

The Release build completed with zero warnings and errors. Component checks passed **44/44**, and the final Node suites passed **228/228 without skips** using separate original 2.29.0 programs. During installation, a case-sensitive comparison of the quoted Windows executable path in `agy-node.cmd` incorrectly left verified Node sidecars classified as App processes. Preview.11 compares only that executable path case-insensitively, retaining exact launcher commands, arguments and parent relationships; unidentified processes continue to block changes. The external Chinese helper received the corresponding fix and passed its own 29 checks; that helper is not part of this package. Package inspection verified 45 payload files and matching ZIP contents for the local UI build. Final tag/CI results are separate from this local package check.

For upgrades, use the new preview.11 package’s `Restore.cmd` to read the existing schema-1 installation receipt and saved originals, then its `Install-Preview.cmd`. This avoids relying on the older restore helper with the path-casing error. Receipt, backup and current-program checks remain required, including refusal to overwrite an updated or unrecognized program.

## Pre-release working-tree audit — 7 November 2026

The working tree based on `bc0165e` added login cancellation, write-time account-store limits, switch recovery and keyboard-focus fixes. It was not a new published preview.10 release. Component checks passed 44/44 and the Node suites passed 225/225 without skips using separate original 2.29.0 programs. The development package recorded `dirty: true`, `release: false`.

With explicit user permission, this package was installed on the existing 2.29.0 application, preserving its account-state directory and reapplying Chinese localization. In a newly created empty project, three pure-text requests completed in one conversation across existing Account A → Account B → Account A. Responses were `ACK-A`, `583149`, and `M4Q2|941385`, correctly recalling and transforming markers supplied only in the first message. Each switch preserved the unsent next-message draft and Gemini 3.8 Flash High selection. Window, project and conversation IDs remained the same. App, language-server and account-component PIDs and start times remained unchanged across both switches. The account menu identified each selected account as active; the test ended on the original Account A.

Exactly three UI submissions were made; native metadata, title-generation and possible retry requests were not counted. No tool calls were observed and the empty test project directory remained empty. The result establishes this short text-context case, not attachments, tool/subagent history, long-context limits, queued sends during switching, expired-token renewal or exact quota charging. No personal conversation content was used for the test.

Full findings and local record locations: [7 November audit](ACCOUNTS_AUDIT_2026-11-07.md). [Native plugin-list feasibility](ACCOUNTS_EXTENSION_INTEGRATION.md) is separate: discovery and persisted enabled preferences worked in an isolated native service, but the Accounts HostAuth lifecycle is not yet controlled by that switch.

## Preview.10 installed application — 6 November 2026

Preview.10 was installed on Antigravity 2.29.0, retaining its account records and reapplying the existing Chinese localization. The installed preload was compared with the current source and matched. The model picker retained the official model options, account switching, View Usage and a single management entry.

During the preceding preview.9 check, the data-use preference was successfully saved as unchecked, but the modal stayed open: two React alternate parent chains both pointed to the current root, and the adapter read the older checked value. The regression check fails with that former resolver and passes with the child/sibling membership resolution used in preview.10.

After installing preview.10 and restarting, returning to the original account produced one Security Notice & Data Use modal. Observation recorded its checkbox becoming unchecked, followed by the modal disappearing automatically. The model picker then identified the original account as active and retained Gemini 3.8 Flash High. The temporary debugging session was closed and the App started normally without the debugging endpoint. This check involved restarts; it is not a same-process round-trip result for preview.10.

The agent sent **zero model requests**. Native OAuth refresh and background account/project/quota/settings requests were permitted for the check, but their total count was not measured. Data-use automation normally uses one native settings read and one save per eligible account-change modal; organization-disabled collection avoids those extra calls.

The management panel was checked separately with synthetic accounts and the native stylesheet under dark/light themes and a 390-pixel viewport. Checks covered trusted label editing, deletion cancellation, all three data-use preferences, scrolling and focus restoration. These results do not represent actual Google sign-in, deletion or labeling of a real account.

The component runner passed 39 checks. The final local Node run passed 217 checks without skips using separate original 2.29.0 program copies. Package inspection checks distribution contents; the release workflow records the exact committed build separately. No new live-account check was performed on 2.28.2 for preview.10.

## Preview.8 installed application (earlier results)

The final preview.8 runtime was installed on Antigravity 2.29.0. Its native model selector showed account groups, original model choices, Add account and View Usage. Account controls inherited the host font and theme. The same application window and workspace remained open, and the system-tray setting was restored.

After the user removed a secondary account that required Google verification, the remaining two accounts completed an authorized Account A → Account B → Account A metadata round trip. Native identity, project, model-list and quota confirmation completed. The application, language server and account component retained their process identities and start times; the original window, workspace, selected Gemini 3.8 Flash High model and empty input remained. Both official Security Notice & Data Use choices were handled by the user. No further account changes were made for this check.

The agent sent **zero model requests**. Native confirmation can cause additional Google background metadata requests; the result does not establish a total external-request count.

An earlier account returned `Verify your account to continue.` during quota confirmation. The host kept new sending paused and identified the need for Google verification. The user chose not to verify that account and removed it from the local list. This failure was not recorded as a successful account change.

## Local checks

| Area | What was checked | Limit |
| --- | --- | --- |
| Account component | Primary/secondary relationships, local deletion, transaction generations, cancellation, DPAPI storage, stdio handling and renewal races with controlled OAuth implementations | Does not verify Google's actual token-renewal behavior |
| Model menu and frontend | Grouping, original model/usage controls, keyboard focus, scrolling, input methods, listener cleanup and model preservation using native frontend code and synthetic state | Does not establish real generation or charging |
| Native adaptation | Reviewed 2.31.2, 2.31.0, 2.29.0 and 2.28.2 programs, diagnostic-route and project-refresh changes, rejection of unknown checked-source/native changes | No guarantee for an unknown future program |
| Installation and restoration | Program-copy apply/restore, refusal to overwrite updated programs, independent repair cache and shared account-state-directory selection | Controlled directories do not establish a future updater's behavior |
| Update handoff | External PowerShell process waited for controlled app/installer processes and changing files, then invoked the repair/start entry | No actual NSIS upgrade was performed |
| Update repair | Replacing installed files with reviewed same-version official files was followed by cached Accounts repair and reapplication of Chinese localization | Same-version replacement, not an unknown-version update |

The menu was also checked with the native stylesheet under light/dark themes, small window heights and synthetic account lists of different sizes. Account controls and model choices remained reachable by scrolling. The external Chinese localization toolkit was checked in its own workspace and is not included here.

## Not verified

- Generation beyond the short pure-text cross-account case above, including attachments, tool/subagent history and long contexts.
- Exact quota charging, sends queued during switching and real expired-token renewal.
- Every combination of native tools, subagents, remote-control work and scheduled tasks.
- A real NSIS update, an unknown future Antigravity version or Marketplace installation.

The adapter waits when work is active or unreadable, and keeps sending paused when identity/project/quota confirmation fails. Those failure rules do not establish that every native task combination has been executed.

## Preview.8 repository release checks — 6 November 2026

After removing the former Profile Hub build targets, the standalone Accounts Release build and component runner passed all 37 checks. The Node run reported 190 passed and one skipped out of 191; the missing Electron helper was then supplied explicitly, and that skipped helper case passed when rerun. These checks used local test inputs and made no account, OAuth or model requests.

The package preflight verified 42 payload files plus its manifest. This checks distribution contents and does not represent a new installation or live-account run of the packaged artifact. The final release build and CI results are reported with the release itself.

## Reproducing source checks

Use .NET SDK 10.0.400 and locked JavaScript dependencies. From the repository root:

```powershell
./scripts/Build.ps1 -DotNet dotnet -LockedRestore -Test
npm ci --prefix packages/hostauth-adapter --ignore-scripts --no-audit --no-fund
node --test packages/hostauth-adapter/test/*.test.mjs packages/account-plugin/test/*.test.mjs packages/account-plugin/test/*.test.cjs
```

The .NET tests use a console runner; `dotnet test` alone does not execute it. Tests needing official program copies accept `ANTIGRAVITY_PROGRAM_ASAR`, `ANTIGRAVITY_LANGUAGE_SERVER` or `ACCOUNTS_TEST_INSTALL`; `ACCOUNTS_ELECTRON_EXECUTABLE` selects a local Electron helper. Supply separate original program copies, not an account-data directory. Missing optional program files cause relevant cases to be skipped; a skip is not a successful native integration check.

Raw local records, personal identifiers, credentials and official program copies are excluded from source and distribution. Package manifests record build provenance and content checks separately; they must not be interpreted as a fresh live-account test of every packaged build.
