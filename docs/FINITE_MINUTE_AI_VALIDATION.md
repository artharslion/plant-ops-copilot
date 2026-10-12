# 有限 OPC UA 演示的分钟趋势证据

2026-10-12最新：[预算核算与上下文压缩](AI_BUDGET_VALIDATION.md)完成，247项回归通过；50条新协议样本形成48/2分钟桶，核对通过。唯一真实调用首请求Timeout、零重试；历史记录保留，真实模型成功引用新增趋势仍待验证。

2026-10-12后续：[二十场景验收](AI_SCENARIO_VALIDATION.md)确定性检查通过；新的真实调查在允许工具调用后第二轮触发BudgetExceeded，无答案、未重试。本页2026-10-10历史Unavailable记录保留，新增趋势的真实成功回答仍未验证。

2026-10-10。本增量补齐前次[联合演示](INDUSTRIALSIM_AI_VALIDATION.md)未运行分钟聚合的缺口，不扩展常驻采集、清理服务、二十场景质量评估或P4。

## 实现

`Run-IndustrialSimInvestigation.ps1` 在本轮新测试schema的50秒采集结束、来源/映射停用后，等待最后一个原始样本所在UTC分钟结束，再调用现有 `IMeasurementMaintenance.AggregateAsync` 一次。等待最多65秒、聚合最多30秒，等待期间继续核对2GiB余量/256MiB新增观测界限。没有延长采集时间，没有改变系统时钟，没有补造读数。

只处理已结束分钟；未结束分钟仍不可发布。未启动RetentionWorker、未调用RetainAsync。旧 `Run-IndustrialSimDemo.ps1` 默认不聚合，显式 `-AggregateMinutes` 可开启；完整有限调查脚本默认开启。旧schema、资料、失败记录与业务plantops保留。

报告 `End` 仍是采集结束时间，`CompletedAt` 为等待/聚合后的完成时间；新增 `AggregationCalls`、`MeasurementMinutes`、`RawCountAfterAggregation`。`minute-check.json`按每个桶列出原始ReadingIds，独立重算样本数、可信计数、最小值、最大值、平均值，并与数据库快照和授权分钟API逐项比较，同时核对HasGap、CoverageRatio、SamplingMode。核对失败即停止，尚未发出模型请求。

调查E4保留窗口统计，新增最近最多三个有值分钟的UTC范围、极值、可信计数和缺口/采样模式。事实仍必须逐字匹配，趋势不代表当前实时值，停止后的陈旧和配置不可验证提示保留。

## 自动检查与Fake编排

- `artifacts/ai-investigation/minute_stage1b_20261010/results.trx`：221/221、零失败/跳过。新增检查验证停止的OPC UA窗口在当前分钟不聚合，结束后统计正确、保留缺口、原始数量不变。覆盖现有调查/授权/资料/采样/聚合回归。首轮格式检查失败未运行测试，记录保留；修正后构建零警告/错误。
- `plantops_tests.opcua_demo_minute_fake_b_20261010`：50秒、50条真实OPC UA样本，一个触发后恢复的告警；聚合一次，两个桶35/15条，raw仍50条。统计及分钟API核对通过，Fake调查成功、十项证据可展开。
- 两个桶均 `HasGap=true`、`SamplingMode=Unknown`、`CoverageRatio=null`：窗口只覆盖部分分钟，无法证明整个分钟的采样连续性。原始入库样本无故障缺口，不等于完整分钟无缺口；两种口径分别记录。
- Fake报告始终 `AiSimulated=true`、`RealProviderVerified=false`，不能替代真实模型成功。

## 复现

```powershell
# 每轮必须新run；保留此前证据
./deploy/Run-IndustrialSimInvestigation.ps1 -Run minute_fake_new
# 显式真实模型，一次业务调查，55秒单请求/60秒总期限/零重试
./deploy/Run-IndustrialSimInvestigation.ps1 -Run minute_real_new -RealAi
```

关键证据在 `artifacts/opcua-demo/<run>/result.json`、`minute-check.json`、`investigation.json`、`expanded-evidence.json`、`investigation-check.json`。仿真观测、真实OPC UA通信与真实/Fake模型分别标记；模型预算仍4轮/8工具/16k输入/2k输出，费用未知。

## 本轮唯一真实调用

`plantops_tests.opcua_demo_minute_real_20261010`，北京时间11:45:23–11:46:13采集，11:47:00完成一次聚合；49条原始协议样本、两个已结束分钟，原始数量仍49。第一个桶36条、min4/max12.6 mm/s，第二个桶13条、min6/max6 mm/s；API计数/极值/均值及原始ReadingIds核对通过。两桶HasGap=true、SamplingMode=Unknown、CoverageRatio=null，短窗口不能证明整分钟采样连续。

调查 `cd7bebe4-59a7-4a4b-8cf4-348960c62034` 关联协议告警 `9784a79c-39a0-44e0-a299-0eb5914f837b`、触发样本 `62d0bc06-c8f0-4b6c-8c53-bfc920b58faf`。E4已包含有值窗口极值及两个桶的统计；十项授权证据可展开，`MinuteEvidenceVerified=true`，没有“最近一小时没有可用分钟测量值”提示，缺口/陈旧/配置停用提示仍保留。

真实 `gpt-6.1-sol` 调用一轮、五次默认取证工具，约8.76秒，结果`Failed/Unavailable`，不是Timeout或BudgetExceeded。保守输入预留12274/输出900，实际费用未知；没有保存供应商原始错误或密钥，不能进一步断言供应商HTTP状态或网络根因。零重试、无Fake降级、无新增工单，告警版本未变。

磁盘预算通过：数据库容器卷初始可用988255526912字节，宿主D盘观测新增81920字节、C盘38211584字节，最终余量分别59265060864/120669163520字节；低于256MiB新增观测上限、余量超过2GiB。来源/映射全部停用、采集及单次调查进程退出，未运行常驻采集或清理。

`minute-check.json Passed=true`表示分钟链路通过；`investigation-check.json Passed=false`、`RealProviderVerified=false`如实保留模型失败，不能把前一轮成功套用为本轮模型已经引用新趋势。当前完成的是分钟聚合、取证及授权展开；真实模型对新增趋势的成功回答尚未验证。本轮不再消耗key重试。

## 浏览器验证

Chrome在独立API/UI中登录admin → 已恢复告警 → 失败调查 → 展开已获取证据 → E4。实际看到有值窗口4–12.6 mm/s、两个分钟36/13可信样本，以及每分钟Unknown/有缺口提示。旧的“没有可用分钟测量值”提示消失，模型Unavailable与停止后陈旧/配置不可验证提示仍可见。未点击新调查、零新增模型调用。

展开证据列表时发现长资料标题按钮造成横向溢出，已仅为证据按钮容器加入折行。最终页面宽1905、视口宽1905，无横向溢出；TypeScript/Vite构建、Prettier及ESLint检查通过，保留既有SignalR依赖注释提示。截图为 `artifacts/opcua-demo/minute_real_20261010/browser-minute-final.jpg`，浏览器检查记录在同目录`browser-check.json`。

独立审阅页面/端口/进程记录在`review-session.json`。只留API/UI供审阅，无常驻采集/聚合/调查Worker；旧API/UI继续保留。仅完成admin浏览器会话，跨用户/工厂以本轮HTTP检查及回归为准。
