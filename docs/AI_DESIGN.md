# AI Copilot 调查设计

工程师从一条告警发起调查。`InvestigationService` 固定发起人、工厂、设备、告警和模型配置中的 Provider 与模型，由 Worker 调用 `InvestigationEngine`。生产注册使用 v3 引擎。v1/v2 结果沿用原格式展示，旧引擎用于兼容与历史回放。[调查服务](../src/PlantOps.Infrastructure/AI/InvestigationService.cs) · [引擎注册](../src/PlantOps.AI/DependencyInjection.cs)

## 系统准备哪些证据，模型能决定什么

| 环节 | 谁决定、决定什么 |
| --- | --- |
| 初始取证 | 服务端读取所选告警和设备摘要，冻结告警点及同设备按编号排序的最多两个其他启用测点，记录当前值、质量、适用阈值和来源健康。第一条工具记录是服务端读取设备摘要。 |
| 补充取证 | 模型可以直接作答，也可以选择读取设备摘要、详细测量、同设备告警、维护历史或授权资料。测量使用固定的事件、恢复和当前窗口。前四类工具只接受空参数，资料检索允许模型指定搜索词及返回上限。查到的文档作为待核对的资料。 |
| 分析与建议 | 模型区分历史事件和当前状态，引用观测提出可能原因、反证、待补信息及核查任务。证据不足时可以保留判断或请求专家复核。服务端检查对象、引用、支持与反证的交叉关系和点位可信度。原因解释、规程适用性及建议是否有用仍需人员评价。 |

同一调查中，相同参数的重复读取消耗工具额度，复用首次结果并重查权限。读数未更新时需结合有效时间、质量和连接状态解释。冻结证据保留来源与哈希，送入模型的内容会按预算裁剪。[工具参数](../src/PlantOps.Application/AI/InvestigationToolPolicy.cs) · [证据与评审示例](AI_INVESTIGATION_QUALITY.md)

## 如何核对一次自主调查

逐轮对照工具供应记录、模型返回的调用、工具实际返回的证据以及最终建议的引用。引擎控制轮数、工具次数、输入输出与超时预算，为最终答复预留空间时可能收回后续工具。一次调查即使成功生成提案，模型也可能只读了测量，漏查已发布规程。脚本测试覆盖直接作答、补读后作答、后续工具因预算被收回和拒绝写工具。真实模型能否作出适当选择要看相应版本的验收记录。[调查引擎](../src/PlantOps.AI/InvestigationEngine.cs) · [决策与预算测试](../tests/PlantOps.Tests/InvestigationAdviceTests.cs) · [v3.14 规程与预算实录](archive/P6_V3_FOLLOW_UP_20261119.md#p6-tool-budget-review-2026-11-19)

网关发送前预留额度，对特定瞬态失败可在预算内重试。诊断区分供应商报告用量与未知用量的预留。复验真实模型需本轮明确授权。[用量与运行记录](INVESTIGATION_DIAGNOSTICS.md) · [模型配置](AI_PROVIDER_SETUP.md)

## 人工决策与业务写入

调查运行状态、建议可用状态和工单草稿就绪状态各自独立。`DraftReady` 允许有权限的人员整理新工单提案，草稿复用同一组建议和引用。向同设备已有工单追加调查备注有独立入口。两种操作均由人员核对目标及完整内容并明确确认，服务端复核权限、证据与版本，在事务中幂等创建关联 Draft 工单或写入备注。后续派工、处理和关闭沿用工单流程。[v3 建议契约](INVESTIGATION_RESPONSE_V3.md) · [提案执行](../src/PlantOps.Infrastructure/AI/ActionProposalService.cs)

当前验收边界、工具和调查行为的变更约束、旧版示例分别记录在下列文档。[开发计划](../ROADMAP.md) · [AI 工程约束](AI_ENGINEERING_GUARDRAILS.md) · [历史设计](archive/AI_DESIGN_LEGACY.md)
