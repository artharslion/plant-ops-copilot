# 告警 AI 调查接入与验证

日期：2026-10-10。对应 [实施计划](plans/2026-10-10-alarm-ai-investigation.md)。

## 当前结论

告警业务现在实际调用 `ILlmGateway`，已实现 `告警页 → API → AiRun → Worker → 授权业务证据 → InvestigationEngine → Gateway → 校验/持久化 → 结果与证据展开`。`OpenAiCompatibleGateway` 不再只用于 Spike。Fake 数据库与 Chrome 业务路径通过；早先一次真实调查超时，后续 [IndustrialSim有限链路](INDUSTRIALSIM_AI_VALIDATION.md) 的唯一一次真实 `gpt-6.1-sol` 调查成功并通过事实/引用校验。完整 P3 质量验收与响应稳定性仍未通过。

| 范围 | 已实现行为 |
|---|---|
| 持久化与队列 | Pending/Running/Succeeded/Failed/Cancelled；90 秒租约、最多两次领取、旧租约不能覆盖新执行；重启可恢复未完成作业 |
| 业务 API | 为指定告警创建/列出自己的调查；读取/取消/展开；POST 使用既有防伪，输出 no-store；同用户同告警的活动调查复用，每用户最多两项活动调查 |
| 授权 | 固定 actor、工厂、设备和告警；创建、工具、模型发送前后、保存、读取和展开实时检查；其他用户不可读/取消；文档换版、撤回或权限失效时隐藏旧结果和全部证据 |
| 数据 | 告警历史快照、设备资料、最近一小时完整分钟趋势、最近三十天最多百条告警数量、最多五条维护摘要、当前发布且授权的资料片段；质量/陈旧/缺口/范围提示同时进入模型证据和页面警告 |
| 编排 | 五个固定只读工具；60 秒、4 模型轮、8 工具、16k 输入/2k 输出；默认收集五个工具后可继续有限工具往返；无写工具、SQL、URL 或路径参数 |
| 事实校验 | observations 必须逐字匹配每个所引证据的事实句；值/单位/UTC 时间不可拆借；summary 固定为非诊断总结；假设标“可能/待验证”，建议/假设不能新增数值 |
| 页面 | 发起、状态、取消、最近十条自己的历史、模拟标识/外部发送范围、事实/假设/建议/缺失信息、每次重新授权展开证据 |
| 失败 | 取消传播到模型；超时/授权/格式/预算错误为受控代码；不自动切成 Fake；既有普通业务继续可用，失败调查仍可读取已授权证据 |

这是保守的摘录事实契约，不能证明任意自然语言推理正确。维护首版只发送工单标题、状态、处理结果摘要，不发送备注全文；趋势是窗口统计，不是完整逐点序列。资料首版节选，不提供通用问答页面。

## 代码定位

- `src/PlantOps.Application/AI/InvestigationContracts.cs`：业务接口、证据/结果 DTO。
- `src/PlantOps.AI/InvestigationEngine.cs`：实际 Gateway 调用、工具参数和事实校验。
- `src/PlantOps.Infrastructure/AI/InvestigationService.cs`：实时授权、数据汇集、队列、取消与持久化。
- `src/PlantOps.Domain/AI/AiRun.cs`、`Persistence/Migrations/20261010014255_AlarmAiInvestigation.cs`：新增表，不改写既有业务数据。
- `src/PlantOps.Api/InvestigationEndpoints.cs`、`src/PlantOps.Worker/InvestigationWorker.cs`：业务入口与后台执行。
- `web/src/components/InvestigationPanel.vue`：告警详情调查面板。

## 验证证据

自动检查在全新 `plantops_tests` schema 和独立资料目录运行。所有旧 schema、失败报告、资料及业务历史保留。

- `artifacts/ai-investigation/accepted_20261010_1000/results.trx`：128/128，通过且无跳过；包括 91 项既有资料/AI/健康检查、29 项调查引擎检查、8 项真实数据库业务检查。
- 质量提示补为模型证据后，最终 `artifacts/ai-investigation/final_20261010_1010/results.trx` 再次 128/128 通过、零跳过。
- 八项数据库检查覆盖实际告警/文档到 Fake Gateway 的闭环、所有者/工厂/防伪、资料撤回、用户禁用、排队取消、运行中取消、模型不可用、租约恢复与旧提交拒绝。检查调查没有确认告警、没有创建工单或写业务审计。
- 首轮 `first_20261010_094751` 为 127/128：测试中的其他用户误连另一个隔离 schema；修正为同 schema 后重跑通过。未清理失败报告。
- 解决方案构建曾零警告/错误；新增真实调用测试也已编译。前端 TypeScript/Vite 构建、ESLint、Prettier 检查和现有一项 Node 测试通过。SignalR 依赖有既有 PURE 注释提示，不影响构建。
- `--investigation-once` 已在独立演示 schema 的空队列执行后退出，不启动测量采集/保留清理。队列实际执行语义由上述数据库检查验证。

29 项引擎检查中包含 20 项输出事实/形状拒绝用例，另有工具往返、非法工具/参数、Fake 和取消检查；**不能把这些称作原定完整二十个现场场景/真实模型质量评估**。

## 一次有限真实业务联调

命令入口为 `deploy/Test-InvestigationLive.ps1 -Run <新后缀>`。只有显式选择真实 Provider 并开启专用测试标志才运行；普通测试不会联网。脚本静默加载本地密钥，创建新测试 schema，不保存密钥/请求正文/原始供应商错误，完成后恢复环境变量。单项调查有限预算，不自动重新运行测试。

本次 `artifacts/ai-investigation/live_business_20261010_1004/live-business.json` 与同目录 TRX：

- `Provider=OpenAiCompatible`、`Model=gpt-6.1-sol`、`Simulated=false`，并断言解析得到真实 Gateway 类型。
- 实际经过业务创建、授权取证、引擎调用和结果读取；模型一轮、默认五个工具、五项证据。该次调用发生在质量警告作为额外证据的补充前。
- 约 30.67 秒结束，`Status=Failed`、`FailureCode=Timeout`。预留输入 7,547/output 900 token，不是上游实际账单；费用未知。
- `OrdinaryAlarmReadable=true`、`NoWorkOrderWrite=true`。没有把失败改为模拟成功，没有重复真实调用。
- `RealProviderVerified=false`、`QualityAcceptance=false`。本次不能给出真实输出正确性、响应稳定性或完整演示通过的结论。

## 浏览器与部署状态

隔离 UI 演示为 `plantops_tests.ai_browser_20261010_1000`，关闭历史预填和持续采集；只增加一个明确标为虚构演示的告警快照，raw 数量为零。此 UI fixture 不替代经过真实 ingestion 的数据库测试。

Chrome 最初显示 `ERR_BLOCKED_BY_CLIENT`，用户浏览时另发现 404。排查修正了启动命令重复参数导致的错误页面目录；重新加载后正常。最终实际验证：登录 → 告警详情 → 开始调查/Pending → 取消/Cancelled → 再开始 → 有限 Worker 执行 → 自动轮询 Succeeded → E1 历史快照展开 → 已发布资料版本/行号展开 → 切换已取消历史再回完成结果。模拟标识、事实/假设分区、无数据/陈旧/缺口/节选警告可见，告警仍未人工确认。截图保存在 `artifacts/ai-investigation/browser_20261010_1000/investigation.jpg`。

浏览器验证使用一个隔离演示会话；跨用户、跨工厂和撤权由独立数据库会话测试，不声称完成两个浏览器会话的全量有限演示或移动端验收。

当前业务库未迁移；增量迁移只在测试库验证。现有正在运行的旧进程不会自动获得功能。实际部署需先应用新增迁移，再重启 Api、Worker、前端；Api/Worker 应加载同一 AI 配置。未加载配置时默认 Fake；真实模式使用本地配置入口，见 [模型配置](AI_PROVIDER_SETUP.md)。

## 仍未完成

- 响应稳定性与固定二十场景质量/安全验收；完整两个浏览器会话的有限演示验收。一次真实模型业务成功见 IndustrialSim 联合验收，不能代表完整质量通过。
- P3.5 完整运行指标、供应商实际成本对账与调查保留策略。当前持久化轮次/工具数/保守 token 预留、错误码和证据；不宣称完整成本监控。
- P4 工单/备注提案、人工审批与幂等执行；模型没有写操作。
- 持续 OPC UA、长期容量 G0、向量/PDF/OCR 为此前独立未验收/后续范围。
