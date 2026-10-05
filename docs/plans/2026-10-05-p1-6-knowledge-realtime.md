# P1.6 Knowledge and Realtime Implementation Plan

**Goal:** 授权文档的不可变版本、发布与下载，以及提交后实时通知与重连补查。
**Architecture:** Application 文档/文件端口；Infrastructure 校验受限内容、受控文件键与 EF 元数据/审计事务。DbContext 在业务 SaveChanges 中写 Outbox，API Dispatcher 领取已提交事件、发送后标记，允许重复。Hub 仅通知、服务端确定工厂范围且持续复查用户与授权；HTTP 为权威。
**Tech Stack:** .NET 10 SignalR / PostgreSQL / Vue / Microsoft SignalR client。

1. 实现 Document/Version/Outbox、文件校验与受控存储；迁移只用于测试库。
2. 文档创建/追加版本/发布/撤回/ACL/归档 API；下载实时复查工厂与 ACL、版本发布状态，禁止公开存储路径。
3. 事务 Outbox 与重试 Dispatcher；Hub 连接注册、范围校验/撤权断开；测量通知每秒合并，业务迁移事件保留。
4. Vue 文档管理与设备入口；连接状态、eventId/version 去重、重连 HTTP 快照、离线轮询。
5. 测试上传校验/ACL/旧链接/冲突/回滚、Outbox 提交边界与重试；浏览器上传/发布/下载/重连，更新验收并提交。

旧版本文件内容永不覆盖；仅 Published 且文档当前仍发布才可下载。撤回当前文档后历史链接一并拒绝；重新发布后仍逐版检查状态。PDF 在 Phase 1 不承诺索引，标 NeedsText，文本只校验 UTF-8。文件先写受控键，数据库失败删除本次文件，不删除已提交文件。通知只带 ID/version；文档通知为工厂资料变更提示以避免暴露受限文档 ID。
