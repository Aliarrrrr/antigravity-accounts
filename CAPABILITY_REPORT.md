# Capability report — 2026-09-08

## Verdict
Desktop 2.12.2 supports the **observed runtime portion** of an experimental multi-profile strategy: independent process homes plus user-data directories. Manual acceptance showed that concurrently running profiles resolved to the Google account signed in later, so concurrent Google-account isolation under one Windows user is **incompatible**. Do not call the product a separate-account manager.

## Environment
Windows 11 Home x64 build 26200; PowerShell 7.6.5; Git 2.55.0.windows.2. Initially no .NET SDK, only .NET 7.0.20 runtime. Antigravity discovered at LocalApplicationData/Programs/antigravity/Antigravity.exe; file version 2.12.2, product 2.12.2.0. app.asar package reports version 2.12.2 and Agentic Desktop Application. No original Antigravity processes were running at inspection. Machine-specific evidence lives in ignored artifacts/probe/capability-report.json.

## Static inspection (installed application code only)
Read ASAR header and packaged JavaScript; did not modify the application or inspect credential files. dist/main.js calls requestSingleInstanceLock(). dist/paths.js computes core state under os.homedir()/.gemini/antigravity and config under os.homedir()/.gemini/config/config.json; UI storage uses app.getPath('userData'). dist/languageServer.js passes --app_data_dir antigravity to its backend, inherits process environment, and creates separate local ports. Child command lines contain ephemeral secrets and must never be logged by Hub. The application only showed deep-link argv handling in inspected main code; desktop --help, --version, --profile, workspace arguments are not established contracts.

## Dynamic probe
scripts/Probe-Antigravity.ps1 created two disposable directories under TEMP/AGProfileHub-Probes/<random-id>. Each launched the same executable with independent --user-data-dir and process-only home/appdata/temp variables. After staggered 15-second observations, roots 3724 and 3204 were alive concurrently with separate language_server children 10724 and 3180. Both created Local State, cache/session directories, and their own home/.gemini/{antigravity,config}. Closing/terminating A left B alive. Final process inspection found no Antigravity or language_server residue; probe reported cleanup completed. The probe does not log in, inspect credentials, or evaluate authentication.

| Capability | Result |
|---|---|
| Executable/version discovery | verified |
| Separate root and backend processes concurrently | observed |
| Electron user-data and home .gemini paths | observed separate directory creation |
| Stop A while B survives | observed |
| Visible independent windows | observed on second visible probe; both rendered separate first-run screens |
| Account A/B login persistence and non-overwrite | failed: sequential restart persistence passed, but concurrently running profiles both displayed the account signed in later |
| OAuth protocol callback destination with two instances | direct backend failed; Sandboxie did not provide a passing authenticated route |
| Windows Credential Manager isolation | synthetic Sandboxie host/A/B matrix passed on 1.18.2; this did not isolate Antigravity's real session |
| DPAPI and other authentication stores | unknown; no real secret access performed |
| Registry / mutex / IPC resources | scoped Electron singleton did not prevent test; exhaustive isolation unknown |
| Settings contents independence across restart | not evaluated; this is not an authentication-isolation result |
| Workspace positional launch | unknown |
| IDE extensions separation | not applicable to Desktop 2; old IDE not probed |
| Standalone CLI/headless identity | not investigated |

The first probe inherited vendor output unintentionally; subsequent code drains/discards stdout and stderr. Reports persist metadata only. No Google credential contents were read or copied. Synthetic home can affect Git, SSH, plugins and downstream tools. No guarantee that all future vendor code honors environment overrides.

## Sandboxie recovery probe

Sandboxie Plus 1.18.2 / core 5.73.2 was installed from the winget `Sandboxie.Plus` manifest after its installer hash was verified. Static review of that exact release found that `cred.c` falls back to the original `CredReadW` when no sandbox PStore entry exists, and exposes no dedicated configuration switch to disable that branch. A dynamic test was therefore required rather than assuming either isolation or leakage from source alone.

`scripts/Test-SandboxCredentialIsolation.ps1` used only a randomly named `AGProfileHub.Probe.<nonce>` generic credential containing public random markers. With `OpenCredentials=n`, empty A/B boxes could not read the host marker; each box retained its own marker; the host remained unchanged; deleting A did not reveal the host value or change B. The matrix passed again after fixing and verifying persistent box configuration. Each operation used a new worker process, which establishes process-restart persistence. A Windows reboot check remains pending. The host test target and disposable boxes were removed; no real credential name was enumerated or accessed.

A fresh P2 environment then launched Desktop 2.12.2 in `AGPH_P2_6a4933eec3_A`. The user reported completing Profile A's official login. Immediately afterward, all observed Antigravity/language-server processes belonged to A; a newly started Antigravity process also remained in A, and no unboxed vendor process existed. This is positive process-routing evidence without inspecting the callback URL. A was terminated with its sandbox data preserved and relaunched into the same box; the user confirmed its login persisted. Profile B was then launched concurrently in a fresh box. Inventory showed no PID overlap, no unboxed vendor process, and one visible window per box; the user reported that B displayed A's existing session. A and B each have an independent `SbiePst.dat` path (only path and metadata were checked; contents were not read). A five-second read-only B resource trace is recorded at `artifacts/sandboxie-resource-trace/650eedee8550456490d9468204ed8342/report.json`: 43,407 session entries, 18,157 B entries, 2,872 unique resources, 337 unmapped entries discarded, and 144 truncated entries. B showed no direct reads of the host user's existing Google/Antigravity configuration, cookie store, Local State, or credential/vault/DPAPI data; loading Windows System32 `DPAPI.DLL` was observed, and B's own user-data `Cookies-wal` is not host authentication storage. The principal shared authentication-related objects were `protected_storage`, `SBIE_ProtectedStorage_Section`, and `SBIE_BOXED_ServiceCrypto_Mutex1`. The trace does not yield safe `ClosedFilePath` or `ClosedIpcPath` rules. The concurrent Sandboxie route therefore fails the core acceptance even though process membership is clean.

A separate disposable C box enabled `UsePrivacyMode=y` and opened only the Antigravity install directory. It launched, but the local Sandboxie installation has no supporter certificate; the five-minute evaluation ended after about five minutes. Privacy Mode cannot be treated as a dependency or evidence of authenticated isolation.

## Sources
- [Google's 2.0 announcement](https://www.antigravity.google/blog/introducing-google-antigravity-2): a separate desktop application from the IDE.
- [Google remote-control docs](https://antigravity.google/docs/remote-control/): Windows shared default .gemini/config location.
- [Node os.homedir](https://nodejs.org/api/os.html): Windows uses USERPROFILE when defined.
- [Electron app paths](https://www.electronjs.org/docs/latest/api/app): userData/sessionData semantics.
- [Microsoft .NET support](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support): .NET 10 LTS supported through November 2028.

Re-run probe on update. Preserve unknowns; directory creation or clean sandbox process membership never proves Google authentication isolation.

## Manual authenticated acceptance
On September 8, the user reported that A and B each kept their then-current session through a sequential Hub stop/relaunch. B initially displayed A's existing session; after the user signed out in B and completed a later login, B retained that session through restart. With A and B running concurrently, both then displayed the account signed in later. No credentials, cookies, tokens, account identifiers, or authentication screens were inspected or recorded. This is an account-isolation failure; both profile data directories are preserved for diagnosis.

## Visible follow-up
Re-ran `-Visible -ObserveSeconds 35` at 09:13 UTC. Roots 11088 and 2740 both had nonzero window handles. Windows computer-use inspection identified two distinct Antigravity windows (723210 and 68022), activated each, and observed each rendering the official first-run Security Notice & Data Use page. No onboarding choices, privacy settings or login actions were performed. Both process homes/state paths were separate; A stop left B alive; cleanup completed without process residue. This proves separate rendered first-run windows, not authenticated functionality.

Further packaged main.js inspection confirms a shared `antigravity` URL protocol: it checks/registers the executable as default handler without a profile identifier, then routes received deep links to its own first window. This strengthens the callback-routing risk; no registry modifications by Hub are proposed. The vendor app itself can check protocol registration and updates when launched, so disposable-directory isolation is not a guarantee of zero system-wide vendor side effects.

The standard Sandboxie route is closed and is not integrated into the product. Version 1.18.2 `cred.c` falls back to the original `CredReadW` when the box cannot find an entry, with no switch to disable that fallback; together with the shared authentication objects and absent safe closed-resource rules, this cannot establish credential isolation.
