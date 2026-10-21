# OPC UA 集成设计 v0.1

2026-10-21 规划更新：剩余采集任务统一归 [开发计划 P5](../ROADMAP.md#3-p5opc-ua-持续采集与实时数据底座)。先区分采样/通知/源时间与可信度，交付最小场景/证据/基础故障以服务 P6.1，再补完整可靠性；外部安全 Server 为核心 Demo 后的可选 P5.5。下文旧阶段顺序、百点满量和通用安全部署描述不作为当前已实现能力或前置；实际受控模式及限制仍见运行验收记录。

2026-10-20：新增 [受控本机常驻多点增量](OPCUA_RUNTIME_VALIDATION.md)，使用独立 Adapter 接入普通 Worker；仅显式 Development 回环批准策略可启用。三点停服重连/Namespace 重排/告警恢复真实协议实验通过；下面“业务 Source 未启用”等语句保留历史范围，不表示当前仍只有有限单点。工业网络、证书运维与完整 P2/G0 仍未验收。

2026-10-09 当前范围：按 [AI 优先演示计划](plans/2026-10-09-ai-demo-acceptance.md)，完整 OPC UA 暂缓，AI 先基于 Simulation 实现。旧 G0 长期容量前置已延期且未验收，不阻塞 AI。下面协议要求保留为后续增强契约，不代表当前已经实现；业务 Source 仍未启用。

2026-10-06 当前进度：[P2.1 固定 SDK/许可与安全隔离实验](P2_1_SPIKE.md)、[P2.2 配置/标准化/采样区间增量](P2_2_VALIDATION.md) 已落地。G0 和完整 P2 未通过，业务 Source/重连/证书运维/健康未完成；后文早期“未开始”保留历史标记。

日期：2026-10-03。Phase 2 仅实现 Client/Server 模式的数值变量读取与订阅。业务不依赖特定服务器 NodeId；不写变量、不调用设备方法、不提供完整 Alarms & Conditions 客户端。

2026-10-05：实现仍未开始。按 [P1 复盘](P1_REVIEW.md) 与 [P2 可执行计划](plans/2026-10-05-p2-opcua.md) 推进固定 SDK/许可 spike、容量门槛、持续采集生命周期、历史采样语义与显式缺口账本；下文是设计要求，不表示 P1 已实现协议连接/队列/重连。

OPC UA Source 与保留的 Simulation Source 均实现 PlantOps.Application 的 `IMeasurementSource`。Phase 2 是新增来源能力；两种源分别跑通同一 Golden Path，不替换模拟实现，也不在断线时自动使用模拟值。

## 1. Adapter 边界

`OPC UA Server → Client Session → Subscription → MonitoredItem → IMeasurementSource / NormalizedSample → 有界 Channel → MeasurementReading 入库/规则 → Outbox → UI`。

Adapter 只负责连接、订阅、Namespace 解析、质量/时间和数值单位规范化。业务资产从 SourceMapping 查得；SDK 的 Session、NodeId、DataValue 不越过集成项目边界进入 Domain。

推荐 OPC Foundation `OPCFoundation.NetStandard.Opc.Ua.Client`。SDK API 可能随版本演进，Phase 2 先锁定稳定 NuGet 与对应 tag 的示例，不照搬 master 文档的方法名。官方入口：[UA-.NETStandard](https://github.com/OPCFoundation/UA-.NETStandard)、[官方示例](https://github.com/OPCFoundation/UA-.NETStandard-Samples)。使用许可及再分发要求在引入依赖时核实并记录。

## 2. 数据源与映射

一个 DataSource 属于一个 Plant，第一版不从一个源跨厂映射。只允许管理员从预先批准的 endpoint 列表配置，禁止把用户输入或模型输出作为任意连接地址；工业网接入留待独立部署设计。

映射存 **Namespace URI + IdentifierType + Identifier**；运行时根据 NamespaceArray 解析 ns index。`ns=2` 只是一次会话的索引，不是稳定身份。禁止模糊匹配节点显示名称或自动猜测映射。

示意配置（虚构节点；需由自建模拟器创建，官方 Server 不保证存在这些节点）：

```json
{
  "sourceCode": "north-simulator",
  "endpointUrl": "opc.tcp://localhost:4840",
  "plantCode": "NORTH",
  "securityPolicy": "Basic256Sha256",
  "messageSecurityMode": "SignAndEncrypt",
  "publishingIntervalMs": 1000,
  "mappings": [
    {
      "assetCode": "P-101",
      "pointCode": "VIBRATION_RMS",
      "namespaceUri": "urn:plant-ops:simulator",
      "identifierType": "String",
      "identifier": "North.Utilities.P101.VibrationRms",
      "rawUnit": "mm/s",
      "canonicalUnit": "mm/s",
      "scale": 1.0,
      "offset": 0.0,
      "samplingIntervalMs": 1000,
      "queueSize": 10,
      "staleAfterMs": 5000
    }
  ]
}
```

`canonicalValue = rawValue × scale + offset`。单位不明确、类型不支持或节点缺失时禁用该映射并报告原因，不默认为 0。一个点位仅有一个启用的主源；映射变更产生新 version，必须暂停该点位、清空旧待处理队列并重建订阅，保留旧样本映射版本。

映射到业务对象：Tag → SourceMapping → MeasurementPoint → Asset；每次通知生成 MeasurementReading；本地 AlarmRule 针对点位生成 Alarm。本文 Namespace/订阅参数属于 SourceMapping 的 OPC UA 类型配置，不要求 Simulation 映射具有 NodeId。Phase 2 不把原生 OPC UA A&C 当业务 Alarm 输入；以后支持时新增独立 EventAdapter，并区分本地规则与 Server condition，避免重复告警。

## 3. Client / Subscription 生命周期

1. 加载管理员已批准 endpoint、客户端证书/私钥和 trust list，发现匹配的安全 endpoint；与配置不一致则失败，不自动降级。
2. 建立 SecureChannel / Session，验证服务端证书与应用身份；用户名密码或用户证书来自 CredentialRef，不进文档/日志。
3. 读取 NamespaceArray，解析映射；检查 NodeClass、DataType、ValueRank、读取权限。浏览只开放批准源和节点范围，支持分页，限制返回数量。
4. 每数据源建立 Subscription；每点位创建 MonitoredItem、Value attribute、Reporting mode。请求 publishing/sampling 1,000 ms、queue 10、discardOldest=true。
5. 记录服务器修正后的 publishing/sampling interval、queue size、keep-alive/lifetime，使用修正值计算健康阈值；服务器拒绝某点位时逐项报告。
6. DataChange 回调捕获值、原始状态、时间和映射版本，形成不可变 Envelope，快速入队；不得在回调中查询数据库或调用 LLM。
7. 停止时取消订阅、关闭 Session、有限时间排空队列；超时损失记录 IngestionGap。

当前 Connector 显式使用 StatusValueTimestamp、未设置 deadband；它允许状态/值/SourceTimestamp 的变化触发通知，不保证 1s SamplingInterval 下每秒必有一条，单独 ServerTimestamp 更新也不满足该触发条件；未设过滤器的默认行为是 STATUS_VALUE。规范见 [Sampling interval](https://reference.opcfoundation.org/specs/OPC-10000-4/5.13.1.2)和[DataChangeTrigger](https://reference.opcfoundation.org/specs/OPC-10000-4/7.10)。客户端保存请求/修正间隔、过滤参数及“周期更新已验证/按变化/未知”的观测契约，不能用当前 Periodic 配置反推通知或物理采样覆盖。

没有通知时不得更新逐点 NotificationFresh，更不能因 Session/Subscription KeepAlive 将点值当作新观测。当前 LastKeepAliveAt 来自 Session 事件，不是独立订阅活性证明；订阅 KeepAlive 本身也不包含新测量，见 [Subscription model](https://reference.opcfoundation.org/specs/OPC-10000-4/5.14.1)。ConnectionHealthy、NotificationFresh、SourceTimestampFresh、ValueChanged 和 DataTrustworthy 的定义/Unknown 状态及测试矩阵以开发计划为准，不要求五个新数据库字段。

必要时后续用受限 Read 确认已批准点位，明确该来源及原始时间；不伪装成新 DataChange/物理采样，也不默认开启无限轮询。首个 AI 闭环不以实现补充 Read 为条件。

## 4. 质量码、时间戳与新鲜度

OPC UA DataValue 包含值、状态和来源/服务器时间，语义依据 [OPC UA Part 4 DataValue](https://reference.opcfoundation.org/specs/OPC-10000-4/7.11)。原始 StatusCode 及信息位必须保留，不能只存 Good/Bad。

| 情况 | 存储/显示 | 对阈值告警的影响 |
|---|---|---|
| Good + 有效数值 + 时间正常 | 存为 Good；显示值和年龄 | 仅新鲜且按序样本参与计时 |
| Uncertain | 原值可保留，标黄并解释 | 不触发、不清除，中断 pending 计时 |
| Bad / 转换失败 | 存状态，NumericValue 可 null；标红 | 不触发、不清除，中断 pending 计时 |
| 数据源失联/超出适用的观测确认时效 | 保留原值及年龄，当前确认不足；与合法恒值静默区分 | 不把历史值当恢复证据，不据此确诊传感器故障 |
| Overflow 信息位 | 存码、计数、登记缺口 | 中断持续时间，需后续连续样本重建计时 |
| 来源时间异常 | 保留原时间和 TimeAnomaly，不更新最新可信投影 | 不参与在线规则 |

EffectiveAt 优先 SourceTimestamp，其次 ServerTimestamp，最后 ReceivedAt；TimestampOrigin 明确 Source/Server/ReceiveFallback。0/无效时间按缺失处理；SourceTimestamp 保留 UTC，不擅自修改源时间。

既有规则以 EffectiveAt/点位 StaleAfterMs 做有限演示的新鲜度门槛，未来时间 >5s 标异常；这不是适用于所有 Server 的完整时间可信度模型。P5 在保留原始时间及历史排序的同时，按观测契约解释源时间年龄：SourceTimestamp 可能是最后值/状态变化时刻，不因它不前进就确认冻结；ServerTimestamp 表示服务器接收或确认值准确的时刻，也不能独立证明现场采样。参见 [SourceTimestamp](https://reference.opcfoundation.org/specs/OPC-10000-4/7.11.3)及[ServerTimestamp](https://reference.opcfoundation.org/specs/OPC-10000-4/7.11.4)。来源/时钟含义未核实则当前证据不足，监测 clock skew，不将合法恒值静默自动当故障；迟到观测不能回退最新快照。

通知接收、源时间年龄、Session/Subscription 活性、Worker 心跳及 LastGood 适用范围分别表达。按变化/未知契约不能以 1.5×SamplingInterval 无通知确认丢样或完整率；已确认的断线、overflow/本地丢弃仍登记相应 gap。无法验证连续观测时不推进持续告警/恢复；数据可信度由后端按点位/窗口判断，AI 保留其他可靠点的异常证据，不用一个点否定全设备。

## 5. 去重、顺序、过载

- 有 SourceTimestamp 时事件键为规范化 `(mappingId, mappingVersion, sourceTimestamp, 原始类型/值, rawStatusCode)` 的哈希；无 SourceTimestamp 时优先 ServerTimestamp。ServerTimestamp 不进入有源时间事件键，避免重传导致不同键。
- 没有任何可信时间时，为入站 Envelope 分配 ID，重试复用 ID；新通知不以“值相同”直接去重，以免丢掉恒定值的采样。
- 同点位按 EffectiveAt 处理；迟到记录归档，不重放在线告警；重连通知重传走同一去重规则。
- 当前 SDK 通知缓冲 100 条，外层 Worker 总队列预算 10,000，500 ms/500 条触发提交；二者分别观测/验证，不能混称同一队列的容量。
- 队列满时 DropOldest 保持近期可见性，但必须记录 dropped count、受影响点位与时间区间，并重置规则 pending 计时。可用自定义缓冲区/丢弃回调拿到被丢 Envelope，不能使用无法追踪丢弃的静默模式。
- 数据库离线时不无限缓存，日志记录损失摘要，恢复后持久化 gap；进程崩溃造成的未知尾部按 LastPersistedAt→恢复时刻记录“可能缺口”。不承诺断线期间无损采集。

## 6. 断线重连

状态：Disabled → Connecting → Connected → Degraded → Reconnecting → Connected；安全/配置错误进入 Faulted，需管理员处理，不无限快速重试。

1. 网络错误后停止新的可信判定，保留已有 Active 告警，开始 gap 区间。
2. 指数退避 1、2、4、8 秒，最大 30 秒，加约 ±20% jitter；单源同一时刻只允许一个重连任务。
3. 当前受控 Runtime 采用重建 Session/Subscription/MonitoredItems 并重验元数据，不承诺离线无损；Republish/TransferSubscriptions 留待另行需求与能力验证，不作为 P5 最小闭环或 P5.5 的必需实现。
4. 每次新 Session 重新读 NamespaceArray、校验节点，Namespace URI 缺失不能退回旧 ns index。
5. 恢复后受限 Read 一次获得当前快照，记录恢复时刻；快照不是离线期间历史。只有服务器支持且配置允许 HistoryRead 时才可未来扩展补采，本阶段不做历史回补。
6. 所有恢复/重传/队列溢出走幂等入库。可恢复通知不意味着完整离线数据；无法证明连续性则保留 gap 并中断 pending 计时。

订阅恢复取决于服务器队列与 SDK 能力，不能假设恢复等于无损；实现以锁定版本的 [官方 Subscription 文档](https://github.com/OPCFoundation/UA-.NETStandard/blob/master/docs/Subscriptions.md) 与样例为参考。

## 7. 证书与运行边界

- 默认 SignAndEncrypt，安全策略与 Server 协商后显式锁定；客户端应用证书、服务端 trust list 持久化，私钥不提交版本库。
- 不可信、过期、主机名/ApplicationUri 不匹配的证书拒绝连接，管理页显示脱敏原因。禁止默认 AutoAcceptUntrustedCertificates。
- 仅显式 Development 模式可使用本机/隔离开发网络的 None 安全 endpoint，页面标记；不得把此配置带入正式环境。
- 容器中的 localhost 指容器自身；模拟器与 Worker 同 Compose 时使用服务名，主机模拟器按容器网络配置选择已批准地址。
- 管理员浏览/配置节点不赋予浏览器直接连接工业服务器的能力；AI 没有 endpoint 编辑、browse、Read 任意 NodeId 或 Write/Call 工具。

## 8. 模拟器与验收实验

锁定 SDK 与原创 Server/NodeManager、有限及常驻三点实验已有实现，见[既有常驻验收](OPCUA_RUNTIME_VALIDATION.md)，不重新安排 spike。新增实验的任务顺序及细节见开发计划：先补60→75→90°C、独立振动变化/停服及最小证据，P8.1准确记录后做首次AI联合调查，再补完整矩阵。运行说明记录版本、命令、场景时间线、过滤/时间语义、证书指纹与固定种子；以下项目是待完成的新范围，不是已执行报告。

| 实验 | 操作 | 验收 |
|---|---|---|
| 正常周期场景 | 三点请求 sampling/publishing 1s、声明并验证周期更新时间；完整版30min | 核对实际生成且满足过滤的通知与入库，不能推算固定物理样本数；1s请求不保证1Hz通知 |
| 恒值但更新时间 | 值恒定、源时间推进，显式 StatusValueTimestamp | 值未变化与新源时间分别可见，不制造设备异常 |
| 合法静默 | 恒值/STATUS_VALUE，或值与源时间均未改变的 StatusValueTimestamp | 无新通知只说明近期确认不足；不伪造观测、完整率、丢样或传感器故障 |
| Session 与订阅分离 | 订阅停止，Session 保活仍成功 | 不能以 Session 保活声称订阅/点位正常；返回独立状态或Unknown |
| 超限/恢复 | 高振动再恢复 | 持续时间/回差正确；同规则只一个 Active |
| 坏质量 | 高值期间注入 Bad/Uncertain | LastGood 保留但标年龄；告警不自动恢复 |
| 停机/网络 | 停 Server 60 秒再启动 | 可见 Reconnecting/Stale/gap；恢复后无重复告警 |
| Namespace index 变化 | 重启改 NamespaceArray 顺序 | 按 URI 仍映射正确；URI 缺失明确 Faulted |
| 重复/乱序 | 重传相同通知、注入老值 | 无重复行；最新投影不回退；在线计时不中途重演 |
| 时间缺失/未来 | 缺来源时间或超前时间 | fallback 标记正确；未来值不进入可信判定 |
| 安全失败 | 更换未受信证书 | 连接拒绝，不自动接受或降级 |
| 队列/数据库 | 消费变慢或暂停数据库 | 有 dropped/gap 指标；恢复后缺口持久化 |
| 部分数据失效 | 温度Bad，振动可信且高值 | AI分别给“部分可信/设备异常证据存在”，仅排除受影响点/结论 |
| 外部安全 Server（P5.5，可选） | 独立部署、批准端点/证书/策略，断网恢复、错误信任与证书失效 | 同一采集链路可安全接入/恢复，失败明确拒绝且不降级；不冒充完整工业运维验收 |

## 9. 可观测性与配置

每数据源暴露 sessionState、lastKeepAliveAt、lastReceivedAt、lastPersistedAt、reconnectCount；每点位暴露 Quality/Freshness、最后可信时间和映射错误。指标包括接收/入库速率、批写延迟、队列长度、drop、Bad/Uncertain、overflow、gap、重连次数。

日志用 dataSourceId / mappingId / correlationId，凭据与私钥不得输出。端点、间隔、timeout、queue、batch、time-skew、stale 和保留时间均配置化；启动时验证正值及 keep-alive/lifetime 关系，记录 Server 修正值。配置更改走版本化、审计和受控重连。
