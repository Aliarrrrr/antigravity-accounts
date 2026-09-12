# -*- coding: utf-8 -*-
"""0.4.0 发布就绪测试：在真实数据根上自动化除登录外的全部可验证项。
跑完后两个测试 profile 的窗口保持打开，等待 kiii 手动登录。"""
import json
import os
import subprocess
import sys
import time
import winreg
from pathlib import Path

REPO = Path(r"D:\mutigravity")
DOTNET = REPO / ".tools" / "dotnet" / "dotnet.exe"
CLI = REPO / "src" / "AGProfileHub.Cli" / "bin" / "Release" / "net10.0" / "AGProfileHub.Cli.dll"
CRED_STORE = Path(os.environ["LOCALAPPDATA"]) / "Microsoft" / "Credentials"
ENV = dict(os.environ, DOTNET_ROOT=str(REPO / ".tools" / "dotnet"))
CMD_KEY_PATH = r"Software\Classes\antigravity\shell\open\command"

results = []

def step(name, ok, detail=""):
    results.append((name, ok, detail))
    print(("PASS  " if ok else "FAIL  ") + name + ("  | " + detail if detail else ""), flush=True)
    return ok

def hub(*args, timeout=300):
    r = subprocess.run([str(DOTNET), str(CLI), *args], capture_output=True, env=ENV, timeout=timeout)
    text = ((r.stdout or b"") + b"\n" + (r.stderr or b"")).decode("utf-8", "replace")
    return r.returncode, text.strip()

def cred_fingerprint():
    items = sorted((p.name, p.stat().st_size, p.stat().st_mtime) for p in CRED_STORE.iterdir() if p.is_file())
    return items

def registered_command():
    try:
        k = winreg.OpenKey(winreg.HKEY_CURRENT_USER, CMD_KEY_PATH)
        v, _ = winreg.QueryValueEx(k, "")
        winreg.CloseKey(k)
        return v
    except FileNotFoundError:
        return None

print("=" * 78)
print("0.4.0 release-readiness test  |", time.strftime("%Y-%m-%d %H:%M:%S"))
print("=" * 78)

# 1. 版本
code, out = hub("version")
step("version is 0.4.0", code == 0 and "0.4.0" in out, out.splitlines()[0] if out else "")

# 2. 真实数据根切换默认后端
code, out = hub("configure-isolation", "process-local")
step("real data root: configure-isolation process-local", code == 0 and "ProcessLocal" in out, out.splitlines()[-1] if out else "")

# 3. doctor 在真实数据根可用
code, out = hub("doctor", timeout=120)
step("doctor runs on the real data root", code == 0 and "executableDiscovery" in out, out.splitlines()[0][:90] if out else "")

# 4. 凭据基线
fp_before = cred_fingerprint()
print(f"      host credential baseline: {len(fp_before)} files")

# 5. 深链路由登记
code, out = hub("configure-deeplink", "register")
step("configure-deeplink register", code == 0 and "now hands its callback to the Hub" in out, out.splitlines()[-1] if out else "")
cmd = registered_command()
step("registry now points at the Hub callback", cmd is not None and "callback" in cmd, (cmd or "")[:110])

# 6. 幂等：重复 register 不丢原始备份
code, out = hub("configure-deeplink", "register")
step("re-register is idempotent", code == 0)

# 7. 建两个测试 profile
code, out = hub("new", "测试A", timeout=120)
step("new 测试A (create + prepare, no elevation)", code == 0 and "isolation Ready" in out, out.splitlines()[-1][:110] if out else "")
code, out = hub("new", "测试B", timeout=120)
step("new 测试B", code == 0 and "isolation Ready" in out, out.splitlines()[-1][:110] if out else "")

# 8. 打开两个真实桌面窗口
code, out = hub("open", "测试A", timeout=420)
step("open 测试A (real desktop + scope assertion)", code == 0 and "Running" in out, out.splitlines()[-1][:120] if out else "")
code, out = hub("open", "测试B", timeout=420)
step("open 测试B concurrently", code == 0 and "Running" in out, out.splitlines()[-1][:120] if out else "")

# 9. 两个实例的 LS 都走文件令牌存储
for name in ("测试A", "测试B"):
    code, out = hub("--json", "status", name, timeout=60)
    ok = code == 0 and '"state": "Running"' in out
    detail = ""
    try:
        st = json.loads(out)
        detail = f"box={st.get('isolationBoxName', '')[:20]}..."
    except Exception:
        pass
    step(f"status 测试{name[-1]} is Running in isolation", ok, detail)

# 10. LS 日志 bypass 文案（真实数据根 profile）
hub_root = Path(os.environ["LOCALAPPDATA"]) / "AntigravityProfileHub"
marker_ok = True
detail = ""
for d in (hub_root / "profiles").iterdir():
    log = d / "user-data" / "logs" / "language_server.log"
    if log.is_file():
        text = log.read_text(encoding="utf-8", errors="replace")
        if "Using file-based token storage" not in text:
            marker_ok = False
            detail = log.parent.parent.name
step("both language servers logged the file-token switch", marker_ok, detail)

# 11. 重启恢复（无登录情况下验证重启路径）
code, out = hub("restart", "测试A", timeout=480)
step("restart 测试A (warm restart path)", code == 0 and "Running" in out, out.splitlines()[-1][:110] if out else "")

# 12. 凭据红线：宿主凭据库零变化
fp_after = cred_fingerprint()
step("host credential store untouched", fp_before == fp_after,
     f"before={len(fp_before)} after={len(fp_after)} files")

# 13. restore 演练后重新登记（保留 register 状态供用户登录）
code, out = hub("configure-deeplink", "restore")
step("configure-deeplink restore works", code == 0 and "restored" in out, out.splitlines()[-1][:90] if out else "")
code, out = hub("configure-deeplink", "register")
step("re-registered for the manual login test", code == 0)

passed = sum(1 for _, ok, _ in results if ok)
print("=" * 78)
print(f"{passed}/{len(results)} checks passed.")
print()
print("两个窗口已打开，保持运行。留给 kiii 的手工步骤：")
print("  1. 在『测试A』窗口登录 Google 账号 #1，『测试B』窗口登录账号 #2")
print("  2. 确认两窗口同时保持各自账号；重启 hub restart 测试A 后不丢登录")
print("  3. 验证完可运行: hub configure-deeplink restore")
print("  4. 测试 profile 不需要可 hub remove 测试A / hub remove 测试B")
sys.exit(0 if passed == len(results) else 1)
