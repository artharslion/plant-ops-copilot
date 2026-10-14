# P2.2 Sampling Profile Transitions Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 离线验收显式修正参数与半开生效区间切换，保护已存在的采样历史。

**Architecture:** Application 新增纯区间转换函数，输入为单点历史与显式修正快照；输出复制所有实体，只关闭最后开放区间并追加新快照。停止只关闭开放区间，重启可以留下未知空窗。没有请求参数回退、数据库写入、SDK producer、配置屏障或 Worker 注册。

**Tech Stack:** 现有 .NET 10 / xUnit / SamplingProfile / SamplingCoverage；不改模型、依赖或 UI。

---

上下文：基线 `5e1c164`，`codex/p2-opcua`。按用户授权在当前共享工作区顺序实施，不创建并行 agent/worktree。G0仍未通过，业务迁移、满量与持续 OPC UA 接入继续禁止；本点不能关闭真实生命周期验收。

选择：直接改 EF 配置服务需要并发事务与旧回调屏障；直接接 SDK 需要生命周期和 G0。先完成纯函数的最小契约，后续持久化必须另行验证事务/并发/授权。

### Task 1: 契约失败测试

- Create: `tests/PlantOps.Tests/SamplingProfileTransitionTests.cs`
- 测试显式完整修正值；同分钟 2s→1s 与 Changes 切换；Close→空窗→Activate；旧历史与输入不变、输出无共享实体；同映射版本倒退、倒序/重叠/重复 ID/跨点/无效参数拒绝。
- Run: `dotnet test tests/PlantOps.Tests --filter FullyQualifiedName~SamplingProfileTransitionTests --no-restore`
- 预期：缺少 `SamplingProfileTransitions` / `SamplingRevision` 导致编译失败，保存真实日志，不伪造失败 TRX。

### Task 2: 最小实现

- Create: `src/PlantOps.Application/Telemetry/SamplingProfileTransitions.cs`
- `SamplingRevision` 必填 PointId、MappingId、Version、EffectiveFrom、Mode、三个 revised 参数；无默认请求值。只支持 Periodic/Changes；周期修正间隔须正有限，变化模式可为零但不得负/非有限；发布间隔正有限，队列 1–10000（现有 DB 上限）。
- 校验历史身份、正版本、有效区间与参数、唯一 ID、完整排序后无重叠/中间开放区间；接受旧快照可空发布/队列，不回填。切换不能倒退同一映射已记录版本，不能覆盖闭合历史；重新连接同版本允许新修正参数。所有输出为副本。
- `Close` 严格晚于开放区间起点；无开放区间拒绝。`Activate` 对闭合末区间可从结束后启动，空窗不填补。
- Run targeted tests and locked solution restore/build sequentially.

### Task 3: 回归、记录与本地提交

- 只为常规回归新建并保留 `plantops_tests.p2_sampling_finaltests_20261009`，资料使用 `PLANTOPS_TEST_DOCUMENT_ROOT=artifacts/p2/documents/sampling_finaltests_20261009`。
- `dotnet test tests/PlantOps.Tests --no-build --no-restore --filter FullyQualifiedName!~SmallBackupRestoreTests`；不重复 opt-in 备份实验。纯离线函数无需数据库证据；完整回归中的 DB/API 测试另列。
- 记录实际数量与日志在 `artifacts/p2/tests`，更新 `docs/P2_2_VALIDATION.md` 和本点报告。旧 schema/资料/Outbox 全保留，业务 plantops 和原展示 schema 不迁移。
- 检查 diff，本地 commit；按用户授权创建并打开下一项目聊天，确认已启动后当前聊天不再写共享工作区。禁止 push/PR。
