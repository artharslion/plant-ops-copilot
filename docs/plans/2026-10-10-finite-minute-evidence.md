# 有限协议演示分钟证据 Implementation Plan

**Goal:** 在新建测试schema的一次有限采集后，只聚合一次已结束分钟，让调查引用真实协议样本形成的趋势，同时保留缺口和陈旧提示。

**Architecture:** 复用 IMeasurementMaintenance.AggregateAsync，不启动RetentionWorker、不调用RetainAsync。采集停止且来源/映射停用后，有界等待最后样本的UTC分钟结束，再执行一次聚合；报告原始采集结束与聚合完成时间分别记录。API检查按每分钟raw样本核对计数/极值/平均值，调查E4展示有值分钟和质量范围。

**Tech Stack:** .NET10、PostgreSQL、PowerShell7、现有只读AI调查。

## 实施与验证

1. OpcUaDemo增加显式AggregateMinutes选项；Run-IndustrialSimInvestigation默认开启，独立旧采集脚本默认关闭。等待最多65秒、聚合最多30秒，继续复核磁盘预算，失败保留result.json。
2. 报告MeasurementMinutes、一次聚合计数及采集结束时间；采集上限仍50秒/100条，不伪造时钟或读数，不改旧schema。
3. Test-IndustrialSimDemo按原始样本核对每分钟实际计数/极值/均值，检查API分钟趋势一致；调查证据加入有值分钟计数/极值/覆盖口径，不改变真实/Fake区分。
4. 运行聚合/采样/调查专项回归，完成后立即提交；新run Fake协议链路验证通过后，最多一次真实调查，不自动重试。
5. 浏览器展开E4趋势证据，保留原始样本关联、质量/缺口/陈旧提示，更新验收记录并提交。保留旧服务、数据与报告；不push、不建PR、不扩展二十场景或P4。

## 实现阶段完成
221/221、零跳过：artifacts/ai-investigation/minute_stage1b_20261010/results.trx。首轮格式检查失败报告保留。Fake minute_fake_b_20261010通过：50条协议读数，50秒采集后等待最后UTC分钟结束，聚合调用一次，产生两个分钟桶（35/15样本），raw数量仍50；minute-check逐样本核对/API一致，调查E4可展开且包含极值及逐分钟计数。两个桶均HasGap=true、SamplingMode=Unknown、CoverageRatio=null，未把部分分钟变成完整覆盖。
