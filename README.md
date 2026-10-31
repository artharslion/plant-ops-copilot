# Plant Ops Copilot

面向工业资产运维的 AI Copilot 演示项目。围绕设备、测量、告警和工单建立业务闭环，再由 AI 联合查询授权证据，辅助人员调查异常。

项目使用虚构工厂、设备与资料，支持单组织、多工厂和角色权限隔离。基础业务可在无需模型服务或工业网络的本地环境中运行。

## 在线演示

**[打开 Plant Ops Copilot 在线工作台](https://p01--plant-ops-copilot-web-api--m4ypq7wr8x2f.code.run/)**

演示环境部署在 Northflank，使用 Web + API、Worker + IndustrialSim 两个组合服务及 PostgreSQL。账号由环境维护者提供，登录密码不写入仓库。

重点演示链路：**North Plant → Cooling Pump P-101 → OPC UA 测量与趋势 → 高振动告警 → AI 证据调查 → 人工确认工单**。IndustrialSim 提供振动、温度和参考温度三个仿真测点，经真实 OPC UA 协议采集；数值由模拟场景控制，正常恒值不代表采集停止。AI 调查由人员手动发起，是否使用真实模型取决于该环境的服务端配置与页面设置。

[应用存活检查](https://p01--plant-ops-copilot-web-api--m4ypq7wr8x2f.code.run/health/live) · [数据库就绪检查](https://p01--plant-ops-copilot-web-api--m4ypq7wr8x2f.code.run/health/ready) · [Northflank 部署与接入说明](deploy/northflank/README.md)

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
| OPC UA | 已实现受控多点常驻采集、重连及健康展示；普通源码配置默认关闭，Docker Hub 联合部署和 Northflank 组合镜像启用 P-101 三点 Development 回环演示；工业网络与完整运维尚未验收 |
| AI 动作审批 | 已支持创建工单和向已有工单追加调查备注：编辑、完整确认、事务内重校验与幂等审计；完整 P4 验收仍待完成 |

AI 负责解释证据与提出建议，业务状态由数据库和应用服务管理；调查工具仅提供只读能力。人员通过现有业务流程确认告警、创建和处理工单。

在告警详情完成调查后，Admin / MaintenanceEngineer 可在调查结果下点击“整理工单草稿”，编辑后“保存并核对完整内容”，再“确认创建工单”。此流程不新增模型调用，创建关联当前设备和告警的 Draft 工单；提案15分钟到期，资料、权限或目标版本变化会阻止执行。保存、拒绝或未确认均不会创建工单；执行结果不明时先查询原提案。详见 [首个 P4 增量验证](docs/P4_WORK_ORDER_VALIDATION.md)。

已有维护任务时，可选“选择已有工单追加备注”→选择同一设备下未关闭/取消的工单→“整理备注提案”→编辑并保存→核对目标、版本和完整内容→“确认追加备注”。目标工单发生变化时需重新整理；成功后从回执打开原工单抽屉，保留告警调查上下文。详见 [追加备注验证](docs/P4_NOTE_VALIDATION.md)。

本项目用于工程演示与集成验证。生产级控制、设备写操作和安全决策不在范围内；长期容量与生产部署仍需独立验证。阶段任务见 [实施路线](ROADMAP.md)，验证证据见 [文档导航](docs/README.md)。

后续先交付最小 OPC UA 场景、证据与基础故障验证，P8.1 准确版本记录先于 P6 改造，再尽早跑通同一采集数据驱动的 AI 联合调查；完整 P5 可靠性随后补齐，P6.1 契约稳定后即可并行启动文本 PDF。调查分开表达数据可信度与设备异常证据，恒值或无通知不直接判故障；采集和告警不自动调用模型。外部安全 Server 验证列为核心 Demo 后的可选 P5.5，工单能力维持可用。具体任务与验收统一见 [开发计划](ROADMAP.md)。

升温调查新增有界原始观测的变化量、变化率和非下降观测段，以及带证据引用的趋势分析；坏值、缺口或配置版本未知时明确限制，不从分钟极值推断持续升温。中英文页面可展开计算样本，工单草稿保留分析，历史调查继续兼容。源码与验收边界见 [升温分析记录](docs/P6_HEATING_VALIDATION.md)。

调查已增加按需取证最小版：初始提供告警、设备摘要及当前质量快照，详细趋势、近期告警、维护和资料按需读取，并记录固定截止、缓存、逐调用结果和后续警告。三点真实协议及模拟浏览器工单路径已有有限验证；本轮唯一真实调查因预算不足失败，后续压缩仅经离线验证，完整真实升温语义仍待验收。详见 [P6.2 实现与验证](docs/P6_2_ON_DEMAND_VALIDATION.md)。

## 演示场景

`North Plant → Utilities → Cooling Pump P-101 → 高振动告警 → AI 证据调查 → 人工确认 → 关联工单 → 处理并关闭`

模拟振动值从 4.0 升至 12.6 mm/s，规则触发告警。人员可查看趋势与资料，在告警页发起调查、展开引用，再手工确认告警并创建关联工单。关闭工单与告警自动恢复分别遵循各自的业务规则。演示阈值仅用于虚构场景。

工作台采用 Element Plus：资产为位置树与设备表格，设备详情在宽抽屉中按标签分区；工单在当前上下文上打开新一层抽屉，默认“创建并打开 → 开始执行 → 完成并关闭”，保留草稿和复核分支。总览、告警调查与设备工单列表共用抽屉入口，旧工单链接也兼容打开抽屉。告警及资料使用统一表格、表单和反馈。布局与验收证据见 [交互验证记录](docs/UI_INTERACTION_VALIDATION.md)，组件维护约定见 [源码规范](docs/CODE_STYLE.md)。

侧栏的“界面语言 / Interface language”支持简体中文与 English，默认中文；选择会保存在当前浏览器，刷新后继续生效。切换即时更新界面和组件提示，不清空正在编辑的表单。新 AI 调查按发起时所选语言生成摘要、分析与核查建议；调查语言随任务保存，排队、恢复执行和历史分析建议不会因界面切换而改变。已有调查的服务端固定事实、证据标题、范围和质量提示随界面即时切换语言；底层事实原文、引用和校验值保持不变，英文证据详情可展开原文。资产名称、资料正文及工单内容保留原文；时间统一为北京时间。前端翻译入口为 `web/src/i18n.ts`，英文词条集中在 `web/src/locales/en.ts`，证据展示翻译在 `web/src/evidencePresentation.ts`。 新生成的工单和备注草稿继承调查语言（包括模拟调查），审批后保留核对过的同一份内容；旧草稿不会自动覆盖。修改证据翻译后，在 `web` 目录执行 `node scripts/export-evidence.mjs`，同步后端生成工单使用的英文词条。


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

**Northflank 两个服务部署**：使用 [Web + API、Worker + IndustrialSim 组合镜像](deploy/northflank/README.md)，只需两个常驻服务与一个 PostgreSQL；OPC UA 保持容器内回环通信。

组合镜像为 `artharslion/plant-ops-copilot:web-api-20261029` 和 `artharslion/plant-ops-copilot:worker-sim-20261029`。两项服务使用相同的 `ConnectionStrings__PlantOps`，仅 Web + API 的 8080 HTTP 公开。配置 `AllowedHosts` 和 HTTPS 转发头后，首次部署还需运行 `deploy/northflank/connect-demo.py` 创建来源与三点映射；该步骤完成后 Worker 才会采集 IndustrialSim。

**直接使用 Docker Hub 镜像（包含 IndustrialSim）**：下载 [联合部署目录](deploy/dockerhub/README.md) 中的 `compose.yaml` 和 `.env.example`，复制并填写 `.env` 后运行 `docker compose up -d --wait --wait-timeout 240`。会自动初始化数据库、启动两套服务并接入 P-101 的三个 OPC UA 测点，无需下载源码或本机安装 .NET / Node。

从源码构建的启动方式如下。

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

- [Northflank 部署与接入](deploy/northflank/README.md)：两个组合镜像、数据库、域名、HTTPS 实时连接、测点接入与模拟场景。
- [Docker 指南](docs/DOCKER.md)：完整容器启动、配置、存储与日常操作。
- [本地开发指南](docs/DEVELOPMENT.md)：完整启动、配置、业务演示、健康检查与测试方法。
- [项目规格](PROJECT_SPEC.md)与[系统架构](ARCHITECTURE.md)：业务契约、权限、模块与数据流。
- [AI 设计](docs/AI_DESIGN.md)与[OPC UA 集成](docs/OPCUA_INTEGRATION.md)：证据、工具边界与协议设计。
- [实施路线](ROADMAP.md)：阶段任务与剩余验收。
- [完整文档导航](docs/README.md)：运行指南、设计契约与近期验收证据，按任务查阅。

## 许可证

[Apache License 2.0](LICENSE)。
