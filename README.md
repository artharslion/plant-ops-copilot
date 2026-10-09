# Plant Ops Copilot

2026-10-09 新增 [IndustrialSim 单点 OPC UA 演示](docs/INDUSTRIALSIM_DEMO_VALIDATION.md)：外部框架独立运行，50秒48条真实协议读数，实际告警触发/恢复、批次去重和授权HTTP读取通过。复现：`./deploy/Run-IndustrialSimDemo.ps1 -Run <全新后缀>`。这是限时本机入口，常驻OPC UA仍禁用；下一主线继续AI调查。

2026-10-09 当前定位：**AI 接入演示项目**。P1 已完成，P2 部分完成，P3/P4 尚未实现。下一主线为模拟告警上的 AI 证据调查，再做人工审批动作；完整 OPC UA 不再作为 AI 前置。旧 G0 长期容量验收延期、未通过，不阻塞当前演示。以 [AI 优先验收计划](docs/plans/2026-10-09-ai-demo-acceptance.md) 为准；下面旧日期的进度与容量前置描述为历史记录。本次未修改运行配置或开启 OPC UA。

2026-10-06 P2 正在按门槛推进：[固定 SDK 和安全隔离实验](docs/P2_1_SPIKE.md)、[P2.2 配置/标准化增量及剩余门槛](docs/P2_2_VALIDATION.md)。114 项后端测试通过；容量与完整协议 Source/运维/健康验收未通过，OPC UA 仅能保存停用配置。

小型工业资产管理系统：先建立可信的业务与数据闭环，再接入 OPC UA，最后增加有证据、受权限约束的 AI Copilot。

## 当前状态

- 2026-10-03：独立复盘 P1.1 并完成 P1.2；运行与验证步骤见下文及 [验收记录](docs/P1_2_VALIDATION.md)。
- 已实现 Identity Cookie 登录/退出/当前用户、四角色与工厂范围交集、工厂/位置/设备管理与归档、版本并发与事务审计、Vue 工作台。
- 2026-10-05：P1.3 已完成模拟采集、不可变读数、LatestObserved/LastGood、授权趋势与 Admin 点位/来源/映射配置；见 [P1.3 验收](docs/P1_3_VALIDATION.md)。
- 2026-10-05：P1.4 已完成持续时间/回差规则、事务告警与事件、授权确认、规则配置和告警台；见 [P1.4 验收](docs/P1_4_VALIDATION.md)。
- P1.5 工单、P1.6 授权文档/SignalR/Outbox 已完成；P1.7 日分区、持久分钟统计、保留任务与规模验收见 [P1.7 验收](docs/P1_7_VALIDATION.md)。OPC UA 与 AI 留在后续阶段。
- 2026-10-05：已完成 [P1 复盘](docs/P1_REVIEW.md) 与 [P2 可执行计划](docs/plans/2026-10-05-p2-opcua.md)，P2 尚未实现。七天满量/整日保留边界容量、已发送 Outbox 历史管理为进入门槛；短期热查询通过不代表满量容量通过。
- 2026-10-06：完成 [P1 复盘整改](docs/P1_REVIEW_FIX_VALIDATION.md)：持续源通用管道、显式损失与恢复、历史采样间隔保护、可选已发送 Outbox 保留；88 项后端回归通过。七天容量与满量维护仍待验收，OPC UA/AI 尚未实现。本轮未升级业务库，运行新代码前须显式应用新增迁移。
- 默认范围：单组织、多工厂、单机开发部署。技术选择是第一版推荐，可通过架构决策更新。

## 项目定位

面向操作员、维护工程师和工厂管理员，管理工厂、位置、设备、测量、告警、工单和设备文档。演示“把 AI 接入已有工业业务系统”的完整工程过程，用于作品展示和后续客户集成验证。

业务来自通用工业资产管理经验，所有代码、名称、数据、文档、接口和界面重新设计。不得复制 KROHNE/AMADAS 的专有资产；不读取或依赖原项目仓库。公开展示统一使用 Plant Ops Copilot 名称，示例工厂和设备全部虚构。

## 目标

1. 从工厂树定位设备，查看最新值、历史趋势、活动告警、维修历史和文档。
2. 用确定性规则把可信测量转成告警，通过确认告警、创建工单、记录处理结果完成业务闭环。
3. 通过统一 `IMeasurementSource` 保留 Simulation Source，并新增 OPC UA Source；复用采集管道，保留质量码、来源时间和连接状态。
4. Investigation Copilot 针对告警联合查询测量历史、告警、维护记录和授权文档，解释证据、列出待验证假设。
5. 后期让 AI 提议有限业务动作，经人工确认后通过相同应用服务执行并审计。

## 设计原则

- **AI is not the source of truth**。数据库保存权威业务状态与观测记录，业务服务决定状态迁移和动作执行；OPC UA 数据源提供带质量/时间的现场观测，已发布文档提供可引用知识。AI 只负责检索、解释和提出建议，不能凭生成内容覆盖事实、绕过授权或直接改变业务状态。
- 业务模型以 `PlantOps` 为核心：`Asset → MeasurementPoint → MeasurementReading`，由 `SourceMapping` 连接数据源与点位。协议标识属于映射，不进入读数或告警规则。
- Simulation Source 与 OPC UA Source 是同一接口的实现；保留模拟模式用于演示、测试和离线开发，不因接入 OPC UA 而删除。

## 非目标

- 不做生产级 SCADA、DCS、PLC 控制、硬实时系统或安全仪表系统。
- 不向 OPC UA Server 写变量、调用设备方法或远程启停设备。
- 不实现多租户 SaaS、计费、库存采购、复杂排班、完整 CMMS 或预测维护模型。
- 第一版不做微服务、Kafka、Kubernetes、独立时序数据库、通用自治 Agent。
- 不声称 AI 能判定设备安全、确认根因或替代现场操作规程。

## 演进路线

| 阶段 | 交付结果 | 进入下一阶段的门槛 |
|---|---|---|
| Phase 1：小型工业资产管理 | 资产、模拟测量、规则告警、工单、文档、权限 | 无 AI、无 OPC UA 也能完成业务闭环 |
| Phase 2：OPC UA（后续增强） | 统一接口下新增 OPC UA Source，保留 Simulation Source；映射、质量、重连 | 两种源分别跑通 Golden Path，正常、断线、恢复可复现；不阻塞 AI |
| Phase 3：Investigation Copilot（下一主线） | 先用模拟源，联合测量历史、告警、维护记录与授权 TXT/Markdown，输出有证据的调查结果 | 真实 Provider 演示、证据可追溯、权限不可越过、失败可降级；无需完整 P2/G0 |
| Phase 4：AI Action Copilot | 创建工单、追加备注的动作提案 | 审批、重校验、幂等与审计验证通过 |

## 推荐技术基线

ASP.NET Core / .NET 10 LTS、EF Core、PostgreSQL；Vue 3 + TypeScript + Vite；SignalR 推送数据变化；OPC Foundation .NET Client；AI Provider 通过自有接口隔离，Phase 3 再引入 pgvector。

选择 .NET 10 LTS 的依据见 [.NET 支持政策](https://dotnet.microsoft.com/en-us/platform/support/policy)。具体 SDK 补丁、NuGet 包、Node 和容器版本在创建骨架时验证兼容性并锁定，文档不替代依赖锁文件。

## 文档导航

- [docs/CODE_STYLE.md](docs/CODE_STYLE.md)：源码风格、自动格式化、检查命令与持续检查。
- [PROJECT_SPEC.md](PROJECT_SPEC.md)：业务范围、场景、权限、API 与验收契约。
- [ARCHITECTURE.md](ARCHITECTURE.md)：模块、部署、数据流、故障处理和架构决策。
- [ROADMAP.md](ROADMAP.md)：按依赖排列的实施任务和阶段验收。
- [docs/DOMAIN_MODEL.md](docs/DOMAIN_MODEL.md)：实体、字段、关系与状态机。
- [docs/OPCUA_INTEGRATION.md](docs/OPCUA_INTEGRATION.md)：采集、映射、质量、重连与模拟器实验。
- [docs/AI_DESIGN.md](docs/AI_DESIGN.md)：RAG、工具、输出、权限、审批与安全评估。

## Golden Path：贯穿所有阶段

`North Plant → Utilities → Cooling Pump P-101 → vibration rise → alarm → acknowledge → work order → close`

虚构工厂 North Plant → Utilities → 冷却泵 P-101 → 振动值从 4.0 升至 12.6 mm/s → 规则产生高振动告警 → 操作员确认 → 维护工程师创建工单 → 记录检查结果并关闭工单。阈值仅用于演示，不能作为真实设备标准。

Phase 1 使用 Simulation Source 跑通闭环；Phase 2 新增 OPC UA Source，在独立演示配置中用 OPC UA 模拟器跑通相同路径，Simulation Source 继续可用。Phase 3 在同一告警页增加有证据的调查结果，工单仍由人创建；Phase 4 增加“确认提案后创建工单”，执行、完成与关闭仍遵循原业务流程。所有阶段保留无 AI 的手工路径，演示不要求真实工业网络或真实客户资料。

## 本地启动（PowerShell）

前置：.NET SDK 10.0.112（global.json）、Node 22.23.1、Docker Desktop Linux engine。版本由 global.json、NuGet lock、web/package-lock.json 和数据库镜像固定；仓库许可证见 [LICENSE](LICENSE)。

在仓库根目录，首次创建本地数据库凭据（已有 deploy/.env 时保留原值）：

```powershell
if (-not (Test-Path -LiteralPath deploy/.env)) {
    $devPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    "POSTGRES_PASSWORD=$devPassword" | Set-Content deploy/.env
    Remove-Variable devPassword
}
if (-not (Test-Path -LiteralPath appsettings.Local.json)) {
    $demoPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + 'a!'
    @{ Demo = @{ Password = $demoPassword } } | ConvertTo-Json | Set-Content appsettings.Local.json
    Remove-Variable demoPassword
}
docker compose -f deploy/compose.yaml up -d --wait
dotnet restore PlantOps.sln --locked-mode
dotnet tool restore
. ./deploy/Use-LocalDatabase.ps1
dotnet run --project src/PlantOps.Worker -- --initialize-db
```

初始化命令仅允许 Development（入口与初始化器均保护；本地 launch profile 已设置），显式迁移并创建 2 工厂、每厂 3 位置、20 设备、100 模拟点位、10 份原创虚构文档和首次一小时历史。原始 P-101/F-201 标识保持稳定。`Demo:SeedHistory=false` 可跳过历史；历史只在首次写入，不补造停机期间观测。初始化器读取 `Demo:Password`，缺失或不满足 12 位及大小写/数字/符号要求时拒绝执行；重复初始化保留已有编辑、密码、角色、授权撤销与禁用状态。正常启动不自动迁移。迁移前停止服务并备份数据库与上传目录；测量分区迁移的降级须恢复备份。

`Use-LocalDatabase.ps1` 会加载被忽略的根目录 `appsettings.Local.json` 到 `Demo__Password`。也可以直接设置此环境变量，或使用 Api/Worker 共用的 .NET user-secrets（UserSecretsId = plantops-development）。不要在源码中添加密码。

| 本地演示账号 | 角色 | 工厂范围 | 当前能力 |
|---|---|---|---|
| viewer | Viewer | North | 浏览 |
| operator | Operator | North | 浏览、确认告警 |
| engineer | MaintenanceEngineer | South | 浏览、确认、工单、授权文档 |
| admin | Admin | North、South | 目录/采集/规则配置、确认、工单、授权文档 |

首次账号密码取本机 `appsettings.Local.json` 的 `Demo.Password`；已有账号不会因重新生成配置而重置密码。请在本地编辑器读取配置，不把密码粘贴到日志或版本库。

另开三个终端，工作目录均为仓库根目录：

```powershell
# 终端 1：API
. ./deploy/Use-LocalDatabase.ps1
dotnet run --project src/PlantOps.Api

# 终端 2：Worker
. ./deploy/Use-LocalDatabase.ps1
dotnet run --project src/PlantOps.Worker

# 终端 3：Web
cd web
npm ci
npm run dev
```

访问 [本地工作台](http://127.0.0.1:5173)，API 在 5080，Worker 在 5081。Vite 代理 `/api`、`/health`、`/openapi`、`/hubs`（含 WebSocket）到本机 API，不需要放开跨域。前端构建产物本轮尚未接入生产托管；`npm run preview` 仅检查静态产物，不能替代带 API 代理的开发服务。

演示：进入资产目录 → 登录 admin → North Plant → Utilities → Cooling Pump P-101 → 编辑设备 → 保存；版本递增且记录审计。换 viewer 验证只读与 North 范围；换 engineer 验证只见 South。两个页面同时编辑同一设备，后保存的旧版本收到 409，需关闭编辑并刷新后重新检查。

工厂、位置与设备都由同一工作台管理。归档通过编辑表单的 `isArchived` 提交，保留目录与审计，不物理删除、不级联删除；本阶段不提供取消归档。工厂归档前须处理所有未归档设备；位置归档前须处理未归档子位置/设备。创建工厂会在同事务给创建者写入显式 UserPlantAccess，Admin 对其余工厂仍必须有授权。

## 健康检查与验证

### P1.3 模拟测量演示

更新代码后，先停止本项目 API/Worker，再加载本地配置、执行 `dotnet run --project src/PlantOps.Worker -- --initialize-db`，随后按上文重新启动三个服务。初始化只允许 Development，新增迁移与种子，不重置账号密码、角色授权或已有资产/点位/来源/映射修改。P1.3 验收没有替你更新业务库；在自己的开发库演示时需要执行此步骤。

进入 North Plant → Utilities → P-101。测量面板展示 `VIBRATION_RMS`（mm/s）、最新观测与最后可信值、接收时间、当前映射及趋势。Worker 每秒采集一次；只启动 API 不产生读数。Simulation 的 UTC Unix 秒模 120：0–29 秒 Good/4.0，30–59 秒 Good/12.6，60–69 秒 Bad/null，70–79 秒 Uncertain/6.0，80–119 秒 Good/6.0；重启不会重置场景。12.6 仅是模拟高值；P1.4 新增下述演示告警规则。

趋势可选 15 分钟、1 小时或 24 小时，实时通知触发补查、每 5 秒轮询兜底，可暂停或手动刷新。原始曲线只连接 Good、有限数值且无时间异常的观测；Bad/Uncertain 和超过期望间隔 1.5 倍的缺口断线。分钟统计由 Worker 持久化，仅统计有效 Good，显示质量数量/覆盖率/缺口；使用完整 UTC 分钟，含缺口的分钟不连线。明细展示最近 100 条。原始查询上限 100,000 行、分钟直接读投影、最多 1,440 桶、窗口不超过 24h；超限返回 400，选择更小窗口或分钟统计。

Admin 可点击“配置测量”创建/编辑/停用点位、同厂 Simulation 来源与映射，写入检查 version、防伪并记录事务审计。模拟映射目前仅支持 Vibration/mm/s/Double；比例和偏移执行 `raw × scale + offset`。同点位最多一个启用映射；改来源/来源键/换算需先停用并保存，再编辑、保存并启用。已有历史（含保留的分钟/快照）或启用映射后不能改点位量纲、单位或期望间隔，应创建新点位。归档设备自动停用点位及映射、保留读数与审计；工厂归档前还须停用数据源。重新启用或修改来源会使旧配置批次失效。

停止 Worker，超过点位陈旧阈值（种子 5 秒）后刷新：最新观测及 LastGood 均标记陈旧，不显示为当前可信。重启后恢复新观测，停机区间保持可能缺口；不补造历史。设置环境变量 `Telemetry__SimulationEnabled=false` 只关闭模拟源。当前使用单 Worker、每源有界缓冲和 500 条/500 ms 双触发提交，有限重试；过载和失败登记显式缺口并影响趋势、分钟和规则计时。损失摘要在内存中保留，崩溃尾部数量未知。

已发送通知的保留清理默认关闭；需要时显式设置 `Outbox__RetentionEnabled=true`，默认保留 24 小时、每 5 秒最多清理 1,000 条，未发送通知与审计保留。部署前应确定历史期限和备份；配置及证据见 [整改验收](docs/P1_REVIEW_FIX_VALIDATION.md)。只读容量快照可运行 `./deploy/Measure-Capacity.ps1 -Schema p17_ui_20261005`，仅查询测试库；输出不代表七天容量通过。

### P1.4 告警演示

按上文 Development 初始化升级迁移与种子后启动 API、Worker 和前端。种子保留已有规则修改。Operator 登录，进入 North → Utilities → P-101 → 查看此设备的告警，或直接进入告警台。高值严格大于 10 mm/s 连续 3 秒触发一次；低值严格小于 8 连续 5 秒恢复，最大样本间隔 2.5 秒。确认仅记录人和时间，Active/Recovered 与已确认各自独立。Bad/Uncertain/陈旧观测显示当前状态无法验证，不能清除活动告警。等待模拟周期可观察完整过程，不提供人工强制恢复。

Viewer 可读；Operator、MaintenanceEngineer、Admin 可确认；有该厂授权的 Admin 可新增/编辑/停用规则。首次确认带 version，冲突 409；已确认重试返回首次确认记录。有活动告警时规则修改、停用及设备归档均拒绝。规则阈值快照、事件和历史保留。页面由实时通知触发补查，每 5 秒轮询兜底，失败隐藏未验证内容；配置期间暂停自动刷新。规模计数仅指已加载记录。SignalR/Outbox 已在 P1.6 实现。

P1.4 当时验收：后端构建零警告/零错误，51 项测试全部通过（27 项真实 PostgreSQL 集成测试，无跳过）；锁定恢复及迁移模型检查通过。前端类型检查与构建通过。浏览器实际检查登录/退出、角色范围、目录编辑与归档、双页面 409 和离线失败提示；详见 [P1.2 验收](docs/P1_2_VALIDATION.md) 与 [P1.3 验收](docs/P1_3_VALIDATION.md)。测试只用独立数据库，未修改业务库。

```powershell
Invoke-WebRequest http://127.0.0.1:5080/health/live
Invoke-WebRequest http://127.0.0.1:5080/health/ready
Invoke-WebRequest http://127.0.0.1:5081/health/live
Invoke-WebRequest http://127.0.0.1:5081/health/ready
Invoke-RestMethod http://127.0.0.1:5080/openapi/v1.json
dotnet build PlantOps.sln --no-restore
dotnet ef migrations has-pending-model-changes --project src/PlantOps.Infrastructure
cd web
npm run build
```

`live` 仅判断进程能响应；`ready` 需数据库连接可用且没有待应用迁移，否则 503。健康检查在 Identity 之前执行，带 Cookie、数据库离线时也保持 live=200/ready=503。OpenAPI 和 `/api/v1/system` 只在 Development 开放，system 不返回连接串/凭据。全部目录端点必须登录，写请求携带 `X-CSRF-TOKEN`；包括登录/退出也校验防伪。Cookie 为 HttpOnly/SameSite=Strict；Development 允许本机 HTTP，非 Development 强制 Secure（需同源 HTTPS 部署）。

数据库集成测试使用独立测试库，不可指向业务/生产库：

```powershell
# 仓库根目录；仅首次创建测试库
docker compose -f deploy/compose.yaml exec -T database psql -U plantops -d postgres -c "CREATE DATABASE plantops_tests;"
. ./deploy/Use-LocalDatabase.ps1
$env:PLANTOPS_TEST_CONNECTION = $env:ConnectionStrings__PlantOps.Replace('Database=plantops;', 'Database=plantops_tests;')
dotnet test PlantOps.sln
Remove-Item Env:PLANTOPS_TEST_CONNECTION
```

未设置测试连接时，数据库集成测试会明确跳过；纯测试无需数据库。当前数量见最新验收记录。连接数据库名必须是 `plantops_tests`，其他数据库会被拒绝。测试使用随机临时账号/资源和随机内存密码；测试数据保留，不删除数据库。Windows 下构建/安装依赖前先停止本仓库 Api/Worker/Vite，避免占用输出文件。

API 在 Development 启动后，前端可通过 `npm run generate:api` 从 OpenAPI 更新已提交的类型文件；正常 `npm ci` / `npm run build` 不要求 API 在线。

## 本地配置与关闭

deploy/.env、appsettings.Local.json 和凭据不会提交。每个终端需要加载本地连接配置；Use-LocalDatabase.ps1 仅支持字母/数字/下划线/连字符的数据库密码，避免连接串转义歧义。首次生成的数据库密码必须与 volume 内账户一致；修改 .env 不会修改已有数据库密码。

当前限制：没有用户/授权管理界面、密码重置、生产静态托管或审计查询界面；角色/授权通过种子与 Identity 数据管理。本阶段每次请求重新校验账号及真实角色/范围，禁用、撤权对已有 Cookie 生效。实时连接也持续复查账号、角色、范围和 stamp，变更后断开。生产部署还需配置 HTTPS、密钥持久化与实际用户生命周期，不属于本地 P1.2 验收。

终端 Ctrl+C 关闭 Api/Worker/Web；`docker compose -f deploy/compose.yaml stop` 停止数据库并保留数据。密钥、客户端私钥、数据库备份和上传文件不得进入版本库。

P1.5：设备详情下方可创建/处理维护工单并查看维修历史。维护写操作使用 engineer（South）或 admin（North/South）。草稿→待执行→执行中→已完成（必填结果）→已关闭；取消/返工必填原因。验收见 docs/P1_5_VALIDATION.md。

P1.6：知识资料入口上传工厂资料，设备详情上传设备资料。上传为草稿，发布后授权下载；修改读取角色或撤回立即生效。生产配置 Documents:StorageRoot（持久受控目录，须与数据库一起备份）；Development 默认同用户 LocalApplicationData/PlantOpsCopilot/documents（API/Worker 共用）。API 提供 /hubs/plant-events，只读通知、自动重连与 HTTP 补查。前端去重验证：npm test --prefix web。验收见 docs/P1_6_VALIDATION.md。

### P1 完整演示与保留配置

登录 admin → North → Utilities → P-101。等模拟高值产生告警，点击“查看此设备的告警”，选择记录并“确认告警”。点击“查看设备并创建关联工单”，关联告警自动带入。填写标题、说明与责任人，保存草稿 → 待执行 → 执行中 → 追加备注 → 填写处理结果 → 已完成 → 已关闭。维修历史显示终态和处理结果；关闭工单不代替告警恢复。engineer 在 South 可完成同一维护流程；operator 可以确认但不能创建工单，viewer 只能读取。

设备资料中选择原创指南并下载，或上传 TXT/MD/PDF → 保存草稿 → 发布 → 下载；选择读取角色可限制访问。PDF 本阶段仅验证头尾并保存，不解析或建立索引。无需配置模型或 OPC UA 服务。

Worker 在启动和每分钟聚合已完成 UTC 分钟，当前分钟不提供统计；有未处理迟到数据的桶明确显示缺口。默认 `Telemetry:RawRetentionDays=7`、`Telemetry:MinuteRetentionDays=90`，可通过对应双下划线环境变量配置。每天 UTC 日期变更执行保留任务；先核对 dirty、分区修订和聚合样本数量，再删过期日分区。失败保留原始数据；原始过期后最新投影、告警快照和工单仍保留。过期迟到样本被拒绝，不能重写已保留统计。保留按整日分区，边界日最多额外保留一天。

复现五客户端 30 分钟负载（先初始化独立测试库并启动 API/Worker；结果目录被忽略，不含密码）：

```powershell
. ./deploy/Use-LocalDatabase.ps1
node web/scripts/benchmark-p1.mjs
```

实际验收只迁移 `plantops_tests`，业务库 `plantops` 保持原状。若复现完整规模，初始化、API、Worker 的连接都须改为测试库，并使用同一独立 Search Path 和文档目录；不要将测试连接设置步骤当作升级业务库授权。30 分钟测量结果、丢失检查、数据库恢复、存储实测与容量边界见 [P1.7 验收](docs/P1_7_VALIDATION.md)。
