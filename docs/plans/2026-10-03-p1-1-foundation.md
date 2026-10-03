# P1.1 Foundation Implementation Plan

> 本计划按 writing-plans 工作流记录，由当前会话逐项执行；用户已授权开始下一步。

**Goal:** 建立可启动、可验证、可重复初始化的 PlantOps 基础骨架。

**Architecture:** Api 与 Worker 共享 Application/Domain/Infrastructure，使用 PostgreSQL。Vue 通过同源代理查询运行状态；本轮不提供未授权的业务数据端点。

**Tech Stack:** .NET 10、EF Core/Npgsql、PostgreSQL、Vue 3、TypeScript、Vite。

## 范围

- P1.1：项目结构、版本锁定、迁移、虚构种子、数据库 Compose、健康检查、OpenAPI、前端基础页面。
- Identity、工厂授权和 CRUD 属于 P1.2；Simulation Source/IMeasurementSource 属于 P1.3；OPC UA/AI 项目在对应阶段创建。
- 原设计文档维持审阅建议状态，本轮仅实现已授权的 P1.1。

## 任务与验证

1. 创建 `global.json`、`Directory.Build.props`、解决方案、五个项目及依赖引用；确认 Domain 不依赖 Infrastructure，Application 不依赖 Host。
2. 新建最小 Plant/Location/Asset 实体、DbContext 和固定 ID 虚构种子；生成初始迁移；在 Worker 中增加显式 Development 初始化命令，重复运行不重复数据。
3. Api/Worker 提供独立存活与数据库/迁移就绪检查；Api 在 Development 提供 OpenAPI 和不含业务数据的系统信息端点。
4. 创建 `deploy/compose.yaml` 和不含真实凭据的 `.env.example`；数据库只绑定本机端口，迁移不在正常 API 启动时隐式执行。
5. 新建 `web/`，显示真实 API/数据库连接状态与 Golden Path；提供刷新、失败与加载状态，不把未实现功能显示为可用。
6. 新建健康检查/初始化幂等的必要测试，执行 `dotnet build`、`dotnet test`、`npm run build`、实际数据库重复初始化和 HTTP 检查；验证数据库不可用时 live=200、ready=503。
7. 更新 README 实测命令及 ROADMAP 状态，检查差异/敏感数据/暂存内容后创建阶段提交。

## 验收记录

- .NET SDK 10.0.112、Node 22.23.1；PostgreSQL 17.11，镜像固定为已拉取的 manifest digest。
- 后端构建 0 warning / 0 error；8 项测试通过，0 跳过，含独立 plantops_tests 数据库。
- 前端 TypeScript 检查与 Vite 生产构建通过；依赖 audit 为 0；浏览器实际显示 API 与数据库已就绪，刷新正常。
- 初始迁移成功；初始化连续执行两次后 Plant/Location/Asset 各 1 条；集成测试证明已有种子修改不会被覆盖。
- Api/Worker 的 live/ready 正常均 200；停数据库后 live=200、ready=503；重启恢复。
- Development OpenAPI 返回 3.1.1，包含 system 端点；Production 不暴露 system/OpenAPI 的测试通过。
- 首次依赖下载遇到间歇 TLS 错误，重试及微软公共包源补充缓存后恢复；最终 locked-mode restore 通过。未更改用户全局包源或关闭 TLS 校验。
- 本轮只验证本机启动；尚未验证其他机器或生产部署。资产实体是种子所需的最小字段子集，完整不变量、Identity/范围授权与 CRUD 在 P1.2 补齐。
