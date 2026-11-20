# Plant Ops Copilot

一个用 .NET 实现的工业运维 AI Agent 作品集项目。以冷却泵 P-101 为例，可信振动持续超限后，规则产生告警。工程师发起调查，Agent 决定是否补查测量、维修记录或规程，给出带引用的核查建议。工程师可以核实依据、编辑建议、确认建单，再记录实际处理结果。

现有演示使用虚构工厂和仿真设备。真实 OPC UA 采集、真实模型调查和人工审批均有本机运行记录，各自的版本和终点如下。系统支持单组织、多工厂授权。

## 面试时可以核查什么

| 问题 | 可直接阅读的证据 |
| --- | --- |
| 坏点会不会掩盖真正的告警？ | 在真实协议与 v3.13 真实模型联合运行中，温度点返回 Bad，独立振动点仍以可信的 12.6 mm/s 触发告警。模型分别建议核查设备振动和温度测量链。 [混合质量运行记录](docs/INDUSTRIALSIM_RUNTIME_VALIDATION.md#p6-补充联合验收2026-11-16) |
| 哪些调查决定是模型做的？ | v3.11 的有规程和空检索对照都由模型发起文档检索。v3.14 的有规程用例只选择读取测量，第二轮受预算约束，最后虽生成待确认提案，仍漏了适用规程。 [有／无规程的真实模型对照](docs/archive/P6_V3_FOLLOW_UP_20261119.md#p6-v313-acceptance-follow-up-2026-11-16) · [工具与请求复盘](docs/archive/P6_V3_FOLLOW_UP_20261119.md#p6-tool-budget-review-2026-11-19) |
| 建议怎样变成可追溯的工作？ | v3.14 真实模型稳定工况已从告警、调查、审批贯通至关闭，维修历史和审计均可回查。 [真实模型与关闭回执](docs/PORTFOLIO_REAL_MODEL_CASE.md) · v3.14 Fake 的 P-101 温度告警在确认前有 0 张工单，确认后创建唯一 Draft，填写结果后可按设备查到 Closed 工单。 [同案审批与维修记录](docs/PORTFOLIO_WORK_ORDER_CASE.md) · v3.5 的一次真实模型调查中，用户确认后创建了一张关联 Draft 工单。该次建议漏用了已有恢复证据。重复确认、撤权和写入失败的处理另有集成验证。 [调查与工单记录](docs/archive/P6_V3_FOLLOW_UP_20261119.md#2026-11-09-local-workbench-acceptance) · [审批与回滚验证](docs/archive/P4_WORK_ORDER_VALIDATION.md#verification) |

想亲手操作，可从源码启动内置模拟源和默认 Fake 模型，体验调查、审批和工单处理。要演示当前源码的真实 OPC UA 链路，可按接入步骤从相邻仓库构建 IndustrialSim，再运行三点场景。 [源码 Docker 指南](docs/DOCKER.md) · [工作台接入步骤](docs/INDUSTRIALSIM_WORKBENCH.md)

## 托管演示

**[打开 Plant Ops Copilot 在线工作台](https://p01--plant-ops-copilot-web-api--m4ypq7wr8x2f.code.run/)**

部署说明记录的是 2026-10-29 验证的固定镜像。Northflank 使用 Web + API、Worker + IndustrialSim 两个组合服务及 PostgreSQL，演示账号由环境维护者提供。当前源码的调查效果见下方验收范围。

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
| 资产运维业务 | 已实现资产、告警、资料权限和工单处理；独立集成测试覆盖人工填写结果、完成、关闭和审计。 [工单生命周期测试](tests/PlantOps.Tests/WorkOrderTests.cs) |
| AI 只读调查 | 已接通后台 v3 调查、按需只读工具、逐点可信度、原始观测变化与授权引用；稳定工况和有规程／空检索均有真实回答及失败记录，独立盲测尚缺案例。 [分模型验收](docs/PORTFOLIO_FOLLOWUP_READS.md) |
| OPC UA | 已实现受控多点常驻采集、重连及健康展示；本机三点 30 分钟运行与浏览器核查已有验证，完整故障矩阵、工业网络及长期容量仍待验收 |
| AI 动作审批 | 建单及追加备注提案有编辑、确认、事务内复核与幂等回执。历史真实模型案例到 Draft；当前源码从同一调查到人工处理并关闭的联合验收仍开放。 |

提案由有维护权限的调查发起人本人核对和确认，有效期 15 分钟，执行时复核权限、资料和版本。整理和确认草稿不新增模型调用。现场根因正确率和维护收益尚无实测依据。 [调查与工单契约](docs/INVESTIGATION_RESPONSE_V3.md) · [开发计划与验收状态](ROADMAP.md)

## 演示场景

`North Plant → Utilities → Cooling Pump P-101 → 高振动告警 → AI 证据调查 → 人工确认 → 关联工单 → 处理并关闭`

模拟振动值从 4.0 升至 12.6 mm/s，持续满足规则后触发告警。在告警页发起调查、展开引用并核对建议，确认提案后可打开关联工单，开始处理并填写结果。告警确认、测量回落后的规则恢复、工单关闭各自独立。演示阈值仅用于虚构场景。

侧栏可切换简体中文或 English。新调查记录发起时的语言，工单和备注草稿沿用该调查的语言。工作台按北京时间显示。

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

**直接使用 Docker Hub 镜像（包含 IndustrialSim）**：默认组合对应 2026-10-28 的协议验证。下载 [联合部署目录](deploy/dockerhub/README.md) 中的 `compose.yaml` 和 `.env.example`，复制并填写 `.env` 后运行 `docker compose up -d --wait --wait-timeout 240`。会自动初始化数据库并接入 P-101 的三个 OPC UA 测点，无需下载源码或本机安装 .NET / Node。

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

要让 Docker 工作台显示 IndustrialSim 三点数据，按 [工作台接入指南](docs/INDUSTRIALSIM_WORKBENCH.md) 配置。需要在隔离测试库复验固定场景、协议与故障恢复时，查阅 [三点采集复验](docs/INDUSTRIALSIM_RUNTIME_VALIDATION.md)。

## 文档

- [Northflank 部署与接入](deploy/northflank/README.md)：两个组合镜像、数据库、域名、HTTPS 实时连接、测点接入与模拟场景。
- [Docker 指南](docs/DOCKER.md)：完整容器启动、配置、存储与日常操作。
- [本地开发指南](docs/DEVELOPMENT.md)：完整启动、配置、业务演示、健康检查与测试方法。
- [项目规格](PROJECT_SPEC.md)与[系统架构](ARCHITECTURE.md)：业务契约、权限、模块与数据流。
- [调查建议 v3](docs/INVESTIGATION_RESPONSE_V3.md)：新调查输出与工单就绪契约；[AI 链路概述](docs/AI_DESIGN.md)与[OPC UA 集成](docs/OPCUA_INTEGRATION.md)按相关任务查阅。
- [实施路线](ROADMAP.md)：阶段任务与剩余验收。
- [文档导航](docs/README.md)：按开发、部署、采集和调查任务定位现行文档。

## 许可证

[Apache License 2.0](LICENSE)。
