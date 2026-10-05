# P1.3 验收记录

日期：2026-10-05。初始 HEAD=f4c4fa5、工作区干净。先复核 README、PROJECT_SPEC、ARCHITECTURE、ROADMAP、DOMAIN_MODEL 和 P1.2 实现；按 [实施计划](plans/2026-10-05-p1-3-telemetry.md) 完成一台泵一个点位的纵向路径。

## 交付

- P-101 的 VIBRATION_RMS → Simulation → 标准化 → PostgreSQL 不可变读数与独立 LatestObserved/LastGood → 授权 API → Vue 趋势。
- Admin 点位、同厂模拟源和映射的新增/编辑/停用；version 冲突 409、防伪、实际角色与工厂范围校验；配置与 AuditLog 同事务。启用映射不能原地换来源/换算；最多一个启用映射；已有读数后点位单位/量纲不能原地修改。
- 采集前读取配置，提交前在工厂事务锁内检查当前配置/版本/归属；设备归档同事务停用点位与映射、分别审计并保留历史。工厂归档检查启用来源。
- 单 Worker 每秒读取，批次最多 500、顺序背压、最多 3 次重试；故障记录失败类型及缺口提示，不输出密码/样本/连接参数。无无限队列或自动替换来源。

## 自动验证

最终 `dotnet test PlantOps.sln --no-restore`：**34 passed / 0 failed / 0 skipped**，其中 20 项真实 PostgreSQL 集成测试，14 项无需数据库。

| 验证 | 结果 |
|---|---|
| P1.2 原有权限/范围/防伪/循环/并发/审计/健康回归 | 全部通过；健康检查带 Cookie、不可达数据库仍 live=200/ready=503 |
| 采集并发重复批次 | 总插入 1、重复 1；唯一事件键在 PostgreSQL 生效 |
| Good → Bad → Uncertain、迟到/未来 | 历史均保留，Bad 数值 null，Uncertain 保留非可信值；LatestObserved 不被迟到/未来回退，LastGood 保留 Good |
| NaN/±Infinity、单位转换、时间来源回退、新鲜度 | 规则测试通过；无效数值不成为可信值，Source/Server/Receive 区分 |
| 入库事务 SQL 执行后注入失败 | 读数与投影均回滚，重试可提交；配置/范围变化拒绝旧批次 |
| 查询与配置权限 | 匿名 401；Viewer/Operator/MaintenanceEngineer 可读但配置 403；跨工厂列表、点位/趋势/映射查询和写入 404；资产与点位 ID 不匹配 404；缺防伪 400 |
| 配置版本/关系 | 点位并发一个 200/一个 409；来源/映射旧版本 409；同点位双启用、跨厂来源、启用时换来源键 400；数据库独立拒绝跨厂点位外键 |
| 配置审计/归档 | 配置 SQL 后失败不留下修改/审计；归档停用点位/映射并保留读数与审计，后续采集拒绝 |
| 种子重复执行 | 固定点位唯一，保留人工名称/停用修改；原有账号密码/角色/授权与资产修改保留回归通过 |
| 确定性 Simulation、分钟统计 | 固定时刻重复结果相同；4→12.6→Bad→Uncertain→6；仅有效 Good 进入统计，空桶 null、质量计数/覆盖率/缺口可见 |

`dotnet restore --locked-mode` 通过；解决方案构建 **0 warning / 0 error**；EF `has-pending-model-changes` 无变化。`npm ci` 通过、审计 0 vulnerabilities；`npm run build` 包含类型检查并成功。由实际 Development OpenAPI 重新生成、提交前端 API 类型。未引入新依赖。

## 实际运行与页面

只使用 `plantops_tests`，UI 使用独立 `p13_ui_20261005` schema，迁移与 Development 初始化成功并重复执行。根目录本地配置读取演示密码，没有打印/提交密码，初始化不会重置已有密码。

实际启动 API:5080、Worker:5081、Vite:5173；两个 Host live/ready 均 200。实际 Worker 在第一次停采时写入 **499 条：Good 419 / Bad 40 / Uncertain 40**，没有通过页面伪造读数。

浏览器验证：

1. Admin 登录 → North → Utilities → P-101，看到 mm/s、当前映射、质量、有效/接收时间与真实曲线。
2. Bad 当前观测显示无可信数值；LastGood 保留 12.6 并显示陈旧，曲线在坏值/不确定值处断开。
3. Admin 将点位名称改为 `Vibration RMS / UI verified`，点位 version=2；数据库核实 1 条 MeasurementPoint Update 审计。
4. 停 Worker 后超过 5 秒，刷新看到 Good · 陈旧、不可作为当前可信值，LastGood 标注仅供历史参考；重启恢复新鲜状态，停机区间保留缺口。
5. 分钟粒度/最近 24 小时正常显示；明细有 60/0/0、100% 完整分钟，以及 40/10/10、67% 有缺口分钟，空桶 0% 且无统计值。
6. 关闭 API 时刷新，显示服务失败并隐藏旧测量状态；恢复后可重新读取。
7. 退出 Admin、登录 Viewer，仅 North 可见；可浏览点位/趋势，无测量配置入口。后端拒绝配置的证据来自集成测试。

本机截图保存在 Git 忽略的 `artifacts/p13/telemetry-admin.png`、`telemetry-stale.png`、`telemetry-viewer.png`，用于本轮交付；不是仓库可移植测试资产。

## 环境问题与处理

首次基线执行时 Docker 尚未启动，数据库测试连接失败；启动 Docker Desktop 和 Compose 后重跑全部通过。首次 UI 专用 schema 未建立导致初始化失败，创建该测试 schema 后成功；未改业务库。一次开发服务运行时构建遇到 Windows DLL 占用，停止 API/Worker 后构建及测试通过。最终结果不把这些失败当作通过。

复核业务库：仍仅 `20261003071706_InitialAssetDirectory` 迁移，Plant/Location/Asset 各 1 行。本轮未向业务库应用 Identity 或 Telemetry 迁移，也未重置本地凭据。

## 限制与下一阶段

本阶段未实现告警/工单/OPC UA/AI/SignalR/Outbox。下一步是 P1.4 的持续时间、回差、告警/事件和确认流程。

分钟统计按需计算；原始表未分区，没有保留清理、持久化分钟投影或预填一小时历史；这些属于 P1.7。单 Worker/本机开发边界，尚未进行 100 点 × 1 Hz × 30 分钟负载实验或查询 p95 性能测量。页面 5 秒轮询，不宣称 <2 秒推送。查询窗口 ≤24h、原始 ≤10,000 行、分钟输入 ≤100,000 行/≤1,440 桶；超限返回明确 400，不静默截断。

采集故障或过载只有限重试，失败批次可能丢失，日志和趋势缺口显式体现；没有离线持久队列/完整 IngestionGap。源参数当前仅固定 Simulation schema，连接健康与协议配置管理在 P2 扩展。历史读数不能经业务 API 修改，直接数据库维护权限仍由部署控制。已有用户生命周期、生产托管等 P1.2 限制继续适用。
