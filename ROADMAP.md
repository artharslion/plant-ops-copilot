# 实施路线 v0.1

日期：2026-10-03。按可验收增量推进，不用“完成 UI”或“接上模型”代替业务验收。各阶段完成后保存演示脚本、测试结果和已知限制，再进入下一阶段。

2026-10-03：P1.1 已独立复盘并修复凭据覆盖/初始化环境保护；P1.2 已完成，验证记录见 [P1.2 验收](docs/P1_2_VALIDATION.md)。P1.3 及后续功能尚未实现。

## 贯穿阶段的 Golden Path

`North Plant → Utilities → Cooling Pump P-101 → vibration rise → alarm → acknowledge → work order → close`

Phase 1 用 Simulation Source 完成路径；Phase 2 保留该模式并增加 OPC UA Source 演示；Phase 3 在告警后插入联合证据调查；Phase 4 在创建工单前增加提案确认。人工确认告警、维护执行和工单关闭仍由业务服务管理。**AI is not the source of truth**，所有阶段必须保留无需 AI 的业务闭环。

## Phase 1：小型工业资产管理系统

目标：从通用资产管理经验抽象业务，独立重建，不依赖旧系统或 OPC UA/AI。

| 编号 | 实现任务 | 完成证据 |
|---|---|---|
| P1.1（已完成） | Api/Worker/Domain/Application/Infrastructure 和 Vue 骨架；锁版本；Compose 数据库；OpenAPI；健康检查 | 本机启动验证；重复初始化幂等；构建零警告；8 项测试通过；前端构建通过 |
| P1.2（已完成） | Identity Cookie、四角色、UserPlantAccess、防伪、事务 AuditLog、version；工厂/位置/设备创建/读取/编辑/归档与 Vue 工作台 | 23 测试通过（15 真 PostgreSQL）；跨厂列表/ID/写入隔离；循环/同厂关系校验；并发 409；SQL 后失败回滚；重复种子保留修改；浏览器登录→P-101→编辑验收 |
| P1.3 | MeasurementPoint、DataSource/SourceMapping、IMeasurementSource 与可重复 Simulation Source、MeasurementReading 批写、LatestObserved/LastGood、趋势 API/UI | Golden Path 的振动点位可追溯到模拟源；质量和时间可见；Bad 值不覆盖可信值；乱序不回退快照 |
| P1.4 | AlarmRule、持续时间、回差、Alarm/AlarmEvent、确认 API 与告警台 | 高值产生一次告警；确认不恢复；Bad 值不能清除告警 |
| P1.5 | WorkOrder 状态机、备注、告警关联与维修历史 | 创建→执行→完成→关闭；关闭需结果；审计完整 |
| P1.6 | 文档上传/版本/发布/下载、文档 ACL；SignalR + Outbox | 未授权下载失败；断开重连能恢复页面；重复事件不重复显示 |
| P1.7 | 测量分区/分钟聚合/保留任务；完整演示和必要测试；补 README 实际命令 | 查询与推送指标达到规格；清理不会删除未聚合数据 |

**阶段验收**：使用 2 个虚构工厂和多角色账号跑通 README 演示路径；不配置模型密钥、不开 OPC UA Server，系统仍可启动和完成闭环。证明数据库重启后业务可恢复，范围外读取/写入被阻止。

## Phase 2：OPC UA 集成

前置：Phase 1 采集、质量、告警和权限契约稳定。

| 编号 | 实现任务 | 完成证据 |
|---|---|---|
| P2.1 | 技术 spike：锁定官方 SDK 稳定包、核对许可、运行官方示例 Server 或自建虚构节点 | 固定版本说明、连接命令、节点清单、证书信任流程可复现 |
| P2.2 | 在 IMeasurementSource 下新增 OPC UA Source；扩展 SourceMapping 的 OPC UA 配置、Namespace URI 定位、单位转换、映射验证与管理 UI | Simulation Source 保留；订阅值落到正确设备/点位；NodeId 不进入 MeasurementReading；错误映射显示具体原因 |
| P2.3 | Client/Session/Subscription/MonitoredItem；有界队列、批写、去重 | 100 点 1 Hz 30 分钟；重复通知无重复测量/告警 |
| P2.4 | 重连、订阅恢复/重建、质量与时间戳、陈旧检测、IngestionGap | 断网/停机/Namespace index 变化/坏值均按设计展示 |
| P2.5 | 数据源健康、指标、日志、Outbox 推送；两种源的契约/回归脚本 | Simulation 与 OPC UA 配置分别跑通 Golden Path；OPC UA 可复现正常→异常→离线→恢复 |

**阶段验收**：执行 [OPC UA 实验矩阵](docs/OPCUA_INTEGRATION.md#8-模拟器与验收实验)。原始状态码/时间可追踪，无法补回的数据有缺口，所有连接为只读；Bad/Uncertain/陈旧值不引发阈值告警恢复。检查队列过载、数据库离线和证书不可信的失败路径。

不包含：OPC UA 写操作、完整 A&C、工业网络部署、高可用集群和无限离线补采。

## Phase 3：Investigation Copilot

前置：Phase 2 数据具备质量/时间/缺口；文档版本与 ACL 稳定。

核心场景：围绕 Golden Path 的高振动告警，联合查询 MeasurementReading 历史、近期告警、维护工单/备注与已发布文档，输出事实、待验证假设、检查步骤、缺失信息和可追溯引用；继续手工创建并关闭工单。

| 编号 | 实现任务 | 完成证据 |
|---|---|---|
| P3.1 | PlantOps.AI、Provider spike、ILlmGateway、预算/超时、Fake Provider、版本化 Prompt/输出 schema | 无密钥可测流程；真实 Provider 支持所需协议；记录数据发送范围；业务模块无 AI 依赖 |
| P3.2 | 文档解析、chunk、pgvector、授权检索、删除/撤回失效 | 引用定位版本/页或标题；撤回后所有检索与缓存立即不可见 |
| P3.3 | 联合测量历史、告警、维护记录与文档的只读工具和调查编排；actor 注入；窗口/工具次数上限 | 同一告警的证据可联合回查；跨工厂、超范围参数被拒绝；无任意 SQL/URL 工具 |
| P3.4 | 告警调查结构化输出、引用校验、质量/缺口提示、UI 展示 | 事实均有可点开证据；假设与事实区分；缺资料则不猜出处 |
| P3.5 | AiRun、日志/指标/成本、取消、失败降级、评估数据集 | [AI 验收矩阵](docs/AI_DESIGN.md#9-评估与验收)通过，普通业务不受模型离线影响 |

**阶段验收**：20 个固定评估案例包含正常证据、缺文档、坏值、缺口、越权、文档注入、工具注入、模型超时和结构错误。高风险安全案例全部通过；事实证据覆盖率达到 100%，不支持的内容降为假设或删除。AI 不持有任何业务写工具。

不包含：自主维修、自动确认告警、自动改变规则、无引用根因结论。

## Phase 4：AI Action Copilot

前置：Phase 3 权限、证据和工具边界通过验收。

| 编号 | 实现任务 | 完成证据 |
|---|---|---|
| P4.1 | ActionProposal schema/状态机；仅 CreateWorkOrder / AppendWorkOrderNote | 模型只生成提案；无直接调用写服务的凭据 |
| P4.2 | 审批页面：目标、内容、证据、版本、有效期；批准/拒绝 | 未点击确认、拒绝、过期均不执行；编辑后需重新确认 |
| P4.3 | 事务内重授权、版本/状态校验、幂等键、审计；失败明确分类 | 双击批准只写一次；角色撤销/目标变化导致拒绝/冲突 |
| P4.4 | 审批 E2E、审计回看和恢复实验 | 进程在提交前后崩溃均可查询权威执行结果；不重复工单 |

**阶段验收**：批准只执行允许的业务动作；审计可以从 AiRun→提案→审批人→工单/备注完整回查。未经批准执行数为 0，重复批准重复写入数为 0。关闭工单、删除、规则修改、OPC UA Write/Call 始终不在工具集合。

## 建议的第一轮实现顺序

1. 骨架/迁移/身份与工厂授权，然后资产树与设备页面。
2. 先让一台设备、一个振动点位贯穿模拟源→存储→趋势。
3. 增加一条高振动规则、告警确认与工单闭环，再扩充到种子规模。
4. 补文档版本/ACL、Outbox、实时推送、聚合/保留及回归检查。
5. 完成 OPC UA spike，在统一接口下新增 OPC UA Source，保留 Simulation Source 并分别验收 Golden Path，再建设 RAG/联合调查。
6. Investigation Copilot 验收完成后实现动作提案和审批。

先完成一条纵向业务路径，再扩充页面和点位；各阶段保留不依赖外部服务的模拟/伪 Provider，以支持回归和演示。
