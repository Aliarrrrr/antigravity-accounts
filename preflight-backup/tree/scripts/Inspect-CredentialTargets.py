#!/usr/bin/env python3
"""Enumerate Windows Credential Manager target names (metadata only).

Read-only forensics. This prints target name, type, blob size and last-written
timestamp. It NEVER reads, prints, hashes or stores the credential blob, so no
secret material is extracted.

Usage:
    python Inspect-CredentialTargets.py [--filter <substring>] [--all]
"""
import ctypes
import sys
from ctypes import wintypes

CRED_TYPE_GENERIC = 1
CRED_TYPE_DOMAIN_PASSWORD = 2
CRED_TYPE_DOMAIN_CERTIFICATE = 3
CRED_TYPE_DOMAIN_VISIBLE_PASSWORD = 4


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


def filetime_to_str(ft: FILETIME) -> str:
    value = (ft.dwHighDateTime << 32) | ft.dwLowDateTime
    if value == 0:
        return "n/a"
    # FILETIME is 100ns intervals since 1601-01-01.
    import datetime
    try:
        return (datetime.datetime(1601, 1, 1) + datetime.timedelta(microseconds=value / 10)).isoformat(timespec="seconds")
    except OverflowError:
        return "out-of-range"


def main() -> int:
    show_all = "--all" in sys.argv
    needle = ""
    if "--filter" in sys.argv:
        idx = sys.argv.index("--filter")
        if idx + 1 < len(sys.argv):
            needle = sys.argv[idx + 1].lower()

    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    cred_enumerate = advapi32.CredEnumerateW
    cred_enumerate.argtypes = [
        wintypes.LPCWSTR,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
        ctypes.POINTER(ctypes.POINTER(ctypes.POINTER(CREDENTIAL))),
    ]
    cred_enumerate.restype = wintypes.BOOL
    cred_free = advapi32.CredFree
    cred_free.argtypes = [ctypes.c_void_p]
    cred_free.restype = None

    count = wintypes.DWORD()
    array = ctypes.POINTER(ctypes.POINTER(CREDENTIAL))()
    ok = cred_enumerate(None, 0, ctypes.byref(count), ctypes.byref(array))
    if not ok:
        print(f"CredEnumerate failed, last_error={ctypes.get_last_error()}")
        return 1

    print(f"total={count.value}")
    matched = 0
    shown = 0
    try:
        for i in range(count.value):
            cred = array[i].contents
            target = cred.TargetName or ""
            user = cred.UserName or ""
            if needle and needle not in target.lower() and needle not in user.lower():
                continue
            matched += 1
            if not show_all and shown >= 40:
                continue
            shown += 1
            print(
                f"target={target!r} user={user!r} type={cred.Type} "
                f"blobSize={cred.CredentialBlobSize} written={filetime_to_str(cred.LastWritten)}"
            )
    finally:
        cred_free(array)
    print(f"matched={matched}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
