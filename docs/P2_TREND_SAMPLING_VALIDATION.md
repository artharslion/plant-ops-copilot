# P2.2 离线分钟趋势采样区间消费

2026-10-09，基线 `440180e`，分支 `codex/p2-opcua`。本点修复 **Application TrendBuilder 的离线分钟路径**，按各段历史修正周期判断观测连续性，直接检查显式损失区间。计划：[实施范围](plans/2026-10-09-p2-trend-sampling.md)。

## 问题与修改

原分钟路径使用数量覆盖率与 raw 的 GapBefore：60条观测集中在分钟开头仍可得到 coverage=1、HasGap=false；1s周期在第30秒改为10s后，第35秒的合法首个观测又因上一观测使用旧周期而误报。Changes静默期间的显式损失还可能被遗漏到下一分钟。输入中外点快照会干扰覆盖率。

协议分钟路径现在按 PointId 筛选 SamplingProfiles，复用现有 SamplingCoverage.Calculate / HasTimingGap。数量覆盖率仍可为1，但时间不连续时 HasGap=true；覆盖率不是连续性证明。各段 `[EffectiveFrom, EffectiveTo)` 使用各自 revised sampling interval，保留现有1.5倍时间容差；未按当前点请求间隔或请求配置回填。

显式损失按当前查询桶的半开交集核对 PointId：`From < coveredTo && (To == null || To > coveredFrom)`。正好在查询起点结束或终点开始的损失不污染该桶；开放损失、部分分钟和 Changes静默中的明确损失均有测试。Changes静默继续返回未知 coverage，不推断周期损失；Unknown历史空窗仍有缺口。

所有参数/模式来自固定离线fixture，本点没有服务器 provenance 或修正值 producer。没有修改历史实体、EF模型/迁移、依赖、API/UI、DI/Worker注册或业务配置。

## 验证

初次测试构建被IDE0055格式检查阻止，原日志保留为 `p2-trend-sampling-format-build.log`；修正格式后，真实 red 为 **4失败/9通过/0跳过**（`p2-trend-sampling-red.log` / `.trx`），对应集中观测、慢周期切换、Changes中的损失和外点快照。最小修改后首批 **13通过**；扩展快周期切换、Periodic→Changes、开放损失边界与质量异常后 **20通过/0失败/0跳过**（`p2-trend-sampling-targeted-final.trx`）。

解决方案 locked restore 成功，最终 build **0 warning/error**。构建/测试顺序执行。常规后端回归 **226通过/0失败/0跳过**，1分19秒，包含新增20项（`p2-trend-sampling-regression.trx`）。显式filter `FullyQualifiedName!~SmallBackupRestoreTests`；没有重复opt-in备份实验，没有备份、满量或协议会话。无模型/UI/依赖改动，未重复EF/前端检查。

常规回归使用新增且保留的 `plantops_tests.p2_trend_sampling_20261009`，测试前先创建独立schema；资料使用正确 `PLANTOPS_TEST_DOCUMENT_ROOT=artifacts/p2/documents/trend_sampling_20261009`。helper随机 `p17_test_*` / `p17_isolated_*` 与资料全保留。新增纯函数测试自身不写数据库；上述数据库/schema用于现有常规DB/API回归。

前后只读保护结果一致：业务 `plantops` 仍仅 `20261003071706_InitialAssetDirectory`；原 `p17_ui_20261005` raw **1,609,600行**，无SamplingProfiles；原 `p2_capacity_20261008_accepted` raw **12,000行**，Enabled来源0。这里只核对行数、表存在、迁移和启用状态，日志哈希一致不代表数据库全文哈希。全部证据保留于 `artifacts/p2/tests/p2-trend-sampling-*`，含red/targeted-final/regression TRX、前后保护日志、result.json与SHA-256 manifest。

## 边界与后续

当前 TelemetryService 的 `bucket=1m` 直接读取 MeasurementMinutes，本点不改变此 API 分支。MeasurementMaintenance 已使用 HasTimingGap，本次只复用算法，没有改其数据库重算。常规回归覆盖现有数据库/API行为，但本轮新增20项都是离线单元测试，不是新增协议或API闭环证据。

raw GapBefore、LeadingGap/TrailingGap 与 MeasurementIngestor 的跨修正区间推断保持原实现，仍需独立评审；本点不宣称全面趋势连续性完成。没有观测时的来源模式识别，以及混合Simulation/OPC UA历史也未新增验收。持久化修正历史的事务/并发/版本屏障、旧回调隔离与真实SDK producer仍缺。

G0五项正式峰值、60,480,000行满量、完整P2.2与P2仍未通过，BudgetComplete/FitsSnapshot/CapacityPassed仍 false。业务持续Adapter继续不注册/不启用。所有历史schema、卷、demo、Outbox、资料与失败证据保留；只本地提交，无push/PR。
