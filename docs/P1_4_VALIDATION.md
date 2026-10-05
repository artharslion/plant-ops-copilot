# P1.4 验收记录

日期：2026-10-05。基线为 P1.3 `856cfa4`，工作区起始干净。本阶段仅完成确定性告警，不引入工单、协议源或 AI。

## 自动验证

`dotnet restore --locked-mode` 成功；解决方案构建 0 警告/0 错误；`dotnet ef migrations has-pending-model-changes --project src/PlantOps.Infrastructure --no-build` 无模型变化。后端 **51 项测试通过，0 失败、0 跳过**：27 项真实 PostgreSQL、24 项无需数据库。前端 `npm ci` 成功、0 vulnerabilities；`npm run build` 包含 Vue 类型检查并通过。Development OpenAPI 生成前端类型，无新增依赖。

- 严格阈值、持续时间、回差、零延迟；Bad/Uncertain/NaN/未来/陈旧、迟到/相同水位、过长样本间隔；即使规则 MaxGap 较长也不能跨过点位陈旧边界。
- 去重不会再次触发；连续高值只有一个 Active；同批恢复后再触发符合部分唯一索引，历史快照保留。
- 读数/最新值/运行状态/告警/事件/审计同事务，SQL 执行后的注入失败全部回滚；并发重试仅一个 Active；事件不可变。
- 未登录 401，Viewer 确认 403；Operator/Engineer/Admin 确认允许；所有角色范围取交集，跨厂列表/ID/事件/写入与撤权均拒绝；CSRF 缺失 400。
- 首次旧版本确认 409；并发确认及重试保留首次人/时间；确认不恢复；恢复不抹去确认；确认 SQL 后失败无业务或审计残留。
- Admin 规则配置范围/阈值/间隔/版本校验；Active 拒绝修改、停用和设备归档；恢复历史保留且可归档；跨厂规则数据库 FK 拒绝。
- Worker 重启清零 pending，Active 保留；映射、点位、来源及规则版本变化切断旧计时；重复种子保留已有配置；游标分页不重复，非法输入明确 400。

## 实际运行与页面

仅独立 `plantops_tests`，页面专用 `p14_ui_20261005` schema。Development 初始化与重复初始化均成功；本地演示密码未输出或提交。实际 API:5080、Worker:5081、Vite:5173，Worker 自动产生并恢复 5 个告警，有对应审计。

浏览器验证：匿名告警入口进入登录；Operator 登录、进入 North/P-101 告警，点击确认。12:49:18 确认不改变 Active；12:49:25 满足可信低值持续时间后 Recovered，保留确认人/时间，事件顺序为 Raised→Acknowledged→Recovered。Bad/Uncertain 时显示当前状态无法验证，不提前恢复。Viewer 只有 North，详情只读且无确认/配置；Admin 新增停用规则并编辑至 version=2，数据库核实 Create/Update 各一条审计；重复初始化保留该名称、停用状态和版本。关闭 API 后刷新显示服务不可用并隐藏未验证状态。

截图位于本机 Git 忽略的 `artifacts/p14/alarm-recovered.png`、`alarm-viewer.png`、`alarm-rule-saved.png`、`alarm-offline.png`，不作为可移植测试资产。页面使用 5 秒 HTTP 轮询；计数仅为已加载记录，配置期间暂停刷新。

复核业务库仍仅 `20261003071706_InitialAssetDirectory` 迁移，Plant/Location/Asset 各 1 行；本轮没有迁移业务库、覆盖本地凭据或删除测试数据。两个错误的 EF 工具 startup-project 尝试因 Host 没引用 Design 包失败，随后按 README 的 Infrastructure 项目命令成功；未为此添加依赖。

## 边界与后续

单 Worker、单机开发部署；没有人工强制恢复/删除告警。活动告警的规则停用及设备归档被拒绝，避免失去恢复来源。读取范围每请求复查。未完成 P1.5 工单、P1.6 文档 ACL/SignalR/Outbox、P1.7 分区/聚合/保留及规模验收；继续依次实现。尚不声称达到 <2 秒推送或规模性能目标。原始读数与告警事件在业务路径不可变，直接数据库管理员维护仍由部署权限控制。
