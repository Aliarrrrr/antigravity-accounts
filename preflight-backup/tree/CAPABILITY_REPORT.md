# Capability report — 2026-09-09

## Verdict

Antigravity Desktop 2.12.2 can run concurrent Google-account Profiles under one Windows user when each instance uses both dedicated filesystem paths and a dedicated Sandboxie box whose local credential store already owns the fixed target `gemini:antigravity`. This exact combination passed manual A/B authentication and restart acceptance with Sandboxie Plus 1.18.2 / core 5.73.2.

Directory-only launch remains incompatible with concurrent accounts and is kept as a serial fallback. The result is version-bound and does not make a Sandboxie box a security boundary between Windows users.

## Evidence chain

- Static and dynamic app inspection established that Desktop 2.12.2 resolves `.gemini` through process home, Electron state through `--user-data-dir`, and launches a separate language server with dynamic local ports.
- Disposable dual-process probes observed independent roots, language servers, home/user-data trees, Host Bridge ports, and visible windows.
- The original authenticated A/B run failed despite those independent paths: both windows resolved to the later signed-in account.
- Metadata-only Credential Manager inspection identified one generic entry with the hardcoded target `gemini:antigravity`. No token bytes were exported or logged.
- Sandboxie testing proved asymmetric credential behavior: boxed writes are isolated, but a missing boxed read falls through to the host entry. The earlier synthetic test was a false positive because its host write had not been positively established.
- A fresh box-local same-name invalid placeholder stopped that fall-through. The vendor then used its own isolated login storage behavior.
- Final manual acceptance observed distinct accounts in two concurrent official windows, persistence after restarting A, no PID interruption in B, and unchanged host credential metadata.

The release-safe root-cause and acceptance evidence is preserved in `KEYRING_ISOLATION_REPORT.md` and `TAKEOVER_REPORT.md`. Raw forensic captures remain local-only and are intentionally excluded from the product repository.

## Current capability matrix

| Capability | Current result |
|---|---|
| Executable/version discovery | verified for installed Desktop 2.12.2 |
| Independent root/backend processes | verified |
| Independent home/user-data | verified |
| Per-box keyring ownership | verified on tested Sandboxie version |
| Distinct concurrent Google accounts | passed manual acceptance |
| Restart one Profile without changing the other | passed manual acceptance |
| Host login left unchanged | passed metadata/hash comparison; no token exported |
| Normal CLI/GUI/shortcut isolation routing | implemented; deterministic regression coverage |
| Duplicate isolated launch | returns existing box state instead of launching another root |
| Mixed launch-all | fail-closed preflight; no partial start |
| Direct-write configuration detection | implemented against actual `OpenFilePath.N` values |
| Workspace host write-through | pending manual acceptance after current rules are corrected |
| Logout non-interference | pending manual acceptance |
| Workspace launch forwarding | unsupported by verified vendor contract; metadata only |
| App or Sandboxie version changes | must be re-probed/re-accepted |
| Standalone CLI/headless identity | not investigated |

## Safety properties

- Google login remains inside the official app.
- The Hub does not collect passwords, token contents, cookies, OAuth codes, or authentication logs.
- The same-name placeholder is invalid data, written only inside a process confirmed to have Sandboxie's injected `SbieDll.dll`.
- Existing unregistered boxes are not claimed unless the caller explicitly passes `--fresh`; this acknowledgement is only valid for a never-signed-in box.
- Missing boxes, unclaimed keyrings, duplicate mappings, corrupt/future isolation state, and unexpected shim exit codes fail closed.
- Profile/workspace direct-write rules require explicit elevation and are reported separately from account-isolation success.

## Current local observation

The two accepted boxes each contain 11 processes and report backend, box, keyring and direct-write readiness. The remaining workspace gate is behavioral: create a harmless file from inside a boxed workspace, confirm it appears on the host, and confirm it survives restart. This is a release-quality acceptance step, not a reversal of the account-isolation result.
