#!/usr/bin/env python3
"""Directly rewrite the OpenFilePath rules in the Sandboxie configuration.

The SbieIni.exe tool silently fails to persist changes in this environment, so
this script edits C:\\Windows\\Sandboxie.ini as text (UTF-16 with BOM, matching
the existing file) and must run elevated.

For each managed box section it drops the stale OpenFilePath entries and writes
one direct-write rule per current profile directory plus the vendor install
directory (read-only usage, opened for compatibility).
"""
import re
import sys
from pathlib import Path

INI = Path(r"C:\Windows\Sandboxie.ini")
LOG = Path(r"D:\mutigravity\artifacts\writethrough-direct-log.txt")

PROFILE_ROOT = r"C:\Users\ZLF\AppData\Local\AntigravityProfileHub\profiles"
VENDOR_DIR = r"C:\Users\ZLF\AppData\Local\Programs\antigravity"
BOXES = {
    "AGPH_5a77cba706854d72b08bf4239aaf515d": "63fa5cb4-b92a-4aad-bd67-1b99cd20a284",
    "AGPH_e7f7a0da251e4ef8b34f39689b051163": "ba20e6f7-b7c5-471d-a986-d5d267ec5fd2",
}
DIRECTORIES = ["home", "roaming", "local", "temp", "user-data"]


def main() -> int:
    raw = INI.read_bytes()
    # Preserve the existing encoding: UTF-16 LE with BOM.
    if raw.startswith(b"\xff\xfe"):
        encoding = "utf-16"
    elif raw.startswith(b"\xef\xbb\xbf"):
        encoding = "utf-8-sig"
    else:
        encoding = "utf-8"
    text = raw.decode(encoding)
    lines = []
    log = [f"editing {INI} as {encoding}"]

    current_box = None
    box_seen = set()
    for line in text.splitlines():
        header = re.match(r"^\[([^\]]+)\]\s*$", line.strip())
        if header:
            current_box = header.group(1)
            if current_box in BOXES:
                box_seen.add(current_box)
                lines.append(line)
                # Sandboxie's parser only accepts repeated bare keys
                # (OpenFilePath=path). Indexed suffixes like OpenFilePath.1=
                # are silently ignored by the driver, so emit one line per rule.
                base = f"{PROFILE_ROOT}\\{BOXES[current_box]}"
                for name in DIRECTORIES:
                    lines.append(f"OpenFilePath={base}\\{name}")
                lines.append(f"OpenFilePath={VENDOR_DIR}")
                log.append(f"box {current_box}: wrote {len(DIRECTORIES) + 1} rules for profile {BOXES[current_box]}")
                continue
        # Skip every pre-existing OpenFilePath line inside managed boxes.
        if current_box in BOXES and re.match(r"^\s*OpenFilePath\.\d+\s*=", line):
            continue
        lines.append(line)

    missing = [box for box in BOXES if box not in box_seen]
    if missing:
        log.append(f"ABORT: box sections not found: {missing}")
        LOG.write_text("\n".join(log) + "\n", encoding="utf-8")
        print("\n".join(log))
        return 1

    backup = INI.with_suffix(".ini.agph-bak")
    backup.write_bytes(raw)
    log.append(f"backup written: {backup}")

    out = "\r\n".join(lines)
    if text.endswith("\r\n") or text.endswith("\n"):
        out += "\r\n"
    INI.write_bytes(out.encode(encoding))

    # Verify by re-reading.
    check = INI.read_bytes().decode(encoding)
    ok = True
    for box, profile_id in BOXES.items():
        for name in DIRECTORIES:
            expected = f"{PROFILE_ROOT}\\{profile_id}\\{name}"
            if expected not in check:
                ok = False
                log.append(f"verify MISSING {box} {name}")
            else:
                log.append(f"verify OK {box} {name}")
    log.append(f"RESULT={'OK' if ok else 'FAILED'}")
    LOG.write_text("\n".join(log) + "\n", encoding="utf-8")
    print("\n".join(log))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
