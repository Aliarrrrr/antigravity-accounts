# Decisions

- ADR-001 accepted: .NET 10 LTS + WPF for native Windows, Core + CLI first. User-local SDK installation avoids administrator changes.
- ADR-002 accepted: schema-versioned JSON, atomic replace/backups, locking; reject unknown future schemas.
- ADR-003 accepted: root ownership requires PID, start time, executable, exact user-data arg; descendants cannot be killed by executable name. Stale runtime files are hints only.
- ADR-004 experimental: Desktop 2.12.2 needs independent process home AND user-data. Old IDE flags alone are not adequate. Local dynamic probe observed separate roots/backends/state directories. Auth remains manual. No new Windows user or token manipulation.
- ADR-005 accepted: shortcuts invoke CLI with immutable UUID; no duplicated vendor launch construction.
- ADR-006 accepted: workspace stored as metadata until a supported Desktop launch mechanism is verified. Never pretend an ignored positional argument works. GUI must communicate this limitation.
- ADR-007 accepted: remove unregisters; no purge for MVP. No import/copy of existing identities.
- ADR-008 accepted: omit arbitrary launch arguments initially; they could override isolation or leak secrets. Log allowlisted templates only; discard vendor output.
- ADR-009 accepted: experimental delivery can continue before human OAuth testing, but full functional acceptance cannot be marked complete. Agent Safe worktrees, tray, CLI identity are later work.
- ADR-010 accepted: manual validation on Desktop 2.12.2 showed concurrent profiles resolving to the Google account signed in later. The Hub preserves profile data, makes no credential changes, and blocks a second Antigravity root instead of claiming separate-account support. Real concurrent multi-account support requires an official profile-scoped authentication contract or a separate Windows identity boundary.
- ADR-011 accepted: GitHub and installed-app review found no supported same-Windows-user override for Desktop 2.12.2's fixed credential identity. Directory-only launchers, token switchers and standard Sandboxie are not authentication backends. Do not add a backend schema or relax DirectSerial gates before an external OS-user proof proves interactive windows, owner/session recovery, OAuth routing and real A/B account isolation; see `GITHUB_SOLUTION_RESEARCH.md`.
