# Security model

Hub manages local app processes, paths and isolation-box configuration; the official app/OAuth flow performs login. Never read/copy/export tokens, cookies, passwords, auth databases, Credential Manager secret contents, or browser session data. Never patch binaries or quotas, proxy authentication, or impersonate Google identities.

Profiles are not security boundaries against the same Windows user. The shipped mechanism keeps each instance's OAuth token in a per-Profile file and refuses to trust a launch until the language server's log proves it, and it passed concurrent A/B acceptance, but it does not isolate Windows users, the network identity, installed binaries, or every possible OS resource. Revalidate after Antigravity changes version.

The Hub never reads, copies, writes, or deletes the Windows user's credential entry. Instead of claiming the vendor target, each instance is steered to its own file token store, and a launch that cannot prove the switch is stopped. Existing unregistered scopes require explicit fresh-scope acknowledgement, and missing scopes fail closed.

Use immutable UUIDs for directory names. Canonicalize managed paths; reject traversal and reparse components before writes. Isolation box names accept only bounded ASCII letters/digits/underscore and are unique per Profile. Workspace is external metadata, never a cleanup target. Remove only unregisters. Atomic writes, backups and file locks protect config and isolation mappings. No profile export/import or sandbox deletion in this release.

Direct stop validates live process ownership and birth timestamp. Never kill by name or trust persisted PID. Isolated stop targets only the Profile's uniquely bound box and verifies it reaches zero processes. It is a terminating operation rather than a graceful document-save protocol; users must save work first. Probe cleans only its random owned temporary root after stopping owned children; retain data if cleanup safety checks fail.

`OpenFilePath` grants direct host writes and therefore requires explicit elevation. Validate every path before changing rules, cap the rule count, verify each stored value, and restore the previous rule set if configuration fails. A missing rule means project/Profile writes may exist only in the sandbox container; Doctor must report this separately from account-isolation readiness.

Hub logs only allowlisted operation metadata. Vendor stdout/stderr is drained and discarded; vendor logs stay within its own profile data and are never copied into Hub logs. Child command lines may contain CSRF/bridge tokens; inspect only in memory where required and never persist them. Disallow arbitrary arguments that could alter isolation or introduce credentials.
