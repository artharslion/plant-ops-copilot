# 本地开发指南

[返回项目首页](../README.md) · [文档导航](README.md)

本文集中说明本地启动、模拟演示和验证方法。所有命令从仓库根目录执行，除非步骤明确要求切换目录。基础业务与模拟采集无需模型密钥或 OPC UA 服务。

如果希望无需宿主机 .NET/Node 即启动完整系统，请使用 [Docker 指南](DOCKER.md)。下文介绍源码开发方式，使用原有 `deploy/compose.yaml` 作为本地数据库。

## 本地启动（PowerShell）

前置：.NET SDK 10.0.112（global.json）、Node 22.23.1、Docker Desktop Linux engine。版本由 global.json、NuGet lock、web/package-lock.json 和数据库镜像固定；仓库许可证见 [LICENSE](../LICENSE)。

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

访问 [本地工作台](http://127.0.0.1:5173)，API 在 5080，Worker 在 5081。Vite 代理 `/api`、`/health`、`/openapi`、`/hubs`（含 WebSocket）到本机 API，不需要放开跨域。容器方案通过 Nginx 托管构建产物；源码开发模式下，`npm run preview` 仅检查静态产物，不能替代带 API 代理的开发服务。

演示：进入资产目录 → 登录 admin → North Plant → Utilities → Cooling Pump P-101 → 编辑设备 → 保存；版本递增且记录审计。换 viewer 验证只读与 North 范围；换 engineer 验证只见 South。两个页面同时编辑同一设备，后保存的旧版本收到 409，需关闭编辑并刷新后重新检查。

工厂、位置与设备都由同一工作台管理。归档通过编辑表单的 `isArchived` 提交，保留目录与审计，不物理删除、不级联删除；目前不提供取消归档。工厂归档前须处理所有未归档设备；位置归档前须处理未归档子位置/设备。创建工厂会在同事务给创建者写入显式 UserPlantAccess，Admin 对其余工厂仍必须有授权。

## 健康检查与验证

### 模拟测量演示

更新代码后，先停止本项目 API/Worker，再加载本地配置、执行 `dotnet run --project src/PlantOps.Worker -- --initialize-db`，随后按上文重新启动三个服务。初始化只允许 Development，新增迁移与种子，不重置账号密码、角色授权或已有资产/点位/来源/映射修改。更新代码不会自动升级开发库；请显式执行初始化步骤。

进入 North Plant → Utilities → P-101。测量面板展示 `VIBRATION_RMS`（mm/s）、最新观测与最后可信值、接收时间、当前映射及趋势。Worker 每秒采集一次；只启动 API 不产生读数。Simulation 的 UTC Unix 秒模 120：0–29 秒 Good/4.0，30–59 秒 Good/12.6，60–69 秒 Bad/null，70–79 秒 Uncertain/6.0，80–119 秒 Good/6.0；重启不会重置场景。12.6 仅是模拟高值；演示告警规则见下节。

趋势可选 15 分钟、1 小时或 24 小时，实时通知触发补查、每 5 秒轮询兜底，可暂停或手动刷新。原始曲线只连接 Good、有限数值且无时间异常的观测；Bad/Uncertain 和超过期望间隔 1.5 倍的缺口断线。分钟统计由 Worker 持久化，仅统计有效 Good，显示质量数量/覆盖率/缺口；使用完整 UTC 分钟，含缺口的分钟不连线。明细展示最近 100 条。原始查询上限 100,000 行、分钟直接读投影、最多 1,440 桶、窗口不超过 24h；超限返回 400，选择更小窗口或分钟统计。

Admin 可点击“配置测量”创建/编辑/停用点位、同厂 Simulation 来源与映射，写入检查 version、防伪并记录事务审计。模拟映射目前仅支持 Vibration/mm/s/Double；比例和偏移执行 `raw × scale + offset`。同点位最多一个启用映射；改来源/来源键/换算需先停用并保存，再编辑、保存并启用。已有历史（含保留的分钟/快照）或启用映射后不能改点位量纲、单位或期望间隔，应创建新点位。归档设备自动停用点位及映射、保留读数与审计；工厂归档前还须停用数据源。重新启用或修改来源会使旧配置批次失效。

停止 Worker，超过点位陈旧阈值（种子 5 秒）后刷新：最新观测及 LastGood 均标记陈旧，不显示为当前可信。重启后恢复新观测，停机区间保持可能缺口；不补造历史。设置环境变量 `Telemetry__SimulationEnabled=false` 只关闭模拟源。当前使用单 Worker、每源有界缓冲和 500 条/500 ms 双触发提交，有限重试；过载和失败登记显式缺口并影响趋势、分钟和规则计时。损失摘要在内存中保留，崩溃尾部数量未知。

已发送通知的保留清理默认关闭；需要时显式设置 `Outbox__RetentionEnabled=true`，默认保留 24 小时、每 5 秒最多清理 1,000 条，未发送通知与审计保留。部署前应确定历史期限和备份；历史整改证据可从 Git 历史查阅。只读容量快照可运行 `./deploy/Measure-Capacity.ps1 -Schema p17_ui_20261005`，仅查询测试库；输出不代表七天容量通过。

### 告警演示

按上文 Development 初始化升级迁移与种子后启动 API、Worker 和前端。种子保留已有规则修改。Operator 登录，进入 North → Utilities → P-101 → 查看此设备的告警，或直接进入告警台。高值严格大于 10 mm/s 连续 3 秒触发一次；低值严格小于 8 连续 5 秒恢复，最大样本间隔 2.5 秒。确认仅记录人和时间，Active/Recovered 与已确认各自独立。Bad/Uncertain/陈旧观测显示当前状态无法验证，不能清除活动告警。等待模拟周期可观察完整过程，不提供人工强制恢复。

Viewer 可读；Operator、MaintenanceEngineer、Admin 可确认；有该厂授权的 Admin 可新增/编辑/停用规则。首次确认带 version，冲突 409；已确认重试返回首次确认记录。有活动告警时规则修改、停用及设备归档均拒绝。规则阈值快照、事件和历史保留。页面由实时通知触发补查，每 5 秒轮询兜底，失败隐藏未验证内容；配置期间暂停自动刷新。规模计数仅指已加载记录。页面变更通过 SignalR/Outbox 推送。

### 服务健康与构建

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

### 数据库集成测试

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

当前限制：没有用户/授权管理界面、密码重置或审计查询界面；角色/授权通过种子与 Identity 数据管理。服务端每次请求重新校验账号及真实角色/范围，禁用、撤权对已有 Cookie 生效。实时连接也持续复查账号、角色、范围和 stamp，变更后断开。生产部署还需配置 HTTPS、密钥持久化与实际用户生命周期，超出本地开发指南的范围。

终端 Ctrl+C 关闭 Api/Worker/Web；`docker compose -f deploy/compose.yaml stop` 停止数据库并保留数据。密钥、客户端私钥、数据库备份和上传文件不得进入版本库。

维护工单：设备详情的“维护工单 / 维修历史”标签提供列表，创建及处理进入独立页面。维护写操作使用 engineer（South）或 admin（North/South）。草稿→待执行→执行中→已完成（必填结果）→已关闭；取消/返工必填原因。

知识资料：知识资料入口上传工厂资料，设备详情上传设备资料。上传为草稿，发布后授权下载；修改读取角色或撤回立即生效。生产配置 Documents:StorageRoot（持久受控目录，须与数据库一起备份）；Development 默认同用户 LocalApplicationData/PlantOpsCopilot/documents（API/Worker 共用）。API 提供 /hubs/plant-events，只读通知、自动重连与 HTTP 补查。前端去重验证：npm test --prefix web。

### 完整业务演示与保留配置

登录 admin → North → Utilities → P-101。等模拟高值产生告警，点击“查看此设备的告警”，选择记录并“确认告警”。点击“查看设备并创建关联工单”，关联告警自动带入。填写标题、说明与责任人，默认“创建并打开 → 开始执行 → 填写处理结果 → 完成并关闭”。步骤条显示“创建任务 → 执行维护 → 提交结果”，维护备注为可选；也可仅保存草稿，或仅提交完成、交由他人复核关闭。取消及返工在“更多操作”中，需填写原因。维修历史显示终态和处理结果；关闭工单不代替告警恢复。engineer 在 South 可完成同一维护流程；operator 可以确认但不能创建工单，viewer 只能读取。

设备资料中选择原创指南并下载，或上传 TXT/MD/PDF → 保存草稿 → 发布 → 下载；选择读取角色可限制访问。PDF 目前仅验证头尾并保存，不解析或建立索引。无需配置模型或 OPC UA 服务。

Worker 在启动和每分钟聚合已完成 UTC 分钟，当前分钟不提供统计；有未处理迟到数据的桶明确显示缺口。默认 `Telemetry:RawRetentionDays=7`、`Telemetry:MinuteRetentionDays=90`，可通过对应双下划线环境变量配置。每天 UTC 日期变更执行保留任务；先核对 dirty、分区修订和聚合样本数量，再删过期日分区。失败保留原始数据；原始过期后最新投影、告警快照和工单仍保留。过期迟到样本被拒绝，不能重写已保留统计。保留按整日分区，边界日最多额外保留一天。

复现五客户端 30 分钟负载（先初始化独立测试库并启动 API/Worker；结果目录被忽略，不含密码）：

```powershell
. ./deploy/Use-LocalDatabase.ps1
node web/scripts/benchmark-p1.mjs
```

完整规模复现须将初始化、API、Worker 的连接统一指向独立测试库，并使用同一独立 Search Path 和文档目录。早期测量、恢复与存储实测保存在 Git 历史中，不代表长期容量已验收；剩余门槛见 [开发计划](../ROADMAP.md)。

## 可选 AI 调查

不加载 AI 配置时默认使用 Fake Provider，不访问外网；模拟结果不能视为真实模型回答。真实模型配置、密钥加载和探针见 [AI 接入配置](AI_PROVIDER_SETUP.md)。启动 API 与 Worker 前，分别在各自终端加载 `deploy/Use-LocalAi.ps1`。告警调查与证据展开的操作和验证见 [调查建议与证据契约](INVESTIGATION_RESPONSE_V3.md)。

P8.1 新运行的实际版本指纹及用量来源保存在 `AiRuns.RunRecordJson`，调查 API 返回 `runRecord`，详见 [最小运行记录](P8_1_RUN_RECORD_VALIDATION.md)。旧记录为 null，旧 `PromptVersion` 和 Token 字段不能证明当时实际请求或计费用量。`InputTokens`/`OutputTokens` 继续是兼容预算核算值，可能混合已报告用量与未知预留；总实耗未知时新记录明确返回 null。迁移需显式执行，源码更新不代表业务 Docker 已升级。

## 项目约定

默认范围为单组织、多工厂、单机开发部署。业务来自通用工业资产管理经验，代码、名称、数据、文档、接口和界面独立设计。不得复制 KROHNE/AMADAS 的专有资产，不读取或依赖原项目仓库。公开展示统一使用 Plant Ops Copilot 名称，示例工厂和设备全部虚构。


## Element Plus 工作台维护

首页 `/` 是运维工作台。`GET /api/v1/plants/{plantId}/overview` 复用工厂读取授权，在同一数据库快照中返回完整统计与有界列表（默认 5 条，可展开至 50 条）。活动告警与其中未确认的告警分别统计；未关闭工单包含 Draft/Open/InProgress/Completed，`mine=true` 按当前负责人筛选，`workOrderStatus` 筛选状态，均不改变摘要总数。数据异常只统计未归档工厂、设备下的启用测点，区分采集未连接、无观测、数据陈旧与不可信观测；陈旧阈值沿用各测点配置。页面监听工厂事件并每 15 秒补查，失败时清空业务快照，显示不可用而非 0。未登录只显示登录入口。

首页告警入口携带 `plantId`/`alarmId` 打开具体告警及调查区域；异常测点入口携带 `assetId`/`tab=measurements`/`pointId` 定位具体测点。工单沿用已有独立详情路由。系统连接检查收在页脚，业务自动刷新不等于连接检查结果的自动更新。

前端固定使用 Element Plus 2.14.7，版本与 `web/package-lock.json` 一起更新；`main.ts` 仅注册已使用的组件，`App.vue` 统一提供简体中文区域设置。品牌绿色、间距、表格密度与响应式布局在 `style.css` 中维护，避免重新引入自制输入/选择/步骤条。

- 资产入口：`/assets?assetId=<id>`，可附带 `alarmId` 自动选中工单分区；`tab=orders` 用于从工单返回。位置树筛选设备，表格中设备代码按钮也支持键盘打开。
- 工单入口：`/assets/<assetId>/work-orders/new` 或 `/assets/<assetId>/work-orders/<orderId>`；关联告警保存在 `alarmId` 查询参数。保存后替换为真实工单地址；“返回设备”重新打开该设备的工单标签。原有 `/assets` 静态目录冲突修复继续保留，独立工单路由可直接刷新。
- 设备详情采用一个宽 Drawer 与五个 Tabs；测量离开即卸载，停止定时器和事件订阅。工单详情后台只比较版本，保留处理结果、备注、选中项及滚动；远端更新后要求重新核对。
- 知识资料页的资料详情采用 Drawer；在已经打开的设备 Drawer 中，资料详情使用 Dialog，避免连续叠加 Drawer。目录编辑、资料上传/元数据编辑、规则/采集配置及调查引用使用 Dialog。
- `SidePanel` 统一封装 Drawer/Dialog 的关闭流程，保存中阻止关闭，未保存内容显示确认。`useLeaveProtection` 保护路由离开/参数切换及刷新；刷新/关页提示文字由浏览器决定。创建工单和目录、资料关键字段使用 Form 校验；服务端仍是权限、版本与输入规则的最终校验方。
- 三步操作不改变后端状态机：默认创建并打开、完成并关闭；显式草稿、仅提交完成、取消、返工、维护备注仍可使用。P4 已实现调查后的工单/备注提案、完整内容确认与幂等执行，见 [创建工单](P4_WORK_ORDER_VALIDATION.md)及[备注验证](P4_NOTE_VALIDATION.md)；完整浏览器审批 E2E 仍待验收。后续能力与维护边界以 [开发计划](../ROADMAP.md) 为准。

验证命令见 [源码规范](CODE_STYLE.md)，本轮证据见 [交互验证记录](UI_INTERACTION_VALIDATION.md)。纯 UI 更新使用下面两条命令，不执行会迁移数据库的初始化脚本：

```powershell
docker compose --env-file deploy/.env.docker build web
docker compose --env-file deploy/.env.docker up -d --no-deps web
```
