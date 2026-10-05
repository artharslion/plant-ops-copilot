# P1.5 验收记录

2026-10-05，基线 `092f007`，工作区干净。工单状态机、同厂同设备告警关联、维护人员指派、不可变备注及维修历史已实现。

全回归 59 项通过、0 跳过；追加关联/并发/撤权验证后工单 9 项全部通过（5 领域、4 PostgreSQL），当前测试总数 60。Vue 类型及 Vite build 通过，EF 无 pending model changes。

- 创建默认 Draft；显式 Open→InProgress→Completed→Closed；完成必须有结果。取消与 Completed→InProgress 返工需原因，原因保存为备注；返工清空完成时间/当前结果，旧结果留在审计。Closed/Cancelled 终态不可修改。
- Engineer/Admin 写，Viewer/Operator 拒绝；跨厂按 ID、列表、备注与迁移 404；撤权即时生效；CSRF 缺失 400；旧版本/并发写 409。指派须启用、同厂、有维护角色。
- 同设备告警关联不改变告警状态；数据库复合 FK 保证归属。创建/迁移/备注 SQL 后注入失败不留业务、备注或审计残余。
- 页面独立 plantops_tests.p15_ui_20261005：engineer 仅 South；F-201 创建并指派，打开、执行、备注，无结果完成被拒绝，填写结果完成并关闭到 v6，维修历史补查成功。截图 artifacts/p15/work-order-closed.png 已检查。新 DI scope 回读持久状态；完整进程/数据库重启 Golden Path 在 P1.7 执行。

业务库仍只有 InitialAssetDirectory，未迁移或改动。编号为 WO-UUID，工厂内唯一；不提供人工改号/重新指派/历史编辑。列表稳定分页，备注按时间/ID 排序。P1.6 文档/Outbox 与 P1.7 规模验收继续实施。
