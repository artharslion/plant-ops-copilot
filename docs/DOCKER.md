# Docker 本地演示

[返回项目首页](../README.md) · [文档导航](README.md)

只需 Docker Engine 和 Docker Compose（Windows 推荐 Docker Desktop Linux engine）。API、Worker、前端和 PostgreSQL 均在容器中运行，无需宿主机安装 .NET 或 Node。

## 一次启动

在仓库根目录使用 PowerShell 7：

```powershell
./deploy/Start-Docker.ps1
```

脚本首次生成被 Git 忽略的 `deploy/.env.docker`，随后构建镜像、等待数据库健康、停止演示栈写服务、显式初始化数据库，再启动并等待全部服务健康。失败会终止，首次构建需要联网下载镜像与锁定依赖。

打开 [Docker 工作台](http://127.0.0.1:8080)，使用 `admin` 登录；密码从本机 `deploy/.env.docker` 的 `DEMO_PASSWORD` 读取，不打印到启动日志。其他演示账号与工厂范围见[本地开发指南](DEVELOPMENT.md)。修改 `WEB_PORT` 可以更换端口。

根目录 Compose 项目名为 `plantops-demo`，使用独立数据库和存储卷。原有 `deploy/compose.yaml` 仍用于宿主机开发，它的数据库和 5432 端口不会被本方案使用。

## 手动启动（任意平台）

先复制 `deploy/docker.env.example` 为 `deploy/.env.docker`，用本地编辑器填写密码。数据库密码仅使用字母、数字、下划线和连字符；演示账号密码至少 12 位，包含大小写、数字与符号。Compose env 文件中的特殊字符按其语法转义，含 `$` 的值建议使用单引号。

在仓库根目录执行：

```sh
docker compose --env-file deploy/.env.docker build api worker web
docker compose --env-file deploy/.env.docker up -d --wait database
docker compose --env-file deploy/.env.docker stop web worker api
docker compose --env-file deploy/.env.docker run --rm --no-deps db-init
docker compose --env-file deploy/.env.docker up -d --wait --wait-timeout 180 api worker web
```

初始化复用现有 Development 初始化器，应用迁移并创建演示账号与模拟数据；重复初始化保留已有密码、角色、授权和业务编辑。升级已有环境前先备份。普通 `up` 和 `restart` 不自动应用迁移，代码更新后需再次执行完整启动流程。

## 服务与存储

| 服务 | 用途 | 宿主机访问 |
|---|---|---|
| `web` | Nginx 静态页面与同源 API/WebSocket 代理 | `127.0.0.1:8080`（可配置） |
| `api` | 业务 API、认证、文件与实时通知 | 仅容器网络 |
| `worker` | 模拟采集、聚合、保留与 AI 调查任务 | 仅容器网络 |
| `database` | PostgreSQL | 仅容器网络 |
| `db-init` | 显式迁移及演示种子，一次性运行 | `tools` profile，不常驻 |

| Compose 卷 | 内容 |
|---|---|
| `database-data` | 数据库、账号、测量、告警、工单与审计 |
| `documents` | API、Worker 与初始化器共用的资料文件 |
| `api-keys` | API Data Protection 登录与防伪密钥 |

`stop`、`down` 和重建容器保留数据卷；不要删除仍需保留的卷。更改 env 文件中的数据库密码不会修改已有数据库账号密码，更改 `DEMO_PASSWORD` 也不会重置已有演示账号。

API 和 Worker 使用非 root 用户。镜像采用多阶段构建、锁定依赖及固定基础镜像摘要。容器 SDK 由构建镜像确定，与宿主机 `global.json` 的 SDK 选择独立；本方案使用 .NET 10 SDK 和 Node 22。

## 健康检查与日常操作

```sh
docker compose --env-file deploy/.env.docker ps
docker compose --env-file deploy/.env.docker logs --tail 100 api worker web
docker compose --env-file deploy/.env.docker exec api bash /app/healthcheck.sh
docker compose --env-file deploy/.env.docker exec worker bash /app/healthcheck.sh
docker compose --env-file deploy/.env.docker stop
docker compose --env-file deploy/.env.docker down
```

浏览器访问 `/health/live` 和 `/health/ready` 会代理至 API；`ready` 要求数据库可用且无待应用迁移。服务之间按健康状态等待，参见 [Compose 启动顺序](https://docs.docker.com/compose/how-tos/startup-order/)。

Nginx 保留请求 Host，处理 SignalR 的 Upgrade 头与长连接；上传限制与 API 的 10 MiB 文件上限配套，代理超时覆盖最长调查等待，参见 [Nginx WebSocket 代理](https://nginx.org/en/docs/http/websocket.html)。

## 可选真实 AI

默认 `AI_PROVIDER=Fake`，不会请求外部模型。需要真实模型时，在本机 `deploy/.env.docker` 配置：

```dotenv
AI_PROVIDER=OpenAiCompatible
AI_BASE_URL=https://your-provider.example/v1
AI_MODEL=your-model
AI_API_KEY=your-local-secret
```

配置后执行 `docker compose --env-file deploy/.env.docker up -d --force-recreate api worker` 使环境变量生效。不要把实际密钥写入示例文件、镜像或提交记录。宿主机 `appsettings.Ai.Local.json` 不会自动加载到容器；容器中的 `localhost` 指向该容器，宿主机代理或本地模型服务需使用容器可达地址。

## 部署范围

Compose 明确使用 Development：初始化器仅允许该环境，本地 HTTP 登录依赖其 Cookie 设置，OpenAPI 也会开放。默认只绑定本机回环地址。生产部署需独立配置 HTTPS、Cookie/代理策略、正式用户与授权、备份恢复和迁移流程，不能直接将该演示入口暴露到公网。

构建上下文使用白名单，仅包含必需源码、前端资源和构建配置；本地凭据、Git 历史、验证产物和备份不会进入镜像。

已执行的构建、登录、资料、实时连接与容器重建检查见 [Docker 本地验证](DOCKER_VALIDATION.md)。
