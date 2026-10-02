#!/usr/bin/env python3
"""Read-only credential visibility probe using synthetic test targets only.

Never touches real credentials. Targets are always of the form
    AGProfileHub.Probe.<nonce>
and are created and removed by this tool itself.

Commands:
    write  <target>   create the synthetic target with a non-secret marker
    read   <target>   report whether the target is visible here, and blob length
    delete <target>   remove the synthetic target created by this tool

The credential blob is never printed.
"""
import ctypes
import sys
from ctypes import wintypes

PREFIX = "AGProfileHub.Probe."
LOG_PATH = r"D:\mutigravity\artifacts\cred-probe-log.txt"


def emit(message: str) -> None:
    """Print and append to a host-visible log.

    Callers launched inside a sandbox cannot have their stdout captured in this
    environment, and sandboxed writes are redirected, so host-side results are
    recorded to a file that the operator reads afterwards.
    """
    print(message)
    try:
        with open(LOG_PATH, "a", encoding="utf-8") as handle:
            handle.write(message + "\n")
    except OSError as exc:  # pragma: no cover - logging must not break the probe
        print(f"log-failed: {exc}")


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


class CREDENTIALW(ctypes.Structure):
    """Layout used for CredWriteW (blob passed inline)."""
    _fields_ = [
        ("Flags", wintypes.DWORD),
        ("Type", wintypes.DWORD),
        ("TargetName", wintypes.LPWSTR),
        ("Comment", wintypes.LPWSTR),
        ("LastWritten", FILETIME),
        ("CredentialBlobSize", wintypes.DWORD),
        ("CredentialBlob", ctypes.POINTER(ctypes.c_char)),
        ("Persist", wintypes.DWORD),
        ("AttributeCount", wintypes.DWORD),
        ("Attributes", ctypes.c_void_p),
        ("TargetAlias", wintypes.LPWSTR),
        ("UserName", wintypes.LPWSTR),
    ]


CRED_PERSIST_LOCAL_MACHINE = 2
CRED_TYPE_GENERIC = 1


def _api():
    adv = ctypes.WinDLL("advapi32", use_last_error=True)
    return adv


def do_write(target: str, length: int = 17) -> int:
    adv = _api()
    cred_write = adv.CredWriteW
    cred_write.argtypes = [ctypes.POINTER(CREDENTIALW), wintypes.DWORD]
    cred_write.restype = wintypes.BOOL

    # The payload is a repeating non-secret marker; only its LENGTH is used to
    # tell two storage scopes apart (host copy vs sandboxed copy).
    if length < 4 or length > 512:
        print("length out of range")
        return 2
    marker = (b"#" * length)
    buf = ctypes.create_string_buffer(marker)
    cred = CREDENTIALW()
    cred.Type = CRED_TYPE_GENERIC
    cred.TargetName = target
    cred.UserName = "agph-probe"
    cred.CredentialBlobSize = len(marker)
    cred.CredentialBlob = ctypes.cast(buf, ctypes.POINTER(ctypes.c_char))
    cred.Persist = CRED_PERSIST_LOCAL_MACHINE
    ok = cred_write(ctypes.byref(cred), 0)
    if not ok:
        emit(f"write-failed last_error={ctypes.get_last_error()}")
        return 1
    emit(f"write-ok target={target}")
    return 0


def do_read(target: str) -> int:
    adv = _api()
    cred_read = adv.CredReadW
    cred_read.argtypes = [
        wintypes.LPCWSTR,
        wintypes.DWORD,
        wintypes.DWORD,
        ctypes.POINTER(ctypes.POINTER(CREDENTIAL)),
    ]
    cred_read.restype = wintypes.BOOL
    cred_free = adv.CredFree
    cred_free.argtypes = [ctypes.c_void_p]
    cred_free.restype = None

    handle = ctypes.POINTER(CREDENTIAL)()
    ok = cred_read(target, CRED_TYPE_GENERIC, 0, ctypes.byref(handle))
    if not ok:
        err = ctypes.get_last_error()
        # 1168 ERROR_NOT_FOUND is the expected "not visible here" result.
        # Exit code 3 communicates invisibility to callers that cannot capture
        # sandboxed stdout.
        emit(f"VISIBLE=no target={target} last_error={err}")
        return 3
    try:
        cred = handle.contents
        emit(f"VISIBLE=yes target={target} blobSize={cred.CredentialBlobSize}")
    finally:
        cred_free(handle)
    return 0


def do_delete(target: str) -> int:
    adv = _api()
    cred_delete = adv.CredDeleteW
    cred_delete.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD]
    cred_delete.restype = wintypes.BOOL
    ok = cred_delete(target, CRED_TYPE_GENERIC, 0)
    emit(f"delete {'ok' if ok else 'failed'} target={target}")
    return 0 if ok else 1


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    command, target = sys.argv[1], sys.argv[2]
    if not target.startswith(PREFIX):
        print(f"refusing to touch non-probe target: {target}")
        return 2
    if command == "write":
        length = 17
        if len(sys.argv) >= 4:
            try:
                length = int(sys.argv[3])
            except ValueError:
                print("length must be an integer")
                return 2
        return do_write(target, length)
    if command == "read":
        return do_read(target)
    if command == "delete":
        return do_delete(target)
    print(f"unknown command {command}")
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
