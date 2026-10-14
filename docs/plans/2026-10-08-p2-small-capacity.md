# P2 小规模 OPC UA 容量实测

**目标：**在新增、保留的 `plantops_tests.p2_capacity_*` schema 中测量真实协议 DataValue 经既有标准化后的 raw 表/索引开销和采样 WAL 观测。沿用 `2026-10-05-p2-opcua.md` 的 G0 边界；不批准满量、不启用持续源、不进入 P2.3。

**方案：**扩展现有安全 spike 的有限 `capacity` 模式，复用固定 SDK、安全端点和显式证书配对。原创模拟器 100 个节点覆盖 120 秒 Good/Bad/Uncertain 周期。配置实体全部禁用，显式 Read(maxAge=0, Both) → `OpcUaNormalizer` → `SamplePolicy.ToReading` → 既有迁移后的 EF/分区结构。此直接写入实验仅用于存储测量，不声称验证业务 MeasurementIngestor、分钟覆盖率或 Worker 生命周期。

## 单一验收点

1. 测试先行：拒绝业务库、public/多项/不匹配/非法 search path；分别验证行数、schema、WAL 生成/占用增长、宿主增长和余量的停止条件。
2. 固定 100 点 × 120 轮 ≤12,000 次真实协议值读取，总实验期限 180 秒（从数据库连接准备开始）；schema 停止阈值 64 MiB，集群 WAL 生成或目录占用增长阈值 128 MiB，C/D 各消耗阈值 256 MiB，最小剩余 2 GiB。每次写入仅一轮 100 行，写前/后采样检查；阈值为观测到后停止，无法限制其他进程在两次探测间的磁盘增长。
3. 在创建 schema 前采样并持久化探测；CREATE SCHEMA 不带 IF NOT EXISTS，重用既有 schema 必须失败。运行目录也必须全新。迁移和资料仅保留新增实验数据，不删除任何旧 schema/文件/卷。
4. 每轮记录精确行数、全部叶分区 table/index、schema 总大小、集群 WAL 目录占用与 insert-LSN 差值、C/D 余量。WAL 不强制 checkpoint/switch，不声称瞬时峰值、满量峰值或仅本实验贡献。失败保存探测与失败类型，退出，不清理证据。
5. 全量回读实验行，比较原值 JSON 语义、完整状态/符号、质量、转换数值、Source/Server/Received/Effective 时间与时间来源；按 PostgreSQL 微秒精度核对时间。同源事件键去重记录数量。Good/Bad/Uncertain 都必须实际存在，所有来源/映射仍禁用。
6. 定向测试、适当后端回归、顺序构建/locked restore；更新容量证据文档并本地提交。下一聊天独立做小规模联合数据库/文件备份恢复，不在此点执行。五项峰值与满量条件继续开放。

涉及：`spikes/PlantOps.OpcUa.Spike/CapacityExperiment.cs`、`CapacityLimits.cs`、`Program.cs`、项目/锁文件；`deploy/Run-SmallOpcUaCapacity.ps1`；`tests/PlantOps.Tests/CapacityLimitsTests.cs` 与链接纯保护文件；`docs/P2_CAPACITY_VALIDATION.md`。
