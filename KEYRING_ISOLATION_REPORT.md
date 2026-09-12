# 根因确认与 keyring 隔离实现

> **时效说明（0.4.0 起）**：本文记录的是 0.2.x–0.3.x 的 Sandboxie keyring 占位方案，该方案已在 0.4.0 移除。现行机制：每个 Profile 的合成进程主目录 + 注入 `WSL_DISTRO_NAME` 让语言服务切换到 Profile 内的文件令牌存储 + 启动时读取语言服务日志自证（未自证即终止实例）。根因分析（第 1 节）依然成立，第 2 节及之后为历史记录。

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
  - `credential-shim --force --fresh`：仅供盒内启动器调用。当前进程没有 Sandboxie 注入的 `SbieDll.dll` 时硬拒绝，防止误覆盖宿主条目。退出码 11 表示盒内占位写入成功。

## 5. 验证结果

用从未登录过的隔离域 Box B 做决定性实验（宿主条目为 503 字节）：

| 步骤 | 退出码 | 含义 |
|---|---|---|
| shim 之前读取 | 0 | 回退到宿主，看到 503 |
| `credential-shim --force --fresh` | 11 | 占位写入成功 |
| shim 之后读取 | **5** | 大小不等于 503，**本域持有独立副本，回退被堵死** |
| 宿主复查 | 0 | 宿主条目仍为 503，**未受任何影响** |

另有一组补充实验（合成目标 `AGProfileHub.Probe.W1`）：宿主与沙箱写入不同长度的同名条目后删除宿主那份，沙箱依然可读，证明沙箱确实持有独立副本。

接管后的回归：Release 构建 0 警告 0 错误，测试 **34/34 通过**；新增覆盖 shim 失败关闭、重复盒映射、损坏状态、Core 路由和混合 `launch-all` 不部分启动。宿主直接调用 shim 返回 `shim_outside_isolation`，调用前后宿主条目均为 503 字节。

## 6. 已接入启动流程

新增隔离后端（不改动原版程序，也不改动既有串行逻辑）：

- `src/AGProfileHub.Core/Isolation/SandboxieBackend.cs`
  封装 `Start.exe`（`/listpids`、`/terminate`、盒内运行）与 `SbieIni.exe`（建盒、配置项）。
- `src/AGProfileHub.Core/Isolation/IsolatedProfileLauncher.cs`
  按 profile 维护隔离盒：建/复用盒子 → 首次在盒内执行 `credential-shim --force --fresh` → 在盒内启动原版 Antigravity → 按盒终止。
- Core/CLI/GUI/快捷方式统一通过 `HubService` 路由。CLI 提供 `iso-prepare <id> [--box <name>] [--fresh]`、`iso-launch`、`iso-stop`、`iso-status`、`iso-writethrough`；普通 `launch`/`restart`/`stop` 自动识别已准备 Profile。

启动走的是既有的 `AntigravityDriver.BuildLaunchPlan`，因此每个实例仍有独立的 `USERPROFILE/HOME/APPDATA/LOCALAPPDATA` 与 `--user-data-dir`，再叠加沙箱隔离。

### 一次性提权说明

Sandboxie 的配置位于 `C:\Windows\Sandboxie.ini`，标准用户不可写，因此**新建盒子需要一次提权**；在已存在的盒子内启动程序不需要。建盒脚本为 `scripts/New-IsolationBox.ps1`。若盒子已预先建好，只有确认它从未完成登录时才可用 `iso-prepare <id> --box <已有盒名> --fresh` 声明；已登录盒不能这样复用。

## 7. 验收结果：通过

两个 profile 在各自隔离盒内**并发**启动，用户在两个窗口中分别登录了不同的 Google 账号，界面确认各自归位。随后做了三项客观复验。

### 7.1 三个作用域持有互不相同的令牌

只取单向哈希，从不输出令牌内容：

| 作用域 | blobSize | SHA-256 前 16 位 |
|---|---|---|
| Pro A 隔离盒 | 501 | `9f49e1eaf2fce46b` |
| Pro B 隔离盒 | 501 | `3f2808b0b16d979b` |
| 宿主（原版登录态） | 503 | `017454ee4b053ad5` |

三者互不相同，说明 A 与 B 各自持有自己的令牌，且都没有读到宿主那份。

### 7.2 重启后各自保持

终止 Pro A 的隔离盒并重新启动：

- A 重启后盒内条目仍为 501，**未退回宿主的 503**，界面仍是原账号
- **B 的窗口 PID 全程保持 32228 未变**，进程数始终为 11 —— 重启 A 对 B 零影响

### 7.3 宿主登录态未受影响

全程复查宿主条目：始终 503 字节、哈希不变。方案没有读取、覆盖或搬运用户的既有凭据。

### 7.4 结论

**核心功能达成**：同一 Windows 用户、同一个官方安装，两个原版 Desktop 窗口并发运行，各自登录不同 Google 账号，并发、重启后均各自保持。此前失败的"后登录覆盖前一个"问题已消除。

## 8. 工作区直写：已配置并实测穿透

此前确认沙箱内写入只落沙箱副本。修复过程发现**三层坑**，全部已解决并封装进 `scripts\Set-IsolationWriteThrough.ps1`：

1. **规则格式**：SbieIni.exe 写出的 `OpenFilePath.1=path` 索引形式会被 Sandboxie **驱动静默忽略**——必须用裸 key 重复行 `OpenFilePath=path`。`SbieIni query` 按索引能读回，制造出配置生效的假象。
2. **重载**：`Start.exe /reload` 必须提权运行；非提权退出码为 0 但什么都不做。
3. **服务重启不充分**：路径规则由内核驱动缓存，改完配置要提权重启 SbieSvc 再提权 reload。

实测结果（两次探针，分别写各盒 profile home）：`host sees file: True`，即沙箱内写入已真实落到宿主磁盘。配置修改前自动备份至 `C:\Windows\Sandboxie.ini.agph-bak`。

顺带修复：隔离启动器原先用盒内 pid 数判断"已在运行"，但 Sandboxie 终止后总残留 1 个辅助进程，导致实例永远无法再次启动；现改为只识别厂商进程（Antigravity/language_server）。

## 9. 限制与遗留

1. 占位值会让 LS 解析失败并回退到文件令牌存储——该降级路径已由二进制字符串确认，真实登录已通过。
2. 工作区直写规则**已对齐当前 Profile 并实测穿透**（见第 8 节）：规则格式改为驱动认可的裸 key 形式，`iso-status` 两盒均为 `writeThrough=True`。直写生效后数据源从沙箱副本切换为宿主 profile 目录，实例首次启动可能要求重新登录一次。
3. 隔离后端已接进 Core 的默认生命周期，GUI 也显示盒名和直写状态；未准备 Profile 仍走原串行逻辑。
4. 沙箱内的凭据**写入**已验证隔离、**读取**依赖"本作用域已持有同名条目"阻断回退；若记录中的盒被删除，启动会拒绝并要求重新准备，不会静默直连。
5. 登出不互扰、以及用户在实例内编辑真实工作区文件的行为级验收，待用户最后确认。
6. 本方案不宣称是同一 Windows 用户之间的安全边界，只解决"多实例各自保持登录"。并发验收。
