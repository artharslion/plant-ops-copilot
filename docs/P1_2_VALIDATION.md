# P1.1 复盘与 P1.2 验收记录

日期：2026-10-03。本记录来自代码检查与本轮执行结果，不沿用上一轮完成总结。

## 环境与数据边界

- Windows / PowerShell；.NET SDK 10.0.112；Node 22.23.1。
- EF Core / Identity 10.0.12，Npgsql EF provider 10.0.3；PostgreSQL 17 镜像使用 deploy/compose.yaml 中的固定 digest。
- Git 初始 HEAD=99d8d02，工作区干净；未发现仓库或父级 AGENTS.md。没有覆盖无关修改。
- 自动测试只接受数据库名 `plantops_tests`。页面验收使用该库的独立 `p12_ui_20261003` schema。没有清空或初始化业务数据库；复核业务库 Plant/Location/Asset 仍各 1 行。
- 密码保存在被 Git 忽略的本机配置，测试账号使用运行时随机密码；没有输出密码或提交凭据。

## P1.1 独立复盘

| 分类 | 检查结果 |
|---|---|
| 已验证正确 | Domain 无包/基础设施引用；Application 只引用 Domain；Infrastructure 实现端口；API/Worker 负责组装，没有 OPC UA/AI 依赖 |
| 已验证正确 | NuGet locked restore、npm ci、前端类型/构建成功；EF migrations has-pending-model-changes 无变化；原有 8 项测试通过，无跳过 |
| 已验证正确 | Plant Code 全局唯一，Location/Asset Code 厂内唯一；Location 父节点和 Asset 位置使用同厂复合外键，删除 Restrict |
| 已验证正确 | 在线实际 Api/Worker live/ready=200；两个 Host 配置不可达数据库端口后 live=200、ready=503；Production Worker 拒绝初始化 |
| 已验证正确 | OpenAPI/system 仅 Development；system 无连接信息；数据库仅监听 127.0.0.1:5432，凭据来自忽略的 .env；总览状态由实际请求驱动，异常转失败 |
| 需要修复（已修） | README 生成凭据命令会覆盖已有 .env，改为文件不存在时生成 |
| 需要修复（已修） | Worker 有环境保护，但初始化器自身无保护；新增必传环境并在任何数据库调用前拒绝非 Development，新增 2 项验证 |
| 本阶段尚未实现 | 原有 Version 只是并发标记，未递增；身份/范围、目录写入/循环、审计等由 P1.2 补齐。测量、告警、工单、文档、OPC UA、AI 仍待后续 |

迁移检查使用 Infrastructure 自身的 DesignTimeDbContextFactory；不要求 API 引入 EF Design 包。重新安装/构建前停止本仓库的旧开发进程，释放 Windows 文件锁。

## P1.2 自动验证

最终命令：

```powershell
dotnet restore PlantOps.sln --locked-mode
dotnet build PlantOps.sln --no-restore
# PLANTOPS_TEST_CONNECTION 已按 README 指向独立测试库
dotnet test PlantOps.sln --no-build
dotnet ef migrations has-pending-model-changes --project src/PlantOps.Infrastructure --no-build
# web 下
npm run generate:api  # 仅需 Development API 在线，生成的文件已提交
npm ci
npm run build
npm audit --omit=dev
```

结果：后端零警告/零错误；23 测试通过、0 失败、0 跳过（15 真 PostgreSQL，8 无数据库）；模型无待迁移变化；前端锁文件恢复、类型检查、构建与类型生成成功；安装时全依赖审计及最终生产依赖审计均 0 漏洞。

| 验收项 | 证据 |
|---|---|
| 未登录 | 工厂列表、设备 ID、me、PATCH 均 401；错误含 correlationId |
| 角色能力 | Viewer/Operator/MaintenanceEngineer 能读；创建工厂、位置、设备和更新设备均 403；Admin 可管理 |
| 范围交集 | 仅 North 的 Admin 在工厂列表只见 North；South 工厂/位置/设备 ID、列表、创建和编辑均 404；跨厂 locationId 过滤不能泄露数据 |
| 防伪 | 登录、业务 PATCH、退出缺令牌拒绝；伪造令牌、其他用户令牌拒绝；真实 Cookie+令牌登录/退出成功 |
| 关系 | 位置自引用、祖先循环、跨厂父节点均 400；并发互设父节点仅一个成功；跨厂设备移动 400，同厂移动成功；原 DB 外键测试直接验证跨厂写入失败 |
| 并发 | 设备同版本并发 PATCH 仅一个成功，另一 409；工厂/位置旧版本也 409，版本只递增一次 |
| 审计事务 | 成功记录 actor/工厂/资源/before/after/UTC/correlationId；故障在 SQL 已执行、提交前注入，设备和审计共同回滚；版本不变 |
| 归档 | 工厂/位置有活跃设备或子位置时拒绝；按设备→位置→工厂归档后仍可按 ID 读取，所有行与 6 条测试审计保留 |
| 重复初始化 | 原资产人工修改保留；账号密码哈希、禁用、角色删除、工厂撤权均保留；固定种子不重复；真实 Worker 命令连续执行两次成功 |
| 会话权限变化 | 已有 Cookie 在范围撤销后读取/写入 404；角色降为 Viewer 后写入 403；禁用账号后 me 401 |
| 健康边界 | 登录 Cookie 下改为真实不可达数据库连接，API live=200/ready=503；API 在线实测 live/ready=200；最终 Worker 实测不可达数据库时 live=200/ready=503；两个 Host 均显式将探测置于身份校验之前 |

防伪实现依据 [ASP.NET Core 官方文档](https://learn.microsoft.com/aspnet/core/security/anti-request-forgery)，JSON 写接口显式调用 IAntiforgery.ValidateRequestAsync，包括登录与退出。

## 实际页面验收

通过 Codex 浏览器在真实 Vite→API→PostgreSQL 路径中操作，无请求替身：

1. Admin 登录 → North Plant → Utilities → Cooling Pump P-101 → 编辑制造商，保存后版本 2；详情显示 Fictional Works，数据库产生审计。
2. 两页面都从版本 2 编辑；第二页保存型号 Demo Pump 成功到版本 3，第一页提交旧版本显示 409 提示并保留输入，刷新后读取版本 3。
3. 退出成功；Viewer 登录只见 North，无新增/编辑按钮，能读设备详情；工程师只见 South 与 F-201。
4. 停止 API 后登录显示“服务暂时不可用”，密码输入清空，没有成功状态。
5. Admin 创建 Utilities 下的 Pump Room、编辑工厂描述、创建同厂 P-102、归档 P-102；位置树、详情与归档状态真实更新。
6. 连续两次 Worker 初始化后查询：P-101 仍保留制造商 Fictional Works、型号 Demo Pump、Version=3；新增位置/设备与 6 条 UI 审计均保留。

本地截图（忽略的 work 目录）：p12-admin.png、p12-viewer.png、p12-conflict.png、p12-offline.png。浏览器操作是本轮手动验收，没有声称建立了可在 CI 自动重放的 UI E2E 套件。

## 交付与限制

已实现本地身份、四角色与工厂范围、目录 CRUD（删除语义为归档）、同厂结构校验、version 409、事务审计、双厂及本地密码种子、真实 Vue 页面、生成的 OpenAPI 类型。所有按 ID 和写操作均检查范围；Admin 也没有范围绕过。创建工厂同事务写入创建者的显式授权。

- 未实现用户/授权管理 UI、密码重置、审计查询 UI、取消归档与生产静态托管。
- 未实现测量源、点位、告警、工单、文档、SignalR、OPC UA 或 AI；不存在历史业务表时只能验证目录/审计保留，未虚构未来历史数据验证。
- 工厂级事务锁只保护现有目录用例；新增数据源时须将其活跃状态纳入工厂归档校验。
- 业务库尚未应用本轮新增迁移；使用 README 的显式 Development 初始化命令升级并保留既有数据。生产部署的 HTTPS、数据保护密钥持久化与正式账号流程未验收。
- 测试数据库/schema 保留验收数据；本轮测试服务完成后停止，不向业务库切换或自动初始化。

提交：d42682e（P1.1 复盘修复）、b4114f3（后端目录/身份）、5b8de98（身份与 API 健康检查边界）、a29e562（Vue 工作台）、f23976c（OpenAPI 类型契约）；Worker 健康顺序及文档提交记录于 Git。未推送、未创建 PR。
