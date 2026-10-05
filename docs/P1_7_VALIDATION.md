# P1.7 验收记录

2026-10-05，基线 `3f8739c`。P1.7 已完成，P1 整体业务闭环验收通过。最后后端回归 74 项全部通过、0 跳过（29 纯测试、45 PostgreSQL），构建 0 警告/0 错误，EF 无 pending；前端类型/构建与去重测试通过，OpenAPI 类型已更新。Rollup 保留上游 SignalR 两条 PURE 注释非阻断提示。

## 实现与数据安全

- 原始读数按 UTC EffectiveDate 日分区，PK/去重包含日期；数据库检查日期与 EffectiveAt 一致。迁移保留旧读数、索引和同厂外键。测试库 p15_ui_20261005 实际非空升级前后均为 899 读数、1 工单、1 文档；迁移标记 17 个 dirty 分钟。
- 采集、分区创建、分钟聚合与保留使用维护事务锁；再取得工厂锁、重新校验配置并批写。新读数、最新投影、规则、审计、Outbox、dirty 与分区 Revision 同事务提交。相同日期/点位/事件键重试不重复写。
- Worker 启动及每分钟重算已完成的 dirty 桶；只统计有效 Good 的 min/max/avg，保存各质量数量、异常、SampleCount、覆盖率、时间缺口。迟到数据使已计算分钟重新 dirty；接口在重算前显示缺口，不提供过期统计。
- UTC 每日默认原始 7 天、分钟 90 天。清理先检查 dirty=0、Revision=AggregatedRevision、原始与分钟 SampleCount 总数一致；任一不符保留整分区。DROP 与审计/登记同事务，SQL 后失败可回滚 DDL。按整日保留，边界日最多额外一天。
- 过期迟到样本拒绝入库；已有分钟历史而日分区已退役时，即使延长保留窗口也拒绝重建分区，防止覆写保留统计。已采样点位的单位/量纲在原始过期后仍由最新投影/分钟历史保护。告警/事件/工单不级联删除；AlarmDto 批查触发读数可用性，过期时 UI 明确显示“原始样本已过保留期”，保留触发值/阈值/事件；最新和 LastGood 值/时间摘要保留，过期原始 ReadingId 仅作追溯标识。
- Development 初始规模为 2 工厂、6 位置、20 设备、100 点位、2 Simulation 来源、10 篇原创虚构指南；首次一小时历史为 360,000 读数。重复初始化保留名称、启停、ACL、密码、授权撤销和业务变更；不会重填已有最新投影的历史。文档默认目录改为同用户 LocalApplicationData/PlantOpsCopilot/documents，API/Worker 共用；显式目录仍优先。

## 浏览器闭环

内置浏览器实际登录 admin，在 North/Utilities/P-101 查看实时振动与质量 → 告警 → 确认 → 设备关联工单（自动带入 alarmId） → 指派 → 草稿/待执行/执行中 → 追加备注 → 填写结果 → 完成/关闭。工单 `WO-3f366b034b5e41df9393339011ab0ce1` 终态 v6，维修历史显示同一记录。关联告警 `adc08fb4-bc09-4b98-a544-f6874c7f4fb8` 于 14:17:25 由规则恢复、14:17:30 人工确认；工单 14:19:41 关闭，三件事独立。截图 artifacts/p17/golden-path-closed.png 已检查。

## 负载与容量

独立测试 schema p17_ui_20261005，所有运行服务只连接 plantops_tests。Intel Core Ultra 7 155H / 22 逻辑处理器 / 31.5 GiB RAM / Windows 11 企业版，.NET 10.0.112、Node 22.23.1、Docker PostgreSQL 17。开始时 D 盘剩余 41.3 GiB。100 启用模拟点位每秒采集，五个独立 Cookie/SignalR 客户端（viewer/operator/engineer/admin/admin），每 5 秒读列表、设备概览和 24 小时/1,440 桶趋势；不是浏览器自动化脚本。

复现时，先确保 plantops_tests 已存在，首次创建一个独立 schema；以下配置须在初始化、API、Worker 三个终端都使用，`Search Path` 与文档根目录相同。正常运行不自动迁移。先停止本仓库运行进程再构建，避免 Windows 文件占用。

```powershell
. ./deploy/Use-LocalDatabase.ps1
$env:ConnectionStrings__PlantOps = $env:ConnectionStrings__PlantOps.Replace('Database=plantops;', 'Database=plantops_tests;').Replace('Command Timeout=5', 'Command Timeout=60') + ';Search Path=p1_acceptance_20261005'
$env:Documents__StorageRoot = Join-Path (Get-Location) 'artifacts/p1-acceptance-documents'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
# 仅首次执行；已存在时保留 schema 和数据
docker exec plantops-database-1 psql -U plantops -d plantops_tests -c 'CREATE SCHEMA IF NOT EXISTS p1_acceptance_20261005;'
dotnet run --project src/PlantOps.Worker --no-build --no-launch-profile -- --initialize-db
# 分别在不同终端运行（均设置上述配置）
dotnet run --project src/PlantOps.Api --no-build --no-launch-profile --urls http://127.0.0.1:5080
dotnet run --project src/PlantOps.Worker --no-build --no-launch-profile --urls http://127.0.0.1:5081
# 另一个终端，加载本地 Demo 密码；默认五客户端、30 分钟
. ./deploy/Use-LocalDatabase.ps1
node web/scripts/benchmark-p1.mjs
```

实测 UTC 06:07:32.140–06:37:32.153（1,800.013 秒），每客户端完成 354 轮热查询，五客户端均无失败/越权/重复通知。p95 如下（ms）：

| 客户端 | 列表 | 概览 | 24h 分钟趋势 | 推送后 HTTP 补查 |
|---|---:|---:|---:|---:|
| viewer | 37.38 | 32.66 | 33.28 | 306 |
| operator | 37.52 | 32.98 | 36.01 | 306 |
| engineer | 37.38 | 31.95 | 35.31 | 307 |
| admin A | 38.26 | 31.71 | 33.94 | 306 |
| admin B | 39.35 | 32.65 | 34.03 | 305 |

推送后补查从 Outbox 事务保存前时间计到 HTTP 读取结束，作为提交后延迟的保守上界；并不冒充页面呈现。另在真实内置浏览器读取页面上记录的 Vue nextTick 呈现延迟：1,049 个独立观测，ReceivedAt→最新观测呈现 p95=541 ms、最大=1,745 ms，接收时刻早于提交，同样是保守上界，达到 UI p95<2s。

去除运行首尾非完整秒，UTC [06:07:33,06:37:32) 共 1,799 秒×100 点=179,900 槽；实际与 distinct 均为 179,900，missing=0。基准结束前累计 657,600 原始行、11,000 分钟、299,059 Outbox（待发送 0）。此前启动/回归阶段已有 300 条显式 gap，均不在本次连续 30 分钟窗口；保留不掩盖这些历史缺口。原始报告 artifacts/p17/benchmark-results.json、browser-latency-results.json、collection-completeness-storage.txt，不写入凭据/Cookie。

最后停止 API/Worker、重复初始化：原始仍 657,600 行、工单仍 Closed/v6、文档 10、历史 seed marker 1，没有补填/覆盖。随后重启 PostgreSQL 容器（保留卷）并重启进程：两 Host ready=200、system=P1.7/0.7.0；工单关联/备注/结果、文档及 10 个 Published 版本保留，100 最新投影重新收到观测。停采到恢复的 100 点间隔新增明确 gap，总数 400；没有造数补齐。原浏览器页在用户切换页面时关闭，恢复验证在新页重新登录完成，因此不把本次说成同一旧页自动重连（该能力已在 P1.6 验证）。新页显示实时已连接、工单关闭与维修历史、持久分钟缺口和已发布文档，点击下载成功；浏览器保存文件路径仍不作承诺。截图 artifacts/p17/recovery-closed-and-document.png。

查询计划实测：原始 15 分钟查询只访问当日日分区的 PointId/EffectiveAt 索引，899 行执行 3.343 ms；分钟投影读 PK 索引，98 行执行 0.165 ms。24 小时接口仍返回至多 1,440 桶，缺历史的桶为空白并带 gap。

存储阶段采样：568,200 原始行，heap 176,545,792 B、index 181,551,104 B，总计 358,178,816 B；9,500 分钟 1,589,248 B；209,277 Outbox 42,385,408 B，待发送 0。按本次行宽外推 60,480,000 原始行约 35.5 GiB，90 天分钟约 2.0 GiB，七天 Outbox 约 11.4 GiB，另需 WAL、备份与余量。Outbox 当前保留已发送记录，因此长期增长还须管理；未伪造七天满量数据。当前磁盘余量不能证明满量容量达标，进入 P2 前必须扩容并验证完整七天容量及 Outbox 历史管理。

## 范围与限制

所有数据库/进程测试保留数据库、schema、卷与文档数据。业务库 plantops 仍仅 InitialAssetDirectory 迁移；本阶段没有替用户升级业务库。PDF 为 NeedsText，仅保存文件，不解析/索引；没有 OPC UA/AI。上传文件与数据库应共同备份；异常终止可能留下未提交孤立文件。单 Worker/API Dispatcher，采集有限重试；无法补齐的时段持久记录 IngestionGap，分钟覆盖率/曲线缺口同时可见。

补充验证：跨保留期 API 测试使用保留的独立随机 schema，避免重复测试创建旧分区时混入上一轮分钟数据；Realtime:Enabled 在任务执行时再次检查，防止测试 Host 配置晚覆盖产生后台派发竞态。新增告警原始过期 API 验证与扩大保留窗口保护验证通过。
