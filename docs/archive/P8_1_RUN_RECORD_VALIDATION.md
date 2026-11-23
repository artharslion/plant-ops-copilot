# P8.1 最小运行记录

日期：2026-10-26。代码基线：`e768c99` 加本次增量。本轮零真实模型请求、零业务工单创建/批准；业务 Docker 未更新，业务数据库未迁移。

## 本轮行为

- 新运行在准备请求前将 Prompt 视为 `unknown`；Engine 按实际请求写入 `investigation-extractive-v8`。旧运行的 v1 字符串与历史 Token 保留，不批量改成 v8。新增可空 `AiRuns.RunRecordJson`，API 的 `runRecord=null` 表示旧来源未知，撤回资料时继续隐藏旧结果与运行详情。
- 每轮准备模型请求时，先保存实际 system prompt、输出 Schema、工具定义、完整请求体及授权证据账本的 SHA-256。记录 Schema 名称、可信策略/取证策略版本、实际选择的 Provider/模型、推理参数、流式开关、请求超时、重试配置、输出上限及整个调查预算。原始 Prompt/资料不复制进日志，密钥与端点凭据不进入记录。
- HTTP 调用在预算预留后、发送前再次持久化。检查点失败则不发送。准备请求或预留不等于远端已收到请求；`reservedAttempts` 是预算预留次数，不冒充 HTTP dispatch trace。
- `reportedInputTokens`/`reportedOutputTokens` 只累计可解析的供应方报告；未知尝试保留独立预留。只有全部已预留尝试均有报告时，`complete=true` 且 `actualInputTokens`/`actualOutputTokens` 为总报告值，否则总实耗为 null。供应方报告不等于已确认账单，未配置价格时不计算费用。Fake 单独标 `simulated`。
- 格式不合规、拒答或截断但含有效 usage 的响应仍核算报告值；HTTP 故障、不可解析响应及断流继续按未知处理。预算超限仍失败，不能因保存 usage 而变成成功。取消或进程中断后的最后持久化预留可能保持未知，不推定零消耗。
- 租约恢复保留上一尝试记录，不覆盖旧版本/用量；此前没有记录的租约明确 `previousAttemptUnknown=true`。`usage.scope=lease-attempt` 表示一次执行内的模型请求/重试总量，恢复后的本次用量不冒充整个作业累计用量，页面有明确提示；整作业核算需连同 `previousAttempt` 汇总，旧尝试缺失时仍未知。现有完成状态、失败码、起止时间、模型和证据继续由 AiRun 保留。
- 调查页面对成功和失败均展示预算核算，并区分模拟、已报告、部分未知及旧来源未知。API 类型从本轮 OpenAPI 重新生成，也补上先前已有但类型落后的 AI 设置端点。

## 验证与失败记录

原始结果保存于 `artifacts/investigation-quality/run-record-20261026/`。历史失败保留，不以修正后结果覆盖。

1. 首轮离线定向回归：118/118 通过，无跳过。
2. 首轮数据库扩展：49 项，41 通过、8 失败。八项均为旧固定场景脚本的正常输出缺少当前要求的关键事实；改为读取请求中的 `requiredObservations`。伪造引用、改值、禁用工具等攻击分支和业务断言继续保留，没有放宽产品校验。
3. 修正后的首次 Release 扩展：219 项，203 通过、16 失败。固定场景通过；工单组遇到独立测试库 PostgreSQL 共享内存耗尽（53100），随后 57P03 恢复中，不能记为产品全绿。保留原 trx。只重启专用 `plantops-p4-tests`，未删除库、schema 或 volume；恢复后在测试连接禁用并行查询/并行维护以控制 64MiB shm 占用。
4. 恢复后的扩展回归 219/219 通过，无跳过。随后收紧检查点失租时停止发送的保护、增加恢复执行的用量范围提示，最终代码再通过 96/96 定向复验（含全部 Service/Gateway/Budget 与工单成功业务路径），不将两轮相加为独立案例数。原始 trx 及摘要在上述产物目录。Release 全方案构建零警告/错误，EF 无待生成模型变更。前端构建、lint、格式和 34 项测试通过；构建仍有既有 SignalR 注解与大包提示。

复验使用独立数据库 `plantops_tests` 和随机 schema，连接仅从专用容器读取到进程环境，禁止输出密码。定向类为 `AiBudgetTests`、`AiGatewayTests`、`InvestigationEngineTests`、`InvestigationQualityTests`、`InvestigationWindowsTests`、`AiInvestigationContractTests`、`InvestigationServiceTests`、`InvestigationScenarioTests`、`ActionProposalTests`。覆盖实际发送字节指纹、发送前持久化、未知/部分/超限用量、拒答/截断用量、旧记录不改写、恢复保留、撤权及工单主路径。现有冻结证据验证器回归继续离线运行。

## 范围与下一步

这是 P8.1 最小记录补缺，不补写过去未通过的先行门槛，也不代表 P8 全阶段验收。逐 HTTP dispatch/重试/工具的时间线与耗时、版本化取证代码快照、完整回放导出、质量对比和五工业场景集中验收仍待完成；hash 依赖保留对应代码/契约及授权证据，不保证仅凭 hash 重建输入。现有授权/资料撤回规则仍约束回放。

迁移只增加 nullable text 列；部署前按既有流程备份数据库/资料，组合使用主 compose 与 IndustrialSim overlay，保留现有卷和精确节点许可。本轮没有把最新代码部署到业务环境。下一优先项为 P5.1 统一逐点可信度/来源健康证据与 P5.2 基础反例，再推进 P6 多点语义验收。
