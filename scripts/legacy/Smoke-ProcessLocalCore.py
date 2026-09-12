# -*- coding: utf-8 -*-
"""
核心功能冒烟测试：用**真实构建产物 + 真实 Antigravity** 验证 process-local 后端
能否独立完成 profile 生命周期，完全不依赖 Sandboxie。

流程（全部走 CLI，因此测的就是产品本身）：
  version → configure-isolation process-local → new → open（含作用域断言）
  → 语言服务器日志取证 → 宿主凭据库指纹比对 → status → close → 再次 open（重启持久性）

独立 scratch 数据根，不碰 kiii 真实的数据根与现有 profile。
红线：不读取任何令牌正文、不写宿主凭据条目（只做逐文件指纹比对）。

用法: python Smoke-ProcessLocalCore.py [--keep]
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

CLI = Path(r"D:\mutigravity\src\AGProfileHub.Cli\bin\Release\net10.0\AGProfileHub.Cli.exe")
# 开发树里的 CLI 是 framework-dependent 的，需要指到仓库自带的运行时；
# 打包产物（publish -Release）是自包含的，不需要这一步。
DOTNET_ROOT = Path(r"D:\mutigravity\.tools\dotnet")
CRED_STORE = Path(os.environ.get("LOCALAPPDATA", "")) / "Microsoft" / "Credentials"
BYPASS_MARKER = "Using file-based token storage"


def cred_fingerprint() -> tuple:
    files = [p for p in CRED_STORE.iterdir() if p.is_file()]
    stats = [(p.name, p.stat().st_size, p.stat().st_mtime) for p in files]
    return tuple(sorted(stats))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep", action="store_true", help="保留 scratch 数据根（默认测试后删除）")
    ns = ap.parse_args()

    if not CLI.is_file():
        print("FATAL: CLI not built at", CLI)
        return 2

    environment = dict(os.environ)
    environment["DOTNET_ROOT"] = str(DOTNET_ROOT)

    root = Path(os.environ["TEMP"]) / ("agph-smoke-" + time.strftime("%Y%m%d-%H%M%S"))
    results: list[tuple[str, bool, str]] = []

    def step(name: str, ok: bool, detail: str = "") -> None:
        results.append((name, ok, detail))
        print(("PASS  " if ok else "FAIL  ") + name + (("  | " + detail[:160]) if detail else ""))

    def run(*args: str):
        r = subprocess.run([str(CLI), "--data-root", str(root), *args],
                           capture_output=True, env=environment)
        text = ((r.stdout or b"") + b"\n" + (r.stderr or b"")).decode("utf-8", "replace")
        return r.returncode, text.strip()

    print("CLI  =", CLI)
    print("data =", root)
    print("=" * 78)

    rc, out = run("version")
    step("version", rc == 0 and "AGProfileHub" in out, out.splitlines()[0] if out else "")

    rc, out = run("configure-isolation", "process-local")
    step("configure-isolation process-local", rc == 0, out.splitlines()[0] if out else "")

    rc, out = run("new", "smoke")
    step("new smoke (create + prepare, no elevation/shim)", rc == 0, out.splitlines()[0] if out else "")

    backends = []
    try:
        document = json.loads((root / "config" / "profiles.json").read_text(encoding="utf-8-sig"))
        backends = [p.get("backend") for p in document.get("profiles", [])]
    except Exception as exc:  # noqa: BLE001
        backends = ["<unreadable: %s>" % exc]
    step("profile record carries ProcessLocal", backends == ["ProcessLocal"], str(backends))

    before = cred_fingerprint()

    rc, out = run("open", "smoke")
    step("open smoke (launch + scope assertion)", rc == 0, out.splitlines()[0] if out else "")

    profile_dirs = list((root / "profiles").glob("*")) if (root / "profiles").is_dir() else []
    log_path = None
    bypass = False
    for directory in profile_dirs:
        candidate = directory / "user-data" / "logs" / "language_server.log"
        if candidate.is_file():
            log_path = candidate
            bypass = BYPASS_MARKER in candidate.read_text(encoding="utf-8", errors="replace")
    step("language server logged the file-token switch", bypass, str(log_path))

    after = cred_fingerprint()
    step("host credential store untouched", before == after, "")

    rc, out = run("status", "smoke")
    step("status verb succeeds", rc == 0, out.splitlines()[0] if out else "")

    rc, out = run("close", "smoke")
    step("close smoke (terminate scope)", rc == 0, out.splitlines()[0] if out else "")

    rc, out = run("open", "smoke")
    step("re-open after close (restart path)", rc == 0, out.splitlines()[0] if out else "")

    rc, out = run("close", "smoke")
    step("final close", rc == 0, "")

    left = subprocess.run(["tasklist", "/FI", "IMAGENAME eq Antigravity.exe", "/NH"],
                          capture_output=True).stdout.decode("gbk", "replace").strip()
    # tasklist 会用本地化文案表示"没有任务"，出现它等同于没有进程
    no_process = ("没有运行的任务" in left) or ("no tasks are running" in left.lower()) or (left == "")
    step("no Antigravity process left behind", no_process, left[:80] if left else "")

    print("=" * 78)
    passed = sum(1 for _, ok, _ in results if ok)
    for name, ok, detail in results:
        if not ok:
            print("FAILED:", name, "|", detail)
    print("%d/%d core-function checks passed." % (passed, len(results)))

    report = {
        "ranUtc": datetime.now(timezone.utc).isoformat(),
        "cli": str(CLI),
        "dataRoot": str(root),
        "results": [{"name": n, "ok": o, "detail": d} for n, o, d in results],
    }
    Path(os.environ.get("TEMP", "."), "agph-smoke-report.json").write_text(
        json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    if not ns.keep:
        try:
            import shutil
            shutil.rmtree(root, ignore_errors=True)
            print("scratch removed:", root)
        except Exception as exc:  # noqa: BLE001
            print("scratch cleanup failed:", exc)
    else:
        print("scratch kept:", root)
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
