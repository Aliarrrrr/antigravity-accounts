# Capability report — 2026-09-07

## Verdict
Desktop 2.12.2 supports the **observed runtime portion** of an experimental multi-profile strategy: independent process homes plus user-data directories. Two different Google accounts persisting simultaneously is **manual verification required**. Do not call the entire product verified yet.

## Environment
Windows 11 Home x64 build 26200; PowerShell 7.6.5; Git 2.55.0.windows.2. Initially no .NET SDK, only .NET 7.0.20 runtime. Antigravity discovered at LocalApplicationData/Programs/antigravity/Antigravity.exe; file version 2.12.2, product 2.12.2.0. app.asar package reports version 2.12.2 and Agentic Desktop Application. No original Antigravity processes were running at inspection. Machine-specific evidence lives in ignored artifacts/probe/capability-report.json.

## Static inspection (installed application code only)
Read ASAR header and packaged JavaScript; did not modify the application or inspect credential files. dist/main.js calls requestSingleInstanceLock(). dist/paths.js computes core state under os.homedir()/.gemini/antigravity and config under os.homedir()/.gemini/config/config.json; UI storage uses app.getPath('userData'). dist/languageServer.js passes --app_data_dir antigravity to its backend, inherits process environment, and creates separate local ports. Child command lines contain ephemeral secrets and must never be logged by Hub. The application only showed deep-link argv handling in inspected main code; desktop --help, --version, --profile, workspace arguments are not established contracts.

## Dynamic probe
scripts/Probe-Antigravity.ps1 created two disposable directories under TEMP/AGProfileHub-Probes/<random-id>. Each launched the same executable with independent --user-data-dir and process-only home/appdata/temp variables. After staggered 15-second observations, roots 3724 and 3204 were alive concurrently with separate language_server children 10724 and 3180. Both created Local State, cache/session directories, and their own home/.gemini/{antigravity,config}. Closing/terminating A left B alive. Final process inspection found no Antigravity or language_server residue; probe reported cleanup completed.

| Capability | Result |
|---|---|
| Executable/version discovery | verified |
| Separate root and backend processes concurrently | observed |
| Electron user-data and home .gemini paths | observed separate directory creation |
| Stop A while B survives | observed |
| Visible, usable independent windows | unknown (probe launched hidden; MainWindowHandle was 0) |
| Account A/B login persistence and non-overwrite | manual-verification-required |
| OAuth protocol callback destination with two instances | manual-verification-required; shared protocol registration is a risk |
| Windows Credential Manager / DPAPI isolation | unknown; no secret access performed |
| Registry / mutex / IPC resources | scoped Electron singleton did not prevent test; exhaustive isolation unknown |
| Settings contents independence across restart | manual-verification-required |
| Workspace positional launch | unknown |
| IDE extensions separation | not applicable to Desktop 2; old IDE not probed |
| Standalone CLI/headless identity | not investigated |

The first probe inherited vendor output unintentionally; subsequent code drains/discards stdout and stderr. Reports persist metadata only. No Google credential contents were read or copied. Synthetic home can affect Git, SSH, plugins and downstream tools. No guarantee that all future vendor code honors environment overrides.

## Sources
- [Google's 2.0 announcement](https://www.antigravity.google/blog/introducing-google-antigravity-2): a separate desktop application from the IDE.
- [Google remote-control docs](https://antigravity.google/docs/remote-control/): Windows shared default .gemini/config location.
- [Node os.homedir](https://nodejs.org/api/os.html): Windows uses USERPROFILE when defined.
- [Electron app paths](https://www.electronjs.org/docs/latest/api/app): userData/sessionData semantics.
- [Microsoft .NET support](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support): .NET 10 LTS supported through November 2028.

Re-run probe on update. Preserve unknowns; directory creation alone never proves Google authentication isolation.
