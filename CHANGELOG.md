# Changelog

## 0.5.0-next.1 — 2026-10-02

### 当前发行

- Windows x64 .NET 10 / WPF Profile Hub Next 与 `agprofile-next` CLI，自包含便携包及独立安装器。
- GUI 和 CLI 统一使用 `NextHubService`；历史生产启动实现移入 `LegacyCore` 测试夹具。
- 每个 Profile 使用独立 home、user-data 和文件令牌存储，已登录实例可并行运行。
- 官方安装的版本、运行组件 SHA-256 与探测策略修订号绑定兼容证明；更新后重探测，冷启动重新检查实际归属和令牌存储标记。
- 普通关闭与显式强停分开；进程枚举不可用、复用 PID 或身份不明时拒绝破坏性操作。
- 当前用户回调注册、原处理器备份与还原、单 Profile 授权等待槽，以及不含 URI 的接收 / 阶段 / 故障诊断。
- 默认 Git include 与 SSH 目录桥接，可关闭并保留宿主目标；有限配置继承预览和应用、旧版元数据迁移预览收据。
- Workspace 仅保存路径，在官方窗口手动打开。
- 70 项回归测试，保留历史夹具；当前 Next 构建、发布、包 / 安装器烟雾验证和无登录契约探测入口。
- 重写使用、架构和安全说明；当前验证结果见 [VALIDATION.md](docs/VALIDATION.md)。Rust GUI 的并行开发源码保留，本次发行不包含该界面。

### 验证范围

Antigravity 2.19.1 本机无登录探测及部分人工验证通过：A/B 独立登录、A 普通关闭后重启保留登录且 B 不受影响、两个 Workspace 手动打开、C 登录、Git 配置和桥接撤销 / 恢复。

真实 Chrome 登录回调端到端未确认，用户已跳过继续调查；SSH 实际连接跳过。当前官方独立对话界面没有文件编辑入口，未保存文件关闭子项不适用。完整人工验收保持 `manualAcceptance=false`，本版继续作为实验发行。

## 历史版本

0.4.x 的实现、说明和发布记录保留在 Git 历史与 [历史 Releases](https://github.com/Aliarrrrr/antigravity-profile-hub/releases) 中。旧文档所述后端和发布入口不代表当前 Next 运行行为。
