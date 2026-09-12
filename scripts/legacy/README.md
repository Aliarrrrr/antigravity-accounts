# scripts/legacy — 一次性取证与验收脚本（不是产品工具链）

这个目录里的脚本针对**特定历史时刻的特定开发机**编写，不随版本维护，也不是
`hub.ps1` 工具链的一部分。它们会触碰真实个人数据，把它们隔离在此并保留，
只是为了可追溯，不是为了继续运行。

| 脚本 | 为什么被归档 |
|---|---|
| `Probe-ReleaseReadiness.py` | 在**真实默认数据根**上创建/注销 Profile、注册真实 `antigravity://`，且硬编码 `D:\mutigravity` 路径并只接受版本 `0.4.0`。不能当作通用发布检查运行。 |
| `Smoke-ProcessLocalCore.py` | 机制冒烟测试，但 CLI 与 SDK 路径硬编码到已不存在的开发目录。 |
| `Verify-TokenLaunch.py` | 机制验证：用 `CreateProcessWithTokenW` 启动真实桌面后**从不回收**该进程。 |
| `Agph-D2-ProfileControl.ps1` | D2 验收时代的会话控制脚本，绑定当时的窗口与 Profile。 |
| `Probe-ProcessEnvironment.py` | 读取同用户任意进程的 PEB 环境块（取证工具，隐私敏感，非产品功能）。 |

当前有效的验证工具见仓库根 `README.md` 的「安全边界」一节。需要真实端到端
验收时，请按 `MANUAL_TEST_PLAN.md` 的流程在新机器上重新确认参数后运行。
