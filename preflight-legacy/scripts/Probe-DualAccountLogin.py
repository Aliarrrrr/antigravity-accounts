# -*- coding: utf-8 -*-
"""
D2 双账号隔离验证：两个 profile 各自独立环境 + 各自登录不同 Google 账号。

分两阶段，中间由用户手动完成两次登录：
  prepare  建立两个 profile 的合成环境（独立 USERPROFILE/APPDATA/LOCALAPPDATA/TEMP + 独立
           user-data），固化宿主凭据基线，然后用 explorer 打开 VBS 拉起**可见**的两个窗口。
  verify   登录完成后核验四条判据。

四条判据：
  C1 两个 profile 各自在自己 home 下生成 jetski-standalone-oauth-token（令牌落在各自目录）
  C2 宿主凭据库目录只读元数据（文件数/总字节/最新写入时间）与基线一致（未被触碰）
  C3 宿主 %USERPROFILE%\.gemini 下的令牌文件与基线一致（未被写入）
  C4 两侧进程仍在运行（后续由人工重启再验"各自保持"）

红线：不读任何令牌内容、不读写宿主凭据条目、不终止任何非本次启动的进程。

用法:
  python Probe-DualAccountLogin.py --phase prepare
  python Probe-DualAccountLogin.py --phase verify
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
HOST_HOME = Path(os.environ.get("USERPROFILE", ""))
STATE_FILE = Path(os.environ.get("TEMP", ".")) / "agph-d2-state.json"
TOKEN_NAME = "jetski-standalone-oauth-token"


def tasklist_pids(image_name: str) -> list[int]:
    r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq %s" % image_name, "/FO", "CSV", "/NH"],
                       capture_output=True)
    text = r.stdout.decode("gbk", "replace")
    pids: list[int] = []
    for row in csv.reader(io.StringIO(text)):
        if len(row) >= 2 and row[1].strip().isdigit():
            pids.append(int(row[1].strip()))
    return pids


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


def file_fingerprint(path: Path) -> dict:
    if not path.is_file():
        return {"exists": False}
    st = path.stat()
    return {
        "exists": True,
        "bytes": st.st_size,
        "mtimeUtc": datetime.fromtimestamp(st.st_mtime, timezone.utc).isoformat(),
    }


def list_tree(root: Path, limit: int = 400) -> list[str]:
    if not root.is_dir():
        return []
    out = []
    for p in sorted(root.rglob("*")):
        try:
            out.append(str(p.relative_to(root)) + ("/" if p.is_dir() else ""))
        except Exception:
            continue
        if len(out) >= limit:
            break
    return out


def build_vbs(vbs_path: Path, exe: Path, user_data: Path, env_extra: dict) -> None:
    lines = ['Set sh = CreateObject("WScript.Shell")', 'Set env = sh.Environment("PROCESS")']
    for key, value in env_extra.items():
        lines.append('env("%s") = "%s"' % (key, value))
    lines.append('sh.Run Chr(34) & "%s" & Chr(34) & " --user-data-dir=" & Chr(34) & "%s" & Chr(34), 1, False'
                 % (exe, user_data))
    vbs_path.write_text("\r\n".join(lines) + "\r\n", encoding="ascii", newline="")


def profile_env(instance_home: Path, label: str) -> dict:
    root = instance_home.drive
    return {
        "USERPROFILE": str(instance_home),
        "HOME": str(instance_home),
        "HOMEDRIVE": root,
        "HOMEPATH": str(instance_home)[2:],
        "APPDATA": str(instance_home / "AppData" / "Roaming"),
        "LOCALAPPDATA": str(instance_home / "AppData" / "Local"),
        "TEMP": str(instance_home / ".." / "temp"),
        "TMP": str(instance_home / ".." / "temp"),
        "WSL_DISTRO_NAME": label,
    }


def phase_prepare(exe: Path) -> int:
    pre_anti = tasklist_pids("Antigravity.exe")
    pre_ls = tasklist_pids("language_server.exe")
    if pre_anti or pre_ls:
        print("ABORT: Antigravity is already running (Antigravity=%s language_server=%s)." % (pre_anti, pre_ls))
        print("       Please close every Antigravity window first, then re-run --phase prepare.")
        return 3

    scratch = Path(os.environ.get("TEMP", ".")) / ("agph-d2-" + time.strftime("%Y%m%d-%H%M%S"))
    profiles = {}
    for label in ("A", "B"):
        instance_home = scratch / ("profile-%s" % label) / "home"
        user_data = scratch / ("profile-%s" % label) / "user-data"
        for d in (instance_home, instance_home / "AppData" / "Roaming", instance_home / "AppData" / "Local",
                  scratch / ("profile-%s" % label) / "temp", user_data):
            d.mkdir(parents=True, exist_ok=True)
        env = profile_env(instance_home, "AGPH-Probe-%s" % label)
        vbs = scratch / ("launch-%s.vbs" % label)
        build_vbs(vbs, exe, user_data, env)
        profiles[label] = {
            "instanceHome": str(instance_home),
            "userData": str(user_data),
            "vbs": str(vbs),
            "injectedEnv": env,
        }

    state = {
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "executable": str(exe),
        "scratchRoot": str(scratch),
        "profiles": profiles,
        "hostCredentialBaseline": cred_fingerprint(),
        "hostTokenBaseline": file_fingerprint(HOST_HOME / ".gemini" / TOKEN_NAME),
        "hostGeminiTopLevel": sorted(p.name for p in (HOST_HOME / ".gemini").iterdir())
        if (HOST_HOME / ".gemini").is_dir() else [],
    }
    STATE_FILE.write_text(json.dumps(state, indent=2, ensure_ascii=False), encoding="utf-8")

    print("scratch = %s" % scratch)
    print("host credential baseline : %s" % state["hostCredentialBaseline"])
    print("host token baseline      : %s" % state["hostTokenBaseline"])
    print("=" * 78)
    for label in ("A", "B"):
        info = profiles[label]
        print("profile %s home     = %s" % (label, info["instanceHome"]))
        print("profile %s userData = %s" % (label, info["userData"]))
        subprocess.run(["explorer.exe", info["vbs"]], capture_output=True)
        time.sleep(2.0)

    print("waiting 25s for both windows to appear...")
    seen_anti, seen_ls = set(), set()
    for _ in range(25):
        seen_anti.update(tasklist_pids("Antigravity.exe"))
        seen_ls.update(tasklist_pids("language_server.exe"))
        time.sleep(1.0)
    print("Antigravity pids : %s" % sorted(seen_anti))
    print("language_server  : %s" % sorted(seen_ls))
    state["pidsAfterLaunch"] = {"antigravity": sorted(seen_anti), "languageServer": sorted(seen_ls)}
    STATE_FILE.write_text(json.dumps(state, indent=2, ensure_ascii=False), encoding="utf-8")
    print("state file = %s" % STATE_FILE)
    return 0


def phase_verify() -> int:
    if not STATE_FILE.is_file():
        print("FATAL: %s not found; run --phase prepare first." % STATE_FILE)
        return 2
    state = json.loads(STATE_FILE.read_text(encoding="utf-8"))

    report = {"verifiedUtc": datetime.now(timezone.utc).isoformat(), "scratchRoot": state["scratchRoot"],
              "profiles": {}, "criteria": {}}

    for label, info in state["profiles"].items():
        home = Path(info["instanceHome"])
        gemini = home / ".gemini"
        token = gemini / TOKEN_NAME
        cache = gemini / "cache"
        profile_result = {
            "instanceHome": str(home),
            "geminiExists": gemini.is_dir(),
            "token": file_fingerprint(token),
            "cacheEntries": sorted(p.name for p in cache.iterdir()) if cache.is_dir() else [],
            "geminiTree": [p for p in list_tree(gemini) if TOKEN_NAME in p or "cache" in p.lower()
                           or "installation_id" in p][:40],
            "homeTopLevel": sorted(p.name for p in home.iterdir()) if home.is_dir() else [],
        }
        report["profiles"][label] = profile_result

    host_cred_now = cred_fingerprint()
    host_token_now = file_fingerprint(HOST_HOME / ".gemini" / TOKEN_NAME)
    report["hostCredentialNow"] = host_cred_now
    report["hostCredentialBaseline"] = state["hostCredentialBaseline"]
    report["hostTokenNow"] = host_token_now
    report["hostTokenBaseline"] = state["hostTokenBaseline"]
    report["processesNow"] = {
        "antigravity": tasklist_pids("Antigravity.exe"),
        "languageServer": tasklist_pids("language_server.exe"),
    }

    a_token = report["profiles"]["A"]["token"]["exists"]
    b_token = report["profiles"]["B"]["token"]["exists"]
    cred_ok = host_cred_now == state["hostCredentialBaseline"]
    htoken_ok = host_token_now == state["hostTokenBaseline"]
    report["criteria"] = {
        "C1a_profileA_hasOwnToken": a_token,
        "C1b_profileB_hasOwnToken": b_token,
        "C2_hostCredentialUntouched": cred_ok,
        "C3_hostTokenUntouched": htoken_ok,
        "C4_bothRunning": bool(report["processesNow"]["antigravity"]),
    }
    report["verdict"] = "isolated" if (a_token and b_token and cred_ok and htoken_ok) else "not-yet"

    out = Path(os.environ.get("TEMP", ".")) / "agph-d2-verify.json"
    out.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    for label in sorted(report["profiles"]):
        p = report["profiles"][label]
        print("profile %s: gemini=%s token=%s cache=%s" %
              (label, p["geminiExists"], p["token"], p["cacheEntries"]))
    print("host credential now      : %s" % host_cred_now)
    print("host credential baseline : %s" % state["hostCredentialBaseline"])
    print("host token now           : %s" % host_token_now)
    print("host token baseline      : %s" % state["hostTokenBaseline"])
    print("processes now            : %s" % report["processesNow"])
    print("-" * 78)
    for k, v in report["criteria"].items():
        print("  %-32s %s" % (k, "PASS" if v else "FAIL"))
    print("VERDICT: %s" % report["verdict"])
    print("report = %s" % out)
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--phase", choices=("prepare", "verify"), required=True)
    ap.add_argument("--executable", default=str(EXE_DEFAULT))
    ns = ap.parse_args()
    exe = Path(ns.executable)
    if ns.phase == "prepare":
        if not exe.is_file():
            print("FATAL: %s not found" % exe)
            return 2
        return phase_prepare(exe)
    return phase_verify()


if __name__ == "__main__":
    sys.exit(main())
