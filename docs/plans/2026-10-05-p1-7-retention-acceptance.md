# P1.7 Retention and Acceptance Implementation Plan

**Goal:** 完成测量日分区、持久分钟统计、安全保留任务及完整 Phase 1 验收。
**Architecture:** 原始读数按显式 UTC EffectiveDate RANGE 分区，键/去重包含分区日期；数据库检查日期与 UTC 时间一致。全局维护 advisory lock 串行化创建分区、采集提交和保留；dirty minute 与分区 revision 防止未聚合删除。Worker 分钟聚合、每日保留，趋势用持久投影。
**Tech Stack:** PostgreSQL declarative partitions / EF migration / .NET Worker / Vue。

1. 保留现有读数迁移为日分区，增加 MeasurementMinute、dirty minute、partition revision 与采样缺口记录。
2. 批写确保分区，原子标记 dirty；分钟任务只重算完成且 dirty 的桶，迟到数据重新标记；每日清理原始 7 日、分钟 90 日，仅已完整聚合的日期允许 DROP。
3. 增加 2 厂×3位置、20设备×5点与10篇原创资料、最近1小时开发数据；重复种子不覆盖修改。
4. 权限/分页/质量/回滚回归，分区边界/迟到/重复聚合/保留与事务失败测试。
5. 独立测试 schema 运行100点×1Hz×30分钟、5用户；记录机器、行数/缺口、热查询与通知延迟、磁盘/查询计划，必要时修正瓶颈。验证数据库及进程重启恢复和实际 UI Golden Path。
6. 更新实际启动/测试/演示命令与验收证据，核实业务库未变更，提交完成阶段；不推送或创建 PR。

原始保留按 UTC 日，删除整个截止日期之前分区；未聚合 dirty/revision 不允许清理。当前不承诺删除后的原始引用仍可下载，告警快照保留。开发预填一小时仅首次完整种子，历史来自同一 SimulationSource。性能基准需真实持续时间，不能用加速历史写入代替30分钟运行。
