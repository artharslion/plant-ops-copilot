# 系统架构

本页说明当前源码的组成和跨模块约束。阶段状态和验收见 [开发计划](ROADMAP.md)，本地与容器启动见 [文档导航](docs/README.md)。

## 服务与依赖

```mermaid
flowchart LR
  UI[Vue 工作台] --> API[Api · HTTP / Identity / SignalR]
  API --> DB[(PostgreSQL)]
  API --> FILES[共享资料目录]
  DB -->|Outbox| API
  API -->|变更通知| UI
  SIM[内置 Simulation] --> WORKER[Worker · 采集 / 保留 / 调查]
  OPC[受控 OPC UA Server] --> WORKER
  WORKER -->|读数 / 告警| DB
  WORKER -->|授权资料读取| FILES
  WORKER -->|受限模型请求| LLM[模型 Provider]
```

模块化单体包含 Api 和 Worker 两个可执行 Host，共享业务库、数据库和资料存储。源码容器部署分开运行 Web、API 和 Worker，组合镜像可由 API 托管 Web 并让 Worker 与 IndustrialSim 同容器运行。当前演示按单 Worker 部署。OPC UA 连接由 Worker 建立，模型和数据库配置保存在服务端。

| 位置 | 职责 |
| --- | --- |
| `src/PlantOps.Api` | HTTP、同源身份会话、防伪、SignalR 和 Outbox 分发 |
| `src/PlantOps.Worker` | 遥测采集、保留维护和调查作业 |
| `src/PlantOps.Domain`、`src/PlantOps.Application` | 业务实体与不变量、用例契约、授权读取端口和规则 |
| `src/PlantOps.Infrastructure` | EF Core、Identity、文件存储、Simulation、入库及业务服务 |
| `src/PlantOps.Integrations.OpcUa` | SDK 隔离、Session、Subscription 和观测标准化 |
| `src/PlantOps.AI` | 调查编排、授权工具调度、输出校验和 Provider 适配 |
| `web` | Vue 3、TypeScript、Vite、Router、Pinia 和 Element Plus |

Domain 不依赖 EF Core、OPC UA 或模型 SDK。Application 定义端口，各实现由 Host 组装。Api 和 Worker 使用 .NET 10、EF Core 与 Npgsql，演示 Compose 使用 PostgreSQL 17。数据库结构由迁移管理，API 在 Development 下提供 OpenAPI，前端可据此更新生成类型。

## 采集、告警与事务

- Simulation 和 OPC UA 通过 Application 的 `IMeasurementSource` 交付标准观测，共用入库和告警管道。每个点位仅启用一个来源映射。Worker 按源管理生命周期，配置变化会重建对应来源。受控启用、白名单与故障语义见 [OPC UA 集成](docs/OPCUA_INTEGRATION.md)。
- 批次入库核对当前来源、点位及映射版本，事务内写入原始观测、最新投影、规则状态、告警变化与 Outbox。工厂级事务锁与目录/配置写入协调。重试保留事件键，重复样本不会重演规则。过载、断流与可能丢失的尾部由缺口显式表达。
- 最新观测与最后可信观测分别保留。告警由满足质量、时效、顺序条件的样本驱动，超限和恢复分别应用持续条件及回差。告警确认、恢复及工单关闭为独立业务变更。
- 原始读数按 UTC 日分区，分钟聚合由维护任务更新。原始/分钟保留默认 7/90 天，部署可以覆盖。运行时容量与可靠性的验收范围见 [P5](ROADMAP.md#3-p5opc-ua-持续采集与实时数据底座)。

## 授权、资料与实时一致性

- 同源 ASP.NET Core Identity Cookie 使用 HttpOnly/SameSite Strict，非 Development 强制 Secure。API 写请求使用 `X-CSRF-TOKEN`。业务服务读取当前角色和工厂授权，文档还受自身 ACL 限制。返回范围与业务权限见 [项目规格](PROJECT_SPEC.md)。
- 资料保存在 API 和 Worker 共享的受控目录。上传校验后生成版本，下载和调查引用通过授权路径读取。TXT/Markdown 支持片段检索，PDF 当前保存为 `NeedsText`，解析计划见 [P7](ROADMAP.md#5-p7设备知识与-rag-增强)。
- 业务更新与审计或通知按对应事务提交。API 从 Outbox 领取通知并通过 `/hubs/plant-events` 发布，测量更新可按点位合并。连接范围由服务端控制，角色、工厂授权或会话失效会中断连接。客户端以 HTTP 快照为准，利用通知刷新相关状态，重连后补查。
- Api 和 Worker 均有 `/health/live`、`/health/ready`。数据源和模型故障各自降级。调试、部署和测试使用 [开发指南](docs/DEVELOPMENT.md) 中的对应入口。

## 调查与人工动作

用户在告警详情提交调查，Worker 处理数据库中的 AiRun。调查在已授权的设备和冻结证据范围内按需读取，经 `ILlmGateway` 调用模型。服务端负责证据来源、时间/质量、引用和类型化输出校验。模型解释现象并提出待核查的假设和建议。字段和边界见 [调查建议 v3](docs/INVESTIGATION_RESPONSE_V3.md)。

整理工单或备注时，应用服务从已保存调查生成提案。人工编辑并确认完整内容后，服务重新校验权限、证据、期限及版本，在事务内执行并记录幂等回执和审计。采集、告警和页面刷新不会触发模型。未来主动调查的范围见 [P9](ROADMAP.md#7-p9主动式工业智能proactive-industrial-intelligence后续规划)。

## 架构决策

| 决策 | 理由与约束 |
| --- | --- |
| ADR-001 模块化单体、两个 Host | 分离 HTTP 与后台作业生命周期，共享业务库及数据库。 |
| ADR-002 PostgreSQL 与受控文件存储 | 关系数据、分区聚合和事务在单库完成。向量检索作为可选实验。 |
| ADR-003 规则与质量由业务后端判断 | 告警可复现，坏值和掉线不会被当作恢复。 |
| ADR-004 先只读调查，再由人工确认动作 | 缩小模型权限面，让每次业务写入可核对与审计。 |
| ADR-005 同源会话与服务端授权 | 使用 Identity 与工厂范围、文档 ACL，支持撤销已建立的访问。 |
| ADR-006 HTTP 快照、SignalR 通知与事务 Outbox | 核心状态在数据库提交。通知可能重复，失联后由客户端补查。 |
| ADR-007 共用数据源契约 | 真实协议和离线演示复用质量、规则和入库逻辑。 |
