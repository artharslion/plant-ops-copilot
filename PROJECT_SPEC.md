# 项目规格

Plant Ops Copilot 是面向高级工程师面试的 .NET 工业运维作品。最终目标是让 AI Agent 基于真实设备观测、告警、维修历史和规程调查异常，提出有依据、可验证的原因假设和维护建议。工程师能核查每条引用，决定是否建单，并在工单中记录处理结果。

## 作品集的完成条件

| 要回答的问题 | 一次演示应交付的证据 |
| --- | --- |
| 当时设备和测量链分别发生了什么？ | 用同一告警还原触发、恢复及调查时的读数、单位、时间和质量。保留坏点、断流或资料缺失，使工程师能分辨哪些现象仍可判断。 |
| Agent 自己决定了什么？ | 区分服务端预置证据、当轮可用工具和模型实际选择。工程师能展开引用，复核哪条证据支持或削弱某个解释、下一项检查可以区分什么。证据不足时允许没有原因假设。 |
| 建议改变了哪一步维护工作？ | 在同一调查中追溯建议、人工采纳或修改的内容、确认回执、关联工单和处理结果。由评审者说明建议是否具体到能执行核查，结果由处理人填写。 |

演示采用单组织、多工厂授权，现有工厂、设备和规程均为虚构。已采集的数据包括本机仿真服务器通过真实 OPC UA 协议产生的观测。P-101 三个变量没有机械或热力因果模型。要评价面向真实设备的建议，还缺有来源、可授权使用的实测片段、工况和独立核查记录。交付进度见 [开发计划](ROADMAP.md)，数据含义见 [P-101 演示规程](docs/INDUSTRIALSIM_P101_DEMO_MANUAL.md)。

## 业务范围

| 域 | 职责 |
| --- | --- |
| 资产目录 | 工厂、位置树、设备、点位定义和归档。 |
| 遥测 | 内置仿真与受控 OPC UA、原始观测、最新可信值、分钟趋势、质量及缺口。 |
| 告警 | 确定性阈值规则、持续时间、回差、确认及自动恢复。 |
| 维护 | 工单、处理备注、维修历史与操作审计。 |
| 知识 | 文件上传、版本、发布、授权下载及 TXT/Markdown 片段检索。 |
| AI | 人员从告警发起调查，Agent 在当前设备范围内按需只读取证。已保存的建议可整理为建单或追加备注提案。 |

设备控制、现场安全联锁、根因确诊、通用工业 Historian、库存和采购均在演示范围之外。

## 角色与权限

业务请求使用同源账户会话。下表以角色能力和用户获得的工厂授权为前提。资料访问还需满足文档 ACL。工作台 AI 模式是 Admin 管理的全局设置。

| 能力 | Viewer | Operator | MaintenanceEngineer | Admin |
| --- | --- | --- | --- | --- |
| 读授权工厂业务数据、发起只读调查 | ✓ | ✓ | ✓ | ✓ |
| 读额外限制角色的资料 | 按 ACL | 按 ACL | 按 ACL | 按 ACL |
| 确认告警 | — | ✓ | ✓ | ✓ |
| 创建或处理工单、添加备注、执行调查提案 | — | — | ✓ | ✓ |
| 上传、发布和撤回资料 | — | — | ✓ | ✓ |
| 管理目录、规则、数据源和映射 | — | — | — | ✓ |

提案由调查发起人本人保存与确认，确认时仍须具备维护角色、该工厂授权和相关证据的访问权。服务端处理范围和版本校验。演示账户只在 Development 初始化。

## 端到端场景

1. 在设备详情查看各点位的最新观测、最后可信观测、质量和历史趋势。Bad、Uncertain 与已知断流区间保留质量提示和曲线缺口。
2. P-101 的可信振动持续超限时产生单一活动告警。人员确认记录确认人和时间。回落满足规则条件后记录恢复。关联工单独立完成与关闭。
3. 工程师上传 PDF、Markdown 或 UTF-8 TXT，最大 10 MiB。文件版本发布后可按 ACL 下载。当前 PDF 标记为 `NeedsText`，文本提取与页码引用属于候选扩展。 [知识范围](ROADMAP.md#5-p7设备知识与-rag-增强)
4. 为批准的本机回环 OPC UA 来源配置节点映射。停服期间呈现质量和连接状态，重连时重新按 Namespace URI 解析并记录无法补齐的缺口。接入条件见 [OPC UA 集成](docs/OPCUA_INTEGRATION.md)。
5. 人员从告警发起调查，查看已授权测量、告警、维护和资料所支持的事实、待核查假设、建议及引用。模型不可用时，普通业务功能继续运行。调查字段以 [v3 契约](docs/INVESTIGATION_RESPONSE_V3.md) 为准。
6. 有维护权限的调查发起人核对并确认提案。服务端检查版本、权限、证据和过期条件后幂等执行。新建工单处于 Draft，随后人员打开、开始处理、填写结果、完成并关闭。审计与提案回执可沿工单追溯到调查。工单状态与告警确认、规则恢复各自独立。

## HTTP 接口入口

业务接口以前缀 `/api/v1` 提供。登录、退出及其他写请求使用通过 `GET /auth/csrf` 获取的 `X-CSRF-TOKEN`。Development 可读取 `/openapi/v1.json`，接口细节以 API 路由和请求类型为准。以下路径省略公共前缀。

| 能力 | 代表性路径 |
| --- | --- |
| 身份 | `GET /auth/me`、`POST /auth/login`、`POST /auth/logout` |
| 目录 | `GET /plants`、`GET /plants/{id}/locations`、`GET /assets`、`PATCH /assets/{id}` |
| 遥测 | `GET /assets/{id}/measurement-points`、`GET /assets/{id}/measurements`、`GET /plants/{id}/data-sources` |
| 告警 | `GET /alarms`、`GET /alarms/{id}/events`、`POST /alarms/{id}/acknowledge` |
| 工单 | `GET /work-orders?assetId=…`、`POST /work-orders`、`POST /work-orders/{id}/transition`、`POST /work-orders/{id}/notes` |
| 资料 | `POST /documents`、`POST /documents/{id}/versions`、`POST /documents/{id}/publish`、`GET /documents/{id}/versions/{version}/download` |
| 调查 | `POST /alarms/{id}/investigations`、`GET /investigations/{id}`、`GET /investigations/{id}/evidence/{evidenceId}` |
| 提案 | `POST /investigations/{id}/proposals`、`POST /investigations/{id}/note-proposals`、`POST /action-proposals/{id}/execute`、`POST /action-proposals/{id}/reject` |

列表按对应资源的范围与分页规则提供。接口错误使用 ProblemDetails，通过 `correlationId` 关联日志。权限不足可返回 403，受限资源按服务规则返回 404，版本和并发冲突可返回 409。测量写入仅经采集管道。

## 非功能目标

- 原始观测与分钟聚合默认分别保留 7 天与 90 天。存储、API 时间使用 UTC，工作台按北京时间显示。
- 演示性能基线为 100 个 Simulation 点位 × 1 Hz，普通列表及设备概览热查询 p95 < 500 ms，入库到 UI 可见 p95 < 2 s。测试需注明样本规模、运行时间和机器配置。测试范围与长期容量的开放项见 [P5](ROADMAP.md#3-p5opc-ua-持续采集与实时数据底座)。
- 进程重启保留已提交的业务变更。对未持久化的观测，只报告有证据支持的已知损失或可能缺口。
- 验收覆盖跨工厂访问、并发修改、重复审批、数据质量、重连和模型失败。真实模型质量和完整工业网络接入分轮验证。
