# P2.2 Raw Trend Continuity Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 离线修复 raw 趋势在历史周期切换与显式损失边界上的误报和漏报。

**Architecture:** Application 新增相邻观测空窗的纯区间判定；按历史边界分段，每段独立采用唯一 Periodic 修正周期的 1.5 倍容忍。Changes、缺失、重叠或无效周期不推断物理损失；仍消费显式损失。TrendBuilder 的 GapBefore、LeadingGap、TrailingGap 复用该判定，Simulation 保持固定周期。

**Tech Stack:** 现有 .NET 10 / xUnit；无新依赖、模型、迁移、API/UI 或 Worker 配置。

---

基线 ac713af；用户授权在当前工作区顺序实施、验证后立即本地提交并创建下一聊天。不得 push/PR、启用业务 Adapter、迁移业务库或清理历史。

选择：只修 raw 是本点最小可验收范围；同时改 Ingestor 需验证实际持久化 gap 起止与告警消费，留待独立点。直接复用分钟 HasTimingGap 会把不足一个周期的无样本小片段判为 gap，故新增专用相邻观测区间消费函数，不改变分钟算法。

### Task 1: 失败测试

- Create tests/PlantOps.Tests/RawTrendContinuityTests.cs。
- 覆盖 1s→10s 误报、10s→1s 漏报、Periodic→Changes、空窗/未知不回退、leading/trailing 跨周期、外点与半开损失边界、Simulation 与 Overflow。
- `dotnet test tests/PlantOps.Tests --filter FullyQualifiedName~RawTrendContinuityTests --no-restore --logger trx`；保留真实失败日志/TRX。

### Task 2: 最小实现

- Modify src/PlantOps.Application/Telemetry/SamplingCoverage.cs：新增 HasIntervalGap，按 from/to 与历史边界切段，唯一有效 Periodic 段长超过 1.5×interval 才推断缺口。
- Modify src/PlantOps.Application/Telemetry/TrendBuilder.cs：三个 raw 指示器用区间判定；显式损失核对 PointId，边缘窗口采用半开交集，样本指示器包含当前时刻发生的损失。
- 无观测仍保留旧 leading/trailing=true；来源判断与混合来源历史不在本点新增验收。
- 格式、定向测试、locked restore/build 顺序执行。

### Task 3: 回归与交接

- 常规测试前创建全新 plantops_tests.p2_raw_continuity_20261009；PLANTOPS_TEST_DOCUMENT_ROOT=artifacts/p2/documents/raw_continuity_20261009，全部保留。
- `dotnet test tests/PlantOps.Tests --no-build --no-restore --filter FullyQualifiedName!~SmallBackupRestoreTests`；不重跑备份实验。
- 前后只读核对业务迁移、原 UI schema raw/表存在、容量 accepted raw/启用状态；保存证据与清单。
- 写验收文档并更新 P2.2 / 总计划入口，审阅 diff、本地提交，创建并打开新聊天；确认启动后本聊天不再写共享区。
