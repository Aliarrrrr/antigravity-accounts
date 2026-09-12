# Antigravity Profile Hub architecture

## Scope and evidence
Windows local desktop profile manager for N profiles. Authentication remains entirely in Google's app. Read CAPABILITY_REPORT.md before changing the driver. Antigravity Desktop 2.12.2 is not the old VS Code-derived IDE. Independent user-data alone is insufficient because Desktop also resolves `.gemini` from the process home and the language server uses one fixed Windows credential target. Concurrent accounts therefore require one prepared process-local scope per Profile, in which the language server keeps its OAuth token in the Profile's own file; unprepared/direct Profiles remain serial-only.

## Stack and boundaries
C# / .NET 10 LTS; WPF native UI; dependency-light console CLI. Core has no WPF dependency. JSON persistence via System.Text.Json. Windows process adapter may use System.Management for process metadata. Unit tests use xUnit if restore is available; otherwise a deterministic console test runner with failing exit status is acceptable. No server, database service, Electron shell or cloud backend.

Projects: src/AGProfileHub.Core, src/AGProfileHub.Cli (agprofile), src/AGProfileHub.Gui, tests/AGProfileHub.Tests. Presentation calls HubService; only IAntigravityDriver builds vendor arguments/environment. Domain records: Profile, Settings, CapabilityReport, ProfileRuntime, ProcessIdentity. Profile IDs are immutable UUIDs; names are editable and never become paths.

## Storage
Default root: LocalApplicationData/AntigravityProfileHub. config/profiles.json and settings.json contain schemaVersion=1; profiles/<uuid>/{user-data,home,roaming,local,temp}; isolation/state.json; runtime/state; runtime/locks; logs. A fixed `global-launch-state` directory under the current Windows user's LocalApplicationData holds the cross-data-root launch lock and short-lived direct-launch barrier. User workspaces stay outside the managed profile root. No copies of existing .gemini, cookies, credentials or old IDE data.

Atomic same-directory temporary writes with replace/backup protect both configuration and isolation mappings. File locks cover config read-modify-write, isolation state, and per-profile launch/stop/restart. Isolation validation rejects invalid names, duplicate Profile IDs and boxes, corrupt JSON, and future schemas without overwriting. All managed paths are computed from validated UUIDs, canonicalized, and checked for reparse points. Remove unregisters only; destructive purge is deferred.

## Driver
Discover explicit setting then standard install paths. Read executable file version without launching. The Desktop 2.12.2 driver launches through Windows CreateProcess with quoted --user-data-dir=<absolute path>, a dedicated environment block, and no inherited handles. Child-only USERPROFILE/HOME/HOMEDRIVE/HOMEPATH/APPDATA/LOCALAPPDATA/TEMP/TMP overrides isolate paths. Never modify the caller's environment or Windows user. This is path separation, not a security sandbox; the same Windows account can access both directories. Tool children inherit the synthetic home and will not automatically see existing SSH/Git/tool settings.

Do not claim --profile, --extensions-dir or positional workspace support: current desktop has no demonstrated IDE CLI contract. Store workspace metadata; refuse an unsupported launch-with-workspace operation clearly until verified. Do not mutate Antigravity's internal project configuration to compensate. CLI/headless identity is out of scope.

## Processes and recovery
Identity = PID + creation timestamp + canonical executable path + exact parsed user-data argument for the root. Never trust PID alone, substring matches, executable name alone, or stale runtime JSON. Reconcile live process roots at startup and each action; enumerate descendants with creation-time checks. Record owned snapshots without storing child command lines (vendor LS command lines include ephemeral bridge/CSRF secrets). Repeated launch reports already running. For the legacy direct path, one per-user lock serializes every Hub data root and an atomic pre-create barrier fails closed across delayed discovery, cancellation and state-write failure. Its live scan rejects any other root of the configured executable because direct instances share the host credential. Isolated lifecycle operations reuse the launch/profile locks for race prevention but rely on dedicated box ownership rather than the serial barrier. Direct graceful stop validates the window/process tree; isolated stop terminates only the Profile's dedicated box. Orphaned or unverifiable direct children produce unknown state rather than broad termination.

ProcessSupervisor owns lifecycle ordering, locks, launch-barrier recovery, retries and logging. ProcessOwnershipResolver centralizes the security-critical live-snapshot rules for root classification, identity matching, status resolution and deepest-first descendant ordering.

## Isolation backend

`HubService` is the single routing boundary used by CLI, GUI and shortcuts. If a Profile has a registered isolation box, lifecycle operations use `IsolatedProfileLauncher`; otherwise they use the serial `ProcessSupervisor`. `launch-all` preflights every Profile and starts none unless every Profile has an existing, keyring-claimed box.

`ProcessLocalBackend` owns a per-Profile scope: preparing it only creates the managed layout and a marker file, with no elevation and no system-wide configuration. Launches go through the shell with a small wrapper so the vendor desktop leaves the host's job object, and the wrapper drops `ELECTRON_RUN_AS_NODE`. A launch is trusted only after the language server's own log proves it moved to the Profile's file token store; an unverified instance is terminated.

Profile directories are ordinary directories, so workspace edits land directly on the host filesystem with no rule configuration.

## Compatibility
The verified strategy is pinned to Desktop 2.12.2. Each launch reads the installed Antigravity file version; unknown versions are refused before launch directories are created. Doctor combines configuration, file-version, live process and scope readiness. Manual acceptance passed distinct concurrent accounts and restart non-interference for the tested pair; a vendor update reopens the manual gate, and the probe scripts under `scripts/` re-verify the store-selection mechanism against a new build.

## Shortcuts
DesktopShortcut writes Windows .lnk files via WScript.Shell, then moves the unique temporary link into place without overwriting existing files. Target is the packaged CLI executable; arguments contain the absolute Hub data root and immutable profile UUID. A rename preserves the old link's behavior. Moving the package requires recreating links. GUI and CLI share this service; no vendor launch arguments are duplicated in shortcuts. Tray integration remains deferred.

## Logging and UI
Allowlist structured event fields: timestamp, operation, profileId, antigravityVersion, sanitized command template, processId, result, error code. Never ingest vendor stdout/logs or child command lines. Drain/discard vendor output. GUI shows Profile, status, workspace, box and write-through readiness, and offers isolation preparation/inspection alongside lifecycle controls. Desktop shortcuts call `agprofile launch <uuid>` and therefore cannot bypass Core routing. Tray remains deferred; CLI JSON is the first automation interface.
