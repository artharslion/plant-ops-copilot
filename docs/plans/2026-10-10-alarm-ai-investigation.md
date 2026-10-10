# 告警 AI 调查 Implementation Plan

> 用户已明确要求写入代码事实并继续完成；当前会话直接实施，不创建子代理，不重复请求开始授权。

**Goal:** 接通告警页到真实业务证据、ILlmGateway、持久化调查和授权结果展示的只读闭环。

**Architecture:** Api 创建 Pending AiRun；Worker 以租约领取并用发起 actor 重新授权，调用现有业务 read 服务建立有界证据账本。PlantOps.AI 引擎通过 ILlmGateway 执行有限只读工具循环，校验结构/引用和原文事实后保存结果；Api 读取及引用展开重新授权，撤权则隐藏旧输出。Fake 明确标识模拟，真实失败不降为 Fake。

**Tech Stack:** .NET 10、EF Core/PostgreSQL、Vue 3/TypeScript、现有 OpenAI-compatible Chat Completions 和 xUnit。

## 2026-10-10 基于代码的基线核查

当前提交 `9f82256`，工作区干净。

| 范围 | 实际代码状态 |
|---|---|
| P1 | 身份/角色/工厂授权、防伪、资产、模拟采集/趋势/告警、人工确认、工单流转/备注、资料上传/发布/ACL、Outbox/SignalR 已实现 |
| P2 | 配置/映射/质量/采样契约及有限 IndustrialSim 协议演示已实现；TelemetryService 明确拒绝常驻 OPC UA 启用；长期 G0 延期未通过 |
| P3.1 | Gateway、Fake、预算、取消/超时/重试、strict JSON schema、引用集合检查已实现；Api/Worker 只有依赖注册，业务没有 CompleteAsync 调用 |
| P3.2 | TXT/Markdown 片段持久化、当前发布版本/ACL 过滤、术语检索与重新授权展开接口已实现；前端没有查询表单 |
| P3.3–P3.5 | 无 AiRun 实体/表/API/Worker，无业务工具执行器，无事实数值/单位/时间校验，无告警调查按钮；首页仍显示计划中；未完成固定 20 案例 |
| P4 | WorkOrder 仅预留 AiProposalId 字段；无 ActionProposal 实体/审批/幂等执行实现 |

证据：`src/PlantOps.Api/Program.cs`、`src/PlantOps.Worker/Program.cs`、`PlantOps.AI/ProviderProbe.cs`、`InvestigationContract.cs`、`Infrastructure/Knowledge/DocumentEvidenceService.cs`、`web/src/pages/AlarmsPage.vue`、`OverviewPage.vue`。已有 `artifacts/ai-evidence/accepted_20261010_091230/results.trx` 为 91 项通过，不能当作告警 AI 已接入。真实 Provider 最新报告仍 RealProviderVerified=false。

## 设计选择与边界

- 选择数据库队列 + Worker，避免浏览器请求承担 60 秒模型调用；相较内存后台任务，重启后 Pending/过期租约可恢复。单次运行 60 秒、4 模型轮、8 工具、16k 输入/2k 输出；所有工具最多 5 秒。租约 90 秒，最多两次领取，过期后明确失败或重新只读执行；旧租约不能提交覆盖新执行。
- 创建/执行/每个工具/发送模型前/保存前/读取结果/展开证据均实时校验 enabled、角色和工厂；模型不能指定 actor/工厂/设备。文档引用每次通过 P3.2 服务重新授权。
- 工具仅固定设备详情、最近一小时分钟趋势、最近三十天告警、最多二十条维护摘要和最多六片段资料检索。服务端默认收集上下文；模型只可收窄查询关键词，不可提供 SQL、URL、shell 或写动作。
- 首版保守事实契约：observations 必须逐字选用被引用证据的服务端事实句；数值/单位/UTC 时间作为同一事实句匹配，不能拆开借用。summary 使用固定非诊断总结；hypotheses 必须标为可能且不得宣称已确认，建议和假设不承载新数值。避免用 regex 数字存在即假装完成语义校验。非原文事实拒绝，不声称支持通用自然语言事实证明。
- 模型输出不生成引用 URL，URL 由服务端保存。质量/陈旧/缺口/原始过期/检索不完整由服务端追加。资料与备注是未可信数据，不得改变系统规则或工具集合。
- 仅发起人可读/取消自己的调查；其他用户不能借链接读取。外部 Provider 和发送范围在 UI 明示。取消写持久状态，Worker 在步骤间检查并向模型传递取消；失败只暴露受控错误码，既有授权证据仍可查。
- 不覆盖本地配置、不打印密钥，不迁移或清理业务库；新迁移只在全新测试 schema 验证。P4 另行完成审批验收，不把只读调查等同动作审批。

## Task 1：持久化、API 与授权证据

Create: `src/PlantOps.Domain/AI/AiRun.cs`、`src/PlantOps.Application/AI/InvestigationContracts.cs`、`src/PlantOps.Infrastructure/AI/InvestigationService.cs`、`Persistence/AiModel.cs`、additive migration、`src/PlantOps.Api/InvestigationEndpoints.cs`。

Modify: DbContext、Infrastructure DI、Api Program。

先增加创建/所有者隔离/取消/撤权测试，再实现 Pending/Running/Succeeded/Failed/Cancelled 状态、租约与证据快照。固定告警/设备范围，复用业务读取 DTO 并删除不必要人名/用户标识；授权无跨用户缓存。查询返回结果前重查文档版本/权限，失效时不给旧答案。

## Task 2：模型编排与事实校验

Create: `src/PlantOps.AI/InvestigationEngine.cs`、`tests/PlantOps.Tests/InvestigationEngineTests.cs`。

Modify: AI DI、Fake（仅针对明确调查请求生成有引用模拟结果）。

真实调用 ILlmGateway，支持有限只读工具往返、strict InvestigationResult.v1、错误/取消/预算传递。契约测试包含正常、缺资料、伪造 ID、篡改值/单位/时间、注入/禁用工具、超时/取消/格式失败等固定原创用例；测试实际业务证据和无业务写入。

## Task 3：Worker 与告警页面

Create: `src/PlantOps.Worker/InvestigationWorker.cs`、`web/src/components/InvestigationPanel.vue`。

Modify: Worker Program、AlarmsPage、OverviewPage。

持久化队列领取/恢复、取消轮询、租约提交校验；告警详情增加“开始只读调查”、状态/取消/历史、事实/假设/建议/缺口分区和每次重新授权的证据展开。面向非技术用户，标清模拟响应/真实模型、发送范围和快照时间。

## Task 4：验证、有限真实联调与交付

Create: `tests/PlantOps.Tests/InvestigationServiceTests.cs`、`deploy/Test-Investigation.ps1`、`docs/AI_INVESTIGATION_VALIDATION.md`。

新建独立 schema/资料目录，运行模型契约/安全案例、真实数据库队列/授权/撤权/取消/恢复/失败降级测试，构建/格式检查及前端检查、浏览器演示。Fake 全过后最多一次有限真实调查验证；如中转失败如实记录，不反复消耗 key，不声称真实质量/稳定性通过。

每个完成且验证的阶段立即提交，不 push/建 PR。回写实际通过数量、仍未验收项和部署迁移要求；未实现项不写完成。

## 执行记录

- Task 2 已完成并提交 `b9d32e4`：有界模型引擎、只读工具集合和逐字事实校验。
- Task 1 与 Task 3 后端已完成：AiRun 增量迁移、所有者 API、授权证据、Worker 租约/取消/恢复；新增 `--investigation-once` 可有限领取一项作业后退出，不启动采集或清理。
- 2026-10-10：`artifacts/ai-investigation/accepted_20261010_1000/results.trx` 128/128 通过，零跳过。包括此前 91 项资料/AI/健康检查、29 项调查引擎检查、8 项数据库业务链路检查。完整解决方案构建零警告/错误。
- 首轮 127/128 的失败为所有者隔离测试的两个用户误连不同 schema，已修正并在全新 schema 重跑；首轮报告保留。
- 页面已实现，浏览器验证和一次真实业务联调待进行。上述 29 项包含 20 个输出拒绝用例，不能替代原定完整 20 个现场场景验收。业务库未迁移。
