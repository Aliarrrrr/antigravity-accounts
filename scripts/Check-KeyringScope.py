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
    finally:
        cred_free(handle)

    print(f"found target={target} blobSize={size} expected={expected}")
    return 0 if size == expected else 5


if __name__ == "__main__":
    raise SystemExit(main())
