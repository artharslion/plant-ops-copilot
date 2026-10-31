# Docker Hub 联合部署

只需要此目录的 `compose.yaml` 和 `.env.example`，无需下载两个项目的源码，也无需安装 .NET、Node 或 Python。宿主机安装 Docker Engine（Linux 容器）与 Docker Compose **2.23.1 或更高版本**（使用该版本引入的[内联配置](https://docs.docker.com/reference/compose-file/configs/)）；Windows 可使用 Docker Desktop。当前应用镜像提供 **linux/amd64**，ARM64 原生镜像尚未发布。

## 首次启动

将两个文件放入一个独立目录，在该目录打开终端：

```powershell
Copy-Item .env.example .env
```

macOS/Linux 使用 `cp .env.example .env`。用编辑器打开 `.env`，填写：

- `POSTGRES_PASSWORD`：仅使用字母、数字、下划线或连字符。
- `DEMO_PASSWORD`：至少 12 位，包含大小写字母、数字与符号；包含 `$` 或 `#` 时用单引号包裹整个值。

然后执行：

```sh
docker compose up -d --wait --wait-timeout 240
```

首次会自动下载镜像；慢网络可先执行 `docker compose pull`，再运行启动命令。

打开 [Plant Ops 工作台](http://127.0.0.1:8080)，使用 `admin` 和刚才填写的 `DEMO_PASSWORD` 登录。[模拟器控制台](http://127.0.0.1:8081) 用于查看仿真设备和手动控制场景。端口可通过 `.env` 中的 `WEB_PORT`、`SIMULATOR_PORT` 修改。

Compose 使用独立项目名 `plantops-hub` 和数据卷。与现有源码工作台并行运行时，需要更换宿主机端口；数据库和配置不会从旧环境自动迁移。

## 自动完成的工作

| 服务 | 作用 |
| --- | --- |
| `database` | PostgreSQL 业务数据存储 |
| `db-init` | 等待数据库健康，应用迁移并创建演示账号和基础数据，完成后退出 |
| `api` | 业务 API、登录、资料与实时通知 |
| `worker` | OPC UA 采集、告警、聚合和调查任务 |
| `industrial-sim` | 独立仿真设备和开发控制台，SQLite 持久化 |
| `demo-connect` | 通过管理员登录、CSRF 和现有授权 API 配置三个采集点，完成后退出 |
| `web` | 等待接入成功后开放工作台 |

`db-init` 和 `demo-connect` 显示 `Exited (0)` 是正常状态；其余五个服务应为 `healthy`。初始化失败会阻止后续依赖服务启动，可用 `docker compose logs db-init demo-connect` 检查。

自动接入 North Plant / Cooling Pump P-101：

| 测点 | 初始值 | OPC UA 节点 |
| --- | --- | --- |
| `VIBRATION_RMS` | 4 mm/s | `plantops-p101/vibration` |
| `TEMPERATURE` | 60 degC | `plantops-p101/temperature` |
| `REFERENCE_TEMPERATURE` | 62 degC | `plantops-p101/referenceTemperature` |

三个测点通过真实 OPC UA 协议采集仿真值。模拟器与 Worker 共享网络空间，以沿用现有 Development 回环地址策略和精确节点白名单；无需发布 OPC UA 宿主机端口。接入容器与 API 共享网络空间，以沿用其现有 Host 限制。初始化使用正常应用接口，保留权限校验、版本控制和审计。

接入可重复执行并复用现有来源、测点与映射，仅停用这三个目标测点上冲突的内置 Simulation 映射；遇到冲突的自定义来源或不兼容点位会失败并要求人工核对。此配置用于独立演示环境，后续启动仍会检查并恢复这三个点位的接入。

内置约 100 点的 Simulation 采集关闭，首次不回填历史测量，只采集上述三个点。初始值保持正常，不自动执行异常场景，也不自动调用模型。可在模拟器控制台手动运行场景；其 YAML `at` 使用设备绝对仿真时间，长时间运行后应按当前时钟设置场景时间。

默认 `AI_PROVIDER=Fake`，无需模型密钥。需要真实 AI 时填写 `.env` 中的服务地址、模型与密钥，设置 `AI_PROVIDER=OpenAiCompatible`，然后重新执行完整启动命令加载配置。已有环境的调查模式还可由管理员在工作台“设置”中调整。

## 持久化和升级

四个命名卷分别保存 PostgreSQL、上传资料、登录密钥和模拟器 SQLite。停止和移除容器会保留这些卷：

```sh
docker compose ps -a
docker compose logs --tail 100 api worker industrial-sim
docker compose stop
docker compose down
```

再次启动继续使用原来的 `.env` 和启动命令。更改 `.env` 中的密码不会修改已有数据库账号密码，也不会重置已有演示用户密码；请保管首次使用的凭据。

镜像固定到已验证的源码版本，默认组合为：

- `artharslion/plant-ops-copilot:api-2cf19c8`
- `artharslion/plant-ops-copilot:worker-2cf19c8`
- `artharslion/plant-ops-copilot:web-2cf19c8`
- `artharslion/industrial-device-simulation-framework:3d5e7c0`

升级前备份数据库与所有数据卷，并先停止写入服务，再修改 `.env` 中匹配的新版本并启动：

```sh
docker compose stop web demo-connect industrial-sim worker api
# Edit PLANTOPS_VERSION / INDUSTRIALSIM_VERSION in .env.
docker compose pull
docker compose up -d --wait --wait-timeout 240
```

启动会重新运行数据库初始化器，应用迁移并保留已有演示账号密码及业务编辑。需要回退数据库结构时使用升级前备份。单独重建 Worker 时也必须重建模拟器，以重新关联共享的网络空间；推荐使用完整启动命令。

原始读数默认保留今天和昨天的 UTC 日分区，分钟汇总保留 90 天，已发送通知保留 24 小时；旧分区还需满足聚合完整等清理条件。可通过 Compose 中的环境变量调整。删除数据卷会删除持久化数据。

这是一套本机 Development 演示部署：仅绑定 `127.0.0.1`，模拟器关闭认证。生产部署所需的 HTTPS、正式身份、访问控制、备份恢复及工业网络支持仍需单独配置和验证。

## 2026-10-28 验证

- 从模拟器干净源码 `3d5e7c0` 构建并推送对应标签；Docker Hub 摘要为 `sha256:3478c1ef06de57fd8c07d7b6942b0b4b674ac81b076430b7a2d0d3a04ecd045c`。
- 使用空 Docker 凭据配置，成功匿名拉取上述四个应用镜像。
- 独立项目 `plantops-hub-smoke-20261028`、端口 18080/18081、全新数据卷首次自动初始化并启动成功；五个常驻服务健康，两个一次性服务退出码为 0。
- 通过登录后的业务 API 验证三个点位值分别为 4/60/62，来源 `OpcUa`、质量 `Good`、数据新鲜；三分钟趋势各取得 161 条真实协议样本，状态码均为 0。
- 重跑接入后来源、测点与映射 ID 保持一致，每个目标点只有一个启用映射。
- 保留数据卷移除容器，在只包含 `compose.yaml` 和 `.env` 的目录重新启动成功；三个点位继续采集，来源和映射 ID 保持不变。
- `AiRuns` 为 0；没有发起模型调查。空密码模板在配置检查时被拒绝。原有 `plantops-demo` 的五个服务在验证前后均保持健康。

证据保存在开发仓库被忽略的 `artifacts/dockerhub-20261028/`。验证范围为本机 Linux/amd64 Docker 环境，未进行 ARM64、生产部署、长期负载或真实模型质量验收。
