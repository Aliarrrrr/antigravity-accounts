# Next 架构

当前生产入口为 .NET 10 WPF GUI 与 `agprofile-next` CLI，两者共用 `NextHubService`。本版管理独立应用状态和进程生命周期；登录操作由用户在官方窗口完成。

## 项目与责任

| 位置 | 责任 |
| --- | --- |
| `src/AGProfileHub.Cli` | 参数解析、JSON 输出、Next 操作入口、协议回调入口 |
| `src/AGProfileHub.Gui` | Profile 列表、操作按钮、桥接和配置继承预览 |
| `src/AGProfileHub.Core/Next` | 兼容证明、启动、归属检查、关闭、授权等待、桥接和迁移 |
| Core 的 `Profiles` / `Storage` | 稳定 Profile ID、元数据、路径检查、锁与原子 JSON 写入 |
| Core 的 `Processes` / `Driver` | Windows 进程快照、命令行解析、文件摘要 |
| Core 的 `Logging` | 有限的结构化生命周期和回调诊断 |
| `tests/AGProfileHub.Tests` | Next 回归及历史 `LegacyCore` 夹具，共 70 项 |
| `src/AGProfileHub.RustGui` | 另一任务开发中的实验界面，不进入本次 WPF 发行包 |

历史 `HubService`、隔离后端和旧启动器仅存在于 `tests/AGProfileHub.Tests/LegacyCore`。测试仍保留旧契约的回归覆盖；Next GUI / CLI 不通过这些后端启动程序。

## 数据布局

默认根目录为 `%LOCALAPPDATA%/AntigravityProfileHubNext`。GUI 和 CLI 可通过 `--data-root` 指定绝对路径，Next 拒绝使用旧版默认数据根目录。

```text
AntigravityProfileHubNext/
  config/                Profile 注册与应用设置
  compatibility.json     当前安装指纹的自动探测和人工验收记录
  profiles/<id>/
    home/                Profile 的 HOME / USERPROFILE
    user-data/           官方应用 user-data
    roaming/ local/ temp/
    bridge.json          受管桥接记录
  runtime/
    state/               Profile 运行身份
    locks/               配置、Profile、桥接与回调操作锁
    login-waiters.json    当前串行授权等待槽
    deeplink-registration.json
  logs/                  管理器结构化诊断
  probes/                无登录探针的独立临时数据
```

Profile 目录由稳定 ID 寻址，标签和 Workspace 路径可以修改。`remove` 只移除注册，不删除磁盘数据。日志与运行记录属于本地诊断信息；发行包不收集这些目录。

## 兼容证明与启动

`NextCompatibility` 计算官方可执行文件、`app.asar`、语言服务器、原生 DLL 和解包运行组件的 SHA-256。证明同时绑定应用版本和 `NextHubService.StrategyRevision`，当前策略修订号为 2。相关组件在启动与回调校验期间保持只读打开，组件变化会使证明失效。

`probe` 在全新的无登录数据根目录启动官方应用，检查独立 home / user-data、文件令牌存储日志标记、进程停止和协议还原。通过后允许实验启动，人工验收时间仍为空。

实际启动为 Profile 准备独立环境变量和 `--user-data-dir`，由桌面启动适配器执行临时包装器。每次冷启动必须观察到本次启动生成的文件令牌存储标记、受管根进程及语言服务器子进程。旧日志或仅存在的目录不能代替本次证明。

## 进程归属与关闭

归属检查联合使用可执行文件、精确 user-data 参数路径、创建时间、父子关系和已记录的进程身份。复用 PID、相似路径和外来镜像不能成为关闭目标。根进程退出后，已记录的子进程仍须逐个校验。

普通关闭只请求关闭窗口，失败时返回未完成；显式 `--force` 才允许强制停止。进程枚举失败或身份无法确认时状态保持未知，并拒绝破坏性操作。启动证明失败只清理本次能够确认归属的进程。

## 串行授权与协议

Windows `antigravity://` 是当前用户共享的协议。由于没有可靠的厂商授权关联标识，Next 一次只允许一个有效授权等待槽；已登录实例仍可并行运行。

回调先检查 URI、协议所有权、唯一等待槽、当前指纹和 Profile 活进程，再向既有实例交接。过期、未来时间或归属不明确的等待记录拒绝交接。诊断保存接收结果、阶段和固定故障码，不保存完整 URI、子进程 argv 或厂商输出。

注册保存此前处理器命令；重新注册同一 Next 保留原备份。官方启动将协议改回厂商处理器时，Next 仅依据自身已有记录重设；其他管理器的所有权不自动接管。还原只在当前命令仍属于 Next 时执行。真实 Chrome 登录回调的端到端验证尚未完成，不能用合成回调测试替代。

## 桥接、继承与迁移

Git 桥接在独立 home 中写受管 include，SSH 桥接创建指向宿主 `.ssh` 的目录链接。受管记录先写入，便于中断后重试。撤销核对当前项目仍由本工具拥有；删除链接本身，保留宿主目标，用户修改的冲突项保留。

配置继承使用有限的语义允许项，支持 JSONC。预览列出允许、冲突和跳过内容；应用保留目的文件冲突值与注释，使用备份、锁和原子写入。任意扩展设置、keybindings 参数和 snippets 正文不自动复制。

迁移先读取旧注册文件并写预览收据，再按同一源目录和注册文件摘要允许应用。它保留稳定 ID、标签、Workspace 元数据和允许的配置，不修改源，也不迁移认证数据、运行状态或兼容证明。

## 构建与发行

`Directory.Build.props` 为版本源；`global.json` 选择稳定 .NET 10 SDK。`Common.ps1` 统一 SDK 查找、环境、锁定还原、构建及控制台测试器。`Publish-Next.ps1` 运行测试后发布自包含 win-x64 GUI / CLI，写逐文件摘要清单和 ZIP，按需调用 Inno Setup 6。

便携目录的 `hub` 与 `cli` 必须保持在同一个包根目录。GUI 注册回调和创建快捷方式时定位该包的 CLI。搬迁包后必须重新注册回调。完整人工验收状态不随发行包或迁移传播。
