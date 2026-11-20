# 项目规格

Plant Ops Copilot 面向单组织、多工厂的工业资产运维演示。设备、工厂和资料均为虚构内容。当前交付范围和剩余验收以 [开发计划](ROADMAP.md) 为准，实体和系统分层见 [领域模型](docs/DOMAIN_MODEL.md) 与 [系统架构](ARCHITECTURE.md)。

## 业务范围

| 域 | 职责 |
| --- | --- |
| 资产目录 | 工厂、位置树、设备、点位定义和归档。 |
| 遥测 | 内置仿真与受控 OPC UA、原始观测、最新可信值、分钟趋势、质量及缺口。 |
| 告警 | 确定性阈值规则、持续时间、回差、确认及自动恢复。 |
| 维护 | 工单、处理备注、维修历史与操作审计。 |
| 知识 | 文件上传、版本、发布、授权下载及 TXT/Markdown 片段检索。 |
| AI | 只读调查、可核查引用、关联告警的 Draft 工单或追加备注提案，由人确认执行。 |

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

服务端处理范围和版本校验，演示账户只在 Development 初始化。前端可用按钮反映当前权限。

## 端到端场景

1. 在设备详情查看各点位的最新观测、最后可信观测、质量和历史趋势。Bad、Uncertain 与已知断流区间保留质量提示和曲线缺口。
2. P-101 的可信振动持续超限时产生单一活动告警。人员确认记录确认人和时间。回落满足规则条件后记录恢复。关联工单独立完成与关闭。
3. 工程师上传 PDF、Markdown 或 UTF-8 TXT，最大 10 MiB。文件版本发布后可按 ACL 下载。当前 PDF 标记为 `NeedsText`，文本提取与页码引用见 P7。
4. 为批准的本机回环 OPC UA 来源配置节点映射。停服期间呈现质量和连接状态，重连时重新按 Namespace URI 解析并记录无法补齐的缺口。接入条件见 [OPC UA 集成](docs/OPCUA_INTEGRATION.md)。
5. 人员从告警发起调查，查看已授权测量、告警、维护和资料所支持的事实、待核查假设、建议及引用。模型不可用时，普通业务功能继续运行。调查字段以 [v3 契约](docs/INVESTIGATION_RESPONSE_V3.md) 为准。
6. 有权限的人员整理工单或备注并核对完整内容。服务端检查版本、权限、证据和过期条件后幂等执行，审计关联提案与调查。

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
