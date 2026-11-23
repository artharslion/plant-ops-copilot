# AI 模型接入与本地密钥

工作台通过 `PlantOps.AI` 的 Chat Completions 适配或 Fake Provider 执行调查。模型模式、管理员权限与已排队调查的行为见 [工作台 AI 设置](AI_SETTINGS.md)。调查输出以 [v3 契约](INVESTIGATION_RESPONSE_V3.md) 为准。

## 密钥放哪里

将仓库根目录的 [appsettings.Ai.example.json](../appsettings.Ai.example.json) 复制为 `appsettings.Ai.Local.json`。在本地编辑器填入 `AI.ApiKey`，并核对 `AI.BaseUrl` 和 `AI.Model` 与获授权使用的服务一致。示例填写了当前中转地址、模型和短时验证预算。

`appsettings.Ai.Local.json` 已由 Git 忽略，与现有 `appsettings.Local.json` 分开。密钥通过服务端注入。请勿将密钥放入聊天、浏览器代码、命令参数、日志或提交记录。

## 如何使用

在用于启动 API 和 Worker 的各自终端加载配置。

```powershell
. ./deploy/Use-LocalDatabase.ps1
. ./deploy/Use-LocalAi.ps1
```

`Use-LocalAi.ps1` 将允许的字段载入当前终端的 `AI__*` 环境变量，并清除缺失字段的旧值。API 和 Worker 通过这些变量加载配置。生产环境由部署系统注入环境变量。配置载入后按本地开发指南启动服务。 [本地开发指南](DEVELOPMENT.md)

数据库没有模式记录时使用进程配置的 `AI.Provider`，默认 `Fake`，不会为调查访问模型服务。已保存的工作台模式优先。选择 `OpenAiCompatible` 后，密钥或服务不可用会令真实调查失败，普通业务仍可启动。

## 接口能力检查

离线探针使用虚构响应，不调用模型服务。

```powershell
./deploy/Test-AiProvider.ps1 -Fake
```

获得本次真实调用授权后，去掉 `-Fake` 可使用本地配置检查服务。用 `-Run <新名称>` 保存独立报告，名称由小写字母、数字和下划线组成。脚本在结束后恢复原环境变量。报告写入 `artifacts/ai-provider/<run>/result.json`，包含状态、错误代码、用量与耗时，省去回答正文和凭据。

探针用虚构数据检查普通回复、强制工具调用、合成工具结果续答和严格结构化输出。当前严格结构化请求使用 `investigation_v2`，其中 `schemaVersion` 固定为 `2`，而探针提示仍要求 `1`。解读该项结果时应计入这处冲突。工作台调查使用独立的 v3 契约。

`CaseBudgets` 分别记录普通回复、工具往返和结构化输出的预算。限值来自探针进程的配置，三个用例共享整体时限。使用示例配置时，套件 60 秒，单次等待 30 秒，各用例限 16,000 输入和 2,000 输出 token。重试占所属用例的预算。Fake 报告标为 `Simulated=true`，只有属于同一轮真实探针的四项均通过才标记 `RealProviderVerified=true`。

## 预算、兼容性与失败

- 根目录 Docker Compose 的业务调查默认限 600 秒、4 轮、8 次工具调用、32,000 输入和 6,000 输出 token。库默认限 180 秒、4 轮、8 次工具调用、16,000 输入和 2,000 输出。初始设备摘要占 1 次工具预算。模型可按适用的预算从 5 个读工具中选择，进一步取证受最终回答预留限制。
- 单次请求等待默认 120 秒，可配为 1–180 秒且须在整次限时内。根目录 Compose 通过 `AI_REQUEST_TIMEOUT_SECONDS` 和 `AI_RUN_TIMEOUT_SECONDS` 覆盖默认值。Worker 租约使用整次限时加 30 秒。
- 根目录 Compose 默认启用 `AI_STREAM_RESPONSES=true`。SSE 响应头受单次等待限制，收到字节后重计连续空闲时限，调查硬期限持续计时。非流式模式按整个请求计时。SSE 以完成事件和 `[DONE]` 收尾，之后校验结构、用量及调查事实。日志记录阶段和计数。响应缓冲或超时可导致失败，且无自动流式降级。
- 输入按请求 UTF-8 字节和协议开销预留。有效的服务商用量替换该次预留，未知尝试继续占预算。实际报告超限时停止后续请求。`InputTokens` / `OutputTokens` 同时包含已报告和未知预留，核算口径见 [调查诊断](INVESTIGATION_DIAGNOSTICS.md)。未配置服务商单价时费用未知。
- Chat Completions 适配发送 `max_completion_tokens`，默认 `reasoning_effort=low`。使用配置的 `ReasoningEffort` 调整，中转服务对模型、工具、结构化输出和用量的支持须按实际接口核对。
- 端点须使用 HTTPS，本机回环可用 HTTP。适配禁用重定向和 Cookie，响应默认上限为 128 KiB，可配为 1–256 KiB。模型只可调用当前请求所列工具，业务服务再次校验参数、账号和设备范围。
- 瞬态网络及服务错误最多重试一次，重试重新预留预算。剩余额度不足以重试时保留原 `Unavailable` 或 `RateLimited` 错误。首次预留失败或供应方报告实际超限时返回预算错误。超时、鉴权失败或输出校验失败不会触发重跑调查。
- 发送 HTTP 前的临时 DNS `TryAgain` 最多查询三次，间隔 300 和 600 毫秒。连接阶段受 20 秒及更短的请求或调查取消期限限制。连接准备不会重放模型 POST。取消本地等待后，上游用量仍可能未知。

历次失败和初始探针结果见 [历史验证](archive/AI_PROVIDER_PROBE_20261009.md)。
