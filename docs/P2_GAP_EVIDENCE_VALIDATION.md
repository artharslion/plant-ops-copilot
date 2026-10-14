# P2.2 离线周期缺口证据范围契约

后续2026-10-09：[跨修正区间入库与规则消费](P2_INGEST_INTERVALS_VALIDATION.md) 已将整个证据片段登记为明确的可能观察空窗，并实际验收同批次规则重置；以下入库未修复描述是本报告原轮状态，真实producer与完整P2仍开放。

2026-10-09，基线 `1dbccbd`，分支 `codex/p2-opcua`。本独立点完成 Application 纯函数的历史空窗证据范围，计划见 [实施范围](plans/2026-10-09-p2-gap-evidence.md)。MeasurementIngestor 持久化与规则消费仍未修复或新增验收；完整 P2.2、P2 与 G0 保持未通过。

## 契约与边界

`SamplingCoverage.FindIntervalGapEvidence` 返回 `SamplingIntervalGapEvidence` 值：半开 From/To、SamplingProfileId、PointId、SourceMappingId、MappingVersion、RevisedSamplingIntervalMs。调用方须先按点筛选历史；函数没有请求配置回退、当前配置读取或服务器参数 producer。测试参数均为离线固定 fixture。

范围是相邻观测或查询边缘空窗按历史边界切出的**整个触发片段**，不是物理损失起止。例如20s→35s，30s由1s改为10s，证据是 `[20s,30s)`，不是猜测的 `[21s,30s)`；29s→40s，30s由10s改为1s，证据是 `[30s,40s)`。没有服务器采样相位，不计算首个丢失时刻、精确样本数，也不自动转换成 IngestionGap。

沿用 raw 已验收算法：每段独立重置容忍；唯一有效 Periodic 段长严格超过该段 revised interval×1.5 才返回证据。多个触发段按时间排列，跨历史快照不合并，保留快照身份；查询外历史、半开端点不参与。Changes、Unknown、缺失、重叠和非正/非有限周期不推断损失；仍返回已知非重叠 Periodic 部分。空结果不能证明连续。

`HasIntervalGap` 复用新契约的非空判定，原35项 raw 连续性回归通过。HasTimingGap、Calculate、Simulation、显式损失与 Overflow 消费未改。结果值与输入实体无共享引用，输入不被修改，事后修改历史不会改写已返回的证据。

## 验证与数据保护

首个真实 red 因缺少 FindIntervalGapEvidence 编译失败，保留 `artifacts/p2/tests/p2-gap-evidence-red.log`，没有 red TRX，也不声称行为断言失败。首轮定向运行58项中4失败/54通过，原因是xUnit整数InlineData不能转换到可空double；改为double字面量后 **新增23项 + 原raw35项 = 58通过/0失败/0跳过**。首轮与最终TRX均保留。

解决方案 locked restore 成功，最终 build **0 warning/error**。常规后端回归 **284通过/0失败/0跳过**，1分55秒，包含新增23项。显式排除opt-in SmallBackupRestoreTests，没有重跑备份、协议实验或满量。新增23项是纯离线测试，常规DB/API回归不作为本点新增入库闭环证据。

测试前创建全新且保留的 `plantops_tests.p2_gap_evidence_20261009`；资料根使用正确 `PLANTOPS_TEST_DOCUMENT_ROOT=artifacts/p2/documents/gap_evidence_20261009`。helper随机schema及资料全部保留。前后只读核对：业务plantops仍仅InitialAssetDirectory；原p17_ui_20261005 raw为1,609,600行且无SamplingProfiles；原p2_capacity_20261008_accepted raw为12,000行且Enabled来源为0。这些核对是行数/表存在/迁移/启用状态，不是数据全文哈希。

证据 `artifacts/p2/tests/p2-gap-evidence-*` 包括编译red、定向失败/最终成功、restore/build、regression TRX、前后保护、result.json与SHA256 manifest；本地保留，不提交原始数据。无模型/迁移/UI/依赖/DI/Worker或业务配置修改，未重复无关EF/前端检查。无push/PR，无清理历史或停止无关进程。

## 下一独立点

MeasurementIngestor仍按reading时刻单一周期推断跨区间缺口。下一点优先定义如何将推断证据登记为明确的可能缺口，并用最小行为失败测试、全新schema中的实际gap起止及规则重置回归验证；不能将本证据From当作首个丢失时刻。需要核对多个片段、Changes/未知部分、映射身份和同批次规则消费。真实SDK修正producer、事务/并发历史切换、配置版本屏障与旧回调隔离仍缺。

本点没有新增API或协议闭环；无观测来源识别、混合来源历史、Worker授权分页Browse与逐映射业务状态仍开放。五项正式峰值、满量与G0不变，BudgetComplete/FitsSnapshot/CapacityPassed仍false；不注册或启用业务持续Adapter，不运行大备份或60,480,000行试验。
