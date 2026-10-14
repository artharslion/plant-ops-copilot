# Docker 本地验证

验证日期：2026-10-14。范围为 Docker Desktop Linux engine 上的 `plantops-demo` 本地演示栈，未修改原有 `plantops` 开发数据库，未调用真实模型。

## 已执行检查

| 检查 | 结果 |
|---|---|
| Compose 配置解析、PowerShell 启动脚本语法 | 通过 |
| API/Worker 锁定依赖还原与 Release 发布 | 容器内通过 |
| 前端锁定依赖安装、类型检查与构建 | 容器内通过 |
| 三个应用镜像构建 | 通过；基础镜像均按 digest 固定 |
| 全新独立数据库、显式迁移与演示种子 | 通过 |
| API、Worker、Nginx、PostgreSQL 健康 | 全部通过 |
| Nginx 配置检查、首页及 `/alarms` 直接访问 | 通过 |
| 未登录访问业务 API | 返回 401 |
| 登录缺少防伪 token | 返回 400 |
| admin 登录、当前用户、两厂与 P-101 读取 | 通过 |
| Worker 持续采集 | 两次读取之间最新接收时间递增 |
| TXT 上传、发布与下载 | 通过；文件使用 `text/plain` 媒体类型 |
| SignalR 协商、WebSocket Upgrade 与 JSON 握手 | 通过 |
| SignalR 不匹配 Origin | 返回 403 |
| 重建 API/Worker/Web 后原 Cookie 和防伪 | 原登录会话仍可用，防伪 token 可获取 |
| 重建应用容器后资料与数据库记录 | 已发布资料仍可下载 |
| 再次执行完整启动脚本 | 通过，保留已有数据与账号 |
| 不满足策略的演示密码 | 一次性初始化失败，在迁移前拒绝 |
| API/Worker 运行用户 | 均为 `app`，非 root |
| 新增 GitHub Actions 工作流语法 | actionlint 通过 |
| Markdown 本地链接、代码围栏与 diff 空白检查 | 通过 |

首次尝试发现宿主机 SDK 补丁没有对应官方容器标签，容器改为独立固定的 .NET 10 SDK 镜像。安装额外探针软件时遇到本机 Docker 网络代理异常，最终使用运行镜像已有 Bash 的 HTTP 状态探针，不增加系统软件包下载步骤。最终镜像构建与启动均成功。

## 验证边界

本次没有执行远端 GitHub Actions；新增工作流会在 push / pull request 时构建镜像、初始化独立数据库并检查健康、静态路由与匿名 API 权限。

本次验证覆盖应用容器重建与卷挂载，不代表完整数据库灾难恢复、长期负载、生产 HTTPS、真实模型或 OPC UA 集成验收。Data Protection 密钥在本地卷内未额外加密；正式部署仍需配置密钥保护与备份策略。

运行方式与日常操作见 [Docker 指南](DOCKER.md)。
