# Antigravity Profile Hub — Codex Master Prompt

你现在工作在一个 Windows 本地开发环境中，需要从零统筹、设计并实现一个长期可维护的 **Antigravity 多 Profile / 多账号本地实例管理器**。

本任务采用两阶段模型协作：

- **第一阶段：GPT-6 Astra**
  - 负责项目统筹
  - 环境调查
  - 技术可行性验证
  - 架构设计
  - 风险识别
  - 工作拆解
  - 编写实施计划
  - 建立项目骨架
  - 定义测试与验收标准
  - 不应急于大量实现业务代码

- **第二阶段：GPT-5.6 Sol**
  - 读取 Astra 生成的全部规划、调查结果和项目状态
  - 接管实际工程实现
  - 按阶段执行
  - 编译
  - 测试
  - 调试
  - 修复
  - 完成 GUI / CLI / Profile 管理等核心功能
  - 最终交付可运行的软件

不要把两个模型视为两个相互独立的开发者。

正确关系是：

```text
GPT-6 Astra
    │
    │ 规划 / 调研 / 架构 / 验收标准 / 项目状态
    ▼
PROJECT_STATE + IMPLEMENTATION_PLAN
    │
    ▼
GPT-5.6 Sol
    │
    │ 实现 / 编译 / 测试 / 修复
    ▼
可运行产品
```

整个过程中必须保持项目状态可恢复，不允许依赖聊天上下文本身作为唯一的信息载体。

---

# 一、项目背景

用户拥有两个正常开通 Google AI Pro 的 Google 账号，并使用 Google Antigravity。

现有问题是：

Antigravity 日常使用通常围绕一个登录状态运行。

用户不希望：

```text
账号 A
↓
退出
↓
重新 OAuth
↓
账号 B
↓
退出
↓
重新登录 A
```

也不希望：

- 使用虚拟机
- 创建两个 Windows 用户
- 使用两台电脑
- 通过 Selenium / UI 自动化反复点击登录
- 频繁 logout/login
- 抓取、修改或代理 Google OAuth Token
- 操作 Cookie
- 搭建用于冒充身份的认证反向代理
- 通过网络代理/IP 区分账号

用户希望实现类似 Android：

```text
应用本体
+
应用分身
```

的体验。

对应：

```text
Antigravity
├── Antigravity · Pro A
└── Antigravity · Pro B
```

两个实例：

- 在同一个 Windows 用户中运行
- 使用同一个 Antigravity 安装程序
- 拥有不同的本地 Profile / user-data
- 分别保持自己的 Google 登录状态
- 可以独立启动
- 可以同时运行
- 不需要反复登录
- 不需要虚拟机
- 不触碰 Google 凭据内部内容

---

# 二、核心产品定义

项目暂定名：

```text
Antigravity Profile Hub
```

简称可使用：

```text
AG Profile Hub
```

它不是一个：

- Google 登录破解器
- OAuth 代理
- Cookie 管理器
- Token 交换器
- 多账号规避工具

它是一个：

> Windows 本地 Antigravity 多实例 / 多 Profile 生命周期管理器。

核心设计思想：

```text
一个 Antigravity.exe

        │
        ├── 独立 Profile A
        │      │
        │      └── Google Account A
        │
        ├── 独立 Profile B
        │      │
        │      └── Google Account B
        │
        └── Future Profile C/D/...
```

项目从第一天开始必须支持：

```text
N Profiles
```

而不是把所有逻辑硬编码成：

```text
A
B
```

两个账号只是首个实际场景。

---

# 三、非常重要：不要预设 Antigravity 的内部实现

已有初步思路认为可能通过类似：

```text
--user-data-dir=<path>
```

这样的独立用户数据目录实现 Antigravity 多实例隔离。

但：

## 禁止直接把这一点当作事实写死。

Antigravity 会持续更新。

因此项目的 Phase 0 必须在当前机器、当前 Antigravity 版本中进行实际 Capability Probe。

需要验证：

1. 当前 Antigravity 可执行文件实际位置
2. 当前安装版本
3. 支持哪些 CLI 启动参数
4. 是否实际支持独立 user-data directory
5. 是否允许两个实例同时运行
6. 两个实例是否产生独立本地状态
7. 是否存在 singleton / IPC / mutex 限制
8. 第二实例是否会自动把窗口转发给第一实例
9. extensions/settings/cache/session 是否隔离
10. 登录状态是否随 user-data directory 隔离
11. 有没有额外使用：
   - Windows Credential Manager
   - DPAPI
   - Local State
   - SQLite
   - Chromium profile
   - Electron profile
   - IPC socket
   - named mutex
   - registry
   等共享资源

对于无法自动安全验证的 Google 登录状态：

输出：

```text
manual verification required
```

不要试图读取 OAuth Token 来验证。

---

# 四、安全边界

本软件应坚持：

> 管理 Antigravity 实例，不管理 Google 身份凭据。

允许：

- 指定独立用户数据目录
- 创建 Profile 目录
- 启动 Antigravity
- 停止 Antigravity
- 检测进程
- 检测窗口
- 设置 workspace
- 管理快捷方式
- 保存普通配置
- 检查 Antigravity 版本
- 管理本地日志

不允许：

- 保存 Google 密码
- 请求 Google 密码
- 读取 OAuth refresh token
- 导出 OAuth token
- 复制 Google Cookie
- 修改 Google Cookie
- 劫持 Google OAuth
- MITM Google 网络流量
- 伪造 Google 身份
- 绕过 Google 登录/资格/订阅限制
- 修改 Antigravity 二进制以绕过服务限制
- patch quota
- 篡改服务端账号识别

Google 登录过程必须继续由官方 Antigravity / Google OAuth 流程完成。

---

# 五、目标用户体验

最终用户安装一次 Antigravity Profile Hub。

首次运行：

```text
Antigravity Profile Hub

Antigravity:
Detected

Version:
x.x.x

Compatibility:
Checking...
```

能力验证完成后：

```text
[ + Create Profile ]
```

用户创建：

```text
Name:
Pro A
```

Hub 创建独立运行环境，然后：

```text
[ Launch ]
```

用户在官方 Antigravity 窗口中正常登录 Google Account A。

然后创建：

```text
Pro B
```

登录 Google Account B。

此后：

```text
┌────────────────────────────────────┐
│ Antigravity Profile Hub            │
├────────────────────────────────────┤
│                                    │
│ Pro A                   ● Running  │
│ Workspace: PixelDungeon            │
│                                    │
│ [Open] [Restart] [Stop] [...]      │
│                                    │
├────────────────────────────────────┤
│                                    │
│ Pro B                   ○ Stopped  │
│ Workspace: AI-Security             │
│                                    │
│ [Launch] [...]                     │
│                                    │
├────────────────────────────────────┤
│                                    │
│ [+ Profile]     [Launch All]       │
└────────────────────────────────────┘
```

必须支持：

```text
A Running
B Running
```

同时存在。

---

# 六、产品分层

建议至少设计以下层次。

```text
                    USER
                     │
                     ▼
              Presentation Layer
              CLI / GUI / Tray
                     │
                     ▼
                 Hub Core
                     │
        ┌────────────┼────────────┐
        │            │            │
 ProfileManager ProcessManager WorkspaceManager
        │            │            │
        └────────────┼────────────┘
                     │
                     ▼
              AntigravityDriver
                     │
                     ▼
              Antigravity.exe
```

## 1. AntigravityDriver

这是整个项目最重要的抽象之一。

禁止在 GUI、CLI 等各处拼接 Antigravity 命令。

统一通过：

```text
IAntigravityDriver
```

或者等价抽象。

至少考虑：

```text
Discover()
GetVersion()
ProbeCapabilities()
BuildLaunchCommand(profile)
Launch(profile)
Stop(profile)
Restart(profile)
GetProcesses(profile)
IsRunning(profile)
ValidateProfile(profile)
```

这样 Antigravity 将来更换参数时，只修改 Driver。

---

# 七、Profile 数据模型

Profile 不是 Google Account。

Profile 是：

> 一套 Antigravity 本地实例运行状态。

示例：

```json
{
  "id": "pro-a",
  "name": "Pro A",
  "userDataDir": "...",
  "workspacePath": "...",
  "launchArgs": [],
  "createdAt": "...",
  "lastLaunchedAt": "...",
  "enabled": true
}
```

禁止保存：

```json
{
  "googlePassword": "...",
  "oauthToken": "...",
  "cookie": "..."
}
```

Profile ID 要：

- 唯一
- 稳定
- 不依赖显示名称
- 支持 rename

---

# 八、建议目录布局

不要把项目源码、Profile 数据和 workspace 混在一起。

建议研究并采用类似：

```text
%LOCALAPPDATA%\AntigravityProfileHub\
│
├── config\
│   ├── settings.json
│   └── profiles.json
│
├── profiles\
│   ├── <profile-id>\
│   │   └── user-data\
│   │
│   └── ...
│
├── runtime\
│   ├── state\
│   └── locks\
│
├── logs\
│
└── cache\
```

项目 workspace 独立存在：

```text
D:\Projects\
```

或由用户自由选择。

必须明确区分：

```text
Profile
=
谁在运行 Antigravity

Workspace
=
Antigravity 正在操作哪个项目
```

---

# 九、必须设计 Capability Probe

Phase 0 实现：

```text
Doctor / Probe
```

例如：

```text
agprofile doctor
```

输出类似：

```text
Antigravity executable : FOUND
Version                 : x.x.x
Multiple instances      : SUPPORTED
User data isolation     : SUPPORTED
Profile isolation       : SUPPORTED
Auth isolation          : MANUAL CHECK REQUIRED
Workspace launch        : SUPPORTED
Known incompatibilities : NONE
```

同时生成机器可读：

```text
capability-report.json
```

例如：

```json
{
  "antigravity": {
    "path": "...",
    "version": "..."
  },
  "capabilities": {
    "multiInstance": true,
    "userDataIsolation": true,
    "workspaceLaunch": true,
    "authIsolation": "manual-verification-required"
  }
}
```

探针必须：

- 不修改用户现有 Antigravity Profile
- 不删除用户数据
- 不触碰 Google token
- 尽量使用临时目录
- 测试结束后安全清理临时资源
- 如果不确定，返回 unknown，而不是猜测

---

# 十、进程管理

Process Manager 必须可靠处理：

```text
launch
stop
restart
status
launch-all
stop-all
```

需要考虑：

- Antigravity 父进程
- Chromium/Electron 子进程
- Renderer
- GPU process
- Utility process
- Crash handler
- 多实例 PID 集合

不要简单认为：

```text
一个 Profile = 一个 PID
```

建议建立：

```text
ProfileRuntime
```

概念。

例如：

```json
{
  "profileId": "pro-a",
  "rootProcessId": 1234,
  "processIds": [1234, 1401, 1402],
  "startedAt": "...",
  "status": "running"
}
```

停止时优先：

1. 尝试 graceful close
2. 等待
3. 必要时终止该 Profile 所属进程树

绝对禁止误杀另一个 Profile。

---

# 十一、重复启动处理

用户点击：

```text
Launch Pro A
```

而 Pro A 已经运行时：

不要再盲目启动。

应该：

```text
if running:
    focus/open existing window
else:
    launch
```

如果无法聚焦：

至少明确显示：

```text
Already Running
```

---

# 十二、Workspace 支持

Profile 应可以：

```text
Launch
```

以及：

```text
Launch with Workspace
```

例如：

```text
Pro A
→ D:\Projects\PixelDungeon
```

Profile 也应允许：

```text
No default workspace
```

避免强绑定。

未来需要支持：

```text
Recent Workspaces
```

---

# 十三、Agent Safe Workspace Mode

这是第二阶段或后续功能。

因为未来用户希望同时：

```text
Pro A
Builder Agent

Pro B
Reviewer Agent
```

如果两个 Agent 同时修改同一个目录，可能导致：

- Git index 冲突
- 文件覆盖
- package-lock 冲突
- 临时文件冲突
- build output 冲突

因此设计：

```text
Agent Safe Mode
```

使用：

```text
Git Worktree
```

示意：

```text
Repository

├── worktree-pro-a
│       └── branch agent/pro-a
│
└── worktree-pro-b
        └── branch agent/pro-b
```

但该功能不要阻碍 MVP。

MVP 首先完成可靠的应用双开。

---

# 十四、CLI

GUI 之前必须先完成可用 Core + CLI。

推荐命令：

```text
agprofile doctor

agprofile list

agprofile create <name>

agprofile rename <id> <new-name>

agprofile remove <id>

agprofile launch <id>

agprofile launch <id> --workspace <path>

agprofile launch-all

agprofile stop <id>

agprofile stop-all

agprofile restart <id>

agprofile status

agprofile show <id>
```

还应考虑：

```text
agprofile open-data <id>
```

方便用户打开 Profile 目录。

删除 Profile 必须有安全机制。

默认：

```text
remove profile registration
```

和：

```text
delete profile data
```

不要混成一个危险操作。

---

# 十五、GUI

CLI 完成并稳定后再开发 GUI。

Windows 优先。

建议：

```text
C#
.NET 8 或当前稳定 LTS/适合版本
```

UI 可根据环境和工程判断选择：

- WPF
- WinUI 3
- 其他成熟 Windows UI

但优先：

> 简洁、稳定、少依赖。

GUI 至少提供：

```text
Profile cards

Create
Rename
Launch
Stop
Restart
Workspace
Open profile folder
Launch all
Stop all
Doctor
Settings
Logs
```

后续：

```text
System Tray
```

可提供：

```text
Pro A → Launch
Pro B → Launch
Launch All
Open Hub
Exit Hub
```

---

# 十六、Windows 快捷方式

项目应该支持：

```text
Create Desktop Shortcut
```

例如：

```text
Antigravity · Pro A
Antigravity · Pro B
```

用户甚至不打开 Hub 就能双击直接启动指定 Profile。

快捷方式可以调用：

```text
agprofile launch pro-a
```

而不要复制一套复杂逻辑。

也就是说：

```text
Shortcut
    ↓
CLI/Core
    ↓
Driver
```

---

# 十七、更新兼容机制

Antigravity 可能自动更新。

Hub 必须保存：

```text
lastKnownAntigravityVersion
```

启动时比较：

```text
last tested version
vs
current version
```

如果版本变化：

运行：

```text
Lightweight Compatibility Probe
```

而不是无条件假定仍兼容。

结果：

```text
Compatible
```

则更新状态。

如果：

```text
Unknown / Failed
```

不要破坏 Profile。

可以：

- 发出 warning
- 提供 doctor
- 输出诊断报告

但避免因为小版本变化无谓阻止用户使用，具体策略由 Astra 设计。

---

# 十八、日志

需要结构化日志。

至少记录：

```text
timestamp
operation
profileId
antigravityVersion
command
processId
result
error
```

但日志必须做敏感信息过滤。

不得记录：

- OAuth token
- Cookie
- Authorization header
- Google password
- 浏览器 session secret

如果命令行中未来出现敏感参数：

必须 redact。

---

# 十九、配置 Schema 和迁移

从第一版开始：

```json
{
  "schemaVersion": 1
}
```

未来配置升级：

```text
v1 → v2
```

必须通过 migration。

不要到处直接 deserialize 任意 JSON 后假定字段永远存在。

---

# 二十、并发和锁

Profile Hub 自身建议单实例运行。

但 Antigravity Profile 可以多实例运行。

需要区分：

```text
Hub Singleton
```

和：

```text
Antigravity Multi-instance
```

Profile 级别应有锁或状态机制，避免：

两个 Hub 操作同时 launch 同一个 Profile。

如果用户直接通过快捷方式同时启动：

也应该尽量安全处理。

---

# 二十一、异常恢复

必须考虑：

1. Hub 崩溃，但 Antigravity 仍运行
2. Antigravity 崩溃
3. Windows 强制重启
4. PID 被复用
5. runtime state 文件过期
6. Profile 目录被用户手动移动
7. Antigravity 路径改变
8. Antigravity 被卸载
9. Profile user-data 损坏
10. Workspace 不存在

因此：

运行状态不能只依赖：

```text
runtime.json
```

启动 Hub 后必须重新 reconcile：

```text
disk state
+
process state
+
config state
```

---

# 二十二、不要过度工程化

MVP 不需要：

- Redis
- Docker
- Kubernetes
- Electron
- Web server
- PostgreSQL
- 微服务
- 云端 backend

优先：

```text
单机
本地
轻量
可恢复
```

除非 Astra 有非常强的技术理由。

---

# 二十三、未来 Automation API

在 Core 稳定后，可以预留：

```text
Automation Layer
```

未来给 Agent Orchestrator 调用。

例如：

```text
launch(profile)
stop(profile)
getStatus(profile)
getWorkspace(profile)
```

可考虑：

- Named Pipe
- localhost IPC
- CLI JSON output

第一版不要开放复杂网络服务。

首选本机 IPC。

---

# 二十四、未来 Agent Orchestrator

最终用户未来可能实现：

```text
                Master Agent
                     │
             Antigravity Hub
                     │
         ┌───────────┴───────────┐
         │                       │
      Pro A                   Pro B
      Builder                 Reviewer
```

流程：

```text
User Goal
   ↓
Planner
   ↓
Pro A implements
   ↓
Pro B reviews/tests
   ↓
Pro A fixes
   ↓
Pro B verifies
   ↓
PASS
```

当前项目必须为此留下架构空间。

但：

> 不要为了未来 Orchestrator 影响当前 Profile Hub MVP 的稳定性。

---

# 二十五、Desktop 与 CLI 身份必须分开研究

如果 Antigravity 同时存在：

```text
Desktop
```

以及：

```text
CLI/headless
```

不要假设两者使用完全一样的 Profile 和认证机制。

需要分别研究。

尤其：

- Windows Credential Manager
- CLI credential storage
- desktop user-data
- session state

都可能不同。

第一目标：

```text
Desktop multi-profile
```

第二目标才是：

```text
CLI multi-profile
```

不要为了 CLI 多账号去交换 credential/token，除非未来有官方支持的安全 Profile 机制，并且在独立研究阶段验证。

---

# 二十六、测试策略

必须有：

## Unit Tests

针对：

- Profile serialization
- ID generation
- config migration
- launch argument generation
- path handling
- state machine
- process ownership
- version comparison

## Integration Tests

针对：

```text
temp profile
→ launch
→ detect
→ stop
```

不能依赖真实 Google 密码。

## Manual Verification

必须生成：

```text
MANUAL_TEST_PLAN.md
```

测试：

### Test A

```text
Create Profile A
Launch
Login Google A
Close
Launch again
```

验证：

```text
仍保持 A 登录
```

### Test B

```text
Create Profile B
Launch
Login Google B
```

验证：

```text
A != B
```

### Test C

同时：

```text
A Running
B Running
```

验证：

- 两个窗口存在
- A 仍为 A
- B 仍为 B
- 互不覆盖
- 重启其中一个不影响另一个

### Test D

分别设置不同 UI/设置或 workspace。

验证：

```text
Profile isolation
```

---

# 二十七、项目文档

项目必须维护：

```text
README.md

ARCHITECTURE.md

CAPABILITY_REPORT.md

IMPLEMENTATION_PLAN.md

PROJECT_STATE.md

DECISIONS.md

SECURITY.md

MANUAL_TEST_PLAN.md

CHANGELOG.md
```

其中最关键的是：

```text
PROJECT_STATE.md
```

每个主要阶段完成后更新。

它至少记录：

```text
Current Phase

Completed

In Progress

Pending

Known Issues

Key Decisions

Build Status

Test Status

Next Actions
```

这样 GPT-5.6 Sol 接手时不依赖 Astra 聊天上下文。

---

# 二十八、GPT-6 Astra 的具体任务

Astra 当前不要直接进入“狂写代码”模式。

首先：

## Step 1 — Inspect

调查：

- 当前 workspace
- Windows 版本
- PowerShell 版本
- .NET SDK
- Git
- Antigravity 安装
- Antigravity 版本
- Antigravity 路径
- 启动方式
- 当前源码状态

不得删除用户文件。

---

## Step 2 — Capability Research

完成 Phase 0。

验证真正的多实例方案。

重点回答：

```text
在同一 Windows 用户中，
是否可以通过独立 user-data/profile 路径，
让两个 Antigravity 实例分别保持两个 Google 登录状态，
并同时运行？
```

不要通过读取 Google secrets 验证。

---

## Step 3 — Architecture

生成：

```text
ARCHITECTURE.md
```

明确：

- 技术栈
- Core
- CLI
- GUI
- Driver
- Profile Manager
- Process Manager
- Workspace Manager
- persistence
- logging
- migrations
- update guard

---

## Step 4 — ADR

重大决定记录到：

```text
DECISIONS.md
```

例如：

```text
ADR-001: UI framework
ADR-002: config format
ADR-003: process identification strategy
ADR-004: profile isolation strategy
ADR-005: shortcut strategy
```

---

## Step 5 — Implementation Plan

写：

```text
IMPLEMENTATION_PLAN.md
```

拆成具体 Phase。

每个 Phase 包含：

```text
Goal
Files
Implementation
Tests
Acceptance Criteria
Rollback
```

---

## Step 6 — Skeleton

可以创建：

- solution
- projects
- test projects
-基础 CI/local build
- 基础配置

但不要一次实现全部。

---

## Step 7 — Handoff

完成：

```text
ASTRA_HANDOFF.md
```

告诉 GPT-5.6 Sol：

- 已验证事实
- 未验证事项
- 关键设计
- 风险
- 下一步
- 具体 Phase 顺序
- 禁止重新推翻哪些已经验证的事实，除非出现新证据

然后更新：

```text
PROJECT_STATE.md
```

Astra 阶段完成。

---

# 二十九、GPT-5.6 Sol 接管协议

GPT-5.6 Sol 开始时必须首先读取：

```text
PROJECT_STATE.md
ASTRA_HANDOFF.md
ARCHITECTURE.md
IMPLEMENTATION_PLAN.md
DECISIONS.md
CAPABILITY_REPORT.md
SECURITY.md
```

然后检查：

```text
git status
build
tests
```

之后继续。

不要重新从零规划。

如果发现 Astra 结论与本机实测冲突：

1. 保存证据
2. 更新 CAPABILITY_REPORT
3. 更新 DECISIONS
4. 调整 IMPLEMENTATION_PLAN
5. 再继续

---

# 三十、建议实现阶段

推荐至少：

## Phase 0
Capability Probe

## Phase 1
Core domain + configuration

## Phase 2
Antigravity Driver

## Phase 3
Profile Manager

## Phase 4
Process Supervisor

## Phase 5
CLI MVP

此时必须已经可以：

```text
create
list
launch
stop
restart
status
doctor
```

## Phase 6
真实双 Profile 验证

## Phase 7
GUI Hub

## Phase 8
Tray + shortcuts

## Phase 9
Workspace Manager

## Phase 10
Compatibility / Update Guard

## Phase 11
Packaging

## Phase 12
Agent Safe / Git Worktree

此阶段可作为增强功能。

---

# 三十一、每个 Phase 的执行协议

每进入一个 Phase：

先：

```text
READ
→ PLAN
→ IMPLEMENT
→ BUILD
→ TEST
→ VERIFY
→ DOCUMENT
→ COMMIT/STATE UPDATE
```

不得：

```text
写大量代码
→ 最后才编译
```

而应该小步验证。

如果发生失败：

```text
Observe
↓
Diagnose
↓
Minimal Fix
↓
Retest
```

不要为了修一个问题进行无关的大规模重构。

---

# 三十二、工程质量要求

必须：

- nullable / null safety
- clean error handling
- structured logging
- cancellation support where relevant
- async only where justified
- deterministic config
- path-safe operations
- atomic config writes
- backup before destructive migration
- no silent data deletion

任何 delete profile data 操作必须特别谨慎。

---

# 三十三、UI 风格

GUI 追求：

```text
Windows native
clean
compact
technical
```

不要：

- 花哨动画
- 巨大营销页面
- 不必要渐变
- Web dashboard 风格堆砌

核心界面首先让用户看到：

```text
Profile
Status
Workspace
Actions
```

---

# 三十四、最终交付标准

项目完成至少需要满足：

### Functional

- [ ] 可以检测 Antigravity
- [ ] 可以建立多个 Profile
- [ ] 可以启动指定 Profile
- [ ] 可以停止指定 Profile
- [ ] 可以同时运行 A/B
- [ ] A/B user-data 独立
- [ ] 用户可以分别完成 Google 登录
- [ ] 登录状态可以跨重启保持
- [ ] A/B 不互相覆盖
- [ ] 可以关联 workspace
- [ ] 有 CLI
- [ ] 有 GUI
- [ ] 有 doctor
- [ ] 有日志
- [ ] 有版本兼容检测

### Safety

- [ ] 不保存 Google 密码
- [ ] 不导出 OAuth token
- [ ] 不复制 Cookie
- [ ] 不 MITM Google
- [ ] 不修改 Antigravity 服务端认证
- [ ] 不误删原 Antigravity Profile
- [ ] 不误杀其他 Profile 进程

### Engineering

- [ ] Build clean
- [ ] Tests pass
- [ ] README 完整
- [ ] Architecture 文档完整
- [ ] Manual test plan 完整
- [ ] 可恢复项目状态
- [ ] 发布产物可运行

---

# 三十五、失败判定

下面情况不得伪装成成功：

```text
第二实例其实只是聚焦第一个窗口

两个实例仍然共享 Google 登录态

user-data-dir 没被 Antigravity 使用

只能通过交换 Google token 实现

只能通过 VM 实现

Profile B 启动后覆盖 Profile A

程序依赖手工修改内部认证数据库

Antigravity 更新后方案完全无法恢复
```

出现上述情况：

必须更新：

```text
CAPABILITY_REPORT.md
```

并重新设计。

---

# 三十六、最重要的项目原则

永远围绕这句话设计：

> Antigravity Profile Hub 管理的是本地 Antigravity 实例和它们的隔离运行环境，而不是 Google Account 的内部认证凭据。

理想结果是：

```text
Antigravity.exe
        │
        ├── Profile A
        │      └── official Google login A
        │
        └── Profile B
               └── official Google login B
```

Hub 只负责：

```text
Create
Launch
Stop
Observe
Organize
```

身份认证仍由 Google 与 Antigravity 自己负责。

---

# 三十七、工作自主性

本任务由 Agent 在当前 workspace 自主执行。

不要因为非关键决策频繁询问用户。

对于：

- 项目目录结构
- 类名
- 内部模块划分
- 测试框架
- 日志实现
- 配置实现

自行选择合理方案。

只有以下情况应阻止继续：

1. 即将执行明显破坏用户数据的操作
2. 需要真实 Google 登录
3. 需要用户完成 OAuth
4. 需要管理员权限且无法避免
5. Antigravity 的实际行为与整个技术假设根本冲突

其他问题：

自行调查、实现、测试和修复。

---

# 三十八、用户数据保护

任何时候都不要：

```text
rm -rf
Remove-Item -Recurse
```

直接针对用户原 Antigravity 数据目录执行。

实验必须使用：

```text
temp/
test-profile/
```

或项目专门创建的 Profile。

如果需要 destructive test：

复制测试数据后进行。

---

# 三十九、Git

如果当前目录不是 Git 仓库：

初始化。

合理使用：

```text
.gitignore
```

不要提交：

- build artifacts
-真实 Profile 数据
- OAuth/session 数据
-日志敏感数据
-机器特定路径
-临时测试目录

每个关键 Phase 应保持：

```text
working tree understandable
```

不要把几十个互不相关的修改堆在一起。

---

# 四十、最终报告

全部工作完成后生成：

```text
FINAL_REPORT.md
```

内容：

```text
What was built

Architecture

How profile isolation works

How to install

How to create Profile A/B

How to launch both

How to verify login isolation

Known limitations

Antigravity compatibility

Security model

Testing performed

Future roadmap
```

并确保用户可以按照 README 从零复现。

---

# 当前最高优先级

现在不要首先做 GUI。

当前最重要的问题只有：

```text
在当前这台 Windows 机器和当前 Antigravity 版本上，
找到并验证一种不使用 VM、
不切 Windows 用户、
不操作 Google token、
可以让两个独立 Antigravity 登录状态长期同时存在的
本地多实例隔离方式。
```

先证明这一点。

证明后：

再把它产品化为 Antigravity Profile Hub。

---

# 开始执行

GPT-6 Astra：

现在开始 Phase 0。

首先检查当前 workspace 和本机环境，然后调查实际安装的 Antigravity，并建立一个不会影响用户现有配置的 Capability Probe。

完成调查、架构、实施计划和 handoff 文件之后，再将工程交给 GPT-5.6 Sol。

GPT-5.6 Sol：

接管时不要重新从零开始。

严格从 Astra 留下的状态继续，实现、测试并完成产品。