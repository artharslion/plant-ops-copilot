# IndustrialSim 三点常驻管道有限验证

2026-10-21，按用户指定将 P5.3 场景接入 `D:/Repos/industrial-device-simulation-framework`。框架版本 `a042753`。本轮 Server 是该仓库的独立示例进程，使用既有 SimulationHost / StateStore / ScenarioRunner / OpcUaAdapter；Plant Ops 仅连接批准节点并运行普通 MeasurementWorker，不在此模式启动自己的 SimulatorServer。

## 复现与来源

先确认独立测试 PostgreSQL 已就绪，以及忽略的 `artifacts/p4/database.local.json` 可用。在 Plant Ops 仓库根目录执行：

```powershell
./deploy/Run-IndustrialSimRuntime.ps1 -Run my_unique_run
# 可显式指定框架位置：-SimulatorRoot D:/Repos/industrial-device-simulation-framework
```

脚本构建两侧、创建全新 `plantops_tests.opcua_runtime_<run>` schema 与证据目录，启动自己的框架进程，等待原子 ready 文件，再启动普通 Worker；测试结束优先通过 stdin 请求示例退出，超时仅终止自己创建的进程树，不停止已有服务。端口占用/目录复用会拒绝。Worker 运行预算沿用150秒/500条及磁盘停止条件；框架示例限80秒、85秒取消保护。没有真实LLM调用，没有业务库迁移或部署更新。

| 协议配置 | 固定批准值 |
|---|---|
| Endpoint | `opc.tcp://127.0.0.1:48481` |
| ApplicationUri | `urn:industrial-sim:server` |
| Namespace URI | `urn:industrial-sim:runtime` |
| 温度 | String `plantops-p101/temperature`，degC |
| 参考温度 | String `plantops-p101/referenceTemperature`，degC |
| 振动 | String `plantops-p101/vibration`，mm/s |

场景在框架 `examples/devices/plantops-three-point.yaml`、`examples/scenarios/plantops-three-point.yaml`；进程入口 `examples/PlantOps.Scenario`。种子0，单调经过时间推进可注入的确定性引擎。温度t=0/12/24/48秒分别60/75/90/60°C；参考温度62°C；振动t=0/8/20/32秒分别4/12.6/7/4 mm/s。t=40请求只关闭协议，t=48请求对同一运行时重开；不重启设备场景、不补造历史。

## 通知与时间契约

三个点均使用框架既有1000ms周期投影。状态变更、周期刷新和读取回调可更新 OPC UA 投影，恒值也可能有新源时间；**SourceTimestamp是适配器投影/读取当前StateStore的时间，不是新物理采样时间，也不等于设备状态最后变化时间**。ServerTimestamp由SDK提供，ReceivedAt由Plant Ops接收端记录。

客户端显式 `STATUS_VALUE_TIMESTAMP`，请求sampling/publishing=1000ms，接受现有1000–2000ms修正；没有每秒通知总量保证。按实际入库读数核对温升、振动和恢复，不从sampling间隔反推物理样本覆盖/丢样，也不把框架名为 `subscriptionDelivery` 的观察事件当作客户端实际收到通知的证明。

报告保留Session健康/保活、订阅修正及实际逐点读数的不同依据；无独立Subscription KeepAlive观测。NotificationFresh和SourceTimestampFresh分开计算，后者仅为投影时间新鲜；ValueChanged=false不推出故障，DataTrustworthy仅表示当前协议投影质量/时间通过检查，设备状态年龄与物理观测时效仍未知。恒值、无通知或保活不能单独证明新物理观测或传感器损坏。

框架没有暴露逐个投影生成事件及其完整源时间序列，因此 `MatchesActualGeneration=null`、`Generated=null`，不伪造与旧spike相同的逐条生成匹配结论。取而代之保留实际场景/状态快照和客户端真实读数作不同层面的证据。既有Periodic覆盖算法尚未修订，不用其覆盖数值验收本轮；P5.1/P5.2另行处理。

## 本轮真实执行

2026-11-06 复验 `roadmap_p5_20261106`，Plant Ops 基线 `dfb530a`，IndustrialSim `3d5e7c0`。在隔离 `plantops_tests.opcua_runtime_roadmap_p5_20261106` schema 运行约 68 秒，三点各入库 50 条。温度实际收到 60、75、90°C，参考温度保持 62°C 且源时间更新，振动实际收到 4、7、12.6 mm/s。停服期间进入重连并保持活动告警，恢复后三点均收到新观测，唯一温度告警最终恢复。退出后 6 段采样区间均关闭，3 条断线缺口均记录恢复时间，另有 3 条具有结束边界的可能采样间隔缺口。付费模型调用为 0。

复验证据保存在 [result.json](../artifacts/opcua-runtime/roadmap_p5_20261106/result.json)。这一轮只有一次停服恢复。30 分钟联合运行、多次停服、同轮 Namespace 重排和浏览器验收仍待完成。Session KeepAlive 与收到 DataChange 分别核对，独立订阅保活和物理采样覆盖仍未知。

成功轮：`framework_trial2_20261021`，北京时间10:19:55–10:20:56，约61秒。证据 [result.json](../artifacts/opcua-runtime/framework_trial2_20261021/result.json) 和 [框架原始报告](../artifacts/opcua-runtime/framework_trial2_20261021/industrial-sim/simulator-result.json)。

| 核查 | 结果 |
|---|---|
| 普通Worker入库 | 3点各47条，共141条，全部OpcUa来源、Good、单位正确、Source时间、无时间异常 |
| 场景 | 同点收到60/75/90°C；振动独立收到4/12.6/7并回到4；参考温度恒值且收到多个源时间 |
| 实际协议生命周期 | 10:20:36.682请求停止、10:20:41.743停止完成；10:20:44.631请求重启、10:20:45.310重启完成 |
| 设备与协议独立 | t≈48.03s的快照中协议仍停止，StateStore温度已经60°C；协议随后重启。StopAsync约5秒内驱动循环等待，之后按单调时钟追上，不承诺严格墙钟动作延迟 |
| 重连与规则 | 累计重连2次，温度90触发唯一告警；停服期间保持Active；恢复60后按5秒规则恢复，唯一Recovered |
| 退出 | 健康Stopped，6段采样区间全部关闭，6条既有缺口全部闭合；来源/映射已停用，端口48481已释放 |

两个原始报告SHA256依次为 `26CDD8A30567B458D5E833DFDBDCD04CE74B1BC9B304190CDA65423135A5F12C`、`3F866396DADD5F105660AC2AAE1FE5684CF133ACD9CA7617D18582203A2F2D7C`。artifacts与schema本地保留，不提交原始密钥、证书或运行产物。

首次失败轮 `framework_trial1_20261021`：Windows Console控制输入阻塞了示例场景驱动，协议可连接但值一直恒定，未触发场景告警；预算结束时带同一取消令牌的日志读取又中断了最终报告写出。改为独立输入任务及不随运行期限取消的日志排空后，新目录重跑通过。失败目录、框架报告、`failure.json`及测试schema保留，本轮测试来源/映射已单独停用，不算验收成功。修复前一次构建也因仍运行的示例锁住DLL失败，进程退出后最终脚本构建0警告/错误。

## 本次回归与限制

- 框架新增真实YAML状态测试1通过，原Scenarios项目19通过；示例构建0警告/错误。
- Plant Ops带专用测试连接定向92通过、0失败/跳过，`artifacts/p53/checks/industrial-focused.trx`，覆盖OpcUa、Worker、入库、规则、采样策略和最小场景。
- 旧 `Run-LocalOpcUaRuntime.ps1 -Run framework_compat_20261021` 实际复验通过，76条/三点、停服/Namespace重排后告警恢复，证据 `artifacts/opcua-runtime/framework_compat_20261021/result.json`。旧spike入口仍用于协议回归。
- 本轮没有重跑完整后端/前端、30分钟、复杂崩溃矩阵、外部工业服务器或真实LLM；不能声称完整P5验收。Docker工作台仍使用原Simulation来源，本轮并未切换其业务数据源。
