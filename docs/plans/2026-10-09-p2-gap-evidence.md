# P2.2 Interval Gap Evidence Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 返回周期缺口判定的历史空窗片段与快照身份，供后续持久化设计评审。

**Architecture:** SamplingCoverage.FindIntervalGapEvidence 返回不可变证据值；片段为查询空窗与历史边界切段后的整个半开区间。唯一有效 Periodic 段严格超过 revised interval×1.5 才返回；HasIntervalGap 复用结果。证据不是物理损失范围或丢失数量，调用方必须先按点位筛选历史。

**Tech Stack:** 现有 .NET 10 / xUnit；无 EF、迁移、API、Worker 或依赖改变。

---

基线 1dbccbd。用户授权顺序实施、验证后本地提交并创建/打开下一聊天；不并行写工作区，无 push/PR。

选择：直接用 bool 写 IngestionGap 会猜测起止；直接修复入库还需定义规则重置与范围含义并实测事务。先完成允许的纯函数证据契约，明确不是入库闭环。保持当前每次历史边界重置容忍语义，不计算采样相位；未知、Changes、缺失、重叠或无效周期无证据，不代表连续。

### Task 1: 最小失败测试

- Create tests/PlantOps.Tests/IntervalGapEvidenceTests.cs：先通过现有 HasIntervalGap 核对判定，再要求新方法返回整个片段、快照身份和修正周期，跨快慢切换、多片段不合并。
- 缺少新方法将导致编译失败，保存真实日志，不冒充行为 red TRX。

### Task 2: 契约实现与边界

- Modify src/PlantOps.Application/Telemetry/SamplingCoverage.cs：新不可变 SamplingIntervalGapEvidence 与 FindIntervalGapEvidence；共享原切段算法，不改 HasTimingGap/Calculate。
- 扩展半开边界、精确1.5倍、未知/重叠/无效、短/倒置窗口、排序和输入不变测试；不把证据转换成 IngestionGap。
- 顺序 locked restore、build、定向测试。

### Task 3: 回归与交接

- 全新 plantops_tests.p2_gap_evidence_20261009，正确 PLANTOPS_TEST_DOCUMENT_ROOT=artifacts/p2/documents/gap_evidence_20261009；全部保留。
- 只读保护前后核对原业务迁移/UI行数与表存在/容量行数与启用状态。
- 常规回归排除 SmallBackupRestoreTests；保存证据、SHA256清单并写验收文档。
- 本地提交，创建/打开下一项目聊天并确认启动；G0及完整P2不通过，入库真实闭环留待下一点。
