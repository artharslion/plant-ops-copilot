# P2.2 Ingestion Interval Gaps Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 修复 MeasurementIngestor 跨修正区间的缺口持久化与同批次规则重置。

**Architecture:** 对相邻前进观测之间的完整点位历史调用 FindIntervalGapEvidence，再核对当前映射 ID/版本。将每个完整触发片段登记为 PossibleSamplingIntervalGap、IsPossible=true、DroppedCount=null，并立即加入事务内 gaps；不猜首个丢失时刻。迟到与重复读数不新建周期缺口，迟到读取既有缺口不重置更晚规则水位；当前水位的显式 Overflow 仍重置。

**Tech Stack:** 现有 .NET 10 / EF Core / PostgreSQL / xUnit；无迁移、依赖、SDK producer 或 Worker 注册变化。

---

基线 4bc2966，codex/p2-opcua。用户已授权顺序实施、验收后立即本地提交并创建/打开下一聊天；不并行写共享区、不 push/PR。

选择：仅替换 bool 无法说明范围；扩展 SDK 生命周期超出最小入库验收。本轮消费已验收的历史证据，并独立实测实际持久化与规则结果。整个证据片段是 possible 观察空窗，不是物理损失区间或计数。完整点历史中的重叠先判未知，不能先过滤映射掩盖冲突；其他映射/版本的单独证据不归到当前来源。未知/Changes/缺失无推断仍检查已知部分，空结果不证明连续。Simulation 固定周期的范围/原因/IsPossible 保留，仅补同批次消费。

### Task 1: 真实行为失败测试

- Create tests/PlantOps.Tests/IngestionIntervalTests.cs；每例新增保留 p2_ingest_<GUID> schema，在实际分区表执行 MeasurementIngestor。
- 验证快慢切换误报/漏报、多片段、不推断未知/Changes、身份/版本/重叠、1.5倍边界、同批次 pending raise/clear 重置、迟到/重复/异常、事务失败回滚、Simulation 契约。
- 测试前新增 plantops_tests.p2_ingest_intervals_20261009；顺序 locked restore、build、定向 red；真实断言失败日志/TRX保留。

### Task 2: 最小实现

- Modify src/PlantOps.Infrastructure/Telemetry/MeasurementIngestor.cs：OPC UA 复用区间证据，完整片段入库并加入 gaps；Simulation 原固定周期不变，加入 gaps。
- Modify src/PlantOps.Infrastructure/Alarms/AlarmBatchEvaluator.cs：来自迟到观测的 gap 消费不能重置已评估更晚时刻的 pending，保留水位；同一时刻的显式损失仍消费。
- 最小实现后 build 与定向测试；实际数据库快照证据保存到独立 artifacts/p2/tests/p2-ingest-snapshots 目录，记录 schema、gaps、readings、rule states、alarms，无连接串。

### Task 3: 验收、提交与交接

- 正确 PLANTOPS_TEST_DOCUMENT_ROOT=artifacts/p2/documents/ingest_intervals_20261009；全部新旧 schema/资料保留。
- 常规回归排除 SmallBackupRestoreTests；不重跑备份或满量。前后只读核对业务迁移、原 UI raw/表存在、accepted raw/来源启用状态。
- Create docs/P2_INGEST_INTERVALS_VALIDATION.md；更新主计划/P2.2入口，记录实际 red/green/回归结果与 SHA256 清单。
- 审查 diff、本地 commit，创建并打开下一项目聊天，确认启动后停止本聊天写入。G0与完整 P2 不通过，真实 producer/事务并发切换/旧回调屏障仍缺。
