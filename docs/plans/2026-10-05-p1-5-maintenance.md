# P1.5 Maintenance Implementation Plan

**Goal:** 完成同厂同设备告警到工单关闭的人工维护闭环。
**Architecture:** Domain 状态规则、Application 契约、Infrastructure EF 事务服务；复用工厂 advisory lock、Identity 实时角色/范围验证、version 与 AuditLog。维修历史直接查询 Completed/Closed 工单和备注。
**Tech Stack:** .NET 10 / EF PostgreSQL / Vue 3。

1. 增加工单、不可变备注与状态迁移规则；明确取消/返工原因和完成结果。
2. 增加同厂复合外键、唯一编号、索引和迁移；不迁移业务库。
3. 实现读取、创建、迁移、备注、可指派维护人员 API；每次写入在锁内重授权。
4. PostgreSQL 集成验证角色、范围、CSRF、版本、关联、SQL 后失败回滚及持久恢复。
5. 增加工单工作台及设备维修历史，实际浏览器验收；更新文档并提交已验证阶段。

设计选择：状态迁移采用显式命令，拒绝任意 PATCH 状态；取消/返工原因存不可变备注并进入审计；所有工单写操作限制 Engineer/Admin。终态禁止追加备注，保持关闭后历史稳定。编号使用服务端 UUID，避免并发编号争用。指派列表仅返回该厂启用且具维护角色的用户。
