# Security model

Hub manages local app processes, paths and isolation-box configuration; the official app/OAuth flow performs login. Never read/copy/export tokens, cookies, passwords, auth databases, Credential Manager secret contents, or browser session data. Never patch binaries or quotas, proxy authentication, or impersonate Google identities.

Profiles are not security boundaries against the same Windows user. Synthetic home changes path resolution only. The tested Sandboxie/keyring mechanism prevents the specific `gemini:antigravity` collision and passed concurrent A/B acceptance, but it does not isolate Windows users, the network identity, installed binaries, or every possible OS resource. Revalidate after either dependency changes.

The keyring shim writes one deliberately invalid payload to the vendor target only inside a fresh Sandboxie scope. Core checks for injected `SbieDll.dll` before the write; direct host invocation fails. Never run `--fresh` against a box that has completed login. Existing unregistered boxes require explicit fresh-box acknowledgement, shim exit codes are checked exactly, and missing/unclaimed boxes fail closed. Tests may create/delete only targets under `AGProfileHub.Probe.`.

Use immutable UUIDs for directory names. Canonicalize managed paths; reject traversal and reparse components before writes. Isolation box names accept only bounded ASCII letters/digits/underscore and are unique per Profile. Workspace is external metadata, never a cleanup target. Remove only unregisters. Atomic writes, backups and file locks protect config and isolation mappings. No profile export/import or sandbox deletion in this release.

Direct stop validates live process ownership and birth timestamp. Never kill by name or trust persisted PID. Isolated stop targets only the Profile's uniquely bound box and verifies it reaches zero processes. It is a terminating operation rather than a graceful document-save protocol; users must save work first. Probe cleans only its random owned temporary root after stopping owned children; retain data if cleanup safety checks fail.

`OpenFilePath` grants direct host writes and therefore requires explicit elevation. Validate every path before changing rules, cap the rule count, verify each stored value, and restore the previous rule set if configuration fails. A missing rule means project/Profile writes may exist only in the sandbox container; Doctor must report this separately from account-isolation readiness.

Hub logs only allowlisted operation metadata. Vendor stdout/stderr is drained and discarded; vendor logs stay within its own profile data and are never copied into Hub logs. Child command lines may contain CSRF/bridge tokens; inspect only in memory where required and never persist them. Disallow arbitrary arguments that could alter isolation or introduce credentials.
