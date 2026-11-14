# IndustrialSim 三点常驻管道有限验证

2026-10-21，按用户指定将 P5.3 场景接入 `D:/Repos/industrial-device-simulation-framework`。框架版本 `a042753`。本轮 Server 是该仓库的独立示例进程，使用既有 SimulationHost / StateStore / ScenarioRunner / OpcUaAdapter；Plant Ops 仅连接批准节点并运行普通 MeasurementWorker，不在此模式启动自己的 SimulatorServer。

## 复现与来源

先确认独立测试 PostgreSQL 已就绪，以及忽略的 `artifacts/p4/database.local.json` 可用。在 Plant Ops 仓库根目录执行：

```powershell
./deploy/Run-IndustrialSimRuntime.ps1 -Run my_unique_run
# 可显式指定框架位置：-SimulatorRoot D:/Repos/industrial-device-simulation-framework
# 可使用独立本地 PostgreSQL：设置 PLANTOPS_TEST_CONNECTION 后传 -UseTestConnection
```

脚本构建两侧、创建全新 `plantops_tests.opcua_runtime_<run>` schema 与证据目录，启动自己的框架进程，等待原子 ready 文件，再启动普通 Worker；测试结束优先通过 stdin 请求示例退出，超时仅终止自己创建的进程树，不停止已有服务。端口占用/目录复用会拒绝。Worker 运行预算沿用150秒/500条及磁盘停止条件；框架示例限80秒、85秒取消保护。没有真实LLM调用，没有业务库迁移或部署更新。

要从这次采集继续验收浏览器调查，启动指向同一隔离 schema 的本地 API 并通过浏览器发起一次手动调查。API 在发起时将所选提供方保存到调查记录，因此 API 创建调查时选用的提供方需要与 Worker 一致。随后在设置了 `PLANTOPS_TEST_CONNECTION` 的终端运行 `./deploy/Invoke-IndustrialSimBrowserInvestigation.ps1 -Run <run>`，仅处理一条已排队调查，默认用 Fake。通过本轮授权后，才可传入 `-RealAi -LedgerPath <current-ledger> -MaxRequests <limit>`；请求预算由账本执行，重试为零。该入口检查成功采集报告、工况预期的告警状态及专用测试库。浏览器仍负责取证展开和人工确认。采集结束后调查的当前快照可能不可验证，应将其与历史事件及恢复证据分开复核。

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

2026-11-14 断流场景在隔离库中通过 `-UseTestConnection -Disconnected` 启动。活动告警形成后关闭本轮 IndustrialSim 进程，等待采集器报告 Reconnecting，保存 `disconnected-ready.json`。采集器在重新连接期间继续报告状态，直到在本轮目录创建 `release` 文件或达到 480 秒总时限。浏览器发起调查后，使用 `Invoke-IndustrialSimBrowserInvestigation.ps1 -Disconnected` 处理队列。调查取得断流证据后即可创建 `release`，模型继续使用冻结快照。

联合轮 `p6_disconnected_20261114` 采到三点共 114 条 OPC UA 数据。断流中告警保持 Active，恢复时间为空，E21 保留最后一条温度样本的有效时间 14:02:00.187710（北京时间）和 Reconnecting 状态。首轮模型流传输失败，第二轮自主调用 `get_recent_measurements` 并生成待审批提案 `d4198a64-bbc2-4aec-81cd-c90e52601279`，关联工单为零。合计 4 次请求。模型区分了陈旧样本和当前设备状态，但正文未写明最后可信样本的具体时间，核查建议也未明确要求复连后验证新样本，严格语义验收仍待复验。联合轮的采集脚本在等待释放时达到总时限，保留了原始失败报告。 [调查与提案证据](../artifacts/opcua-runtime/p6_disconnected_20261114/browser-real-acceptance.json) · [断流快照](../artifacts/opcua-runtime/p6_disconnected_20261114/disconnected-ready.json)

随后独立协议复验 `p6_disconnected_protocol_20261114` 在断流快照写入后释放，再次采到 114 条 OPC UA 观测，活动告警仍未恢复，采集脚本正常退出。该复验模型请求为零。 [协议复验报告](../artifacts/opcua-runtime/p6_disconnected_protocol_20261114/result.json)

离线复查将账本第 4 次请求与最终完成响应按 SHA-256 对齐。最终请求的 E21 含样本有效时间 `06:02:00.187710Z` 和不同的接收时间，含 `SourceNotConnected` 和 `StaleObservation` 原因码。压缩省略了冻结 E21 中的 `Reconnecting` 和窗口明细。E9 保留 `06:01:23.690722Z` 至 `06:02:00.187710Z` 的事件观测段。F1 报告了该历史段，F4 和 F5 没有将 E21 样本时间与当前取证时间分开写明。R1 要求核对连接和时间记录，并指出连接恢复或 Good 标签不能单独作为依据，但没有要求复连后核对新样本及其连续性。主要缺陷在模型取舍和建议表述，具体连接状态的丢失属于证据投影缺口。

`investigation-advice-v3.7` 保留冻结的来源健康状态及其时效标记，并要求报告关键读数的有效时间、质量与实际观测边界，以及通过复连后的样本时间、质量与连续性来决定能否重新评估。调查概览直接显示告警测点的保留读数及其有效时间，并按当前可信度标注。历史观察和建议正文中的完整时间戳按工作台时区显示。断流和已恢复后可信读数的对照用于离线回归。仍需经授权复验真实模型是否给出完整建议。短时缺少新样本时，可记录证据不可用并交人工复核。仅凭连接恢复或单个 Good 样本通过验收会产生误通过，将明确说明证据不足的建议一律视为无效则可能误拒绝。

旧联合轮的保存结果通过中文浏览器离线重放。调查概览显示 `90 degC / Good` 的有效时间 `14:02:00.18771`，标记为当前不可验证的留存读数。E9 的历史观测段显示为 `14:01:23.690722` 至 `14:02:00.18771`。两处都采用北京时间，沿用界面去除小数末尾零的格式。重放使用保存的 API 响应，模型请求为零。[重放记录](../artifacts/build/p6_v37_20261114/browser-replay.json)

获授权后的断流复验共使用 6 次请求，自动重试为零。v3.7 输出用 F3 和 R3 补齐了样本时间和复连后核对步骤，但 `NeedsMoreEvidence` 缺少根级阻塞引用。v3.8 自主读取历史测量并补齐根级阻塞引用，假设却重复使用同一证据作支持和反证。两次均由既有校验拒绝。v3.9 提示明确两个阻塞字段的适用条件、可立即开始的诊断任务，以及互斥的假设证据引用。校验器和响应结构沿用原有版本。[v3.7 失败记录](../artifacts/opcua-runtime/p6_v37_outage_20261114/browser-1-acceptance.json) · [v3.8 失败记录](../artifacts/opcua-runtime/p6_v37_outage_20261114/browser-2-acceptance.json)

v3.9 在新的隔离 schema 完成断流验收。模型自主调用 `get_recent_measurements`。F1 引用历史观测段，F2 阐明 Active 且无恢复时间，并写出 E21 的 90°C Good 样本有效时间 `18:02:58.476561`（北京时间）；F3 保留当前不可验证及 Reconnecting。R1 要求复连后逐点核对新样本的有效时间、质量和连续性，再重新评估。浏览器打开待审批提案 `9cffe631-eb68-4dde-83e6-326857d00453` 的完整核对页，关联工单为零。R2 引用了已发布的振动检查节选，现场适用性仍需人工核对。协议报告保存 120 条读数并正常退出。回归中的结构拒绝不等于建议内容全部错误；只看提案成功也会漏掉判断、范围和建议适用性的偏差。[断流复验](../artifacts/opcua-runtime/p6_v39_outage_20261114/browser-1-acceptance.json) · [协议报告](../artifacts/opcua-runtime/p6_v39_outage_20261114/result.json) · [共享请求账本](../artifacts/investigation-quality/p6_v37_joint_20261114/requests.jsonl)

2026-11-14 完成两种告警状态的真实 OPC UA、真实模型与浏览器联合验收。两轮均在隔离的 `plantops_tests` schema 从 P-101 告警手动发起调查，使用 `gpt-6.1-sol` 各发送 2 次请求，生成待审批提案，关联工单为 0。

- 恢复场景 `p6_joint_live_20261113`。模型自主调用 `get_recent_measurements`，引用 E1 中的历史规则和恢复时间，以及 E19 中同一时间的 60°C Good OPC UA 样本。已用保存的调查复验提案入口和弹窗均显示「恢复阶段原始观测（有界节选）」。[采集报告](../artifacts/opcua-runtime/p6_joint_live_20261113/result.json) · [调查与提案证据](../artifacts/opcua-runtime/p6_joint_live_20261113/browser-real-20261114-acceptance.json)
- 短暂低值场景 `p6_brief_low_20261114`。用 `Run-IndustrialSimRuntime.ps1 -UseTestConnection -BriefLow` 在协议重连后注入 3 秒低值，三点共入库 170 条 OPC UA 观测。温度从 90°C 短暂降至 60°C 后回到 90°C，告警仍为 Active，恢复时间为空。模型自主读取测量，引用 E17 中触发后的 60°C Good 样本及 E1 中低于 75°C 持续 5 秒、最大样本间隔 2.5 秒的规则。浏览器展开两条引用并停在待审批提案。[采集报告](../artifacts/opcua-runtime/p6_brief_low_20261114/result.json) · [调查与提案证据](../artifacts/opcua-runtime/p6_brief_low_20261114/browser-real-acceptance.json)

2026-11-13 复验 `p6_protocol_20261113`。独立本机 PostgreSQL 与 IndustrialSim `3d5e7c0` 联合运行约 73 秒，三点各入库 48 条 Good OPC UA 观测，温度实收 60/75/90°C、参考温度恒定 62°C 且源时间更新、振动实收 4/7/12.6 mm/s。温度告警在协议停止及重连期间保持活动，协议恢复后转为 Recovered，三点均有新读数。模型调用为零。[运行证据](../artifacts/opcua-runtime/p6_protocol_20261113/result.json)

同日使用 `-UseTestConnection` 准备浏览器验收。首次运行收到 57 条观测，未触发告警，达到时限后退出并保留报告。独立 schema 中的第二次运行约 61 秒，收到 148 条观测，P-101 温度告警 `6cf0664b-2e18-4921-956a-717ffda9d0ce` 触发并恢复。浏览器从该告警发起 Fake 调查、打开 E4 授权引用、核对提案并显式确认，打开了状态为 Draft 的关联工单 `WO-37dcd6b8407d49bf8c790b42732f6359`。该浏览器验证发生在有限采集结束后，当前测点显示不可验证。真实模型请求为零，业务库和部署均未参与。[首次运行](../artifacts/opcua-runtime/p6_browser_20261113/result.json) · [成功运行](../artifacts/opcua-runtime/p6_browser_retry_20261113/result.json) · [浏览器截图](../artifacts/opcua-runtime/p6_browser_retry_20261113/browser-draft.jpg)

联合验证准备轮 `p6_joint_20261113` 使用新的隔离 schema，约 62 秒收到 145 条 OPC UA 观测，P-101 温度告警 `ca854f1c-f1f5-4b13-97ae-bae17af8f025` 已恢复。有限 Worker 在队列为空时退出；真实模型联合验收需另行运行。[采集报告](../artifacts/opcua-runtime/p6_joint_20261113/result.json)

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
