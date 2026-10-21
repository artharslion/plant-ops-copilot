# P5.3 最小场景源

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
