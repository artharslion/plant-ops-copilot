# P5.3 最小场景源

2026-10-21 用户指定后的推荐入口已改为 **IndustrialSim框架独立进程**：`./deploy/Run-IndustrialSimRuntime.ps1 -Run <新名称>`。三点YAML位于 `D:/Repos/industrial-device-simulation-framework`，接入记录及投影时间契约见 [框架三点验证](INDUSTRIALSIM_RUNTIME_VALIDATION.md)。下文保留此前内置spike的生成契约和历史证据；框架的SourceTimestamp仅为投影/读取时间，不能沿用旧spike逐条生成匹配结论。

版本 `p5.3-minimal-v1`，无随机输入，种子记为 0。原创仿真观测，经真实 OPC UA SDK → 现有 Adapter → 普通 MeasurementWorker → 测量/规则事务；不调用 LLM。旧 Simulation GoldenPath、协议 selftest/capacity/runtime 入口保持原行为。

## 点位与固定时间线

Namespace URI 为 `urn:plant-ops:simulator`。本场景运行器只批准以下三个 String 节点；模拟器中的旧节点不属于本场景批准列表。同一设备下两路温度和一路振动，转换 scale=1、offset=0。

| 精确标识符 | 单位 / UN/CEFACT ID | 相对启动时间（秒）与值 |
|---|---|---|
| `North.Utilities.P101.Temperature` | degC / 4408652（°C） | [0,12) 60；[12,24) 75；[24,48) 90；[48,∞) 60 |
| `North.Utilities.P101.ReferenceTemperature` | degC / 4408652（°C） | 始终 62 |
| `North.Utilities.P101.ScenarioVibrationRms` | mm/s / 4403510 | [0,8) 4；[8,20) 12.6；[20,32) 7；[32,∞) 4 |

计划 t=40 停服，t=48 恢复，操作实际时间单独记录；停服期间不生成观测、不补造历史。恢复沿用同一单调时钟和 UTC 原点，不从头回放。运行器的调度延迟不改变阶段值定义。温度 >80°C 持续3s触发、<75°C 持续5s恢复，仅为原创演示规则，不是维修阈值。

三个点分别遵循相同的 **PeriodicGenerated / StatusValueTimestamp** 契约：创建节点时生成一次，Server 定时器约每秒生成一次；SourceTimestamp=本轮 UTC 原点+单调经过秒数，恒值阶段也更新源时间。状态为 Good。实际执行的生成记录保存；定时器调度不保证精确1 Hz。SDK 自行赋 ServerTimestamp，接收时间由现有 Adapter 记录，均保留在原始读数中。SourceTimestamp 表示本仿真生成时间，不表示现场采样。

请求 SamplingInterval / PublishingInterval=1000ms，接受现有策略1000–2000ms修正，过滤器显式 `STATUS_VALUE_TIMESTAMP`。请求采样间隔不保证每秒通知；恒值但源时间更新可满足过滤，未变化的状态/值/源时间不要求重复通知。可以核对实际生成与实际收到的通知，但不以请求间隔推算精确通知总数、物理样本覆盖或丢样。本步不改现有 Periodic 投影/覆盖算法，其语义修订留 P5.1/P5.2，不能用旧覆盖值作本场景验收结论。

## 观测语义与证据边界

- **ConnectionHealthy**：现有健康行的 Connected 与 Session KeepAlive 只证明连接侧依据；成功订阅创建/采样修正和收到 DataChange 是订阅依据。当前没有独立持续 Subscription KeepAlive 时间指标，报告必须明确这一限制，不能仅凭 Session 保活断言订阅仍活跃。
- **NotificationFresh**：逐点最近实际接收时间是否在5秒内；源级 LastReceivedAt 不能替代逐点依据。
- **SourceTimestampFresh**：逐点最近源时间是否在5秒内，且非未来异常；与接收新鲜度分开。
- **ValueChanged**：相邻实际观测的数值变化；恒值时为 false，不推出设备异常或传感器损坏。
- **DataTrustworthy**：只就本次观测，联合质量 Good、源/接收时效、时间异常和已知仿真契约判断；不等于设备正常或连接健康，也不证明未收到期间存在新物理观测。

无 DataChange、源时间不变、恒值或 KeepAlive 均不能单独证明设备异常/传感器损坏/新物理观测。按变化报告或未知契约不按 sampling 间隔反推覆盖。本步不新增产品 DTO 五字段，不扩展 P5.1。

## 验证记录

实施中的运行结果、实际执行命令与限制在本文件后续追加；历史记录不算本次重跑。完整 P5 的30分钟、复杂崩溃矩阵、真实现场设备、真实模型与部署更新均不属于本步。

里程碑1（2026-10-21）：`dotnet test tests/PlantOps.Tests --no-restore --filter FullyQualifiedName~MinimalScenarioTests`：2通过，0失败/跳过；核对温度阶段边界、独立振动变化与三点单位。`dotnet build spikes/PlantOps.OpcUa.Spike --no-restore`：0警告/错误。TRX 保留于 `artifacts/p53/checks/p53-scenario.trx`。首次检查发现格式空行问题已修复；一次并行构建遇共享输出锁，串行重跑通过。此里程碑尚不代表真实协议验收。

## 有限真实协议验收（2026-10-21）

复现：先确保独立测试 PostgreSQL 已就绪，在仓库根目录运行 `./deploy/Run-LocalOpcUaRuntime.ps1 -Run <新小写名称> -MinimalScenario`。连接配置沿用忽略的 `artifacts/p4/database.local.json`。只允许 `plantops_tests` 和全新 `opcua_runtime_*` schema / 空证据目录；保留150秒/500条、每盘至少2GiB余量/单轮新增≤256MiB的既有预算。端口48480已被占用则拒绝，不停止现有进程。运行后停 Worker/Server、停用本轮测试来源与映射；保留测试 schema。未更改业务数据库、部署或模型配置。

`p53_trial1_20261021` 在创建 schema 前因测试容器启动恢复中的 PostgreSQL `57P03` 失败；未启动 OPC UA。失败目录和 `setup-failure.json` 保留，未复用。

`p53_trial2_20261021` 通过，schema `plantops_tests.opcua_runtime_p53_trial2_20261021`。北京时间09:45:14–09:46:13，约60秒的有限运行：

| 核查 | 实测 |
|---|---|
| 真实普通 Worker 管道 | 3点各53条，共159条 OPC UA 来源读数 |
| 温度与振动 | 同一温度点实际收到60、75、90°C；振动实际收到4、12.6、7 mm/s并回到4；参考温度始终62°C但源时间持续更新 |
| 单位/质量/时间 | 全部单位匹配、Good、无时间异常，采用 Source 时间；源时间精度对齐 PostgreSQL 微秒，保留精确生成匹配 |
| 生成与入库对应 | 实际生成165条，每点55条；入库159条均精确匹配真实生成的点/值/源时间。差额不解释为物理丢样，也不据sampling反推覆盖 |
| 停服与恢复 | t≈40.23s请求停服，t≈48.13s请求重启；运行中实际进入Reconnecting，累计重连2次，三点重启后均收到新源时间观测 |
| 规则链路 | 温度90°C触发唯一告警；停服期间仍Active；恢复60°C后满足5秒恢复条件，最终唯一Recovered |
| 退出 | 健康Stopped；6段采样区间全部关闭，6条既有缺口记录全部闭合，不将其解释为精确样本损失 |
| 时效语义 | 停服前逐点通知/源时间均新鲜；ValueChanged=false仍可有本仿真契约下的新观测。报告保留Session健康、订阅修正与实际通知依据，独立Subscription KeepAlive仍未知 |

完整原始证据：[result.json](../artifacts/opcua-runtime/p53_trial2_20261021/result.json)，含读数、实际生成记录、三点契约、各阶段时间、采样修正、健康、缺口和告警；SHA256 `9A15B25A85B02BFAADB96ED5DB3302EC2FF0C3AF924CB27727422F30FF899C6B`。原始 artifacts 为本地保留文件，不纳入Git。0次模型调用；观测为原创仿真，订阅与业务入库是真实执行。

兼容检查：旧加密 `selftest` 在 `artifacts/p53/secure_20261021/result.json` 通过，不受本场景批准列表影响；8秒窗口恒值收到8条/8个源时间，证书拒绝与显式信任、17项元数据/身份场景通过。未据此验收长期运行或现场设备。

旧 `runtime` 入口也实际重跑通过：`./deploy/Run-LocalOpcUaRuntime.ps1 -Run p53_legacy_20261021`，84条读数、三点、停服/Namespace重排后恢复，证据 `artifacts/opcua-runtime/p53_legacy_20261021/result.json`。旧 Simulation GoldenPath 生成实现未修改；本轮未另跑其完整端到端演示。

最终定向后端检查：加载专用测试连接后，筛选 `MinimalScenarioTests / OpcUa* / ThresholdEvaluatorTests / MeasurementWorkerTests / MeasurementPipelineTests / SamplePolicyTests`，**92通过、0失败、0跳过**（28秒），TRX `artifacts/p53/checks/p53-focused.trx`。锁定恢复、spike构建通过（0警告/错误），`git diff --check`通过。查询确认本场景测试来源及映射已停用。未跑完整后端/前端套件、30分钟或复杂崩溃矩阵；没有实现P5.1新证据DTO或修改既有覆盖算法。
