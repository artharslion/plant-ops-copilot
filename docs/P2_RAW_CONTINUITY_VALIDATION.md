# P2.2 离线 raw 趋势区间连续性

2026-10-09，基线 `ac713af`，分支 `codex/p2-opcua`。本独立点完成 Application `TrendBuilder` 的历史区间空窗判定与显式损失边界修复。计划：[实施边界](plans/2026-10-09-p2-raw-continuity.md)。真实采样生命周期、入库跨区间行为及完整 P2.2 仍开放。

## 行为与算法

原 raw `GapBefore` 采用前一时刻的周期，`TrailingGap` 采用查询末尾周期：29s→35s、30s切换1s→10s会误报；29s→40s、30s切换10s→1s会漏报。新 `SamplingCoverage.HasIntervalGap` 将相邻观测间或查询边缘的空窗按历史边界切段，唯一有效 Periodic 段的时长严格超过该段修正周期的1.5倍才推断缺口。恰为1.5倍不报，略超则报。快慢切换、多次修正及 Periodic↔Changes 各段独立消费，不改历史实体。

修正边界重置容忍窗口，与分钟算法对每段分别检查边缘的语义一致；没有服务器采样相位，不能据此推算确切丢失样本数。不能直接使用分钟 HasTimingGap：相邻观测之间本来没有内部样本，它会把不足一个周期的小段也判为缺口。此次新增函数只判定空窗，不改变分钟计数覆盖算法或分钟可信样本筛选。

Changes、Unknown、缺失/重叠快照和非正/非有限周期不推断周期物理损失，也不回退点位的当前请求周期；仍独立检查已知的非重叠 Periodic 部分。返回false只表示没有可推断的周期缺口，**不能证明完整连续**。现有 raw布尔DTO没有未知模式字段，本点未扩大API契约。Simulation继续采用固定间隔；无观测时leading/trailing继续保持旧true语义，不作为周期模式识别。

显式损失核对PointId；leading `[from,first)` 和trailing `[last,to)` 消费整个空窗内的损失，而非只检查在查询端点仍活跃的损失。结束恰在窗口起点、开始恰在查询终点均不污染窗口。`GapBefore` 含当前样本时刻的损失，因此当前观测的瞬时Overflow/损失仍可标记；结束恰在前一观测时刻的损失不延伸到下一观测。原状态码Overflow独立保留。

## 验证与保护

首批真实red：20项中 **11失败/9通过/0跳过**（`artifacts/p2/tests/p2-raw-red.trx`），均为行为断言失败，没有以格式/编译失败代替。最小修复后20项通过，扩展后 **35通过/0失败/0跳过**（`p2-raw-targeted-final.trx`），包含无效/未知/重叠历史、外点历史、开放损失、半开边界、Overflow与输入不变。

解决方案locked restore成功，build **0 warning/error**。常规回归 **261通过/0失败/0跳过**，2分3秒，含新增35项（本地 `p2-raw-regression.trx` 与 `p2-raw-result.json`）；显式排除opt-in `SmallBackupRestoreTests`，没有再次备份实验。测试前创建全新 `plantops_tests.p2_raw_continuity_20261009`，正确资料根 `PLANTOPS_TEST_DOCUMENT_ROOT=artifacts/p2/documents/raw_continuity_20261009`；helper随机schema与全部资料保留。新35项自身为纯离线fixture，不写数据库或建立协议会话。

前后只读核对业务plantops仍只有InitialAssetDirectory；原p17_ui_20261005 raw为1,609,600行且无SamplingProfiles；原p2_capacity_20261008_accepted raw为12,000行、Enabled sources为0。这里只核对迁移/行数/表存在/启用状态，日志一致不代表数据库全文哈希。证据 `artifacts/p2/tests/p2-raw-*` 含失败/成功测试、构建、保护、结果与SHA-256清单，均本地保留。

## 未完成范围

TelemetryService raw分支调用本路径，但新增35项没有新增API/真实协议验收；现有常规API回归另行列入总结果。API分钟分支仍直接读MeasurementMinutes。Application分钟返回的leading/trailing也复用修复后的公共边缘逻辑，分钟覆盖与可信算法没有改动。无EF/迁移/UI/依赖/DI/Worker或业务配置改变。

MeasurementIngestor仍按当前reading的单一历史周期推断跨区间缺口，可能与修复后的raw判断不一致，需下一独立点先失败测试，再验证实际持久化gap、规则与告警消费。当前函数不返回缺口起止，不能直接替换入库判定后猜写损失范围。

无观测来源模式识别、混合Simulation/OPC UA历史、真实SDK修正参数producer、事务/并发持久化、映射版本生效屏障与旧回调隔离仍未新增验收。G0五项正式峰值/满量与完整P2.2/P2保持未通过，BudgetComplete/FitsSnapshot/CapacityPassed仍false；不注册/启用业务持续Adapter、不运行满量或大备份、不清理历史，仅本地提交，无push/PR。
