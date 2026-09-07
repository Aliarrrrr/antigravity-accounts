# Antigravity Profile Hub architecture

## Scope and evidence
Windows local desktop instance manager for N profiles. Authentication remains entirely in Google's app. Read CAPABILITY_REPORT.md before changing the driver. Antigravity Desktop 2.12.2 is not the old VS Code-derived IDE. Independent user-data alone is insufficient because desktop also resolves .gemini from the process home.

## Stack and boundaries
C# / .NET 10 LTS; WPF native UI; dependency-light console CLI. Core has no WPF dependency. JSON persistence via System.Text.Json. Windows process adapter may use System.Management for process metadata. Unit tests use xUnit if restore is available; otherwise a deterministic console test runner with failing exit status is acceptable. No server, database service, Electron shell or cloud backend.

Projects: src/AGProfileHub.Core, src/AGProfileHub.Cli (agprofile), src/AGProfileHub.Gui, tests/AGProfileHub.Tests. Presentation calls HubService; only IAntigravityDriver builds vendor arguments/environment. Domain records: Profile, Settings, CapabilityReport, ProfileRuntime, ProcessIdentity. Profile IDs are immutable UUIDs; names are editable and never become paths.

## Storage
Default root: LocalApplicationData/AntigravityProfileHub. config/profiles.json and settings.json contain schemaVersion=1; profiles/<uuid>/{user-data,home,roaming,local,temp}; runtime/state; runtime/locks; logs; cache. User workspaces stay outside the managed profile root. No copies of existing .gemini, cookies, credentials or old IDE data.

Atomic same-directory temporary writes with replace/backup, process-wide file locks for config read-modify-write, and per-profile locks for launch/stop/restart. Reject unsupported future schemas without overwriting. Do not silently recover corrupt config to an empty profile set. All managed paths are computed from validated UUIDs, canonicalized, and checked for reparse points. Remove unregisters only; destructive purge is deferred from MVP.

## Driver
Discover explicit setting then standard install paths. Read executable file version without launching. For known Desktop 2.x driver, launch the executable using ProcessStartInfo.ArgumentList with --user-data-dir=<absolute path>, and child-only USERPROFILE/HOME/HOMEDRIVE/HOMEPATH/APPDATA/LOCALAPPDATA/TEMP/TMP overrides. Never modify the caller's environment or Windows user. This is runtime namespace separation, NOT a security sandbox; same Windows account can access both directories. Tool children inherit the synthetic home and will not automatically see existing SSH/Git/tool settings. Explain this in UI/docs.

Do not claim --profile, --extensions-dir or positional workspace support: current desktop has no demonstrated IDE CLI contract. Store workspace metadata; refuse an unsupported launch-with-workspace operation clearly until verified. Do not mutate Antigravity's internal project configuration to compensate. CLI/headless identity is out of scope.

## Processes and recovery
Identity = PID + creation timestamp + canonical executable path + exact parsed user-data argument for the root. Never trust PID alone, substring matches, executable name alone, or stale runtime JSON. Reconcile live process roots at startup and each action; enumerate descendants with creation-time checks. Record owned snapshots without storing child command lines (vendor LS command lines include ephemeral bridge/CSRF secrets). Repeated launch focuses existing window or reports already running. Graceful window close first, bounded wait, then only verified owned processes; never taskkill by name. Orphaned/unverifiable children produce unknown state rather than broad termination. Hub GUI singleton is independent of per-profile command locks; CLI shortcuts can run concurrently.

## Compatibility
Persist last tested exact version + executable path + probe strategy revision. Version change performs lightweight discovery/file-version/static checks and emits unknown unless actively probed. Existing known strategy can be offered as experimental with a warning when the contract remains present; definitively incompatible versions must not launch with guessed flags. Auth isolation always manual-verification-required until the user records successful A/B tests; a version change invalidates that attestation. Doctor read-only by default; doctor --probe explicitly runs disposable processes and cleans only its own directories.

## Logging and UI
Allowlist structured event fields: timestamp, operation, profileId, antigravityVersion, sanitized command template, processId, result, error code. Never ingest vendor stdout/logs or child command lines. Drain/discard vendor output. Do not serialize arbitrary exception contents containing launch secrets. GUI cards/table: Profile, Status, Workspace, actions; create/rename/remove registration, launch/stop/restart, all actions, doctor, settings, open-data, logs. Desktop shortcuts call agprofile launch UUID; tray is an enhancement. CLI JSON is the first automation interface.
