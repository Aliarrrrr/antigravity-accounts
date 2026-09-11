# 核心运行时节点 1：受控探针

2026-09-09。本节点在已冻结的取证结论之上实现；不是完整产品恢复，也不是生产级常驻 LS supervisor。

## 已实现

- `AgentRuntimeProbe` 启动已取证 SHA-256 的原版 LS；未知二进制拒绝运行。运行期间保持禁止写入/删除共享的二进制句柄。
- 每次生成独立目录、随机 CSRF 和动态 HTTP 端口，仅使用 `--use_mocked_data`。不打开任何注册 Profile。
- 通过 Windows TCP owner 表检查回环监听器的 PID，并用保留的进程对象及创建时间核对身份；在 CLI 调用前后再次验证。
- 调用官方 `language_server.exe agentapi get-conversation-metadata`，查询全零会话 ID。没有自行猜测 REST/protobuf，也没有连接 HTTPS UI 端口。
- stdout 限额 64 Ki 字符，stderr 和 LS 输出仅排空，不落盘。公开结果仅包含监听归属、退出码、固定枚举分类。
- 总运行期限 30 秒，Ctrl+C 可取消；正常退出、异常和取消路径执行进程树终止及等待。保留独立 scratch 供本地检查，不删除用户目录。

## 使用

```powershell
./.tools/dotnet/dotnet.exe run --project src/AGProfileHub.Cli -- agent-probe --json
```

可用 `--data-root <目录>` 指定数据根。scratch 位于其 `agent-probes/<随机 ID>` 下。厂商自行生成的文件仍应视为本地敏感数据，Hub 不主动保存令牌或原始输出。

## 本机验收

- 全解决方案 build：0 警告、0 错误。
- 回归测试：29/29 通过（新增 envelope 分类组包含 8 个用例）。
- 真实、哈希匹配的 LS mocked-data 探针：`listenerOwned=true, exitCode=1, outcome=remote-error`。
- 该结果证明监听归属检查和官方 CLI 的 JSON envelope 通路完成；**不证明会话成功、认证成功或语义 readiness**。错误内容未输出，不能将它重新命名为已确认的 NotFound。
- Hub 对 remote-error 返回非零退出码；不以“收到 JSON”冒充成功。

## 明确限制与下一节点

1. 当前是一次性探针，不是长期会话服务；未实现 new/send，也未接 GUI。
2. 端口释放到 LS bind 存在竞争；当前拒绝/超时，不自动重试。端口核对也不是对恶意同用户进程的安全隔离边界。
3. `Kill(entireProcessTree)` 无法保证根进程提前退出后的孤儿回收。下一节点需要 suspended launch + Windows Job Object、退出/取消/超时及 PID 复用故障注入，然后才能提升为常驻 supervisor。
4. 仅验证元数据负向请求；下一节点需确认结构化错误码、正向语义探针、错误/缺失 CSRF、限额/超时测试。不能靠模糊字符串推断 readiness。
5. 环境目录重定向不等于凭据隔离；DirectSerial 和 Google-account-incompatible 限制未改。Host Bridge、OAuth、多账号仍未解决。

按用户的阶段停点要求，本次停在“C# 探针接通官方 CLI 并完成回归”节点。遵循 Karpathy 指南，改动局限于探针、CLI 入口及测试，没有重构已有 Profile 生命周期。
