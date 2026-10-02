#!/usr/bin/env python3
"""Apply Sandboxie OpenFilePath write-through rules, meant to run elevated.

Called by the host as:  python Apply-WriteThrough.py
Loops over the two current isolation boxes, clears their old OpenFilePath
entries and re-points them at the profile directories that are actually in
use. Writes a result log for the host to verify.
"""
import subprocess
import sys
from pathlib import Path

SBIE_INI = r"C:\Program Files\Sandboxie-Plus\SbieIni.exe"
SYSTEM_INI = r"C:\Windows\Sandboxie.ini"
LOG = r"D:\mutigravity\artifacts\writethrough-apply-log.txt"

PROFILE_ROOT = r"C:\Users\ZLF\AppData\Local\AntigravityProfileHub\profiles"
BOXES = [
    ("AGPH_5a77cba706854d72b08bf4239aaf515d", "63fa5cb4-b92a-4aad-bd67-1b99cd20a284"),
    ("AGPH_e7f7a0da251e4ef8b34f39689b051163", "ba20e6f7-b7c5-471d-a986-d5d267ec5fd2"),
]
DIRECTORIES = ["home", "roaming", "local", "temp", "user-data"]


def run(args: list[str]) -> subprocess.CompletedProcess:
    return subprocess.run([SBIE_INI, *args], capture_output=True, text=True, timeout=30)


def main() -> int:
    lines = [f"elevated run, argv={sys.argv}"]
    for box, profile_id in BOXES:
        # Clear every possible previous slot; failures just mean it was unset.
        for i in range(1, 33):
            run(["del", box, f"OpenFilePath.{i}"])
        base = Path(PROFILE_ROOT) / profile_id
        index = 1
        for name in DIRECTORIES:
            target = str(base / name)
            result = run(["set", box, f"OpenFilePath.{index}", target])
            lines.append(f"set {box} OpenFilePath.{index}={target} exit={result.returncode}")
            index += 1

    # Verify by re-reading the system configuration.
    try:
        content = Path(SYSTEM_INI).read_text(encoding="utf-16")
    except OSError as exc:
        lines.append(f"verify-failed: {exc}")
        content = ""
    for box, profile_id in BOXES:
        for name in DIRECTORIES:
            expected = f"{PROFILE_ROOT}\\{profile_id}\\{name}"
            ok = expected in content
            lines.append(f"verify {box} {name}: {'OK' if ok else 'MISSING'}")

    Path(LOG).write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
