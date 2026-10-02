#!/usr/bin/env python3
"""Write a marker file from inside an isolation scope.

Used to determine whether edits made inside a sandbox land on the real disk or
only in the sandbox copy.
"""
import sys

if len(sys.argv) < 3:
    print("usage: Write-ProbeFile.py <path> <text>", file=sys.stderr)
    raise SystemExit(2)

path = sys.argv[1]
text = sys.argv[2]
try:
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)
    print(f"written path={path}")
except OSError as exc:
    print(f"write-failed path={path} error={exc}")
    raise SystemExit(1)
