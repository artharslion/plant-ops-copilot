# P2 容量调查与门槛

2026-10-08 增量：[小规模真实 OPC UA 存储与 WAL](P2_SMALL_CAPACITY_VALIDATION.md) 在新增保留 schema 实测 12,000 行，raw 含索引 648.533 字节/行，八天 raw 外推约 41.75 GiB；共享集群 WAL 生成与采样占用分别记录，不代替满量峰值。联合数据库/文件备份恢复及五项正式峰值仍缺，G0 未通过。

2026-10-06：只读快照 `artifacts/p2/capacity-start.json`。G0 **未通过**；此次没有填充满量数据、清理原 Outbox 或长期启动 OPC UA 采集。

| 项目 | 此轮时点观测 |
|---|---|
| 数据卷 | plantops_plantops-data → /var/lib/postgresql/data，保留 |
| Docker VHD | C 盘，逻辑文件长度 39,685,455,872 字节（不是物理占用） |
| C 盘余量 | 149,096,624,128 字节，约 138.9 GiB |
| D 盘余量 | 59,248,500,736 字节，约 55.2 GiB |
| 容器虚拟余量 | 989,273,272,320 字节，不能替代宿主容量 |
| plantops_tests | 1,592,506,035 字节；所有既有 schema 保留 |
| 原 p17_ui_20261005 | 1,314,004,992 字节，未改 |

旧 P1 行宽外推八天 raw 约 40.6 GiB、90 天分钟约 2 GiB、24h Outbox 约 1.6 GiB；基础预算约 44.2 GiB，再加现存数据、gap/审计/资料、维护/WAL/备份恢复峰值及至少 30% 余量。该外推不是此轮行宽测量或容量批准，宿主可用空间也不是备份恢复验收。

仍缺：60,480,000 七天真实结构 raw；第八天整日滚动；90 天分钟；实时写入与维护/五客户端并行；冷/热 p95；WAL 与联合备份恢复峰值；阈值实测。未满足前不得开展满量或持续真实接入，短期隔离协议 spike 可继续。Outbox 清理默认关闭；不清理原验收历史。

## 2026-10-08：预算工具与执行边界

后续恢复验证：Docker Desktop 启动后，单独启动已有 `plantops-database-1`（原先停止），待 PostgreSQL 恢复就绪。只读快照 `artifacts/p2/capacity-20261008-live.json` 与预测 `artifacts/p2/budget-20261008-live.json` 成功生成，扩展 SQL 已在真实库验证。原 schema 精确 raw 1,609,600 行/1,045,618,688 字节，分钟 27,400 行/4,685,824 字节，Outbox 260,620,288 字节，WAL 时点占用 184,549,376 字节（非峰值）。原历史 row width 外推八天 raw 44,901,319,406 字节、90天分钟 2,216,360,549 字节、24h Outbox 1,793,403,114 字节；不能替代 OPC UA 小规模结构实测。五项峰值仍缺，BudgetComplete/FitsSnapshot/CapacityPassed 均 false。

预算离线检查再次通过。解决方案构建 0 warning/error，后端 114 通过/0失败/0跳过（`artifacts/p2/tests/p2-recheck-20261008-success.trx`）；前端 lint/test/build 通过，既有 SignalR 注释提示保留。首次回归漏建隔离 schema 导致失败，补建后重新完整运行通过，未以失败轮计作验收。新增 schema `p2_recheck_20261008` 与资料根 `artifacts/p2/documents/recheck_20261008` 保留；helper 创建的随机测试 schema 均保留。只读核对业务 plantops 仍仅 InitialAssetDirectory，原 p17_ui_20261005 无 SamplingProfiles。未清理原 Outbox。

本点完成容量工具的真实连接验证与既有 P2.2 回归；G0 的小规模 OPC UA 行宽/WAL/联合备份恢复实测、满量及峰值验收仍开放。后续新聊天继续 P2.2 隔离节点元数据校验，持续接入门槛不变。

用户选择先完成预算与小规模实测，再决定满量运行。本轮没有满量试验授权，未新增满量数据或启动持续采集。

`deploy/Measure-Capacity.ps1` 已扩展为读取 raw/分钟精确行数、所有叶分区总大小、Outbox 总大小、所有数据库总大小，以及关系 table/index/TOAST 分项。table_bytes 已包含 TOAST，不能再把 toast_bytes 相加。关系列表仅显示最大的 30 项；预算所用 rawBytes 独立遍历全部叶分区，不受列表上限影响。

`deploy/Get-CapacityBudget.ps1` 从新格式快照推算 100 点 1 Hz 八天 raw、90 天分钟与 24h 已发送 Outbox；保留既有所有历史，额外分配量不再重复计入已占空间，但 30% 安全余量包含既有数据库体积。WAL/维护/恢复/文档/备份峰值必须显式输入，缺任何一项则 BudgetComplete/FitsSnapshot=false；零值须另有实测理由。备份盘与数据库盘分别核算，同盘合并预算，容器虚拟空间不能替代宿主余量。即使所有预算满足，CapacityPassed 仍为 false，不能替代 G0 验收。

离线验证 `./deploy/Test-CapacityBudget.ps1` 已通过：缺失峰值拒绝放行、八天/90天计算、跨盘不足、同盘合并、既有历史余量、旧快照缺精确行数拒绝使用。证据目录 `artifacts/p2/budget-tests_1cfafafbabc14f1a86c04d981c38178c` 为合成测试数据，不能作为真实容量测量。

**早先离线轮阻塞（后续 live 轮已解决）：**当时执行只读测量时 Docker Desktop Linux engine 管道不存在，数据库容器不可访问。该轮未启动/停止 Docker 或其他实例，扩展 SQL 未执行、无新快照；未用旧快照估计行数补充不存在的精确测量。后续成功记录见上方 2026-10-08 live 轮及下方小规模实测，不以此历史阻塞描述当前状态。

后续顺序：Docker 可用后重新取得只读快照 → 在新增且保留的 `p2_capacity_*` schema/资料目录做有明确行数与磁盘增长上限的小规模结构/行宽/WAL/备份恢复实测 → 使用实测峰值核算各盘预算 → 用户再决定满量运行。小规模需包含真实 OPC UA 原值/状态字段，不能用 Simulation 行宽直接批准 OPC UA 满量。

```powershell
./deploy/Measure-Capacity.ps1 -OutputPath artifacts/p2/capacity-<run>.json
# 不提供峰值时只生成明确不完整的预测，不会放行。
./deploy/Get-CapacityBudget.ps1 -SnapshotPath artifacts/p2/capacity-<run>.json
./deploy/Test-CapacityBudget.ps1
```
