# P1 Review Fixes Implementation Plan

**Goal:** 修复复盘中可由当前项目确认的采集、历史语义和历史管理问题，保留已有数据库/演示资料；OPC UA 与 AI 不在本轮接入。

**Architecture:** Application 增加按数据源运行的有界采集执行器与可重试损失摘要，Infrastructure 用已有维护/工厂锁持久化 gap 并重置规则 pending；Worker 负责配置生命周期。Outbox 清理显式启用、仅处理已发送历史；P1 点位已有历史或启用映射后固定采样间隔，灵活采样版本化留给 P2。

**Tech Stack:** 现有 .NET 10、EF Core/Npgsql/PostgreSQL、Vue/TypeScript；无新外部服务或 SDK。

**完成状态（2026-10-06）：**下列代码整改、只读容量定位及隔离验证已执行，88 项后端测试通过；详细结果见 [整改验收](../P1_REVIEW_FIX_VALIDATION.md)。步骤 6 的七天满量与并发维护验收仍开放，不把容量快照算作通过。

## 修复步骤与验收

1. **R4 历史采样语义：**修改 `TelemetryService.SavePointAsync`，将 ExpectedIntervalMs 纳入已有观测/分钟/最新投影/启用映射保护；`TelemetryTests` 验证改间隔失败且保留版本、分钟覆盖。新增未采集点仍可修改。P2 SamplingProfile 是以后解除限制的前提。
2. **R1 Outbox 增长：**新增 `OutboxMaintenance` 与 Host 调度、已发送时间索引迁移。默认禁用，显式配置 Outbox:RetentionEnabled/SentRetentionHours/CleanupBatchSize/CleanupIntervalSeconds 后仅有限批清理已发送过期记录；测试 pending/新记录/领取锁/重复运行、SQL失败回滚。绝不在当前验收schema执行清理。
3. **R2 采集生命周期：**新增 `MeasurementSourceRunner`，每DataSource独立运行，有限枚举每秒重复、持续流保持；有界可追踪DropOldest、500ms/500条先到flush，取消可排空。Worker 每秒核对配置，改版本取消/重建，SimulationEnabled只过滤模拟配置；Fake测试验证低速、满批、源并存及取消。
4. **R3 显式缺口：**新增Loss契约、gap幂等记录器及恢复/数量字段迁移；异常打开、恢复关闭，数据库不可用暂存有界摘要，失败批次及丢弃均可追踪；gap写入与pending清零同事务，不清Active。原始/分钟趋势显式加入gap，晚记录的gap也不能被已保存分钟隐藏。测试满量样本但显式缺口、永久离线、重试和规则。
5. **R5 通用时间边界：**在标准化中将空/最小时间视为缺失；限制异常远未来样本的分区创建并登记拒收，保留普通未来异常历史。协议StatusCode/Overflow解析仍属于未接入的OPC UA Adapter，不假装本轮已验证。
6. **R1/R6 容量实测：**新增只读容量采样脚本，定位Docker卷与宿主磁盘边界、记录DB大小/关系/待发送/dirty/WAL；不制造6000万行，不为凑容量删卷/schema。结合真实余量报告是否能开展满量；满七天/维护争锁无实测则仍未通过。
7. **验证/交付：**先定向失败测试，再实现/回归；仅 `plantops_tests` 新schema使用新迁移。构建使用Release输出与既有Debug展示隔离；全后端0跳过、EF无pending、前端类型/构建/去重。同步复盘整改状态和验证文档，本地提交不push。

## 数据保护与剩余门槛

保留 `plantops`（不迁移）、所有既有schema/卷/文件。新增测试schema允许迁移及构造故障，不自动升级当前 `p17_ui_20261005`；现有演示产物继续用旧schema，不用新代码误连接旧模型。Outbox清理默认关闭是保留历史要求；真正启用长期实例时须配置该任务并验收，不能将已实现策略称为已清理历史。

2026-10-06容量初读：Docker卷 `plantops_plantops-data`、容器虚拟文件系统可用约921GiB，宿主C约131.40GiB、D约55.26GiB，测试库约1.31GiB。容器虚拟上限不等于宿主可扩展容量；需核实虚拟磁盘位置及WAL/备份预算。G0满量和R6性能门槛保持未关闭。
