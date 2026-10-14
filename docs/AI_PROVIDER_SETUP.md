# AI 模型接入与本地密钥

当前增量：独立 `PlantOps.AI` 已提供 Chat Completions 适配、Fake、调用预算和版本化输出；[P3.2 授权资料检索](AI_EVIDENCE_VALIDATION.md) 与 [告警业务调查接入](AI_INVESTIGATION_VALIDATION.md) 已实现。现在告警页经 API/AiRun/Worker 实际调用 Gateway，保存并展示授权证据及校验结果。135项专项/回归通过；早先真实超时记录保留，[IndustrialSim联合演示](INDUSTRIALSIM_AI_VALIDATION.md) 的一次真实业务调查约11.22秒成功。响应稳定性及完整P3质量验收仍未通过。

## 密钥放哪里

本机文件：`D:\Repos\plant-ops-copilot\appsettings.Ai.Local.json`。

将 key 填在 `AI.ApiKey`。地址为 `https://zzone.cc.cd/v1`，模型按用户确认使用 `gpt-6.1-sol`。这个文件已加入 `.gitignore`，不会提交；已有的 `appsettings.Local.json` 继续保存账号密码，互不覆盖。

新环境先复制根目录 `appsettings.Ai.example.json` 为 `appsettings.Ai.Local.json`，再用本地编辑器填写密钥。不要将密钥粘贴到聊天、浏览器代码、命令参数、日志或提交记录。

## 如何使用

在启动 Api/Worker 的各自终端中执行：

```powershell
. ./deploy/Use-LocalDatabase.ps1
. ./deploy/Use-LocalAi.ps1
# 随后按 README 启动 Api 或 Worker
```

`Use-LocalAi.ps1` 只读取白名单字段到当前终端的 `AI__*` 环境变量，不输出配置内容；缺失字段清掉终端旧值，由程序默认值补充。Api/Worker 不自动寻找根目录私密文件，必须加载这个脚本或自行注入环境变量。生产环境应由部署系统注入，不使用本地脚本。

不加载 AI 配置时默认 Fake，不联网。明确配置 OpenAiCompatible 后，缺 key/失败会明确报错，不自动变成 Fake 成功。验证发生在调用时，密钥缺失、站点离线或无效模型不会阻止普通业务启动。

## 接口能力检查

无密钥检查（不访问外网）：

```powershell
./deploy/Test-AiProvider.ps1 -Fake
```

填写 key 后真实检查：

```powershell
./deploy/Test-AiProvider.ps1
```

可以加 `-Run <全新小写后缀>` 保存到独立目录。探针会自动加载本地 AI 文件，运行后恢复原环境变量。每次最多 60 秒，单个请求最多 30 秒；报告位于 `artifacts/ai-provider/<run>/result.json`，不会覆盖旧报告。

它检查四项能力：普通回复、一个强制只读工具调用、合成工具结果续答、InvestigationResult.v1 strict JSON Schema。只发送原创虚构数据（`demo-p101`、`12.6 mm/s`、`E1`），不连接数据库、读取文档或执行业务动作。报告包含能力状态、安全错误代码、用量和耗时，不含正文或 key。

四项能力属于三个独立测试用例：问候、工具往返、结构化输出。每个用例各用一份调查预算（16,000 输入/2,000 输出），整个套件仍共用 60 秒总时限；报告 `CaseBudgets` 是各用例预算，顶层计数为套件合计，不能把它当一次业务调查。默认四次请求最多预留 1,712 输出 token；允许瞬态请求各重试一次，重试也占所属用例预算。

Fake 报告始终 `Simulated=true`、`RealProviderVerified=false`。只有真实四项均通过才记录真实兼容成功；这仍不是 AI 调查/授权/20 案例验收完成。

## 预算、兼容性与失败

- 每次业务调查预算为 60 秒、4 轮、8 工具、16,000 输入、2,000 输出，单次请求默认 30 秒、可显式配置 1–60 秒且仍受总期限约束，瞬态重试最多一次。有限 IndustrialSim 联调显式使用 55 秒/零重试。已实现的调查编排每次新建并贯穿整次运行；默认五个取证工具也计入工具预算。
- 输入先按请求 UTF-8 字节和协议开销保守预留。2026-10-12起，有效供应商usage替换本次预留（可上调或释放余量）；缺失usage、超时/错误尝试仍保留完整预留。重试分别计入，禁止重复/不匹配结算。中转可能附加上游提示，报告实际用量超限即停止，不再发请求。持久化InputTokens/OutputTokens因此是已知实际用量与未知尝试预留之和，不能全部当成实际账单。
- 发送 `max_completion_tokens`，默认 `reasoning_effort=low`，不发 temperature/top_p。可在本地 JSON 加 `ReasoningEffort` 调整；空字符串表示不发送这个可选字段。低推理强度是官方支持的 Sol 设置，有助于减少延迟，但不保证中转站响应时间。
- 不假设中转名称、模型实现、数据保留和计费与官方一致。OpenAI 官方要求 GPT-6.1 Sol 的工具调用使用 Responses；本适配按用户提供的 Chat Completions 协议验证中转站自身兼容行为，不能据此声称官方 Chat Completions 支持 Sol 工具调用。[官方模型说明](https://developers.openai.com/api/docs/models/gpt-6.1-sol)、[推理设置](https://developers.openai.com/api/docs/guides/reasoning)。
- HTTPS，HTTP 仅允许本机；禁重定向和 Cookie，防止凭据跟随重定向。响应流最多 128 KiB（可设 1–256 KiB），JSON 深度和工具数量有界。模型工具名必须属于当前请求提供的定义；业务调查引擎另行校验参数，服务端固定 actor/设备范围并实时重授权。
- 限流、鉴权、协议不支持、格式错误、拒绝、截断、取消和超时分别处理。不会自动换模型、去掉 schema 或重试授权错误。错误不含供应商正文或原始异常。
- 使用量缺失或尝试结果未知时，汇总 usage/cost 保留未知。未配置并验证中转单价，`EstimatedCost=null`，不标记为免费。取消/超时只能停止本地等待，不能保证上游停止计算或不计费。
- 业务调查另行验证实时 actor/资料权限，observations 必须逐字选用证据事实句，将数字、单位、时间一起校验。它是保守摘录契约，不证明通用自然语言推理正确；详见调查验收记录。供应商协议层自身仍只负责结构与引用集合检查。

## 验证记录

实现和首轮验证日期：2026-10-09。

- 锁定依赖还原和解决方案构建通过，零警告/错误；最终 70 项（64 项新 AI 测试、6 项既有健康检查）全部通过，零失败/跳过，报告 `artifacts/ai-provider-tests/foundation_final.trx`。本轮未重跑需要数据库的全量回归，也未变更业务表或迁移。
- Fake 四项能力通过，报告 `fake_foundation_20261009/result.json`。
- 真实首轮 `sol_live_20261009_1`：超时及输入用量对账失败。第二轮 `sol_live_20261009_2`：工具调用/续答通过，普通回复/schema 超时。失败证据保留，不算通过。
- 低推理强度轮次 `sol_live_low_20261009_4`：普通回复、工具调用和续答通过；将三个独立测试误合到同一份 16,000 输入预算导致 schema 在发送前被阻止。现已分成独立测试预算，业务调查上限保持不变。站点报告三次输入分别为 4,399 / 4,539 / 4,629 token。
- 独立用例轮次 `sol_live_cases_20261009_5`：普通回复/工具调用/strict schema 通过，工具续答超时。schema 返回 89 输出 token，并通过完整字段和 E1 引用验证。这一轮结合第四轮说明四项能力分别成功，但不能合并标记某次完整套件成功。
- 最后轮次 `sol_live_final_20261009_6`：上游不可用；schema 的一次失败尝试已预留 1,200 输出，重试将超过同一用例 2,000 上限而被阻止。所有报告保留 `RealProviderVerified=false`。当前结论是**具有已观察到的协议能力，完整套件和响应稳定性未通过**；不再连续消耗 key 重试。
- 本地配置加载另有 2 项脚本检查：缺省字段清掉旧值且无输出；无效 JSON 错误不含测试密钥。凭据仅使用虚构 fixture。
- 密钥未写入源码、日志或报告；现有业务库和历史数据未修改。
