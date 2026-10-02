#!/usr/bin/env python3
"""Ask SbieSvc (via SbieIni.exe query) what configuration it actually serves.

Run elevated. Compares a setting known to be old (Enabled), one known stale
(OpenFilePath.6 pointing at the vendor dir from the acceptance setup) and the
newly written rules, so we can tell whether the service cache is behind.
"""
import subprocess
from pathlib import Path

SBIE_INI = r"C:\Program Files\Sandboxie-Plus\SbieIni.exe"
LOG = Path(r"D:\mutigravity\artifacts\sbie-query-diag.txt")

BOX = "AGPH_5a77cba706854d72b08bf4239aaf515d"
QUERIES = ["Enabled", "OpenFilePath.1", "OpenFilePath.5", "OpenFilePath.6", "ConfigLevel", "BoxNameTitle"]


def main() -> int:
    lines = []
    for key in QUERIES:
        result = subprocess.run([SBIE_INI, "query", BOX, key], capture_output=True, text=True, timeout=30)
        stdout = result.stdout.strip()
        stderr = result.stderr.strip()
        lines.append(f"query {key}: exit={result.returncode} out={stdout!r} err={stderr!r}")

    # Also try the non-indexed form, which returns all values of the setting.
    result = subprocess.run([SBIE_INI, "query", BOX, "OpenFilePath"], capture_output=True, text=True, timeout=30)
    lines.append(f"query OpenFilePath (all): exit={result.returncode} out={result.stdout.strip()!r}")

    Path(LOG).write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
