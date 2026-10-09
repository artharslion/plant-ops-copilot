# P2.2 跨采样修正区间入库与规则消费

2026-10-09，基线 `4bc2966`，分支 `codex/p2-opcua`。本独立点修复 MeasurementIngestor 的历史区间缺口持久化，并在同一事务、同一批次中消费到规则重置。计划见 [实施范围](plans/2026-10-09-p2-ingest-intervals.md)。这是固定快照下的真实 PostgreSQL 入库验收；不是服务器修正参数 producer 或业务持续 OPC UA 接入验收。

## 范围与推断语义

OPC UA 前进观测间的空窗现在调用 `SamplingCoverage.FindIntervalGapEvidence`，按整个点位历史切段后，才选择当前 MappingId/MappingVersion 的证据。先保留跨映射冲突供区间算法判未知，避免提前过滤身份将重叠变成唯一有效周期。其他映射/版本的独立片段不归到当前来源；未知、Changes、缺失和重叠部分不推断，仍消费已知非重叠 Periodic 片段。每段独立以严格大于 revised interval×1.5 判定，多个片段不合并，空结果不能证明连续。

每段写为 `PossibleSamplingIntervalGap`，`IsPossible=true`、`DroppedCount=null`；PointId/DataSourceId 取已校验的当前样本配置，DetectedAt 是该观测 ReceivedAt。From/To 是半开**整个触发观察空窗片段**，不是物理损失起止、首个缺失时刻或丢失数量。没有采样相位，不做周期偏移猜测。例如20s→35s、30s由1s改为10s，保存 `[20s,30s)`；29s→40s、30s由10s改为1s，保存 `[30s,40s)`。20s→31s、30s由10s改为1s，各段均未超容忍，不登记。

没有新增实体字段/迁移。现有 IngestionGap 不保存 SamplingProfileId/MappingId/MappingVersion，因此本轮只有选择时的身份核对与 fixture 原历史快照证据，不能宣称 gap 行具有完整快照 provenance。后续若要求单行追溯具体修正快照，须独立设计和验收。

新增 gap 立即加入事务内 `gaps`，当前观测在规则 Evaluate 前重置 pending raise/clear；后续观测越过该段后不重复重置。读数、规则、gap、告警/事件/Outbox同属原事务，失败整体回滚。规则自身 MaxSampleGapMs/StaleAfterMs 仍可独立重置，不把历史周期修正视为规则阈值变更。

重复事件走原去重路径，不再登记或评估；迟到与同有效时刻的新事件不新建周期缺口。更早的读数消费既有 gap 时不能抹掉更晚规则水位的 pending；同水位的显式 Overflow 仍重置。异常未来时间只归档，不推进观测水位或推断周期 gap。按输入顺序消费，同批次迟到数据仍遵循水位保护；不会因迟到补入历史读数撤销已检测的可能空窗，账本保留检测时的推断。

Simulation 继续固定周期、`From=prior+ExpectedIntervalMs`、`Reason=SampleIntervalGap` 与原 IsPossible=false；仅补新登记 gap 的同批次规则消费。显式损失和 ServerOverflow 语义保留。

## 真实失败与通过证据

最初测试构建被 SA1516/IDE0011/EF1002 阻止，日志 `p2-ingest-build-red.log` 保留，不计为行为 red。修正测试布局、括号及随机 schema SQL 的调用形式后，首批 **12失败/0通过/0跳过**，均为实际入库/规则断言（`p2-ingest-red.trx`）。最小实现后 **12通过**（`p2-ingest-green.trx`）。扩展四项后，新16项与既有20项规则/维护用例共 **36通过**（`p2-ingest-targeted-final.trx`）。

审查中补入“同水位 Overflow”回归：先有格式构建失败日志，修正格式后真实行为 **1失败**（`p2-ingest-overflow-red.trx`），暴露首版水位保护把同一时刻的显式损失也屏蔽。条件改为只保护更早时刻后，最终 **新增17项 + 既有规则/维护20项 = 37通过/0失败/0跳过**，53秒（`p2-ingest-targeted-accepted.trx`）。

新增用例实际验证快慢切换误报/漏报、整段起止与可能语义、多段不合并、Changes/Unknown/缺失、映射身份/版本与跨映射重叠、严格1.5倍边界、首次观测、同批次 pending raise/clear、后续连续低值恢复告警、迟到/去重/异常时间、失败回滚与重试、Simulation固定范围、同水位Overflow。fixture为了隔离历史缺口重置，把规则MaxSampleGapMs和点StaleAfterMs固定为60s，持续时间30s；没有测试协议生命周期或生产者来源证明。

最终常规回归 **301通过/0失败/0跳过**，2分1秒（`p2-ingest-regression.trx`），显式排除opt-in SmallBackupRestoreTests，未重跑备份、协议实验或满量。锁定恢复成功；最后构建日志 `p2-ingest-build-accepted.log` 为0 warning/error。

## 数据保护与证据留存

主测试 schema 是全新且保留的 `plantops_tests.p2_ingest_intervals_20261009`，正确资料变量 `PLANTOPS_TEST_DOCUMENT_ROOT=artifacts/p2/documents/ingest_intervals_20261009`。新增入库用例各建 `p2_ingest_<GUID>` 并仅在其中初始化/迁移；使用原初始化fixture的单点映射，直接设置测试来源类型为OpcUa，不走业务启用API或启动任何Adapter。其他常规回归helper随机schema也全部保留。

`artifacts/p2/tests/p2-ingest-snapshots/{red,green,targeted-final,overflow-red,accepted,regression}` 保存每个新增用例的实际 profiles、raw readings、gaps、rule states 和 alarms JSON，不含连接串。最终定向17项共19个schema快照（一个用例分别验证Changes/Unknown/缺失）；失败轮与通过轮分目录保留。本地日志、TRX、result.json、snapshot-summary 与 SHA256 manifest 不提交原始数据。

前后只读保护核对一致：业务plantops仍仅InitialAssetDirectory；原p17_ui_20261005 raw为1,609,600行且无SamplingProfiles；原p2_capacity_20261008_accepted raw为12,000行、Enabled来源为0。仅核对迁移、行数、表存在和启用状态，不是数据库全文哈希。没有业务库迁移、模型/依赖/API/UI/DI/Worker/配置修改，不重复备份或满量；旧schema/卷/demo/Outbox/资料全部保留，不停无关进程，无push/PR。

## 剩余门槛与下一点

本点关闭“固定历史快照下，MeasurementIngestor跨修正区间持久化可能空窗与规则消费”的独立验收。真实SDK修正参数producer、历史事务/并发切换、配置版本屏障和旧回调隔离仍缺；Worker授权分页Browse/逐映射业务状态与混合来源历史也未新增验收。完整P2.2/P2不能称完成。

下一点优先真正获得并验证服务器 revised sampling/publishing/queue 参数与生效身份的最小 producer 路径，再设计事务持久化/关闭与版本屏障；先审查隔离 spike 与现有 SDK 能力，采用有限节点/时间的独立保留实验，不能用请求值或固定fixture冒充服务器修正值。不得为了推进注册/启用业务持续 Adapter。

G0五项正式峰值、满量和第八天边界仍缺，BudgetComplete/FitsSnapshot/CapacityPassed保持false；P2.3等待G0。用户未授权60,480,000行满量或大备份，小规模已验收备份保留，不重跑或填零峰值。
