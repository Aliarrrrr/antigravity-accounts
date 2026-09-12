# -*- coding: utf-8 -*-
"""
D1 探针：验证「注入探测器环境变量能否让原版 language_server 改走纯文件令牌存储」。

为什么用 Python 而不是 PowerShell：
  本机 PowerShell 工具固定跑 Windows PowerShell 5.1 / .NET Framework，踩到多个差异
  （ProcessStartInfo.ArgumentList 不存在、Kill(bool) 重载不存在、脚本必须 UTF-8 带 BOM、
   $HOME/$PID 是只读自动变量、不支持「if 作表达式」）。Python 的 subprocess 可精确控制
   环境块与进程生命周期，没有这些坑。

设计：
  * 故意**不带 --headless**（--headless 会经 FSConfig.Headless 强制 useFile，会污染判断）；
    E 组是它的正对照，用来确认「信号可被观测到」。
  * 每组用独立的 scratch 主目录（USERPROFILE/HOME/APPDATA/LOCALAPPDATA/TEMP）+
    独立 --gemini_dir，模拟 Hub 的每实例合成环境。
  * 红线守护：运行前后对宿主凭据库目录只做「文件数/总字节/最新写入时间」元数据快照，
    绝不读取任何条目内容、不写入、不移走。

用法: python Probe-KeyringBypass.py [--observe-seconds 18] [--report <path>]
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

EXE_DEFAULT = Path(os.environ.get("LOCALAPPDATA", "")) / "Programs" / "antigravity" / "Antigravity.exe"
CRED_STORE = Path(os.environ.get("LOCALAPPDATA", "")) / "Microsoft" / "Credentials"
BYPASS_MARKER = "Using file-based token storage"
SIGNAL_PATTERN = ("token storage", "keyring", "marker", "authenticat", "auth")


def cred_fingerprint() -> dict:
    """宿主凭据库的只读元数据指纹（不含任何条目内容）。"""
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


def file_sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for block in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def kill_tree(pid: int) -> None:
    # taskkill 是独立可执行文件（不是 cmd 内建），可直接调用
    try:
        subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"],
                       capture_output=True, timeout=30)
    except Exception:
        pass


def run_one(tag: str, extra_env: dict, extra_args: list, exe: Path, ls: Path, version: str,
            scratch: Path, observe_seconds: int) -> dict:
    run_root = scratch / tag
    home = run_root / "home"
    gemini = home / ".gemini"
    roaming = home / "AppData" / "Roaming"
    local = home / "AppData" / "Local"
    tmpp = run_root / "temp"
    for d in (home, gemini, roaming, local, tmpp):
        d.mkdir(parents=True, exist_ok=True)

    env = dict(os.environ)
    env.update({
        "USERPROFILE": str(home),
        "HOME": str(home),
        "HOMEDRIVE": home.drive,
        "HOMEPATH": str(home)[2:],
        "APPDATA": str(roaming),
        "LOCALAPPDATA": str(local),
        "TEMP": str(tmpp),
        "TMP": str(tmpp),
    })
    env.update(extra_env)

    args = [
        str(ls),
        "--standalone",
        "--override_ide_name", "antigravity",
        "--subclient_type", "hub",
        "--override_ide_version", version,
        "--override_user_agent_name", "antigravity",
        "--http_server_port", "0",
        "--https_server_port", "0",
        "--csrf_token", uuid.uuid4().hex,
        "--gemini_dir", str(gemini),
        "--app_data_dir", "antigravity",
        "--disable_telemetry",
        "--use_mocked_data",
    ] + list(extra_args)

    before = cred_fingerprint()
    started = False
    rc = None
    out = err = b""
    t0 = time.time()
    try:
        proc = subprocess.Popen(args, env=env, cwd=str(run_root),
                                stdin=subprocess.DEVNULL,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        started = True
        try:
            out, err = proc.communicate(timeout=observe_seconds)
            rc = proc.returncode
        except subprocess.TimeoutExpired:
            kill_tree(proc.pid)
            try:
                out, err = proc.communicate(timeout=15)
            except Exception:
                pass
            rc = "killed-after-timeout"
    except Exception as exc:  # noqa: BLE001
        err = ("start-failed: %s" % exc).encode()
    elapsed = round(time.time() - t0, 1)

    text = (out + b"\n" + err).decode("utf-8", "replace")
    lines = [ln.strip() for ln in text.splitlines() if ln.strip()]
    bypass = [ln for ln in lines if BYPASS_MARKER in ln]
    signals = [ln for ln in lines if any(k in ln.lower() for k in SIGNAL_PATTERN)][:40]

    token_file = gemini / "jetski-standalone-oauth-token"
    cache_dir = gemini / "cache"
    cache_entries = sorted(p.name for p in cache_dir.iterdir()) if cache_dir.is_dir() else []
    created = sorted(str(p.relative_to(run_root)) for p in run_root.rglob("*") if p.is_file())

    after = cred_fingerprint()
    changed = before != after

    return {
        "runId": tag,
        "extraEnv": extra_env,
        "extraArgs": extra_args,
        "started": started,
        "exitCode": rc,
        "elapsedSeconds": elapsed,
        "bypassSignalFound": bool(bypass),
        "bypassLines": bypass[:5],
        "tokenFileCreated": token_file.is_file(),
        "tokenFileBytes": token_file.stat().st_size if token_file.is_file() else 0,
        "geminiCacheEntries": cache_entries,
        "signalLines": signals,
        "stdoutChars": len(out),
        "stderrChars": len(err),
        "filesCreated": created[:40],
        "hostCredentialStoreBefore": before,
        "hostCredentialStoreAfter": after,
        "hostCredentialStoreChanged": changed,
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--observe-seconds", type=int, default=18)
    ap.add_argument("--report", default=str(Path(os.environ.get("TEMP", ".")) / "agph-d1-report.json"))
    ap.add_argument("--executable", default=str(EXE_DEFAULT))
    ns = ap.parse_args()

    exe = Path(ns.executable)
    ls = exe.parent / "resources" / "bin" / "language_server.exe"
    if not ls.is_file():
        print("FATAL: language_server.exe not found at %s" % ls)
        return 2
    version = "2.12.2"

    scratch = Path(os.environ.get("TEMP", ".")) / ("agph-d1-" + time.strftime("%Y%m%d-%H%M%S"))
    scratch.mkdir(parents=True, exist_ok=True)

    plan = [
        ("A-control", {}, []),
        ("B-wsl", {"WSL_DISTRO_NAME": "AGPHProbe"}, []),
        ("C-ssh", {"SSH_CONNECTION": "10.0.0.1 1 10.0.0.2 2"}, []),
        ("D-container", {"container": "docker"}, []),
        ("E-headless", {}, ["--headless"]),
    ]

    print("language_server = %s" % ls)
    print("sha256           = %s" % file_sha256(ls)[:32])
    print("scratch          = %s" % scratch)
    print("host credential  = %s" % CRED_STORE)
    print("=" * 78)

    results = []
    for tag, extra_env, extra_args in plan:
        r = run_one(tag, extra_env, extra_args, exe, ls, version, scratch, ns.observe_seconds)
        results.append(r)
        print("[%s] env=%s args=%s" % (tag, extra_env, extra_args))
        print("    started=%s exit=%s elapsed=%ss out=%d err=%d" %
              (r["started"], r["exitCode"], r["elapsedSeconds"], r["stdoutChars"], r["stderrChars"]))
        print("    BYPASS=%s | tokenFile=%s(%dB) | cache=%s" %
              (r["bypassSignalFound"], r["tokenFileCreated"], r["tokenFileBytes"], r["geminiCacheEntries"]))
        print("    HOST CRED CHANGED = %s" % r["hostCredentialStoreChanged"])
        for ln in r["signalLines"][:8]:
            print("      | %s" % ln[:170])
        print("-" * 78)

    report = {
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "languageServerPath": str(ls),
        "languageServerSha256": file_sha256(ls),
        "antigravityVersion": version,
        "observeSeconds": ns.observe_seconds,
        "scratchRoot": str(scratch),
        "hostCredentialPath": str(CRED_STORE),
        "runs": results,
    }
    Path(ns.report).write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print("report = %s" % ns.report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
