#!/usr/bin/env python3
"""Report the blob size of a credential entry and signal it via exit code.

Used to prove that an isolation scope owns its own copy of a target rather than
falling through to the host copy. The blob content is never printed.

Usage:
    python Check-KeyringScope.py <target> <expectedSize>

Exit codes:
    0  entry found and its size equals <expectedSize>
    3  entry not found here
    5  entry found but size differs
"""
import ctypes
import sys
from ctypes import wintypes

CRED_TYPE_GENERIC = 1


class FILETIME(ctypes.Structure):
    _fields_ = [("dwLowDateTime", wintypes.DWORD), ("dwHighDateTime", wintypes.DWORD)]


class CREDENTIAL(ctypes.Structure):
    _fields_ = [
        ("Flags", wintypes.DWORD),
        ("Type", wintypes.DWORD),
        ("TargetName", wintypes.LPWSTR),
        ("Comment", wintypes.LPWSTR),
        ("LastWritten", FILETIME),
        ("CredentialBlobSize", wintypes.DWORD),
        ("CredentialBlob", ctypes.POINTER(ctypes.c_byte)),
        ("Persist", wintypes.DWORD),
        ("AttributeCount", wintypes.DWORD),
        ("Attributes", ctypes.c_void_p),
        ("TargetAlias", wintypes.LPWSTR),
        ("UserName", wintypes.LPWSTR),
    ]


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: Check-KeyringScope.py <target> <expectedSize>", file=sys.stderr)
        return 2

    target = sys.argv[1]
    try:
        expected = int(sys.argv[2])
    except ValueError:
        print("expectedSize must be an integer", file=sys.stderr)
        return 2

    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    cred_read = advapi32.CredReadW
    cred_read.argtypes = [
        wintypes.LPCWSTR,
        wintypes.DWORD,
        wintypes.DWORD,
        ctypes.POINTER(ctypes.POINTER(CREDENTIAL)),
    ]
    cred_read.restype = wintypes.BOOL
    cred_free = advapi32.CredFree
    cred_free.argtypes = [ctypes.c_void_p]
    cred_free.restype = None

    handle = ctypes.POINTER(CREDENTIAL)()
    if not cred_read(target, CRED_TYPE_GENERIC, 0, ctypes.byref(handle)):
        print(f"not-found target={target} last_error={ctypes.get_last_error()}")
        return 3
    try:
        size = handle.contents.CredentialBlobSize
        # A one-way digest proves two scopes hold different secrets without
        # ever emitting the secret itself.
        import hashlib
        raw = ctypes.string_at(handle.contents.CredentialBlob, size) if size else b""
        digest = hashlib.sha256(raw).hexdigest()[:16]
        del raw
    finally:
        cred_free(handle)

    line = f"found target={target} blobSize={size} expected={expected} sha256_16={digest}"
    print(line)
    # When run inside a sandbox, stdout cannot be captured from outside; writing
    # the same line lets the host read it from the sandbox's file root.
    if len(sys.argv) >= 4:
        with open(sys.argv[3], "w", encoding="utf-8") as handle:
            handle.write(line + "\n")
    return 0 if size == expected else 5


if __name__ == "__main__":
    raise SystemExit(main())
