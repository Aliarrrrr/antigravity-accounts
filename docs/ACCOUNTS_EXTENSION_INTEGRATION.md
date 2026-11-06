# Accounts 原生插件列表与开关可行性

检查日期：2026-11-07。对象：Accounts `0.4.0-preview.10`、Antigravity App `2.29.0`、当前工作树提交 `bc0165e`。

**原生插件列表的发现、禁用和重新启用均可使用现有宿主功能；Accounts 的 HostAuth 生命周期尚未接入该开关。** 本次只确认可行性，没有向用户配置安装插件条目，也没有修改已安装客户端。不能把下面的原生列表结果表述为“Accounts 已支持扩展界面开关”。

## 原生入口

这里的入口是 Antigravity App 的 Customizations/Plugins，以及设置中的 Plugins 列表。它使用该 App 自己的插件清单与配置，不是 VS Code 扩展宿主。

2.29.0 随程序解压的官方说明 `app/builtin/skills/agy-customizations/docs/plugins.md`、`json_configs.md` 描述了以下约定：

- 插件位于自定义根目录的 `plugins/<目录名>/` 内，由 `plugin.json` 标识。项目可使用 `.agents/plugins/`；全局根目录为 `~/.gemini/config/`，也可通过 `plugins.json` 的 `entries` 引用其他插件目录。
- `plugin.json` 最少可包含 `name`。Accounts 现有清单还包含显示名、版本、描述和图标，见 [plugin.json](../packages/account-plugin/plugin.json)。
- 已发现插件通常默认启用；清单声明 `disabled: true` 时默认禁用。
- 用户选择保存在 `~/.gemini/config/config.json` 的 `plugins` 映射，键是**插件目录名**。显式配置优先于清单默认值。
- 禁用的插件仍保留在列表中，可以重新启用；其 skills、rules、hooks、MCP 等标准内容不再加载。

例如以下配置关闭 `plugins/antigravity-accounts/`：

```json
{
  "plugins": {
    "antigravity-accounts": { "enabled": false }
  }
}
```

从当前语言服务可执行文件中的前端 JavaScript 检查到，原生 `togglePlugin` 会取 `plugin.path` 最后一个目录名，调用：

```javascript
jetboxWriteState({
  userConfig: {
    plugins: { "antigravity-accounts": { enabled: false } }
  }
});
```

列表由 `getAllPlugins()` 或 `subscribeToPlugins()` 提供。对应本地 RPC 为：

```text
/exa.language_server_pb.LanguageServerService/GetAllPlugins
/exa.language_server_pb.LanguageServerService/JetboxWriteState
```

这些调用解决插件条目与配置状态，未提供独立账号的 HostAuth 注册或卸载接口。

## 隔离原生检查结果

在新建目录中复制当前 2.29.0 语言服务程序，以独立 home、AppData、临时目录和 `--gemini_dir` 启动。测试目录只放合成插件清单；账号及元数据响应由 `127.0.0.1` 上的本地服务返回。未打开官方 App 窗口，未读取真实账号、凭据或会话，未请求模型生成。

| 操作 | 原生返回 | 对应文件结果 |
| --- | --- | --- |
| 添加 `gemini/config/plugins/antigravity-accounts/plugin.json` 后调用 `GetAllPlugins` | HTTP 200；返回该条目，`isGlobal: true`，默认 `userConfig.enabled: true` | 插件清单位于新建目录 |
| 调用 `JetboxWriteState` 写入 `enabled: false`，重新取列表 | 两次请求均 HTTP 200；条目保留，`disabled: true`，`userConfig.enabled: false` | 合成 `gemini/config/config.json` 保存 `enabled: false` |
| 写入 `enabled: true`，重新取列表 | 两次请求均 HTTP 200；条目保留，`disabled` 省略，`userConfig.enabled: true` | 同一配置保存 `enabled: true` |

本地结果位于：

```text
artifacts/extension-integration-20261107/check-plugin-lifecycle.mjs
artifacts/extension-integration-20261107/run-e426a6af-25d4-4468-8227-1613be4feae2/result.json
```

前者可在 Windows 工作树内通过 `node artifacts/extension-integration-20261107/check-plugin-lifecycle.mjs` 重现；每次使用新目录。它需要本机已安装的 2.29.0 语言服务程序，复制后运行，不写回安装目录。`artifacts/` 不随源码提交，原生程序副本也不应加入仓库或发布包。

检查程序使用与 App 相同的 `--standalone=true`、`--subclient_type=hub`、`--override_ide_name=antigravity`。普通 standalone 模式没有初始化 Jetbox state store，写配置会返回 HTTP 500；`--use_mocked_data=true` 对此调用返回空成功结果，但未写配置，不能用它证明开关有效。

成功运行中，真实凭据读取为零、模型生成请求为零、拒绝外联的本地代理未收到请求；源程序文件 SHA-256 前后相同，测试子进程已退出。检查通过端点覆盖与代理拒绝外联，没有设置操作系统级网络隔离。程序 SHA-256 为 `169355a713b8199f6fe91dd5eaf81bc7f366544b2808014425fc820ccdd04bfc`，对应本机已安装的诊断适配副本。

这证明当前原生语言服务会发现条目并持久化启停偏好。**尚未在真实窗口中观察条目、开关交互或重新启动后的显示。**

## 当前 Accounts 为什么不随开关停用

[runtime.cjs](../packages/hostauth-adapter/src/runtime.cjs) 的 `prepare()` 从插件自己的 `hostauth-addon/config.json` 和 `host-provider.json` 读取配置。它依据曾启用的独立 provider 标记及组件返回的 `primaryAccountId` 选择 `native-provider`，并创建账号 IPC 与模型菜单 preload。它没有读取原生 `config.json.plugins["antigravity-accounts"].enabled`，也没有订阅原生插件变化。

[package-copy.mjs](../packages/hostauth-adapter/src/package-copy.mjs) 将 runtime 和 preload 放入 App 程序；[patch-plan.mjs](../packages/hostauth-adapter/src/patch-plan.mjs) 在 App 启动时加载它们，并将 `hostauthSession.usesHostAuth` 传给语言服务。该值决定语言服务启动参数是否包含 `--use_host_auth=true`，以及是否使用独立 provider 程序副本。这一接入不由标准插件加载器管理。

因此，仅把现有清单复制进全局 `plugins/` 会出现一个可开关的条目，但该开关**不会停止 Accounts 功能**。`plugin.json` 的 metadata、标准 hooks 或 MCP 启停均不能单独替代这些启动参数。

## 推荐的完整开关行为

建议使用原生插件配置作为“下次启动是否启用 Accounts”的来源，并明确显示“重启后生效”。完整实现需要同时处理以下行为：

1. 安装时注册无凭据的全局清单；更新和恢复仅管理安装器自己创建的条目，保留用户的 enabled 偏好及其他插件内容。
2. 在组件启动、获取独立账号 token 和启动语言服务**之前**读取开关。明确禁用时不启动账号组件、不注册账号菜单，并使用官方语言服务认证路径。原有 `host-provider.json` 继续保留，避免重新启用时把组件故障当作回到官方账号的理由。
3. 运行中关掉开关时，不直接关闭当前 token 来源，不中断正在执行的任务，也不把后续请求自动交给官方身份。当前语言服务仍按原认证方式完成这一轮运行；界面同时显示“关闭待重启生效”，让用户主动退出并重新打开。
4. 运行中重新打开同样显示“重启后生效”。需要重启的是 Accounts 与语言服务的认证接入，普通附属账号之间的现有切换仍按账号切换流程处理。
5. 开关文件损坏、目录不明或状态读取失败时，不能推断为“用户明确关闭”；应停止初始化并说明读取失败，沿用现有防止无意更换身份的行为。

还需要为原生条目补充重启提示或相应状态展示。当前原生通用开关只写入 enabled，没有 Accounts 专属的“等待任务结束后重启”回调；不宜让条目显示已关闭而继续无提示地使用独立账号。

完整实现后的客户端检查应覆盖：启用状态启动、关闭后的待重启提示、在途任务完成、关闭状态重启不启动独立组件、重新启用后恢复原独立账号，以及窗口中的条目和偏好持久化。真实身份与生成检查仍需用户授权具体账号、端点及请求范围；本报告的本地合成结果不覆盖这些行为。
