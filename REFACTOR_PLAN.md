# 重构方案 — 降低复杂度三件事（沙盒 / 上手 / GUI）

> **状态（2026-09-10）：P0-1 / P0-2 / P0-3 / P1-1 / P1-2 已全部落地**（检查点 cp1…cp5，测试 38/38，构建 0 警告 0 错误）。P2（LS 文件 token 存储开关）未做，仍为探索项。命令表化（`ICliCommand` 字典重构）未做，改用轻量新动词层实现，见 P1-1 注记。

对象：`D:\mutigravity`（v0.2.3，master @ 40fea45）。三个痛点的共同根因是同一个：**外部编排没有收进 Hub**。Sandboxie 的建盒、直写、服务重载被拆成脚本 + 手工提权 + 人工确认，暴露成 23 个子命令和一行 12 个按钮。因此本方案不改变隔离原理（keyring 占位 + 盒内声明），只做三件事：把外部编排收进内部状态机、把命令面收敛到 8 个、把 GUI 从 code-behind 控件堆换成有设计系统的界面。

## 现状证据

| 痛点 | 证据 | 后果 |
|---|---|---|
| 沙盒复杂 | `IsolatedProfileLauncher.WriteThroughPaths()` 返回 5 条路径；`Set-IsolationWriteThrough.ps1` 需管理员 + 停全部盒子 + 重启 `SbieSvc`；`WindowsSandboxieRuntime.EnsureBoxAsync` 写 `OpenFilePath.{n}` 索引形式（驱动不认，与 ps1 里的裸 key 写法冲突，同一份 ini 解析在 C# 与 ps1 各写一遍） | 建一个 Profile 要 6 步、2 次手工提权、1 次服务重启 |
| 使用复杂 | `CliApplication.ExecuteAsync` 是一个 150 行 switch，23 个子命令；所有命令强制 UUID；`iso-prepare` / `--fresh` / `iso-writethrough` / `iso-status` / `credential-shim` 全部暴露给用户 | README 光"完整隔离配置教程"就 10 步 |
| GUI 简陋 | `MainWindow.xaml` 无资源字典，硬编码 `#F7F8FA` / `#FFF4D6`；顶部 9 个按钮 + 每行 12 个按钮 `WrapPanel`；`MainWindow.xaml.cs` 手写 `Prompt()` 弹窗；顶部横幅文案仍是 0.1.2 的"unsupported"口径 | 视觉原始，主次操作无区分 |

---

## 一、沙盒：从 6 步 2 次提权 → 3 步 0 次手工提权

### 1.1 提权内建（消除 2 次手工提权）

新增 `src/AGProfileHub.Core/Isolation/ElevationBridge.cs`：

```csharp
public sealed record ElevationRequest(string Operation, string BoxName, IReadOnlyList<string> Paths);
public sealed record ElevationResult(bool Succeeded, string? Detail, string? BackupPath);

public interface IElevationBridge
{
    bool IsElevated { get; }
    Task<ElevationResult> RunAsync(ElevationRequest request, CancellationToken ct = default);
}

public sealed class WindowsElevationBridge : IElevationBridge
{
    public bool IsElevated { get; }
    public Task<ElevationResult> RunAsync(ElevationRequest request, CancellationToken ct = default)
    {
        // 1) 把 request 序列化到 %LOCALAPPDATA%\AntigravityProfileHub\runtime\elevate-<guid>.json
        // 2) ProcessStartInfo { FileName = 自身 CLI, Verb = "runas", UseShellExecute = true }
        //    ArgumentList = [ "admin-helper", "<json path>" ]
        // 3) 等待退出，读回同名 .result.json；UAC 被拒绝 → HubException("elevation_declined")
    }
}
```

CLI 新增内部子命令 `admin-helper <request-json>`（`Program.cs` 顶部，与 `credential-shim` 同层，不出现在 help 里），把 `New-IsolationBox.ps1` 与 `Set-IsolationWriteThrough.ps1` 的逻辑平移进来：

- 建盒：`SbieIni.exe set <box> Enabled y` + 四项保守设置，失败带 `sandbox_create_failed`；
- 直写：改 `C:\Windows\Sandboxie.ini`（裸 key 重复行、UTF-16、先备份）→ 停止/启动 `SbieSvc` → `Start.exe /reload`（**必须提权，非提权 `exit=0` 但什么都不做**）→ 校验落盘 → 失败回滚备份。

不再向用户打印"请复制这条命令"。`IsolatedProfileLauncher.PrepareAsync` 的 catch 分支（现在拼一段让用户跑 ps1 的提示）改为直接 `_elevation.RunAsync(...)`。

保留 `scripts\*.ps1` 作为兜底，但 README 里删掉第三、七步。

### 1.2 直写路径 5 → 2（消除"改 workspace 就要重配"）

`WriteThroughPaths()` 现在返回 `home/roaming/local/temp/user-data` 五个子目录，加 workspace 共 6 条规则，改一处处处重配。改为返回两级：

```csharp
public IReadOnlyList<string> WriteThroughPaths(Guid profileId, string? workspacePath = null)
{
    var result = new List<string> { _paths.ProfileRoot(profileId) };   // 一条覆盖 home/roaming/local/temp/user-data
    if (!string.IsNullOrWhiteSpace(workspacePath)) result.Add(Path.GetFullPath(workspacePath));
    return result;
}
```

代价：整棵 Profile 根变为直写，沙箱不再保存 Profile 数据副本，只隔离凭据与注册表。这符合当前模型（Profile 数据本来就在 `%LOCALAPPDATA%\AntigravityProfileHub\profiles\`，不是安全边界），但要在 `SECURITY.md` 明确写一句。

### 1.3 ini 解析单一实现（消除两份格式逻辑）

新增 `src/AGProfileHub.Core/Isolation/SandboxieIniFile.cs`，把 `SandboxieBackend.GetIndexedSettingValues`（108–154 行，含 BOM 探测与两种 key 拼写）与 `Set-IsolationWriteThrough.ps1` 的 `Read-SandboxieConfiguration` 合并为一个实现：

```csharp
public sealed class SandboxieIniFile(string path)
{
    public static SandboxieIniFile OpenSystem();
    public IReadOnlyList<string> GetValues(string box, string key);          // 兼容 Key= 与 Key.N=
    public void ReplaceValues(string box, string key, IReadOnlyList<string> values); // 原子替换 + .bak
    public void Save();                                                       // 保留原始编码与 BOM
}
```

`SandboxieBackend` 改为调用它；ps1 退化为 `agprofile admin-helper` 的薄包装。**这是目前最容易出 bug 的一处重复**——`WindowsSandboxieRuntime.EnsureBoxAsync` 写的 `OpenFilePath.{n}` 索引形式已被证实驱动不认，而 ps1 写裸 key，两条路径并存意味着"看起来配好了"的假象随时能回来。

### 1.4 幂等准备状态机（让"补配置"变成一次点击）

新增 `src/AGProfileHub.Core/Isolation/IsolationSetup.cs`：

```csharp
public enum IsolationStage { NotStarted, BoxMissing, ElevationRequired, KeyringPending, WriteThroughMissing, Ready }

public sealed record SetupStep(IsolationStage Stage, bool Applied, string? Detail);

public sealed class IsolationSetup(IIsolationBackend backend, IElevationBridge elevation, IProfileIsolation isolation)
{
    public Task<IsolationStage> DetectAsync(Guid profileId, CancellationToken ct = default);
    public Task<IReadOnlyList<SetupStep>> EnsureReadyAsync(Guid profileId, bool allowFreshBox, CancellationToken ct = default);
}
```

每一步幂等可重入：已建盒跳过、已声明 keyring 跳过、直写已全跳过。这样 `hub open` 可以在启动前自动补齐，而不是抛 `isolation_not_prepared` 让用户回去翻 README。

`--fresh` 语义保留但收窄：`EnsureReadyAsync` 只在检测到"盒子存在但未登记"时要求一次显式确认（GUI 弹确认框、CLI 需 `--fresh`），不再让用户判断什么时候该加这个参数。

---

## 二、上手：23 个子命令 → 8 个，UUID → 名字

### 2.1 命令表化

`CliApplication.ExecuteAsync` 的 switch 换成命令表：

```csharp
internal interface ICliCommand
{
    string Name { get; }
    string Usage { get; }
    bool Hidden { get; }              // credential-shim / admin-helper / iso-* 标 Hidden
    Task<object?> ExecuteAsync(HubService hub, CommandArguments args, CancellationToken ct);
}
```

`Program.cs` 只保留解析、分发、退出码、错误 JSON。新增命令不再改动 switch。目录：

```
src/AGProfileHub.Cli/
  Program.cs                      // 仅解析与分发
  Commands/SetupCommand.cs  NewCommand.cs  OpenCommand.cs  CloseCommand.cs
           ListCommand.cs  StatusCommand.cs  DoctorCommand.cs  FixCommand.cs
           Hidden/AdminHelperCommand.cs  CredentialShimCommand.cs  Iso*.cs
```

### 2.2 面向用户的 8 个命令

| 命令 | 等价于现在的 | 说明 |
|---|---|---|
| `hub setup` | `doctor` + 环境自检 | 首次运行向导，CLI 版；检查 Antigravity / Sandboxie / 版本 |
| `hub new <名字> [--workspace <path>]` | `create` + `iso-prepare` + `iso-writethrough` | 一次做完，提权自己弹 |
| `hub ls` | `list` | 表格含名字、状态、隔离、workspace |
| `hub open <名字|id>` | `launch` + 前置补齐 | 缺什么补什么 |
| `hub close <名字|id> [--force]` | `stop` | |
| `hub status [名字]` | `status` | |
| `hub doctor` | `doctor` | 保留 |
| `hub fix <名字>` | `iso-status` + `iso-writethrough` + 自动修复 | 诊断 + 自愈，替代排障表格 |

`iso-prepare`、`iso-writethrough`、`iso-status`、`iso-launch`、`iso-stop`、`credential-status`、`credential-shim`、`launch-all`、`stop-all`、`rename`、`workspace`、`shortcut`、`open-data`、`open-logs`、`configure-executable`、`agent-probe` 全部保留实现，标 `Hidden = true`（`launch-all` 仍保留可见别名，GUI 用）。**只删门面，不删能力**——`HubService` 与 `IProfileIsolation` 的公开面不动，34/36 条测试因此不受影响。

### 2.3 名字寻址

`ProfileManager` 新增：

```csharp
public Task<ProfileRegistration> ResolveAsync(string nameOrId, CancellationToken ct = default);
```

规则：UUID 精确 > 显示名精确 > 唯一前缀；0 匹配抛 `profile_not_found`，多匹配抛 `profile_name_ambiguous` 并列出候选。这是当前最大的单点上手成本——所有命令现在只吃 32 位 UUID。

### 2.4 交互式确认收敛

`--fresh`、`--force`、`-ConfirmAllBoxesStopped` 三个确认合并成一个语义：仅在"可能覆盖既有登录态"时要求一次确认，措辞固定为"该隔离盒从未登录过 Antigravity，确认接管？"，不再让用户理解 `--fresh` 是什么意思。

---

## 三、GUI：从默认控件堆到有设计系统的界面

### 3.1 结构

```
src/AGProfileHub.Gui/
  App.xaml                        → 合并 Themes/Theme.xaml + Styles/Controls.xaml
  Themes/Theme.xaml               → 色板（Light/Dark 两套 DynamicResource）
  Styles/Controls.xaml            → Button / DataGrid / Card / InfoBar / Badge / Menu
  Converters/StateConverters.cs   → StateToBrush, StageToBadge, BoolToVisibility
  Views/ProfileCard.xaml          → 单卡：名字 + 状态徽章 + 隔离态 + workspace + 主操作
  Views/SetupWizardWindow.xaml    → 首次启动向导（对应 hub setup）
  Views/DiagnosticsWindow.xaml    → doctor 结果（替掉现在的 MessageBox 纯文本）
  ViewModels/MainViewModel.cs     → 从 code-behind 迁出，RelayCommand 手写，不引第三方
  MainWindow.xaml / .cs           → 只剩布局与 DI 装配
```

不引入 Prism / MVVM Light / MaterialDesign。WPF 自带资源字典 + 手写 `RelayCommand` 足够，避免给自包含包再叠依赖。

### 3.2 具体改动

1. **色板**：`Theme.xaml` 定义 `Brush.*`（Surface / SurfaceAlt / Border / TextPrimary / TextSecondary / Accent / Success / Warning / Danger），所有硬编码 `#F7F8FA`、`#FFF4D6`、`#624B00` 全部替换。Dark 用 `System.Windows.Forms.SystemInformation` 或 `HKCU\...\AppsUseLightTheme` 检测，默认跟随系统。
2. **顶部横幅 → InfoBar 组件**：不再是写死文案，由 `IsolationRuntimeStatus` + `DoctorReport` 动态生成变体（Ready / Warning / Error），可关闭。现在的文案与 0.2.3"已在指定版本人工验收"的口径矛盾，属于必须修的内容错误。
3. **操作收敛**：行内 12 个按钮 → `Open` + `⋯`（ContextMenu）。次操作（Rename / Workspace / Data / Shortcut / Remove / Force stop）进菜单，主操作永远在最左。
4. **DataGrid → 卡片列表**：`ItemsControl` + `ProfileCard`，每张卡显示名字、状态徽章（Running / Stopped / Unknown 三色）、隔离态（Ready / Write missing / Direct / serial）、Workspace、主操作按钮。窄窗口自动换行（现在的 `WrapPanel` 按钮墙在 1080 宽下已经挤成两行）。
5. **状态徽章**：`IsolationStage` 映射成带色 chip，替掉纯文本 `Isolation` 列。
6. **手写 `Prompt()` 删除**：换成 `Views/InputDialog.xaml`，带占位符、校验、Esc 取消、Enter 确认，且走主题。
7. **刷新并行化**：`RefreshAsync` 现在对每个 Profile 串行 `GetStatusAsync`（N 个 Profile = N 轮进程扫描）。改为 `Task.WhenAll` + 5 秒节流，刷新期间界面不再整体 `IsEnabled = false`（改成顶部细进度条 + 仅禁用当前操作按钮）。
8. **空状态**：没有 Profile 时显示引导卡（"创建第一个 Profile →"），不是空表格。

---

## 四、落地顺序

| 阶段 | 内容 | 风险 | 验收 |
|---|---|---|---|
| P0-1 | `SandboxieIniFile` 单一实现 + 修 `EnsureBoxAsync` 索引形式 | 低 | 现有 36 测试全绿；新增 ini 解析往返测试（UTF-16 / UTF-8 BOM / 裸 key / 索引 key 四种） |
| P0-2 | `WindowsElevationBridge` + `admin-helper` 子命令 | 中（UAC） | 手动：非提权父进程能拉起提权子进程；拒绝 UAC 返回 `elevation_declined` 且不改配置 |
| P0-3 | `IsolationSetup` 状态机 + 直写路径收敛为 2 条 | 中 | 幂等测试：对同一 Profile 连跑 3 次 `EnsureReadyAsync`，第二次起应全部 `Applied=false` |
| P1-1 | CLI 命令表化 + 8 命令门面 + 名字寻址 | 中 | `hub --json` 输出契约不变（脚本依赖）；新增 `ResolveAsync` 歧义测试 |
| P1-2 | GUI 主题 / 卡片 / InfoBar / 空状态 | 低（纯表现） | 截图对比 + 手动冒烟：创建、启动、停止、提权路径 |
| P2 | 探索 LS 文件 token 存储开关（根治沙盒依赖） | 高，探索性 | 若能找到让 `language_server` 走文件 token store 的开关，Sandboxie 可整体下架 |

P2 说明：取证已确认 LS 存在 `Failed to load stored token from keyring, falling back to file` 降级路径，且 headless 分支会把"使用文件令牌存储"作为可选项传入。若能定位到该选项的开关（CLI 参数或环境变量），就可以用"每 Profile 独立数据目录 + 文件 token"替代沙箱隔离，届时 `Sandboxing/` 与 `Isolation/` 整棵树可删。这是唯一能真正回答"沙盒太复杂"的方向，但当前不作为 P0/P1 的依赖。

## 五、明确不做

- 不改动隔离原理（keyring 占位 + 盒内声明），不碰凭据内容的读写边界；
- 不删除 Direct 后端与全局 launch barrier——它是 Direct 路径的安全兜底，删了会让 `ProfileBackend.Direct` 那批既有 Profile 失去保护；
- 不引入第三方 UI 框架或 MVVM 库；
- 不自动化 Google 登录，不代理认证。
