# Antigravity Profile Hub (experimental)

Windows local manager for N independent Antigravity Desktop instances. Current local probe: Antigravity 2.12.2 with separate process home + Electron user-data. Independent processes/state directories observed; two Google identities persisting across restarts is **manual verification required**. This is not a security sandbox.

See PROJECT_STATE.md for build/implementation status, CAPABILITY_REPORT.md for evidence, ARCHITECTURE.md for design and MANUAL_TEST_PLAN.md for user login checks. Do not assume older IDE CLI flags apply to Desktop 2.

## Probe
With PowerShell 7: `pwsh -NoProfile -File scripts/Probe-Antigravity.ps1`. Optionally supply `-Executable <path>`. It launches two hidden disposable instances, observes process/state directory separation, stops owned processes and cleans checked temporary directories. It does not authenticate or inspect credential contents. Machine report: artifacts/probe/capability-report.json (ignored by Git).

## Development
Install .NET 10 SDK, or use the repository-local ignored .tools/dotnet installation. Build and test instructions are maintained as implementation lands. Source, build artifacts, Hub profile data and user workspaces remain separate.

## Limitations
Synthetic process home changes where child tools find configuration. OAuth deep links, Credential Manager and DPAPI remain unverified. Workspace launch arguments are unverified; association alone is metadata. Standalone Antigravity CLI and old IDE identities are outside current scope. No credential import, profile purge, quota changes or auth proxies.
