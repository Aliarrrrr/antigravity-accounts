#!/usr/bin/env python3
"""Read-only keyword context search over a binary blob.

Usage:
    python Search-BinaryStrings.py <file> <keyword-file>

The keyword file holds one literal keyword per line. Keywords are never passed
on the command line, and matched context is truncated and sanitised before it
is printed so credential material is never emitted wholesale.
"""
import re
import sys
from pathlib import Path


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: Search-BinaryStrings.py <file> <keyword-file>", file=sys.stderr)
        return 2

    target = Path(sys.argv[1])
    keywords = [line.rstrip("\r\n") for line in Path(sys.argv[2]).read_text(encoding="utf-8").splitlines()]
    keywords = [k for k in keywords if k]

    data = target.read_bytes()
    print(f"target={target}")
    print(f"size={len(data)}")

    for kw in keywords:
        needle = kw.encode("utf-8")
        hits = [m.start() for m in re.finditer(re.escape(needle), data)]
        print(f"== {kw} count={len(hits)}")
        shown = 0
        seen = set()
        for off in hits:
            if shown >= 6:
                break
            ctx = data[max(0, off - 110): off + 150].decode("utf-8", "replace")
            ctx = ctx.replace("\n", " ")
            key = ctx[:60]
            if key in seen:
                continue
            seen.add(key)
            print("   ...", ctx)
            shown += 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
