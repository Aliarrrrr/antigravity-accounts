# -*- coding: utf-8 -*-
"""
读取任意同用户进程的环境块（经 PEB），用于判定一个运行中的 Antigravity / language_server
实例究竟继承了谁的环境 —— 即它是否拿到了我们注入的旁路变量与合成 home。

为什么需要它：
  `--user-data-dir` 只出现在命令行里，而 **USERPROFILE / WSL_DISTRO_NAME 只在环境块里**。
  一个实例是否"干净"取决于环境，不能从命令行推断。

实现：NtQueryInformationProcess(ProcessBasicInformation) -> PebBaseAddress
      PEB + 0x20 (x64) -> RTL_USER_PROCESS_PARAMETERS
      RTL_USER_PROCESS_PARAMETERS + 0x80 (x64) -> Environment (UTF-16, 双 NUL 结尾)
  只读，不修改目标进程，不写任何东西。

用法:
  python Probe-ProcessEnvironment.py --pid 27400
  python Probe-ProcessEnvironment.py --pid 27400 --keys USERPROFILE,WSL_DISTRO_NAME,APPDATA
  python Probe-ProcessEnvironment.py --all-antigravity
"""
from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import json
import sys
from pathlib import Path

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
ntdll = ctypes.WinDLL("ntdll", use_last_error=True)

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
STATUS_SUCCESS = 0

# x64 offsets
PEB_PROCESS_PARAMETERS_OFFSET = 0x20
RTL_PARAMETERS_ENVIRONMENT_OFFSET = 0x80


class PROCESS_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("Reserved1", ctypes.c_void_p),
        ("PebBaseAddress", ctypes.c_void_p),
        ("Reserved2", ctypes.c_void_p * 2),
        ("UniqueProcessId", ctypes.c_void_p),
        ("Reserved3", ctypes.c_void_p),
    ]


k32.OpenProcess.restype = wt.HANDLE
k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
k32.ReadProcessMemory.restype = wt.BOOL
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                  ctypes.POINTER(ctypes.c_size_t)]
k32.CloseHandle.argtypes = [wt.HANDLE]
ntdll.NtQueryInformationProcess.restype = ctypes.c_long
ntdll.NtQueryInformationProcess.argtypes = [wt.HANDLE, ctypes.c_int, ctypes.c_void_p, ctypes.c_ulong,
                                            ctypes.POINTER(ctypes.c_ulong)]


def read_memory(handle: wt.HANDLE, address: int, size: int) -> bytes:
    buffer = ctypes.create_string_buffer(size)
    read = ctypes.c_size_t(0)
    ok = k32.ReadProcessMemory(handle, ctypes.c_void_p(address), buffer, size, ctypes.byref(read))
    if not ok:
        raise OSError("ReadProcessMemory failed at 0x%X (err=%d)" % (address, ctypes.get_last_error()))
    return buffer.raw[: read.value]


def read_pointer(handle: wt.HANDLE, address: int) -> int:
    return int.from_bytes(read_memory(handle, address, 8), "little")


def read_environment(pid: int) -> dict:
    handle = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not handle:
        raise OSError("OpenProcess(%d) failed (err=%d)" % (pid, ctypes.get_last_error()))
    try:
        pbi = PROCESS_BASIC_INFORMATION()
        returned = ctypes.c_ulong(0)
        status = ntdll.NtQueryInformationProcess(handle, 0, ctypes.byref(pbi),
                                                 ctypes.sizeof(pbi), ctypes.byref(returned))
        if status != STATUS_SUCCESS:
            raise OSError("NtQueryInformationProcess failed (status=0x%X)" % (status & 0xFFFFFFFF))
        peb = int(pbi.PebBaseAddress or 0)
        parameters = read_pointer(handle, peb + PEB_PROCESS_PARAMETERS_OFFSET)
        env_ptr = read_pointer(handle, parameters + RTL_PARAMETERS_ENVIRONMENT_OFFSET)

        env: dict[str, str] = {}
        chunk_size = 0x8000
        offset = 0
        done = False
        while offset < 0x200000 and not done:
            raw = read_memory(handle, env_ptr + offset, chunk_size)
            text = raw.decode("utf-16-le", "replace")
            # 环境块以空字符串结尾（双 NUL）；分块时保留最后一个不完整元素
            parts = text.split("\x00")
            for part in parts:
                if part == "":
                    done = True
                    break
                if "=" in part:
                    key, _, value = part.partition("=")
                    env[key] = value
            offset += chunk_size
        return {
            "pid": pid,
            "peb": hex(peb),
            "processParameters": hex(parameters),
            "environment": env,
        }
    finally:
        k32.CloseHandle(handle)


def antigravity_pids() -> list[int]:
    import csv
    import io
    import subprocess
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq Antigravity.exe", "/FO", "CSV", "/NH"],
                         capture_output=True).stdout.decode("gbk", "replace")
    pids = []
    for row in csv.reader(io.StringIO(out)):
        if len(row) >= 2 and row[1].strip().isdigit():
            pids.append(int(row[1].strip()))
    return pids


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--pid", type=int)
    ap.add_argument("--all-antigravity", action="store_true")
    ap.add_argument("--keys", default="USERPROFILE,HOME,APPDATA,LOCALAPPDATA,TEMP,WSL_DISTRO_NAME,SSH_CONNECTION")
    ap.add_argument("--report")
    ns = ap.parse_args()

    keys = [k.strip() for k in ns.keys.split(",") if k.strip()]
    targets = antigravity_pids() if ns.all_antigravity else ([ns.pid] if ns.pid else [])
    if not targets:
        print("no target pid; pass --pid or --all-antigravity")
        return 2

    results = []
    for pid in targets:
        try:
            info = read_environment(pid)
            subset = {k: info["environment"].get(k) for k in keys}
            results.append({"pid": pid, "ok": True, "env": subset, "envCount": len(info["environment"])})
            print("pid=%d envCount=%d" % (pid, len(info["environment"])))
            for k in keys:
                print("    %-20s %s" % (k, subset.get(k)))
        except Exception as exc:  # noqa: BLE001
            results.append({"pid": pid, "ok": False, "error": str(exc)})
            print("pid=%d ERROR %s" % (pid, exc))
        print("-" * 70)

    if ns.report:
        Path(ns.report).write_text(json.dumps(results, indent=2, ensure_ascii=False), encoding="utf-8")
        print("report = %s" % ns.report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
