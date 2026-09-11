# 根因确认与 keyring 隔离实现

日期：2026-09-09。本文只记录可复现的观测结果，所有凭据只涉及**存在性与大小**，从未读取、复制或记录任何令牌内容。

## 1. 根因：整个 Windows 用户只有一条 token

决定性证据（Win32 `CredEnumerateW`，仅枚举目标名与大小）：

```
target='gemini:antigravity'  user='antigravity'  type=1  blobSize=503  written=2026-09-08T11:52:11
```

Antigravity 的 language server 把 Google OAuth 令牌存放在 Windows 凭据管理器中，**目标名是硬编码字面量** `gemini:antigravity`，既不来自 `--app_data_dir`，也不来自 CLI 的 `--profile`。因此同一 Windows 用户下无论开几个实例，最终都读写同一条记录——**后登录的账号必然覆盖前一个**。这就是「并发时两个窗口都显示后登录账号」的直接原因。

### 排除掉的错误方向

此前多轮尝试失败，是因为根因判断错了。本轮逐项证伪：

| 曾怀疑的方向 | 本轮结论 |
|---|---|
| 目录隔离不足 | **不成立。** 双实例探测确认 LS 数据目录分别为 `home-a\.gemini\antigravity` 与 `home-b\.gemini\antigravity`，Host Bridge 端口 A=6188/B=6221，CSRF 令牌各自独立 |
| Electron 侧 OAuth 共享 | **不成立。** `app.asar` 中 `accounts.google.com` 出现 0 次，`auth/callback` 也是 0 次；OAuth 完全在 LS 侧，回调走各自本地端口 `http://localhost:%d/auth/callback` |
| 系统浏览器 SSO 共享 | **不成立为主要原因。** LS 自带 Playwright Chromium（`ms-playwright-go/1.57.0`），其 user-data 落在被隔离的 `LOCALAPPDATA\Google\Chrome\User Data` |
| 端口冲突 | **不成立。** 端口为动态分配且互不重叠 |

## 2. 为什么 Sandboxie 路线此前失败

Sandboxie 的凭据处理是**不对称**的，这是关键：

| 操作 | 实测行为 |
|---|---|
| 宿主写入 → 沙箱读取 | **可见**（沙箱内查不到时回退到宿主存储） |
| 沙箱写入 → 宿主读取 | **不可见**（写入确实被隔离） |

`OpenCredentials=n` 已经配置在每个盒子上，**无法阻止读取回退**。后果是：B 盒启动时读取 `gemini:antigravity`，本地没有，于是回退到宿主，直接拿到了 A 的令牌。

此前 P1「凭据隔离通过」是**假阳性**：那轮测试用 `cmdkey` 在宿主写测试凭据，而 `cmdkey /list` 在本环境返回 0 行——宿主那条很可能根本没写成，导致「沙箱读不到」被误判为隔离成功。本轮改用 Win32 API 直接写入并验证宿主侧确实可见后，回退立即暴露。

## 3. 解法：让每个隔离域拥有自己的同名条目

既然读取只在「本地没有」时回退，那么**让每个隔离域预先持有一条自己的同名条目**，回退就不会发生。

LS 二进制中确认存在降级路径：

```
Failed to load stored token from keyring, falling back to file: %v
Keyring SaveToken timed out after %v, falling back to file storage
```

配合 `NewFileTokenStorage` / `fileTokenStorage.SaveToken|LoadToken|RemoveToken` 与 `LOCALAPPDATA` + `.antigravity` 可知：keyring 不可用时 LS 会改用**文件令牌存储**，而该文件位于实例自己的数据目录内——同样是隔离的。两条路径都能达成隔离。

该方法**不删除、不读取、不复制用户现有令牌**，只在隔离域内写入一个明确无效的占位值。

## 4. 已实现

- `src/AGProfileHub.Core/Security/WindowsCredentialStore.cs`
  Win32 凭据 API 封装。`GetInfo` 只返回存在性与 blob 大小，blob 内容永不离开本机内存；`EnsurePlaceholder` 幂等创建；`WritePlaceholder` 无条件覆盖（仅供全新隔离域）。
- `src/AGProfileHub.Core/Security/AntigravityKeyring.cs`
  领域常量与状态构建。`EnsureIsolation()` 默认不覆盖任何已存在条目；`EnsureIsolation(forceFresh: true)` 仅在全新隔离域使用。
- CLI
  - `credential-status`：报告宿主共享条目的存在性与大小，附隔离建议。
  - `credential-shim [--force --fresh]`：在设计作用域内声明自己的条目。退出码 11 = 本次新建，0 = 本域已拥有（便于在无法捕获沙箱 stdout 的环境判定）。

## 5. 验证结果

用从未登录过的隔离域 Box B 做决定性实验（宿主条目为 503 字节）：

| 步骤 | 退出码 | 含义 |
|---|---|---|
| shim 之前读取 | 0 | 回退到宿主，看到 503 |
| `credential-shim --force --fresh` | 11 | 占位写入成功 |
| shim 之后读取 | **5** | 大小不等于 503，**本域持有独立副本，回退被堵死** |
| 宿主复查 | 0 | 宿主条目仍为 503，**未受任何影响** |

另有一组补充实验（合成目标 `AGProfileHub.Probe.W1`）：宿主与沙箱写入不同长度的同名条目后删除宿主那份，沙箱依然可读，证明沙箱确实持有独立副本。

回归：Release 构建 0 警告 0 错误，测试 **30/30 通过**（新增 1 项 keyring 原语测试）。

## 6. 限制与下一步

**尚未完成的部分要如实说明：**

1. 隔离原语已实现并验证，但**尚未接入 profile 启动流程**。Core 中目前没有 Sandboxie 后端，需要从零实现盒子管理、启动、进程发现与停止。
2. 当前产品仍是串行、单实例门禁状态，多实例并发尚未开放。
3. **真实双账号登录验收必须由用户完成**：需要在两个隔离域中各登录一个 Google 账号，确认并发时各自保持、重启后保持、互不影响。这一步无法由自动化替代。
4. 占位值会让 LS 解析失败并回退到文件令牌存储——该降级路径已由二进制字符串确认，但**真实登录路径下的行为仍需实测**。

建议的下一步顺序：

1. 决定是否为 profile 引入隔离后端（Sandboxie），并解除并发门禁。
2. 在启动 Antigravity 之前，于对应隔离域执行 `credential-shim --force --fresh`（仅首次）。
3. 由用户在两个隔离域分别完成官方登录并做并发验收。
