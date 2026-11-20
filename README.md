# Plant Ops Copilot

面向工业资产运维的 AI Copilot 演示项目。围绕设备、测量、告警和工单建立业务闭环，再由 AI 联合查询授权证据，辅助人员调查异常。

项目使用虚构工厂、设备与资料，支持单组织、多工厂和角色权限隔离。基础业务可在无需模型服务或工业网络的本地环境中运行。

## 托管演示

**[打开 Plant Ops Copilot 在线工作台](https://p01--plant-ops-copilot-web-api--m4ypq7wr8x2f.code.run/)**

Northflank 演示使用 Web + API、Worker + IndustrialSim 两个组合服务及 PostgreSQL。账号由环境维护者提供，登录密码不写入仓库。部署说明记录镜像版本和该版本的检查结果。

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
| AI 只读调查 | 已接通后台 v3 调查、按需只读工具、逐点可信度、原始观测变化与授权引用；稳定工况、当前版本自主检索规程及独立盲测仍待验收 |
| OPC UA | 已实现受控多点常驻采集、重连及健康展示；本机三点 30 分钟运行与浏览器核查已有验证，完整故障矩阵、工业网络及长期容量仍待验收 |
| AI 动作审批 | 已支持创建工单和向已有工单追加调查备注：编辑、完整确认、事务内重校验与幂等审计；完整 P4 验收仍待完成 |

AI 负责解释证据与提出建议，业务状态由数据库和应用服务管理；调查工具仅提供只读能力。人员通过现有业务流程确认告警、创建和处理工单。

在告警详情完成调查后，Admin 或 MaintenanceEngineer 可选择整理关联 Draft 工单，或向同一设备下仍可处理的工单追加备注。编辑并保存后，核对目标与全部内容，再明确确认。提案有效期为 15 分钟，执行时重新检查权限、资料和版本。执行结果不明时先查询原提案。整理和确认草稿不新增模型调用。详见 [调查与工单契约](docs/INVESTIGATION_RESPONSE_V3.md)。

本项目用于工程演示与集成验证。生产级控制、设备写操作和安全决策在演示范围之外。调查由人员手动发起，采集与告警独立运行。各阶段的当前状态与验收证据见 [开发计划](ROADMAP.md)。

## 演示场景

`North Plant → Utilities → Cooling Pump P-101 → 高振动告警 → AI 证据调查 → 人工确认 → 关联工单 → 处理并关闭`

模拟振动值从 4.0 升至 12.6 mm/s，规则触发告警。人员可查看趋势与资料，在告警页发起调查、展开引用，再手工确认告警并创建关联工单。关闭工单与告警自动恢复分别遵循各自的业务规则。演示阈值仅用于虚构场景。

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
