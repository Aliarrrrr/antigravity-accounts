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
| `tests/AGProfileHub.Tests` | Next、共享生产组件、Legacy 与 runner 分组；桌面适配器集成测试显式启用 |
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

`probe` 在全新的 GUID 无登录数据根目录启动官方应用，检查独立 home / user-data、文件令牌存储日志标记、进程停止和协议还原。通过后允许实验启动，人工验收时间仍为空。仅在进程已停止、协议已恢复且父级证明保存后清理本次 GUID 目录；删除前检查目录与链接边界，失败保留诊断文件。

设置中的 `ExecutableSha256` 是兼容字段；实际 Next 启动及回调检查的是 `compatibility.json` 的完整安装指纹，并使用 `NextCompatibility.HoldAsync` 持有真实组件文件租约。

实际启动为 Profile 准备独立环境变量和 `--user-data-dir`，由桌面启动适配器执行临时包装器。每次冷启动必须观察到本次启动生成的文件令牌存储标记、受管根进程及语言服务器子进程。旧日志或仅存在的目录不能代替本次证明。

## 进程归属与关闭

归属检查联合使用可执行文件、精确 user-data 参数路径、创建时间、父子关系和已记录的进程身份。复用 PID、相似路径和外来镜像不能成为关闭目标。根进程退出后，已记录的子进程仍须逐个校验。

普通关闭只请求关闭窗口，失败时返回未完成；显式 `--force` 才允许强制停止。进程枚举失败或身份无法确认时状态保持未知，并拒绝破坏性操作。启动证明失败只清理本次能够确认归属的进程。

## 串行授权与协议

Windows `antigravity://` 是当前用户共享的协议。由于没有可靠的厂商授权关联标识，Next 一次只允许一个有效授权等待槽；已登录实例仍可并行运行。

登录前操作按 Profile → login → protocol 获取文件锁，运行状态检查及等待槽写入都在锁内；停止和登录按同一顺序串行。protocol 锁按 Windows 用户共享，跨数据根生效。协议注册、状态和恢复全链 await，不在 WPF 线程同步等待文件锁。

回调先检查 URI、协议所有权、唯一等待槽、当前指纹和 Profile 活进程，再向既有实例交接。过期、未来时间或归属不明确的等待记录拒绝交接。交接进程的创建时间从持有的原始进程句柄读取；身份无法确认时保留槽且不终止猜测的 PID。失败、超时或原根身份改变均保留等待槽。诊断保存接收结果、阶段和固定故障码，不保存完整 URI、子进程 argv 或厂商输出。

注册保存此前处理器命令；重新注册同一 Next 保留原备份。初次注册允许空处理器、本实例或精确匹配配置厂商且参数形状受支持的处理器；其他处理器拒绝接管。变更前允许取消，开始后完成非取消持久化或补偿，无法确认补偿成功时保留恢复记录。官方启动改回厂商处理器时，Next 仅依据锁内已有记录重申。还原前重查所有权，无记录或第三方接管安全跳过，真实失败保留记录并阻止卸载。真实 Chrome 登录回调的端到端验证尚未完成，不能用合成回调测试替代。

## 桥接、继承与迁移

Git 桥接在独立 home 中写受管 include，SSH 桥接创建指向宿主 `.ssh` 的目录链接。受管记录先写入，便于中断后重试。撤销核对当前项目仍由本工具拥有；删除链接本身，保留宿主目标，用户修改的冲突项保留。

`NextHubService.CreateAsync` 直接保存关闭策略，WPF 创建默认 off、实验 Rust GUI 共享默认不勾选且缺省 payload 为 off。清单缺失或 `enabled` 缺失按 false，已有明确 true/false 保持兼容。损坏 JSON、无效 schema 或无效受管项拒绝处理且不替换记录。缺少所有权记录时不删除未知旧项目，默认 off 不提供 OS 文件访问隔离。

配置继承使用有限的语义允许项，支持 JSONC。预览列出允许、冲突和跳过内容；应用保留目的文件冲突值与注释，使用备份、锁和原子写入。任意扩展设置、keybindings 参数和 snippets 正文不自动复制。

迁移先读取旧注册文件并写预览收据，再按同一源目录和注册文件摘要允许应用。它保留稳定 ID、标签、Workspace 元数据和允许的配置，不修改源，也不迁移认证数据、运行状态或兼容证明。

新迁入 Profile 不继承宿主桥接，缺清单按 off；已有目标桥接策略保留，不改变明确 on 的存量行为。

## 构建与发行

`Directory.Build.props` 为版本源；`global.json` 精确选择 .NET SDK 10.0.400，禁止自动滚动。开发入口要求 PowerShell 7，发行包桌面适配器仍使用 Windows 内置 PowerShell。`Common.ps1` 统一 SDK 查找、环境、锁定还原、构建及控制台测试器。

`Publish-Next.ps1` 先运行锁定构建和默认测试，再选择项目本地 win-x64 自包含发布锁做锁定还原，无非锁定重算或锁文件恢复。完整应用包集合、版本和 NuGet 内容哈希须与测试图一致，实际 assets / deps 也须匹配。运行时及平台包单独记录；产物包含提交、SDK、锁文件及依赖摘要。Inno Setup 固定 6.7.3，CI 从官方固定 release 下载并核对固定 SHA-256，以 portable 模式放入本次 runner 的独立目录；数字版本取 GUI FileVersion。

PR CI 在干净 Windows runner 执行系统测试、便携包与安装 / 卸载冒烟。发布分为只读验证 job 和写权限上传 job；后者仅下载前者的验证产物。Actions 固定完整 SHA，checkout 不保留凭据，并设置超时及并发控制。

卸载器默认调用 CLI `rollback`；显式 `/HubDataRoot=<绝对盘符或UNC路径>` 经非空、重复、引号/控制字符及完整绝对路径检查后，调用 `rollback --data-root "<路径>"`。引用按 Windows argv 规则转义尾反斜线，无效参数不启动回滚或卸载。回滚继续只枚举选定 HubPaths，协议还原保留每用户共享锁和所有权核验。`tests/InstallerArguments.iss` 直接运行产品参数函数并在 InitializeSetup 结束前退出，避免安装/注册副作用。

便携目录的 `hub` 与 `cli` 必须保持在同一个包根目录。GUI 注册回调和创建快捷方式时定位该包的 CLI。搬迁包后必须重新注册回调。完整人工验收状态不随发行包或迁移传播。
