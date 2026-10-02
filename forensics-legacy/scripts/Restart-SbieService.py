#!/usr/bin/env python3
"""Restart the Sandboxie service so it reloads its configuration.

Start.exe /reload did not make the driver pick up the new OpenFilePath rules in
this environment, so the service is restarted instead. Must run elevated.
"""
import subprocess
import time

LOG = r"D:\mutigravity\artifacts\sbie-restart-log.txt"

SERVICE = "SbieSvc"


def run(cmd: list[str]) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, capture_output=True, text=True, timeout=120)


def main() -> int:
    lines = ["restarting Sandboxie service"]
    stopped = run(["net", "stop", SERVICE])
    lines.append(f"net stop exit={stopped.returncode} out={stopped.stdout.strip()[:120]} err={stopped.stderr.strip()[:120]}")
    # Give the service a moment to fully release.
    time.sleep(2)
    started = run(["net", "start", SERVICE])
    lines.append(f"net start exit={started.returncode} out={started.stdout.strip()[:120]} err={started.stderr.strip()[:120]}")

    # Verify via sc query.
    query = run(["sc", "query", SERVICE])
    running = "RUNNING" in query.stdout
    lines.append(f"sc query running={running}")
    lines.append(f"RESULT={'OK' if running else 'FAILED'}")
    with open(LOG, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n")
    print("\n".join(lines))
    return 0 if running else 1


if __name__ == "__main__":
    raise SystemExit(main())
