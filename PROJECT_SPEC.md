# 项目规格 v0.1

2026-10-09 范围调整：本项目以 AI 接入演示为核心，当前以 [AI 优先验收计划](docs/plans/2026-10-09-ai-demo-acceptance.md) 为准。完整 OPC UA 与长期容量转为后续增强，AI 可使用明确标识的 Simulation 数据先行实现。

日期：2026-10-03。状态：第一版推荐规格。实体字段以 [DOMAIN_MODEL](docs/DOMAIN_MODEL.md) 为准；分阶段交付以 [ROADMAP](ROADMAP.md) 为准。

## 1. 范围与业务域

| 域 | 职责 | 不承担的职责 |
|---|---|---|
| 资产目录 | Plant、Location、Asset、设备点位定义 | 设备控制、完整资产财务 |
| 遥测 | 测量存储、最新值、历史趋势、数据质量 | 硬实时采样、完整工业 Historian |
| 告警 | 确定性阈值规则、告警实例、确认与恢复 | AI 生成阈值、现场安全联锁 |
| 维护 | 工单、处理备注、维修历史 | 库存、采购、人员排班 |
| 知识 | 文档上传、版本、授权、索引 | 抓取任意外部网站 |
| 身份与审计 | 登录、角色、工厂范围、操作记录 | 多租户与复杂组织同步 |
| 集成与 AI | 外部数据适配、受控辅助调查和动作提案 | 绕过业务服务直接写数据 |

## 2. 核心实体

| 实体 | 核心用途 |
|---|---|
| Plant | 权限与业务数据的工厂范围 |
| Location | 工厂内位置树，如 Utilities / Pump Room |
| Asset | 可维护设备，关联位置、点位、文档和工单 |
| MeasurementReading | 某 MeasurementPoint 一次观测，包含值、单位、质量、时间和来源映射追溯 |
| Alarm | 某条规则的一次异常持续区间，确认与恢复独立 |
| WorkOrder | 人工维护任务，记录责任人、状态和结果 |
| Document | 工厂或设备知识资料，包含不可变内容版本 |
| User | 用户身份、角色与可访问工厂集合 |

必要辅助实体：MeasurementPoint、DataSource、SourceMapping、AlarmRule、AlarmEvent、WorkOrderNote、UserPlantAccess、AuditLog；Phase 1 通过统一 IMeasurementSource 接入 Simulation Source，Phase 2 新增 OPC UA Source 与 IngestionGap；Phase 3 增加 DocumentChunk、AiRun；Phase 4 增加 ActionProposal。不要把读数、点位定义和来源映射放在一张表。

## 3. 角色与权限

全部业务请求必须登录。角色能力与工厂范围取交集，系统管理员也要显式获得工厂授权；演示可给管理员预置所有工厂权限。

| 能力 | Viewer | Operator | MaintenanceEngineer | Admin |
|---|---|---|---|---|
| 读授权工厂内资产、测量、告警 | ✓ | ✓ | ✓ | ✓ |
| 读有额外文档 ACL 的资料 | 按 ACL | 按 ACL | 按 ACL | 按 ACL |
| 确认告警 | — | ✓ | ✓ | ✓ |
| 创建/处理工单、添加维护备注 | — | — | ✓ | ✓ |
| 上传、发布/撤回文档 | — | — | ✓ | ✓ |
| 管理资产、规则、数据源、映射 | — | — | — | ✓ |
| 管理用户和工厂授权 | — | — | — | ✓ |
| AI 只读调查 | 按上述读取能力 | 同左 | 同左 | 同左 |
| 审批 AI 工单/备注动作 | — | — | ✓ | ✓ |

Phase 1 使用 ASP.NET Core Identity 本地账户与服务端会话 Cookie；只在 Development 允许演示账号种子，密码由本地配置提供。前后端同源部署，写请求校验防伪令牌；生产身份联邦留出接口。权限必须在服务端实现，隐藏按钮不能替代授权。

## 4. 必须跑通的场景

### S1：设备定位与趋势

用户选工厂和位置 → 设备列表 → 设备详情。页面展示设备静态状态、每点位最新观测及最新可信值、质量/新鲜度、最近 1 小时趋势、活动告警、工单和可访问文档。历史查询必须带时间范围；图表对 Bad、Uncertain、断线区间显示缺口，不连成正常曲线。

### S2：告警到维护闭环

内置模拟源每秒生成 P-101 振动值。持续超限达到规则要求后只生成一个活动告警；确认只记录人和时间，不表示恢复。值恢复并满足回差/恢复延迟后标记 Recovered。工单可关联该告警；工单关闭需要处理结果，不反向清除告警。所有业务修改记录审计。

### S3：文档与历史

工程师上传自编说明书并绑定设备或工厂，发布后可下载；更新生成新版本，旧版本可追溯。第一版允许 PDF / Markdown / UTF-8 TXT，最大 10 MiB；存储采用服务端生成路径，校验扩展名、内容类型与文件头。PDF 无可提取文本时标记 NeedsText，不假装已索引。

### S4：OPC UA 接入（Phase 2）

管理员配置本地模拟器数据源并审批服务端证书 → 浏览允许的 Namespace → 建立 SourceMapping → 订阅 → 测量/规则告警出现在同一业务页面。断线时显示数据陈旧与连接异常；恢复后重新校验 Namespace URI 映射，展示不能补齐的缺口。

### S5：AI 告警调查（Phase 3）

用户在告警页发起调查。系统读取授权设备上下文、最近趋势、维修工单及文档片段，返回“观察事实、可能原因、验证步骤、缺失信息、引用”。缺少证据或质量差时明确说明；无可用模型时仍可使用全部普通业务功能。

### S6：AI 工单提案（Phase 4）

AI 输出创建工单或追加工单备注提案 → 页面展示目标、内容和证据 → 有权限用户确认 → 服务端复查权限、版本、有效期 → 幂等执行 → 审计链接到提案和 AiRun。修改提案必须重新确认；拒绝或过期不产生工单。

## 5. 第一版 API 契约

所有端点以 `/api/v1` 为前缀；列表默认 50、最大 200 条，使用稳定排序和游标。校验失败 400，未登录 401，能力不足 403，范围外资源按统一策略 404，版本冲突 409；错误使用 ProblemDetails 和 correlationId。

| 方法/路径 | 行为 | 阶段 |
|---|---|---|
| GET /plants；GET /plants/{id}/locations | 授权工厂与位置树 | 1 |
| GET /assets?plantId=&locationId=；GET /assets/{id} | 设备与概览 | 1 |
| POST /plants/{id}/assets；PATCH /assets/{id} | 管理设备；PATCH 带 version | 1 |
| GET /assets/{id}/measurement-points | 点位定义、质量、新鲜度 | 1 |
| GET /assets/{id}/measurements?pointId=&from=&to=&bucket= | 原始/聚合趋势；原始窗口最多 24 小时 | 1 |
| GET /alarms?plantId=&active=；POST /alarms/{id}/acknowledge | 查询与确认；确认不改变 Active | 1 |
| POST /work-orders；GET /work-orders?assetId= | 创建与查询 | 1 |
| POST /work-orders/{id}/transition；POST /work-orders/{id}/notes | 显式状态迁移、追加备注；带 version | 1 |
| POST /documents；POST /documents/{id}/versions | 上传、生成新版本 | 1 |
| POST /documents/{id}/publish；POST /documents/{id}/withdraw | 发布/撤回，权限立即生效 | 1 |
| GET /documents/{id}/versions/{version}/download | 授权后返回文件，不能公开静态路径 | 1 |
| GET /data-sources/{id}/health；PUT /source-mappings/{id} | 集成管理；连接凭据不返回 | 2 |
| POST /alarms/{id}/investigations；GET /ai-runs/{id} | 202 异步调查、查看状态与结果 | 3 |
| POST /action-proposals/{id}/approve；POST .../reject | 明确批准/拒绝，approve 带幂等键 | 4 |

创建/编辑 Plant、Location、点位和规则遵循相同管理端点模式。P1.2 已实现 POST /plants、GET/PATCH /plants/{id}、POST /plants/{id}/locations、GET/PATCH /locations/{id}，以及上述资产目录端点；PATCH 发送完整可编辑字段及 version，isArchived=true 表示归档，禁止物理删除。登录接口为 GET /auth/csrf、POST /auth/login、POST /auth/logout、GET /auth/me；所有写请求含 X-CSRF-TOKEN。P1.3 已实现授权测量读取/趋势和 Admin 点位/来源/映射配置；P1.4 已实现 GET /alarms（plantId、active、assetId、游标）、GET /alarms/{id}、GET /alarms/{id}/events、POST /alarms/{id}/acknowledge（version）及设备点位 alarm-rules 查询/创建/PATCH。其他业务 API 继续按路线图推进。测量写入是 Worker 内部入口，不向普通用户公开。非归档告警不能物理删除。

## 6. 规模与非功能验收

以下为当前演示基准，需记录机器配置；不是生产容量承诺。此前 100 点/30 分钟/五客户端证据保留，不要求每轮重跑。

- 种子：2 个虚构工厂、每厂 3 个位置、共 20 个设备，每设备 5 个点位；10 篇自编资料、不同角色账号。
- 采集：单次演示 10 分钟，最多 100 个 Simulation 点位 × 1 Hz、60,000 条新增 raw；允许显式丢失，不允许静默丢失。运行前检查空间并设置时限/增长停止条件，见演示计划。
- 保留：原始测量默认 7 天、分钟聚合 90 天为现有配置，暂不修改。七天满量、第八天滚动及长期容量验收延期，不作为 AI/当前演示前置；开发种子仍只预填 1 小时。
- 普通列表与设备概览热查询 p95 < 500 ms；测量落库到 UI 可见 p95 < 2 s；趋势按分钟桶最多返回 1,440 点/点位。
- 两个独立会话验证主路径与权限隔离；五用户并发满量属于后续扩展。进程重启不丢失已提交业务变更；未持久化采集缓存可丢失，但必须记录缺口。
- 全部时刻以 UTC 存储，API 使用 ISO 8601 带 Z；UI 转换用户时区并显示单位。
- API / Worker 各有存活与就绪检查；AI / 单个数据源离线属于降级，不应使业务 API 就绪检查失败。
- 日志不输出密码、私钥、完整访问令牌；上传原文与完整 Prompt 默认不进入日志。
- 验收至少覆盖跨工厂越权、并发修改、数据质量、重连、重复告警、重复审批与模型失败。

## 7. 默认约束与待验证项

1. 所有演示数据原创且虚构；不用旧系统源码、真实工厂文档或真实接口。
2. 测量以数值型点位为 MVP；布尔点位作为状态采集的后续扩展，复杂数组不隐式转换。
3. 工厂内移动设备允许，跨工厂移动禁止；避免历史权限归属变化。
4. 包版本、OPC UA SDK 使用许可和模拟器部署方式在 Phase 2 spike 验证；AI Provider、模型和 embedding 维度在 Phase 3 spike 决定。
5. 第一版 AI 写权限仅覆盖工单/备注，不扩展到告警确认、阈值修改、删除或设备控制。
