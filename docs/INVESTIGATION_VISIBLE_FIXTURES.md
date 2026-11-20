# 可见仿真调查记录

这些案例是编码者已读过的工程材料，用于检查当前 v3 调查、证据冻结和权限边界。它们不属于独立留出集，Fake 回答不用于估算诊断准确率、误采纳、误拒或节省工时。

## 五类场景与对照

| 类别 / 导出文件 | 可核查的输入与确定性预期 | 人工审阅时应防止的误读 |
| --- | --- | --- |
| 当前稳定 `trusted-stable.json` | 历史温升告警后，三点当前观测为 60 °C、62 °C、4 mm/s；当前 `Trusted / NotFound`，保留历史触发及恢复记录 | 将当前有界稳定解释为设备安全、排除间歇故障，或丢弃历史告警 |
| 温升 `sustained-heating-zh-cn.json`、`sustained-heating-en.json` | 告警测点依次为 60、60、75、75、90、90、90、90 °C；另两点为 62 °C 和 4 mm/s；当前 `Trusted / Present` | 把观测变化直接认定为根因，或凭测点名称推断物理关联 |
| 混合质量 `partial-bad-high-vibration.json` | 温度变为 Bad，独立 Good 振动为 12.6 mm/s；当前 `Partial / Present`。`all-unverifiable.json` 是三点均 Bad 的反例，预期 `Unverifiable / Insufficient` | 因温度坏值压掉独立可信振动异常，或将 Bad 数值用于设备温升归因 |
| 连接故障 `disconnected.json` | 保留 Good 历史值，连接状态为 Reconnecting；当前 `Unverifiable / Insufficient`，活动告警不因断线恢复 | 把 LastGood 当成当前可信值，或把失联认定为设备物理故障 |
| 关联规程 `published-manual.json` | 同设备原创规程已发布，保存文档权限和发布版本、工具返回的片段与引用。`withdrawn-manual.json` 是撤回后无文档证据的对照 | 将规程当作观测或故障证明，引用已撤回版本，或把受限检索无命中解释为不存在规程 |

表内 `Trusted` 等标签是已有确定性证据策略的预期，代码沿用原断言。自然语言解释和任务质量仍需人工按含义审阅；表格和导出内容不是关键词评分器。正常场景仅指所查当前窗口，所有案例都含历史告警。

## 导出内容

[导出器](../tests/PlantOps.Tests/InvestigationFixtureCapture.cs)默认关闭，仅供上述 Fake 数据库场景启用。每份记录包含：

- 提交给正常入库器的有序输入：点位、单位、值、质量、三种时间、来源和映射版本、采样与陈旧参数。它记录提交输入，不保证每条输入均被接受；最终冻结证据用于核对实际调查所见。
- 发起调查前读取的测试用户启用状态、角色、工厂授权，以及本设备文档的读取角色和当前发布版本。授权实际读取时间与仿真数据截止时间分别保留；这不是历史时刻的权限重建，也不是完整目录快照。
- 运行 ID、冻结证据及其 hash、点位/连接状态、工具与请求元数据、版本、限额及 Fake 结果。
- 既有确定性预期与供人工审阅的允许/禁止解释；`fixtureOnly=true`、`independentHoldout=false`、`protocolObservation=false` 和 `semanticAssessment=NotPerformed`。

来源配置中的连接参数、源参数、私有路径及凭据不会随输入配置导出。`OpcUa` 来源登记不代表此处进行了协议采集。记录可供逐项复核，不是包含服务和连接配置的完整协议回放包。

## 执行

先按[开发指南](DEVELOPMENT.md#数据库集成测试)设置独立 `plantops_tests` 连接；测试自行创建隔离 schema。使用新建、绝对路径的空目录，导出以 `CreateNew` 写入，不覆盖已有记录。

```powershell
$caseRoot = Join-Path (Get-Location) 'artifacts/investigation-quality/my-visible-fixtures'
New-Item -ItemType Directory -Path $caseRoot -ErrorAction Stop | Out-Null
$env:PLANTOPS_OFFLINE_CASE_ROOT = $caseRoot
try {
    dotnet test tests/PlantOps.Tests/PlantOps.Tests.csproj --no-restore --filter 'FullyQualifiedName~PlantOps.Tests.P61RealCounterexampleTests.OfflineCounterexample|FullyQualifiedName~PlantOps.Tests.InvestigationHeatingServiceTests.SameReadingsRaiseTemperatureAlarmAndGroundFrozenAnalysisAndDraft'
} finally {
    Remove-Item Env:PLANTOPS_OFFLINE_CASE_ROOT
}
```

此筛选仅包含显式 Fake 场景，共 8 项，不发出外部模型请求。真实模型入口不使用此导出器。导出不是语义验收通过回执；独立评审仍使用[人工首答和 AI 复答记录](PORTFOLIO_PAIRED_REVIEW.md)，故障及注入边界继续由[既有场景测试](../tests/PlantOps.Tests/InvestigationScenarioTests.cs)验证。

## 2026-11-21 工程核对

8 项 Fake 隔离库测试全部通过，生成 8 份记录；两个温升语言版本各保存 24 条提交输入，其余各保存 48 条。逐份核对 v3 结果、Fake 来源、实际角色与工厂授权、确定性预期与结果一致，以及输入配置未包含源参数或连接参数。格式、构建和本地链接检查通过。

输出位于 `artifacts/investigation-quality/mvp_20261120_100/p8-visible-fixtures/`，检查回执为相邻的 `fixture-capture.trx`、`fixture-capture-summary.json` 和 `fixture-capture-environment.json`。实际模型账本保持 64/100，此阶段新增外部请求 0 次。导出增加文件写入和测试权限读取；默认关闭，生产调查、Prompt、模型输入、只读工具及人工审批均未改动。这里的语义审阅说明只供人工使用，没有新增自动措辞或症状评分；误采纳和误拒仍未知。
