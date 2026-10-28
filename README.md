# Plant Ops Copilot

面向工业资产运维的 AI Copilot 演示项目。围绕设备、测量、告警和工单建立业务闭环，再由 AI 联合查询授权证据，辅助人员调查异常。

项目使用虚构工厂、设备与资料，支持单组织、多工厂和角色权限隔离。基础业务可在无需模型服务或工业网络的本地环境中运行。

## 功能概览

- **运维工作台**：按授权工厂汇总活动/待确认告警、未关闭工单与数据异常测点；支持我的待办、工单状态筛选及详情跳转。
- **资产管理**：工厂、位置、设备目录与归档，版本并发检查和事务审计。
- **测量与趋势**：可重复的模拟采集、最新观测与最后可信值、质量码、陈旧与缺口提示、原始趋势和分钟统计。
- **告警与维护**：持续时间和回差规则、告警确认、关联工单、处理记录与维修历史。
- **知识资料**：文档上传、版本、发布与读取权限，支持 TXT/Markdown 证据检索及引用展开。
- **AI 告警调查**：联合测量、告警、工单和授权资料，展示事实、待验证假设、检查建议与引用；支持取消、预算限制和失败提示。
- **实时更新**：SignalR 与 Outbox 通知，断线后通过 HTTP 补查。

## 实现与验证范围

| 模块 | 当前范围 |
|---|---|
| 资产运维业务 | 已实现资产、模拟测量、告警、工单、文档与权限闭环 |
| AI 只读调查 | 已接通告警页、后台编排与授权证据；固定场景确定性测试和有限真实模型样本已有验证，完整真实质量、长期稳定性与成本仍待验收 |
| OPC UA | 有限协议演示与受控本机多点常驻首增量已实现，支持重连及健康展示；默认关闭，仅显式批准的 Development 回环配置可启用，现有 Compose 未开启，工业网络与完整运维尚未验收 |
| AI 动作审批 | 已支持创建工单和向已有工单追加调查备注：编辑、完整确认、事务内重校验与幂等审计；完整 P4 验收仍待完成 |

AI 负责解释证据与提出建议，业务状态由数据库和应用服务管理；调查工具仅提供只读能力。人员通过现有业务流程确认告警、创建和处理工单。

在告警详情完成调查后，Admin / MaintenanceEngineer 可在调查结果下点击“整理工单草稿”，编辑后“保存并核对完整内容”，再“确认创建工单”。此流程不新增模型调用，创建关联当前设备和告警的 Draft 工单；提案15分钟到期，资料、权限或目标版本变化会阻止执行。保存、拒绝或未确认均不会创建工单；执行结果不明时先查询原提案。详见 [首个 P4 增量验证](docs/P4_WORK_ORDER_VALIDATION.md)。

已有维护任务时，可选“选择已有工单追加备注”→选择同一设备下未关闭/取消的工单→“整理备注提案”→编辑并保存→核对目标、版本和完整内容→“确认追加备注”。目标工单发生变化时需重新整理；成功后从回执打开原工单抽屉，保留告警调查上下文。详见 [追加备注验证](docs/P4_NOTE_VALIDATION.md)。

本项目用于工程演示与集成验证。生产级控制、设备写操作和安全决策不在范围内；长期容量与生产部署仍需独立验证。阶段任务见 [实施路线](ROADMAP.md)，验证证据见 [文档导航](docs/README.md)。

后续先交付最小 OPC UA 场景、证据与基础故障验证，P8.1 准确版本记录先于 P6 改造，再尽早跑通同一采集数据驱动的 AI 联合调查；完整 P5 可靠性随后补齐，P6.1 契约稳定后即可并行启动文本 PDF。调查分开表达数据可信度与设备异常证据，恒值或无通知不直接判故障；采集和告警不自动调用模型。外部安全 Server 验证列为核心 Demo 后的可选 P5.5，工单能力维持可用。具体任务与验收统一见 [开发计划](ROADMAP.md)。

## 演示场景

`North Plant → Utilities → Cooling Pump P-101 → 高振动告警 → AI 证据调查 → 人工确认 → 关联工单 → 处理并关闭`

模拟振动值从 4.0 升至 12.6 mm/s，规则触发告警。人员可查看趋势与资料，在告警页发起调查、展开引用，再手工确认告警并创建关联工单。关闭工单与告警自动恢复分别遵循各自的业务规则。演示阈值仅用于虚构场景。

工作台采用 Element Plus：资产为位置树与设备表格，设备详情在宽抽屉中按标签分区；工单在当前上下文上打开新一层抽屉，默认“创建并打开 → 开始执行 → 完成并关闭”，保留草稿和复核分支。总览、告警调查与设备工单列表共用抽屉入口，旧工单链接也兼容打开抽屉。告警及资料使用统一表格、表单和反馈。布局与验收证据见 [交互验证记录](docs/UI_INTERACTION_VALIDATION.md)，组件维护约定见 [源码规范](docs/CODE_STYLE.md)。

侧栏的“界面语言 / Interface language”支持简体中文与 English，默认中文；选择会保存在当前浏览器，刷新后继续生效。切换即时更新界面和组件提示，不清空正在编辑的表单。新 AI 调查按发起时所选语言生成摘要、分析与核查建议；调查语言随任务保存，排队、恢复执行和历史分析建议不会因界面切换而改变。已有调查的服务端固定事实、证据标题、范围和质量提示随界面即时切换语言；底层事实原文、引用和校验值保持不变，英文证据详情可展开原文。资产名称、资料正文及工单内容保留原文；时间统一为北京时间。前端翻译入口为 `web/src/i18n.ts`，英文词条集中在 `web/src/locales/en.ts`，证据展示翻译在 `web/src/evidencePresentation.ts`。


## 技术栈

| 层次 | 技术 |
|---|---|
| 后端 | ASP.NET Core / .NET 10、EF Core |
| 数据库 | PostgreSQL 17 |
| 前端 | Vue 3、TypeScript、Vite、Element Plus 2.14.7 |
| 实时通知 | SignalR、事务 Outbox |
| 数据接入 | Simulation Source、OPC Foundation .NET Client |
| AI 接入 | 独立 `ILlmGateway`、OpenAI 兼容适配、Fake Provider |

## Docker 启动

安装 Docker Desktop（Linux engine）后，在仓库根目录使用 PowerShell 7 执行：

```powershell
./deploy/Start-Docker.ps1
```

脚本构建并启动前端、API、Worker 与独立数据库，同时显式初始化演示数据。打开 [Docker 工作台](http://127.0.0.1:8080)，账号 `admin`，密码从本机 `deploy/.env.docker` 的 `DEMO_PASSWORD` 读取。无需安装 .NET 或 Node；默认 AI 使用 Fake Provider。

手动启动、数据持久化、可选真实 AI 与升级步骤见 [Docker 指南](docs/DOCKER.md)。该配置用于本地 Development 演示。

## 本地源码启动

准备 .NET SDK（见 [global.json](global.json)）、Node.js 22（见 [web/package.json](web/package.json)）与 Docker Desktop Linux engine。以下命令使用 PowerShell，在仓库根目录执行。

首次创建本地凭据并初始化数据库：

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

初始化仅允许 Development，会应用迁移并创建演示数据。已有数据库升级前请停止服务并备份数据库与上传目录；重复初始化保留已有账号密码及业务修改。详细说明见 [本地开发指南](docs/DEVELOPMENT.md)。

分别在三个终端启动服务：

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

打开 [本地工作台](http://127.0.0.1:5173)。API 使用端口 5080，Worker 使用端口 5081。仅启动 API 不会产生新测量；前端开发服务负责代理 API 与实时连接。

可用演示账号：`admin`（两厂管理）、`operator`（North 告警确认）、`viewer`（North 只读）、`engineer`（South 维护）。首次密码来自本机 `appsettings.Local.json` 的 `Demo.Password`，重新初始化不会重置已有密码。本地凭据文件已忽略，不应提交到版本库。

默认 AI 使用 Fake Provider，不访问外网。真实模型为可选配置，接入方法见 [AI 配置指南](docs/AI_PROVIDER_SETUP.md)；模拟结果与真实模型结果分别标记。

三点 OPC UA 场景推荐使用相邻 `industrial-device-simulation-framework` 仓库提供的Server：在本仓库运行 `./deploy/Run-IndustrialSimRuntime.ps1 -Run <新名称>`，按固定时间线演示温升、独立振动与停服恢复，并经普通Worker写入独立测试schema。它不会自动切换Docker工作台的数据源，详见 [IndustrialSim三点验证](docs/INDUSTRIALSIM_RUNTIME_VALIDATION.md)。

## 文档

- [Docker 指南](docs/DOCKER.md)：完整容器启动、配置、存储与日常操作。
- [本地开发指南](docs/DEVELOPMENT.md)：完整启动、配置、业务演示、健康检查与测试方法。
- [项目规格](PROJECT_SPEC.md)与[系统架构](ARCHITECTURE.md)：业务契约、权限、模块与数据流。
- [AI 设计](docs/AI_DESIGN.md)与[OPC UA 集成](docs/OPCUA_INTEGRATION.md)：证据、工具边界与协议设计。
- [实施路线](ROADMAP.md)：阶段任务与剩余验收。
- [完整文档导航](docs/README.md)：领域模型、代码规范、设计计划、验收报告及开发日志。

## 许可证

[Apache License 2.0](LICENSE)。
