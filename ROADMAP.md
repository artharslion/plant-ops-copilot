# 实施路线 v0.1

## 当前执行顺序（2026-10-09 调整）

2026-10-12 最新：[延长等待与流式调查](docs/AI_STREAMING_VALIDATION.md)已实现默认单请求120秒/整次180秒、可选SSE接收及页面等待秒数/证据进度/取消，257项回归通过。唯一真实流式抽查约58.24秒后InvalidResponse，非Timeout、未重试；中转站流式兼容与新增分钟证据的真实成功回答仍待验证。页面进度不代表逐字展示模型结论。

2026-10-12 后续：[预算核算与紧凑上下文](docs/AI_BUDGET_VALIDATION.md)已修复，有效usage替换预留、未知失败仍保守计入；中文机器JSON与重复取证压缩，原预算上限不变。247项回归及模拟HTTP两轮通过。本轮唯一新真实调用首请求Timeout，未进入第二轮、未重试；新增分钟趋势的真实成功回答仍待验证。下文“下一步减少上下文”为此前记录。

2026-10-12：[二十个固定业务调查场景](docs/AI_SCENARIO_VALIDATION.md)已通过确定性验收，241项回归零失败/跳过；覆盖正常分钟证据、缺资料、质量/缺口、授权变化、注入、伪造输出、供应商失败、取消及预算。Fake仅验证强制边界；完整真实模型多场景质量、稳定性/成本及P4仍待验收。下文“二十场景开放”为此前状态。

本轮唯一新真实调查在一次允许工具调用后，第二模型轮因剩余输入预算不足而BudgetExceeded；48条协议样本及两个分钟核对通过，模型无答案、未重试。下一步先解决16k预算内多轮上下文成本，再做真实质量抽查；新增分钟趋势的成功模型回答仍待验证。

2026-10-10 分钟趋势增量：[有限采集后一次性聚合](docs/FINITE_MINUTE_AI_VALIDATION.md)完成，221项检查通过；49条协议样本形成两个可核对的分钟桶，调查E4可授权展开，保留缺口与陈旧提示。新增真实模型调用Unavailable，未重试；新增趋势的真实回答尚未验证。

2026-10-10 最新增量：[告警 AI 调查](docs/AI_INVESTIGATION_VALIDATION.md) 已接通告警页/API/AiRun/Worker/授权证据/ILlmGateway/持久化结果，支持取消、租约恢复、事实与引用校验和证据展开。135项专项/回归通过；[IndustrialSim有限联合演示](docs/INDUSTRIALSIM_AI_VALIDATION.md) 的48条真实OPC UA读数触发/恢复一个告警，一次真实gpt-6.1-sol调查约11.22秒成功，10项证据可授权展开，观测仿真与AI真实独立标记。早先Timeout报告保留。完整P3.5二十场景/稳定性/成本指标未完成，P4未实现；业务库未迁移。下列较早日期的“调查/UI待实现”为当时历史状态。

2026-10-10 已完成 [P3.2 授权资料证据](docs/AI_EVIDENCE_VALIDATION.md)：TXT/Markdown 版本化片段、确定性术语查询与重新授权引用展开，91 项专项与回归全过；旧资料按需补建，撤权/撤回/换版立即失效。未迁移业务库、未调用真实模型；P3.3–P3.5 的 AiRun/联合调查/语义校验/告警 UI 和 P4 仍待实现。

2026-10-09 已实现 [P3.1 接入基础](docs/AI_PROVIDER_SETUP.md)：PlantOps.AI、ILlmGateway、Fake、预算/超时/取消、版本化输出和本地密钥入口。70 项适配/输出/健康检查通过。`gpt-6.1-sol` 的四项接口能力分别有成功记录，完整套件仍有超时/上游不可用，真实稳定性未通过；AiRun、授权工具/RAG、调查 UI 与完整 P3 验收未完成。

2026-10-09 已完成 [IndustrialSim 有限OPC UA增量](docs/INDUSTRIALSIM_DEMO_VALIDATION.md)：单点实际订阅→入库→一个告警→自动恢复及授权读取。此增量不代表完整P2通过，不改变AI优先顺序；P3已进入接入基础，P4仍未实现。

用户明确本项目重点是 AI 接入演示，采用 [AI 优先验收计划](docs/plans/2026-10-09-ai-demo-acceptance.md)：**P1 已完成 → P3 AI 只读调查 → P4 人工审批动作 → 可选 P2 OPC UA**。阶段编号保留，实施不再按编号顺序。

G0 七天满量/八天滚动/90 天分钟及正式维护、WAL、备份恢复峰值转为长期扩展，延期且未验收，不再阻塞 AI 或有限演示。P2 仍部分完成，当前业务 OPC UA 仍禁止启用。下面历史日期的旧容量前置和顺序要求以本节及新计划为准。

2026-10-08：[P2.2 节点元数据独立验收](docs/P2_NODE_METADATA_VALIDATION.md) 通过普通/重排真实协议各 17 项校验，后端 141 项全过。Worker 授权 Browse 与完整 P2.2 仍开放，G0 未通过，持续 OPC UA 接入仍禁止启用。下一点为预算前置的小规模结构/WAL/备份恢复实测。

2026-10-06 当前 P2 进度：[SDK/许可与安全隔离 spike](docs/P2_1_SPIKE.md) 通过；[配置、标准化、历史采样语义增量](docs/P2_2_VALIDATION.md) 已实现，114 项后端回归通过。完整 P2.2 与 P2.3–P2.5 尚未完成，G0 七天满量/并发维护/WAL备份仍未通过，应用持续 OPC UA 接入保持禁止启用。下文旧“未开始”描述为对应日期的历史状态。

日期：2026-10-03。按可验收增量推进，不用“完成 UI”或“接上模型”代替业务验收。各阶段完成后保存演示脚本、测试结果和已知限制，再进入下一阶段。

2026-10-03：P1.1 已独立复盘并修复凭据覆盖/初始化环境保护；P1.2 已完成，验证记录见 [P1.2 验收](docs/P1_2_VALIDATION.md)。2026-10-05：P1.3 已完成，验证记录见 [P1.3 验收](docs/P1_3_VALIDATION.md)。P1.4 已完成，见 [P1.4 验收](docs/P1_4_VALIDATION.md)。P1.5 → P1.6 → P1.7 已完成，P1 整体验收见 [P1.7 验收](docs/P1_7_VALIDATION.md)。P2 实现尚未开始；完整七天磁盘容量与 Outbox 历史管理是前置门槛。

## 贯穿阶段的 Golden Path

2026-10-06：[P1 复盘整改](docs/P1_REVIEW_FIX_VALIDATION.md) 已完成通用持续采集、有界损失摘要/显式缺口、历史间隔保护与可选 Outbox 清理，88 项回归通过。七天满量和并发维护未通过；P2 协议实现尚未开始。

`North Plant → Utilities → Cooling Pump P-101 → vibration rise → alarm → acknowledge → work order → close`

Phase 1 用 Simulation Source 完成路径；Phase 2 保留该模式并增加 OPC UA Source 演示；Phase 3 在告警后插入联合证据调查；Phase 4 在创建工单前增加提案确认。人工确认告警、维护执行和工单关闭仍由业务服务管理。**AI is not the source of truth**，所有阶段必须保留无需 AI 的业务闭环。

## Phase 1：小型工业资产管理系统

目标：从通用资产管理经验抽象业务，独立重建，不依赖旧系统或 OPC UA/AI。

| 编号 | 实现任务 | 完成证据 |
|---|---|---|
| P1.1（已完成） | Api/Worker/Domain/Application/Infrastructure 和 Vue 骨架；锁版本；Compose 数据库；OpenAPI；健康检查 | 本机启动验证；重复初始化幂等；构建零警告；8 项测试通过；前端构建通过 |
| P1.2（已完成） | Identity Cookie、四角色、UserPlantAccess、防伪、事务 AuditLog、version；工厂/位置/设备创建/读取/编辑/归档与 Vue 工作台 | 23 测试通过（15 真 PostgreSQL）；跨厂列表/ID/写入隔离；循环/同厂关系校验；并发 409；SQL 后失败回滚；重复种子保留修改；浏览器登录→P-101→编辑验收 |
| P1.3（已完成） | MeasurementPoint、DataSource/SourceMapping、IMeasurementSource 与可重复 Simulation Source、MeasurementReading 批写、LatestObserved/LastGood、趋势 API/UI | 34 测试通过（20 真 PostgreSQL）；Worker 实际入库；质量/时间/来源版本可见；Bad/Uncertain 不替换 LastGood；乱序/未来不回退；停采陈旧；Admin 编辑与 Viewer 浏览；分钟统计缺口可见 |
| P1.4（已完成） | AlarmRule、持续时间、回差、Alarm/AlarmEvent、确认 API 与告警台 | 51 测试通过（27 真 PostgreSQL）；持续/回差/坏值/陈旧/迟到；去重/重启/事务回滚；角色/工厂/防伪/并发；实际 Worker→确认→恢复及规则编辑页面 |
| P1.5（已完成） | WorkOrder 状态机、备注、告警关联与维修历史 | 9 工单测试；状态机/取消/返工/结果要求，范围/角色/CSRF/409/回滚；实际工程师创建→关闭，见 docs/P1_5_VALIDATION.md |
| P1.6（已完成） | 文档上传/版本/发布/下载、文档 ACL；SignalR + Outbox | 7 文档/Outbox 测试与前端去重；实际上传/发布、断线重连补查；见 docs/P1_6_VALIDATION.md |
| P1.7（已完成） | 测量日分区/持久分钟聚合/安全保留；20设备100点10文档；完整闭环与恢复验收 | 74 测试通过；100点1Hz/30分钟五客户端，179,900完整秒采样无丢失/重复；最差列表p95 39.35ms、实际UI p95 541ms；见 docs/P1_7_VALIDATION.md |

**阶段验收**：使用 2 个虚构工厂和多角色账号跑通 README 演示路径；不配置模型密钥、不开 OPC UA Server，系统仍可启动和完成闭环。证明数据库重启后业务可恢复，范围外读取/写入被阻止。

## Phase 2：OPC UA 集成

前置：Phase 1 采集、质量、告警和权限契约稳定。

2026-10-05：[P1 复盘](docs/P1_REVIEW.md) 与 [P2 详细计划](docs/plans/2026-10-05-p2-opcua.md) 已完成。2026-10-06 已补齐通用源生命周期、显式损失消费与固定间隔保护；P2 仍需协议事件、修正采样/变化通知与生效区间配置。G0 的 Outbox 策略已实现但容量尚未通过，G1 固定版本/许可尚未通过。小规模隔离 spike 可与容量调查推进，持续接入和满量试验必须先具备容量，不能只注册 Adapter。P2 协议实现未开始。

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

前置：P1 模拟采集的质量/时间/缺口与文档版本/ACL 稳定，复用已有授权业务读取服务；完整 Phase 2 和长期 G0 不作为前置。

核心场景：围绕 Golden Path 的高振动告警，联合查询 MeasurementReading 历史、近期告警、维护工单/备注与已发布文档，输出事实、待验证假设、检查步骤、缺失信息和可追溯引用；继续手工创建并关闭工单。

| 编号 | 实现任务 | 完成证据 |
|---|---|---|
| P3.1（基础已实现，真实稳定性待验） | PlantOps.AI、Provider spike、ILlmGateway、预算/超时、Fake Provider、版本化 Prompt/输出 schema | 70 项适配/健康检查通过；真实能力分别成功但套件尚未全过，详见 AI_PROVIDER_SETUP；普通业务不依赖模型在线 |
| P3.2（已完成首轮） | 首轮 TXT/Markdown 解析、版本化片段与确定性术语检索、授权过滤及撤回失效；pgvector/embedding/PDF 后续增强 | 91 项专项与回归通过；引用定位版本/标题/行号/片段，撤权/撤回/换版后引用失效；未解析 PDF 明确提示，见 AI_EVIDENCE_VALIDATION |
| P3.3（首版已实现） | 固定告警/设备范围的五个只读工具、actor 重新授权、证据账本与有界编排 | 128 项专项/回归通过；实际业务 Gateway 调用；跨用户/工厂与非法参数拒绝；无 SQL/URL/写工具，见 AI_INVESTIGATION_VALIDATION |
| P3.4（首版已实现，完整质量待验） | 逐字事实/引用校验、质量/缺口提示、告警调查 UI | Chrome Fake 交互通过；IndustrialSim协议告警的一次真实模型调查及授权展开成功，完整质量评估未通过 |
| P3.5（部分完成） | AiRun、租约恢复、取消、预留 token/错误码、模型失败降级已实现；完整指标/成本/保留与二十场景仍开放 | 普通业务不受已测模型离线影响；完整 [AI 验收矩阵](docs/AI_DESIGN.md#9-评估与验收)未通过 |

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
5. 在已有模拟业务闭环上优先建设 Provider、授权资料检索与联合调查，完成有限演示与安全评估。
6. Investigation Copilot 验收完成后实现动作提案和审批；完整 OPC UA、向量/PDF 与长期容量后续推进。

先完成一条纵向业务路径，再扩充页面和点位；各阶段保留不依赖外部服务的模拟/伪 Provider，以支持回归和演示。
