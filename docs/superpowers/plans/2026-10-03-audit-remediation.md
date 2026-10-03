# Audit remediation implementation plan

Approved scope: commit 442c753; official WPF/CLI/Core/build/release only. Keep experimental/manualAcceptance=false, existing tags, user data, and Rust source unchanged. Developer scripts require PowerShell 7; shipped runtime keeps Windows built-in PowerShell support.

1. Delivery: UTF-8 BOM entry scripts; exact SDK 10.0.400; per-project win-x64 self-contained locks; compare application graphs; provenance manifest; pinned Actions/Inno; readonly validation plus isolated release publisher; package/installer checks in PR CI; numeric installer version from published GUI.
2. Protocol: async record operations, injected command store, shared per-user protocol file lock, guarded registration/restoration and noncancelable transaction completion after mutation begins. Preserve CLI JSON/arguments. Missing/foreign ownership safely skips restore; genuine restore errors retain evidence and abort uninstall.
3. Lifecycle: Profile -> login -> protocol lock order; atomic BeginLogin; injected waiter store/handoff launcher while retaining real proof/hash/file locks; handle-based creation identity; callback failure retains waiter and never kills an unverified PID.
4. Boundaries/runner: exact schema/error/byte preservation, negative reparse/junction tests, empty/invalid waiter tests, grouped Next/shared/Legacy results and opt-in desktop integration.
5. Recovery/diagnostics/docs: only successful owned probe scratch cleanup after proof persistence and checked path/link boundaries; preserve failed probes; smoke timeout process cleanup; stop/rollback/screenshot fixed diagnostics; shared OZONE removal; obsolete shortcut removal; accurate coverage/security docs.

Acceptance: locked default and system suites pass; malformed graphs/locks fail publication; complete package/installer validation on a clean Windows runner; cancellation/concurrency/foreign ownership/URI privacy boundaries verified. Local installer verification must refuse an environment containing real profiles.

Interfaces: NextProtocol remains a static facade with RegisterAsync(paths, cli, vendorExecutable, token), IsRegisteredForAsync(paths, token), RestoreAsync(paths, token), and internal overloads accepting IProtocolCommandStore and a protocol lock path. Internal IProtocolCommandStore has Read/Write/Delete. NextHubService's public constructor stays compatible; an internal constructor injects protocol commands/lock, callback launcher/waiters and test deadline.
