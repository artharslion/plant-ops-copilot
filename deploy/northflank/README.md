# Northflank：两个组合镜像

两个常驻 Service 和一个 PostgreSQL 即可运行本演示。镜像为 Linux/amd64，沿用 Development 演示模式。2026-10-29 用户已完成 Northflank 部署并确认访问、实时连接及 OPC UA 接入恢复；免费规格的长期容量、真实模型质量和生产部署仍需独立验证。

在线入口：**[Plant Ops Copilot 工作台](https://p01--plant-ops-copilot-web-api--m4ypq7wr8x2f.code.run/)**。演示账号由环境维护者提供，不在文档中发布密码。

| Service | Docker Hub 镜像 | 端口 |
| --- | --- | --- |
| `app` | `artharslion/plant-ops-copilot:web-api-20261029` | **8080 HTTP：Public**，由平台提供 HTTPS |
| `runtime` | `artharslion/plant-ops-copilot:worker-sim-20261029` | 无公网端口；8080 仅健康检查；可选 **8081 HTTP：Private**，用于模拟器控制台 |
| PostgreSQL addon | 使用 Northflank 提供的 PostgreSQL | 5432，仅内部访问 |

OPC UA 在 runtime 容器内使用 `opc.tcp://127.0.0.1:48481`，**不需要在 Northflank 添加 4840 或 48481 端口**。P-101 三点配置已装入镜像。Web 静态文件由 ASP.NET Core API 提供，不再依赖 Nginx 或 Docker DNS。runtime 同时监管两个进程，任一退出都会停止另一个并以非零码退出容器；SIGTERM 会转发给两者，最多等待15秒后强制结束未退出的子进程，平台终止宽限应至少25秒。每个 Service 使用 **1 个副本**，避免重复采集与 SQLite 多写入。

## 1. 配置数据库和存储

创建 PostgreSQL addon，使用平台给出的**内部连接参数**。两项 Service 的 `ConnectionStrings__PlantOps` 必须相同，格式为 Npgsql 连接字符串，而不是 `postgres://` URL：

```text
Host=<内部主机>;Port=5432;Database=<数据库>;Username=<用户名>;Password=<密码>;SSL Mode=Require;GSS Encryption Mode=Disable
```

TLS 参数按数据库实际设置填写；不要把真实连接串提交到仓库。表结构和数据量沿用现有应用，默认仅采集三个 OPC UA 点，原始分区保留期为 1 天配置，已发送通知保留 24 小时。

配置持久化路径：

| 路径 | 挂载位置 | 用途 |
| --- | --- | --- |
| `/var/lib/plantops/documents` | **app 与 runtime 共享同一个 Multi Read/Write 卷** | 种子资料、上传、下载及资料证据校验 |
| `/home/app/.aspnet/DataProtection-Keys` | app 独立卷 | 登录/防伪密钥 |
| `/app/simulator-data` | runtime 独立卷 | 模拟器 SQLite 控制面状态 |

两个同名但独立的本地目录不是共享存储。Northflank 的 [Multi Read/Write 卷](https://northflank.com/docs/v1/application/databases-and-persistence/add-a-volume) 可跨服务挂载；若账户不提供这种存储，需要另行提供共享文件存储，不能直接认为当前完整资料功能可用。卷不占常驻 Service 名额，但可用性和费用以账户控制台为准。镜像使用 `app:app` 非 root 用户，确保卷允许该用户组读写。

## 2. 部署 app

镜像填写 `artharslion/plant-ops-copilot:web-api-20261029`，保留镜像默认启动命令。添加 **8080 / HTTP / Public**，获取平台域名后填写以下环境变量：

| 变量 | 值 |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Development` |
| `ConnectionStrings__PlantOps` | 上一步的实际连接串 |
| `AllowedHosts` | `localhost;127.0.0.1;<实际平台域名>`，只写主机名，不含协议/路径 |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` |
| `PLANTOPS_INITIALIZE_DB` | 首次部署 `true` |
| `Demo__Password` | 自行生成的演示账号密码，至少12位且含大小写、数字、符号 |
| `Demo__SeedHistory` | `false` |
| `AI__Provider` | `Fake`，不调用外部模型 |

可选内部访问 app 时，将实际内部服务主机名也加入 `AllowedHosts`，例如 `app`。不要填写 `*`。转发头开关用于还原 HTTPS 请求，使 Secure Cookie 和 SignalR 的同源校验正常；它依赖受信任的平台 HTTP 入口，不应再通过原始 TCP 方式直接公开 API。[ASP.NET Core 代理说明](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)

首次启动会先运行现有数据库初始化器，完成迁移、账号和种子资料初始化后才启动 API。失败会退出，不会继续接受请求。启动成功后将 `PLANTOPS_INITIALIZE_DB` 改为 `false`，可移除 app 的 `Demo__Password`；已有账号密码不会被重新初始化重置。

设置启动探测宽限约 180 秒；readiness 使用 **HTTP 8080 `/health/ready`**，liveness 使用 **HTTP 8080 `/health/live`**。平台探测的 Host 必须在白名单内；若不能设置 Host，可使用命令探测 `bash /app/healthcheck.sh`。

## 3. 部署 runtime

镜像填写 `artharslion/plant-ops-copilot:worker-sim-20261029`，保留默认启动命令。必填变量：

```text
ASPNETCORE_ENVIRONMENT=Development
ConnectionStrings__PlantOps=<与 app 相同的实际连接串>
AI__Provider=Fake
```

镜像已内置 OPC UA 开关、精确节点白名单、低容量保留设置及三点 YAML，无需再次复制设置，也不需要为 OPC UA 改动回环限制。模拟器使用 HTTP 8081，Worker 使用 HTTP 8080，避免端口冲突。若需控制模拟场景，额外添加 **8081 / HTTP / Private**，通过平台端口转发访问；该模拟器控制台关闭认证，不要公开。

readiness 使用命令探测 `bash /app/runtime-healthcheck.sh`，liveness 使用 `bash /app/runtime-healthcheck.sh live`；两者同时检查 Worker 和模拟器，liveness 不依赖数据库可用性。首次启动宽限约 60 秒。数据库连接环境、AI 模式和共享资料路径需与 app 匹配。

## 4. 接通三个测点

服务启动后，在本地准备一个被忽略的凭据文件，例如 `.env.connect`：

```dotenv
PLANTOPS_BASE_URL=https://<实际平台域名>
DEMO_PASSWORD=<首次初始化使用的密码>
```

在包含 `connect-demo.py` 和 `.env.connect` 的目录执行：

```sh
docker run --rm --env-file .env.connect --mount type=bind,source=./connect-demo.py,target=/connect-demo.py,readonly python:3.13-alpine@sha256:8bcfa47b04b853a17aaf909362b847b2112fd6ec4105ea553ef9a43bafbb5916 python /connect-demo.py
```

也可在装有 Python 的本机设置这两个环境变量后运行 `python connect-demo.py`。脚本通过管理员登录、CSRF、版本控制和授权 API 复用/创建来源与映射；重复执行不创建重复配置。仅停用目标测点上冲突的内置 Simulation 映射，遇到不兼容点位或自定义来源冲突会失败。它不运行异常场景、不发起模型调查。完成后可删除本地凭据文件。

工作台登录账号为 `admin`，密码为首次配置的 `Demo__Password`。North Plant / P-101 的振动、温度与参考温度初始值分别为 4、60、62；内置约100点 Simulation 已关闭，其余点位不会继续产生模拟值。异常场景需要手动执行。

`connect-demo.py` 完成的是 Plant Ops 侧的接入配置：创建或复用 `OpcUa` 来源，将三个 Sim 节点映射到对应测点，并停用这些点位上冲突的旧 Simulation 映射。运行两个镜像或完成数据库初始化不会自动执行这一步。

## 5. 访问检查、场景与排查

首页、`/health/live` 和 `/health/ready` 应返回 HTTP 200；两个健康端点正文应为 `Healthy`。2026-10-29 从外部重新检查当前在线地址，三项均通过。健康检查不等同于 OPC UA 采集已连通。

- **400 Invalid Hostname**：将实际公网主机名加入 app 的 `AllowedHosts`，不带 `https://`、端口或路径；保存运行环境变量并重新部署。
- **SignalR negotiate 返回 Same-origin realtime connection required / 403**：确认 app 的运行环境变量 `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` 已加载到新实例，再重新登录。检查页面自动发出的已登录 POST 请求，成功时为200；地址栏直接打开链接不能替代 SignalR 验证。
- **Sim 有 DataPointChanged，工作台没有变化**：先检查登录后的 `/api/v1/plants/10000000-0000-0000-0000-000000000001/data-sources`。若只有 `Simulation`，执行上面的接入脚本；若存在 `OpcUa`，继续检查 `enabled=true`、`opcUa.endpointReference=industrial-sim-workbench`、`health.state=Connected`，以及 P-101 测点映射、最新接收时间和质量。Sim 内部事件不能单独证明 OPC UA 数据已入库。

需要改变仿真值时，在 runtime 添加 8081 HTTP Private，并通过 Northflank 本地端口转发打开 Sim 控制台。进入 **Scenarios → New scenario**，Reference device 和 Run target 都选 `plantops-p101`。例如配置一个步骤：Trigger=`when`、条件 `referenceTemperature == 62`、Action=`ramp`、Datapoint=`vibration`、From=`4`、To=`12.6`、Duration=`15 s`，点击 **Run scenario**。振动会逐渐升高并保持高值，Worker 采集后按现有规则判断告警。恢复时将 Action 改为 `set`、Datapoint=`vibration`、Value=`4` 后再次运行。原生 `at` 使用设备绝对仿真时间，长时间运行后不要直接套用从0秒开始的多步骤模板。

事件 `observedAtUtc` 为 UTC，Plant Ops 页面按北京时间显示，查询趋势时注意8小时时差。异常场景和接入脚本均不自动调用模型。

## 初始化 Job、升级与本地验证

也可将 web-api 镜像用于一次性数据库初始化 Job，保持镜像 entrypoint，只将 command/arguments 设置为 `--initialize-db`。Job 必须使用同一数据库、Development、`Demo__Password`，并共享 app 的资料卷。worker-sim 镜像同样支持该参数且不会启动模拟器。不要同时使用启动初始化和初始化 Job。

升级前备份数据库及卷，停止 runtime，在 app 停止接受请求时运行初始化/迁移，再启动两项服务。修改环境变量需重新部署；单纯重启不一定加载新变量。不要横向扩容本演示。

本地复现只需本目录的 `compose.yaml`、`.env.example` 和 `connect-demo.py`：复制示例为 `.env`，填写密码，然后执行：

```sh
docker compose up -d --wait --wait-timeout 240
```

它创建两个应用服务和 PostgreSQL，自动初始化数据库。按上一步用 `PLANTOPS_BASE_URL=http://127.0.0.1:8080` 接通测点。若已有工作台占用端口，在 `.env` 更改 `WEB_PORT` / `SIMULATOR_PORT`。默认保留数据卷；`down` 后可重建恢复。

源码构建命令（仓库根目录）：

```sh
docker build -f deploy/Dockerfile.combined --target web-api -t artharslion/plant-ops-copilot:web-api-20261029 .
docker build -f deploy/Dockerfile.combined --target worker-sim -t artharslion/plant-ops-copilot:worker-sim-20261029 .
```

Northflank 不执行这里的 Compose；它是本地验证与配置参考。上述在线部署确认和本地检查不代表生产部署或免费规格的长期容量已验收。

## 2026-10-29 本地验证

- 两个组合镜像以 Linux/amd64 构建，容器使用非 root 用户；模拟器来自已发布的固定摘要版本。
- 两个镜像已推送 Docker Hub，并使用空凭据配置成功匿名拉取。
- 18 项网页托管、SPA 路由、授权保留和健康检查测试通过，修改文件的 .NET 格式检查通过。
- 两项服务加全新 PostgreSQL 自动初始化成功；宿主机已有 `plantops-demo` 未被修改。
- 通过模拟 HTTPS 代理验证登录、Secure Cookie、静态 JS、SPA 深链接、API 401/404、种子资料下载及 SignalR WebSocket 握手；其他 Origin 被拒绝。
- 三个点位通过正常 API 取得实际 OPC UA 样本，质量 Good、原始状态码0、当前值4/60/62。重建后采集恢复，来源、测点和映射 ID 保持一致；接入脚本重复执行没有重复配置。
- 分别终止模拟器和 Worker 子进程，组合容器均退出码1；正常停止约15.8秒退出码0，符合有界停止设计，不保证所有子进程在期限内完成优雅退出。
- 双进程 readiness 与 liveness 命令通过；`AiRuns` 为0，没有调用真实模型。
- 本机一次启动后快照约为 app 111 MiB、runtime 333 MiB、PostgreSQL 49 MiB；这是短时观测，不是容量下限或免费规格验收。

详细临时证据保存在仓库忽略的 `artifacts/combined-20261029/`。CI 新增了两个组合目标的构建和启动检查，尚未触发远程 CI。
