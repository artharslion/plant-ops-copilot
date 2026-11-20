# OPC UA 集成

[开发计划 P5](../ROADMAP.md#3-p5opc-ua-持续采集与实时数据底座) · [本机复现与验收记录](INDUSTRIALSIM_RUNTIME_VALIDATION.md) · [隔离协议实验](../deploy/opcua/README.md)

普通 Worker 可在显式启用的 Development 回环环境中，通过 OPC UA 采集数值变量。受控运行使用匿名只读连接及明确批准的节点。外部受信加密 Server 属于可选的 P5.5，隔离实验中的加密自检有独立入口。

## 运行边界

`OPC UA Server → Session / Subscription / MonitoredItem → NormalizedSample → MeasurementWorker → 测量、告警和实时通知`

- `PlantOps.Integrations.OpcUa` 负责 SDK 连接、订阅、元数据核查和样本规范化。`OpcUaRuntimeSource` 与 Simulation 均实现 `IMeasurementSource`。点位、告警和工单继续使用业务标识。
- 每个数据源归属单一工厂。管理员用 `EndpointReference` 选择部署方批准的地址。启用需在 API 和 Worker 加载 `OpcUa:LocalRuntimeEnabled=true` 和同一精确节点白名单。示例见 [回环配置](../deploy/opcua/local-runtime.example.json)。
- 运行地址限 `opc.tcp://localhost:<port>` 或 `opc.tcp://127.0.0.1:<port>`。客户端核对端点、服务器 ApplicationUri、安全模式与策略，要求 `None`。容器中的回环地址指其网络命名空间。IndustrialSim overlay 通过与 Worker 共享网络空间复用这条策略，见 [工作台接入](INDUSTRIALSIM_WORKBENCH.md)。
- 映射以 `NamespaceUri + IdentifierType + Identifier` 标识，支持 String、Numeric 和 Guid。每次建立 Session 重新解析 Namespace index，核对确切节点、标量类型、读取权限和单位。当前允许速度单位 mm/s、m/s 和温度单位 degC、K 的指定换算。采集只读取批准节点及所需属性，无工业凭据解析、通用 Browse、Write、Call 或 HistoryRead。调查只通过授权的业务数据读取工具取证。

## 订阅与采样

受控连接每源允许 1 到 10 个映射，请求采样和发布间隔均为 1,000 ms，每点队列 1 到 10 条。服务器修正的采样或发布间隔超出 1,000 到 2,000 ms、修正队列超出范围或任一节点拒绝订阅时，该源进入配置故障。实际修正参数写入版本化采样区间，完成配置复核后放行样本。映射变更时撤销旧配置，保留历史区间与读数归属。

Connector 使用 `StatusValueTimestamp`，由状态、值或 SourceTimestamp 变化触发，无 deadband。仅 ServerTimestamp 变化不会触发。请求或修正的采样间隔不保证逐秒通知，也不证明设备物理采样。Worker 记录 Session KeepAlive 与逐点实际接收时间。独立 Subscription KeepAlive 仍未知，Session 保活不会刷新点位时效。停发但 Session 健康时，逐点证据仍可变陈旧或不可验证。

## 质量、时间与缺口

- 保留原始 OPC UA 状态码和信息位。Bad、Uncertain、Overflow、时间异常或陈旧的观测不推进阈值持续计时。LastGood 用于回看，当前可用性随时间和采集状态单独判断。
- 有效时间依次取 SourceTimestamp、ServerTimestamp、ReceivedAt，保留 `TimestampOrigin`。超出未来容差的样本只保留为历史异常。IndustrialSim 的 SourceTimestamp 是协议投影时间，物理采样时效未知。按变化通知的点位可能合法静默。
- 去重键绑定映射及版本、可信来源时间、原始值和状态码。无来源时间时优先服务器时间，两种时间都缺失时同一入站样本的重试复用其标识。迟到观测入历史，最新投影不会回退，在线告警也不会重演。
- Connector 另有 100 条本地通知缓冲，超出时结束流并记录可能缺口。`MeasurementWorker` 独立维护有界缓冲与批写。连接中断、服务端 Overflow、本地丢弃及数据库不可用均需按可观测范围记录缺口。未知丢失数量保留为空。
- 仅在历史采样区间可验证时使用对应修正间隔解释通知间断。重复通知、按变化静默与物理采样覆盖分别核查。恢复前保留活动告警，恢复后按新可信观测重新累计待恢复时长。

## 错误与恢复

配置、身份、节点元数据或安全策略拒绝后，来源保持 `Faulted`，由管理员核对并更新配置。连接中断进入 `Reconnecting`，采用带抖动的指数退避（上限 30 秒）。复连重建 Session、Subscription 和 MonitoredItems，重新解析 Namespace 并记录新的采样区间。断开期间保留可能缺口，待收到新样本后更新可用性。当前流程未实现历史回补、通用 Republish 或 TransferSubscriptions；切换来源需明确操作。

[验收记录](INDUSTRIALSIM_RUNTIME_VALIDATION.md)保存三点 30 分钟运行、重复停服与 Namespace 重排、Worker 强停、Session 保活期间停发、独立 PostgreSQL 暂停与恢复的结果。现场网络、证书运维、真实溢出和其余可靠性矩阵的状态见 [开发计划 P5](../ROADMAP.md#3-p5opc-ua-持续采集与实时数据底座)。
