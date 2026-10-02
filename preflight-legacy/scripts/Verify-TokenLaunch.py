# -*- coding: utf-8 -*-
"""
验证：用 CreateProcessWithTokenW 在新登录会话中启动 Electron 桌面，能否逃脱
宿主 Job 对象（禁止 breakaway 的那个），同时保留我们注入的合成环境。

这是产品级修复的验证。仅观察，不改任何东西。
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import os
import sys
import time
from pathlib import Path

adv = ctypes.WinDLL("advapi32", use_last_error=True)
k32 = ctypes.WinDLL("kernel32", use_last_error=True)

adv.OpenProcessToken.restype = wt.BOOL
adv.OpenProcessToken.argtypes = [wt.HANDLE, wt.DWORD, ctypes.POINTER(wt.HANDLE)]
adv.DuplicateTokenEx.restype = wt.BOOL
adv.DuplicateTokenEx.argtypes = [wt.HANDLE, wt.DWORD, ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.POINTER(wt.HANDLE)]
adv.CreateProcessWithTokenW.restype = wt.BOOL
adv.CreateProcessWithTokenW.argtypes = [wt.HANDLE, wt.DWORD, wt.LPWSTR, wt.LPWSTR, wt.DWORD,
                                        ctypes.c_void_p, wt.LPWSTR, ctypes.c_void_p, ctypes.c_void_p]
k32.GetCurrentProcess.restype = wt.HANDLE

TOKEN_DUPLICATE = 0x0002
TOKEN_QUERY = 0x0008
MAXIMUM_ALLOWED = 0x02000000
SecurityImpersonation = 2
TokenPrimary = 1
LOGON_WITH_PROFILE = 0x00000001
CREATE_UNICODE_ENVIRONMENT = 0x00000400
STARTF_USESHOWWINDOW = 0x00000001
SW_HIDE = 0


class STARTUPINFOW(ctypes.Structure):
    _fields_ = [
        ("cb", wt.DWORD), ("lpReserved", wt.LPWSTR), ("lpDesktop", wt.LPWSTR),
        ("lpTitle", wt.LPWSTR), ("dwX", wt.DWORD), ("dwY", wt.DWORD),
        ("dwXSize", wt.DWORD), ("dwYSize", wt.DWORD), ("dwXCountChars", wt.DWORD),
        ("dwYCountChars", wt.DWORD), ("dwFillAttribute", wt.DWORD), ("dwFlags", wt.DWORD),
        ("wShowWindow", wt.WORD), ("cbReserved2", wt.WORD), ("lpReserved2", ctypes.c_void_p),
        ("hStdInput", wt.HANDLE), ("hStdOutput", wt.HANDLE), ("hStdError", wt.HANDLE),
    ]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [("hProcess", wt.HANDLE), ("hThread", wt.HANDLE),
                ("dwProcessId", wt.DWORD), ("dwThreadId", wt.DWORD)]


def env_block(env: dict) -> bytes:
    text = "".join(f"{k}={v}\0" for k, v in env.items()) + "\0"
    return text.encode("utf-16-le")


def launch_with_token(exe: str, args: str, env: dict, cwd: str) -> int:
    token = wt.HANDLE()
    if not adv.OpenProcessToken(k32.GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY, ctypes.byref(token)):
        raise OSError("OpenProcessToken failed: %d" % ctypes.get_last_error())
    dup = wt.HANDLE()
    if not adv.DuplicateTokenEx(token, MAXIMUM_ALLOWED, None, SecurityImpersonation, TokenPrimary, ctypes.byref(dup)):
        raise OSError("DuplicateTokenEx failed: %d" % ctypes.get_last_error())

    si = STARTUPINFOW()
    si.cb = ctypes.sizeof(STARTUPINFOW)
    si.dwFlags = STARTF_USESHOWWINDOW
    si.wShowWindow = SW_HIDE
    pi = PROCESS_INFORMATION()
    block = env_block(env)
    ok = adv.CreateProcessWithTokenW(dup, LOGON_WITH_PROFILE, exe, args,
                                     CREATE_UNICODE_ENVIRONMENT, block, cwd,
                                     ctypes.byref(si), ctypes.byref(pi))
    k32.CloseHandle(dup)
    k32.CloseHandle(token)
    if not ok:
        raise OSError("CreateProcessWithTokenW failed: %d" % ctypes.get_last_error())
    k32.CloseHandle(pi.hThread)
    return int(pi.dwProcessId)


def main() -> int:
    exe = str(Path(os.environ["LOCALAPPDATA"]) / "Programs" / "antigravity" / "Antigravity.exe")
    root = Path(os.environ["TEMP"]) / ("agph-tok-" + time.strftime("%H%M%S"))
    home = root / "home"
    ud = root / "user-data"
    for d in (home, home / ".gemini", ud):
        d.mkdir(parents=True, exist_ok=True)

    env = {k: os.environ[k] for k in ("SystemRoot", "windir", "TEMP", "TMP", "PATH") if k in os.environ}
    env.update({
        "USERPROFILE": str(home), "HOME": str(home),
        "HOMEDRIVE": home.drive, "HOMEPATH": str(home)[2:],
        "APPDATA": str(home / "AppData" / "Roaming"),
        "LOCALAPPDATA": str(home / "AppData" / "Local"),
        "TEMP": str(root / "temp"), "TMP": str(root / "temp"),
        "WSL_DISTRO_NAME": "AGPH-Token",
    })

    pid = launch_with_token(exe, f'--user-data-dir="{ud}"', env, str(root))
    print("CreateProcessWithTokenW 启动 pid =", pid)
    logs = []
    for i in range(30):
        time.sleep(1)
        logs = list(ud.glob("logs/language_server.log"))
        alive = k32.WaitForSingleObject
        if i % 5 == 4 or (logs and logs[0].stat().st_size > 200):
            print("  t=%2ds  LS日志=%s" % (i + 1, ("有(%dB)" % logs[0].stat().st_size) if logs else "无"))
        if logs and logs[0].stat().st_size > 200:
            text = logs[0].read_text(encoding="utf-8", errors="replace")
            print("✅ bypass 文案:", "Using file-based token storage" in text)
            for line in text.splitlines()[:8]:
                print("     ", line.strip()[:170])
            break
    else:
        print("30 秒内 LS 日志未出现")
    print("scratch =", root)
    return 0


if __name__ == "__main__":
    sys.exit(main())
