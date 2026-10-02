# -*- coding: utf-8 -*-
"""
D2-前置探针：验证**真实 Desktop 路径**里 Electron 是否把它自己的环境块原样传给 LS。

为什么需要它：
  D1 是「我们直接拉起 LS」证明环境变量能触发厂商的 file-based token storage。
  但生产路径是 Electron(Desktop) 拉起 LS。若 Electron 过滤了环境，注入在生产路径上就失效。

判据（不需要登录，因此不碰任何凭据）：
  LS 自己会在 %USERPROFILE%\.gemini 下写运行期产物（migration marker、installation_id、
  antigravity_state.pbtxt 等）。若这些文件出现在我们合成的 home 里，说明 LS 确实继承了
  我们的 USERPROFILE —— 环境透传成立。
  同时观察 language_server.exe 是否真的被 Electron 拉起。

红线：
  * 不传入任何凭据相关变量，不读/写宿主 keyring 条目。
  * 只做宿主凭据库目录的只读元数据快照（计数/总字节/最新写入时间）。
  * 只终止「本次新出现」的进程；运行前若已有 Antigravity/LS 在跑，直接放弃（避免误杀用户的实例）。

用法: python Probe-DesktopEnvPropagation.py [--observe-seconds 60] [--report <path>]
"""
from __future__ import annotations

import argparse
import csv
import io
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

EXE_DEFAULT = Path(os.environ.get("LOCALAPPDATA", "")) / "Programs" / "antigravity" / "Antigravity.exe"
CRED_STORE = Path(os.environ.get("LOCALAPPDATA", "")) / "Microsoft" / "Credentials"
PROBE_ENV_MARKER = "WSL_DISTRO_NAME"


def tasklist_pids(image_name: str) -> list[int]:
    r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq %s" % image_name, "/FO", "CSV", "/NH"],
                       capture_output=True)
    text = r.stdout.decode("gbk", "replace")
    pids: list[int] = []
    for row in csv.reader(io.StringIO(text)):
        if len(row) >= 2 and row[1].strip().isdigit():
            pids.append(int(row[1].strip()))
    return pids


def kill_pids(pids: list[int]) -> None:
    for pid in pids:
        try:
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True, timeout=30)
        except Exception:
            pass


def cred_fingerprint() -> dict:
    if not CRED_STORE.is_dir():
        return {"exists": False}
    files = [p for p in CRED_STORE.iterdir() if p.is_file()]
    total = 0
    newest = None
    for p in files:
        try:
            st = p.stat()
        except OSError:
            continue
        total += st.st_size
        if newest is None or st.st_mtime > newest:
            newest = st.st_mtime
    return {
        "exists": True,
        "fileCount": len(files),
        "totalBytes": total,
        "maxLastWriteUtc": (datetime.fromtimestamp(newest, timezone.utc).isoformat() if newest else None),
    }


def build_vbs(vbs_path: Path, exe: Path, user_data: Path, env_extra: dict, extra_args: list[str]) -> None:
    lines = [
        'Set sh = CreateObject("WScript.Shell")',
        'Set env = sh.Environment("PROCESS")',
    ]
    for key, value in env_extra.items():
        lines.append('env("%s") = "%s"' % (key, value))
    arg_text = 'Chr(34) & "%s" & Chr(34) & " --user-data-dir=" & Chr(34) & "%s" & Chr(34)' % (exe, user_data)
    for a in extra_args:
        arg_text += ' & " %s"' % a
    lines.append('sh.Run %s, 0, False' % arg_text)
    vbs_path.write_text("\r\n".join(lines) + "\r\n", encoding="ascii", newline="")


def snapshot_tree(root: Path) -> list[str]:
    if not root.is_dir():
        return []
    out = []
    for p in root.rglob("*"):
        try:
            rel = str(p.relative_to(root))
            out.append(rel + ("/" if p.is_dir() else ""))
        except Exception:
            continue
    return sorted(out)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--observe-seconds", type=int, default=60)
    ap.add_argument("--report", default=str(Path(os.environ.get("TEMP", ".")) / "agph-d2p-report.json"))
    ap.add_argument("--executable", default=str(EXE_DEFAULT))
    ns = ap.parse_args()

    exe = Path(ns.executable)
    if not exe.is_file():
        print("FATAL: %s not found" % exe)
        return 2

    # 前置：不能有既存实例（单实例锁会让我们的启动秒退，也会造成误杀）
    pre_anti = tasklist_pids("Antigravity.exe")
    pre_ls = tasklist_pids("language_server.exe")
    if pre_anti or pre_ls:
        print("ABORT: an Antigravity instance is already running "
              "(Antigravity=%s language_server=%s)." % (pre_anti, pre_ls))
        print("       Close it first: our launch would hit the single-instance lock and we must not kill yours.")
        return 3

    scratch = Path(os.environ.get("TEMP", ".")) / ("agph-d2p-" + time.strftime("%Y%m%d-%H%M%S"))
    instance_home = scratch / "home"
    roaming = instance_home / "AppData" / "Roaming"
    local = instance_home / "AppData" / "Local"
    tmpp = scratch / "temp"
    user_data = scratch / "user-data"
    for d in (instance_home, roaming, local, tmpp, user_data):
        d.mkdir(parents=True, exist_ok=True)

    env_extra = {
        "USERPROFILE": str(instance_home),
        "HOME": str(instance_home),
        "HOMEDRIVE": instance_home.drive,
        "HOMEPATH": str(instance_home)[2:],
        "APPDATA": str(roaming),
        "LOCALAPPDATA": str(local),
        "TEMP": str(tmpp),
        "TMP": str(tmpp),
        PROBE_ENV_MARKER: "AGPHProbe",
    }

    vbs = scratch / "launch_probe.vbs"
    build_vbs(vbs, exe, user_data, env_extra, [])

    cred_before = cred_fingerprint()
    print("exe            = %s" % exe)
    print("scratch        = %s" % scratch)
    print("instance home  = %s" % instance_home)
    print("user-data      = %s" % user_data)
    print("vbs            = %s" % vbs)
    print("=" * 78)

    # 用 explorer 打开 vbs（等价双击；工具上下文直接起 Electron 会撞单实例锁）
    subprocess.run(["explorer.exe", str(vbs)], capture_output=True)
    t0 = time.time()
    seen_ls: list[int] = []
    seen_anti: list[int] = []
    deadline = t0 + ns.observe_seconds
    while time.time() < deadline:
        for pid in tasklist_pids("Antigravity.exe"):
            if pid not in seen_anti:
                seen_anti.append(pid)
        for pid in tasklist_pids("language_server.exe"):
            if pid not in seen_ls:
                seen_ls.append(pid)
        time.sleep(1.0)
    elapsed = round(time.time() - t0, 1)

    home_tree = snapshot_tree(instance_home)
    gemini_dir = instance_home / ".gemini"
    gemini_tree = snapshot_tree(gemini_dir) if gemini_dir.is_dir() else []
    cred_after = cred_fingerprint()

    # 只清理本次新出现的进程
    kill_pids(seen_ls)
    kill_pids(seen_anti)
    time.sleep(1.5)
    leftover_ls = tasklist_pids("language_server.exe")
    leftover_anti = tasklist_pids("Antigravity.exe")

    ls_artifacts = [p for p in gemini_tree]
    verdict = "inconclusive"
    if not seen_anti:
        verdict = "desktop-did-not-start"
    elif not seen_ls:
        verdict = "ls-never-spawned"
    elif ls_artifacts:
        verdict = "env-forwarded-to-ls"
    else:
        verdict = "ls-spawned-but-no-artifacts-under-synthetic-home"

    report = {
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "executable": str(exe),
        "observeSeconds": ns.observe_seconds,
        "elapsedSeconds": elapsed,
        "scratchRoot": str(scratch),
        "instanceHome": str(instance_home),
        "userDataDir": str(user_data),
        "injectedEnv": env_extra,
        "vbsContent": vbs.read_text(encoding="ascii"),
        "antigravityPidsObserved": seen_anti,
        "languageServerPidsObserved": seen_ls,
        "homeFileTree": home_tree[:200],
        "geminiFileTree": gemini_tree[:200],
        "hostCredentialBefore": cred_before,
        "hostCredentialAfter": cred_after,
        "hostCredentialStoreChanged": cred_before != cred_after,
        "leftoverAntigravity": leftover_anti,
        "leftoverLanguageServer": leftover_ls,
        "verdict": verdict,
    }
    Path(ns.report).write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    print("antigravity pids observed : %s" % seen_anti)
    print("language_server pids       : %s" % seen_ls)
    print("host credential changed   : %s" % report["hostCredentialStoreChanged"])
    print("leftovers                 : anti=%s ls=%s" % (leftover_anti, leftover_ls))
    print("-" * 78)
    print("instance home tree (%d):" % len(home_tree))
    for p in home_tree[:60]:
        print("   ", p)
    print("-" * 78)
    print("VERDICT: %s" % verdict)
    print("report = %s" % ns.report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
