# P2.2 Minute Trend Sampling Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** TrendBuilder 的离线 OPC UA 分钟路径按历史修正区间检查连续性，与现有分钟聚合使用同一算法。

**Architecture:** TrendBuilder 的 OPC UA 分钟分支复用 SamplingCoverage.HasTimingGap，按查询点筛选历史快照，并直接检查显式损失区间与查询桶的半开交集。保留数量覆盖率与连续性标记的区别，不修改 raw 趋势或入库生命周期。

**Tech Stack:** 现有 .NET 10 / xUnit / Application 纯函数，无新依赖、EF/API/UI改动。

---

基线 `440180e`，用户已授权当前工作区顺序继续，每个独立点验证后本地提交并创建/打开下一聊天。无需并行 agent/worktree。

评审选择：配置服务有事务/版本锁，但没有修正历史 producer 或旧回调屏障；接入持久化需另验完整一致性。真实 SDK producer 受生命周期/G0约束。本点选更小的离线消费缺口：TrendBuilder 分钟路径漏检数量足够但集中到达的观测，且跨慢周期切换可能被 raw 的上一时刻间隔误报。当前 API 的 `1m` 分支直接读取 MeasurementMinutes，不调用此路径；本点不宣称修复或重新验收 API 分钟展示。

### Task 1: 失败测试

- Create: `tests/PlantOps.Tests/TrendSamplingTests.cs`。
- 固定离线 fixture：集中观测、1s→10s切换、显式损失的半开边界、外点快照/损失隔离、Changes静默、Unknown空窗、查询部分分钟、Simulation兼容。
- 在新增且保留的 `plantops_tests.p2_trend_sampling_20261009` 初始化前创建 schema；资料变量使用 `PLANTOPS_TEST_DOCUMENT_ROOT=artifacts/p2/documents/trend_sampling_20261009`。
- 顺序 locked restore，然后 targeted test 保存真实 red 日志/TRX；失败应来自错误连续性结果。

### Task 2: 最小修改

- Modify: `src/PlantOps.Application/Telemetry/TrendBuilder.cs`。
- 查询点快照参与 Calculate/HasTimingGap；协议分钟 HasGap 使用当前桶内可信观测、每段历史间隔、质量与显式损失，而不继承 raw GapBefore。
- 显式损失交集条件 `gap.From < coveredTo && (gap.To == null || gap.To > coveredFrom)`，并核对 PointId。
- 保留 Changes 的未知覆盖率，不以静默推断周期损失；Unknown继续有缺口。
- raw标记、LeadingGap/TrailingGap、入库与聚合实现不在本点修改范围；不宣称全面趋势生命周期完成。

### Task 3: 验收与交接

- 顺序 build、targeted tests、常规回归 `FullyQualifiedName!~SmallBackupRestoreTests`。
- 保存前后业务迁移/原UI raw/无快照/accepted raw及Enabled来源只读核对；全部新旧schema与资料保留。
- Create: `docs/P2_TREND_SAMPLING_VALIDATION.md`；更新P2.2与主计划入口，记录真实结果和SHA-256清单。
- 检查差异，本地commit；创建并打开下一项目聊天，确认已开始。禁止push/PR，不做备份/满量或业务持续Adapter注册。
