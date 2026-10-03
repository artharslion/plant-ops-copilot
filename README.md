# Plant Ops Copilot

小型工业资产管理系统：先建立可信的业务与数据闭环，再接入 OPC UA，最后增加有证据、受权限约束的 AI Copilot。

## 当前状态

- 2026-10-03：P1.1 基础骨架已完成并在本机验证；运行与验证步骤见下文。
- 当前只有基础环境和最小设备种子；登录/权限、资产 CRUD、测量、告警、工单、OPC UA 和 AI 尚未实现。
- 设计文档中的业务 API、性能数字与示例仍是后续实现契约或验收目标；本轮验证范围仅覆盖 P1.1。
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
| Phase 2：OPC UA | 统一接口下新增 OPC UA Source，保留 Simulation Source；映射、质量、重连 | 两种源分别跑通 Golden Path，正常、断线、恢复可复现 |
| Phase 3：Investigation Copilot | 联合测量历史、告警、维护记录与文档，输出有证据的调查结果 | 证据可追溯、权限不可越过、失败可降级 |
| Phase 4：AI Action Copilot | 创建工单、追加备注的动作提案 | 审批、重校验、幂等与审计验证通过 |

## 推荐技术基线

ASP.NET Core / .NET 10 LTS、EF Core、PostgreSQL；Vue 3 + TypeScript + Vite；SignalR 推送数据变化；OPC Foundation .NET Client；AI Provider 通过自有接口隔离，Phase 3 再引入 pgvector。

选择 .NET 10 LTS 的依据见 [.NET 支持政策](https://dotnet.microsoft.com/en-us/platform/support/policy)。具体 SDK 补丁、NuGet 包、Node 和容器版本在创建骨架时验证兼容性并锁定，文档不替代依赖锁文件。

## 文档导航

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
docker compose -f deploy/compose.yaml up -d --wait
dotnet restore PlantOps.sln --locked-mode
dotnet tool restore
. ./deploy/Use-LocalDatabase.ps1
dotnet run --project src/PlantOps.Worker -- --initialize-db
```

初始化命令仅允许 Development（本地 launch profile 已设置），显式执行迁移并写入固定 ID 的虚构 North Plant / Utilities / P-101；重复执行保留已有种子和人工修改。不在 API/Worker 正常启动时自动迁移，也不生成用户密码或真实客户数据。

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

访问 [本地工作台](http://127.0.0.1:5173)，API 在 5080，Worker 在 5081。Vite 代理 `/api`、`/health`、`/openapi` 到本机 API，不需要放开跨域。前端构建产物本轮尚未接入生产托管；`npm run preview` 仅检查静态产物，不能替代带 API 代理的开发服务。

## 健康检查与验证

本机验收：后端构建零警告/零错误，8 项测试通过（含独立 PostgreSQL 测试库）；前端类型检查与生产构建通过，npm audit 为 0。数据库两次初始化后 Plant/Location/Asset 各 1 条。Api/Worker 正常时 live/ready 均 200；停止本项目数据库后 live 仍 200、ready 为 503，重启数据库后恢复。页面已检查实际连接状态与刷新行为。

```powershell
Invoke-WebRequest http://127.0.0.1:5080/health/live
Invoke-WebRequest http://127.0.0.1:5080/health/ready
Invoke-WebRequest http://127.0.0.1:5081/health/live
Invoke-WebRequest http://127.0.0.1:5081/health/ready
Invoke-RestMethod http://127.0.0.1:5080/openapi/v1.json
dotnet build PlantOps.sln --no-restore
dotnet test PlantOps.sln --no-build
cd web
npm run build
```

`live` 仅判断进程能响应；`ready` 需数据库连接可用且没有待应用迁移，否则 503。OpenAPI 和 `/api/v1/system` 只在 Development 开放；当前没有公开的资产/工厂读取端点，业务 API 将在 P1.2 授权完成后提供。

数据库集成测试使用独立测试库，不可指向业务/生产库：

```powershell
# 仓库根目录；仅首次创建测试库
docker compose -f deploy/compose.yaml exec -T database psql -U plantops -d postgres -c "CREATE DATABASE plantops_tests;"
. ./deploy/Use-LocalDatabase.ps1
$env:PLANTOPS_TEST_CONNECTION = $env:ConnectionStrings__PlantOps.Replace('Database=plantops;', 'Database=plantops_tests;')
dotnet test PlantOps.sln
Remove-Item Env:PLANTOPS_TEST_CONNECTION
```

未设置测试连接时，两项数据库集成测试会明确跳过；其余 API 健康/开发端点测试无需数据库。测试库的种子会保留，不自动删除数据库。

## 本地配置与关闭

deploy/.env 和凭据不会提交。每个终端需要加载本地连接配置；Use-LocalDatabase.ps1 仅支持字母/数字/下划线/连字符的本地密码，避免连接串转义歧义。首次生成的密码必须与数据库 volume 内账户一致；修改 .env 不会修改已有数据库密码。

终端 Ctrl+C 关闭 Api/Worker/Web；`docker compose -f deploy/compose.yaml stop` 停止数据库并保留数据。密钥、客户端私钥、数据库备份和上传文件不得进入版本库。
