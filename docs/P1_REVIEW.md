# Phase 1 复盘与 Phase 2 进入条件

日期：2026-10-05。审查基线：`main / f296da7`；P1.5 `71fd2e3`、P1.6 `3f8739c`、P1.7 `f296da7`。本轮只进行代码/测试/记录复盘与计划编写，不修改应用、依赖、迁移或数据库，不启动 OPC UA/AI。

## 结论与证据边界

P1 已完成本机开发规模的业务闭环：模拟观测 → 确定性告警 → 人工确认 → 关联工单 → 备注/结果 → 关闭，文档版本、权限和实时通知也已验收。可以保留当前演示；**不能把 P1 的短期运行结果视为七天满量容量通过，也不能只注册一个 OPC UA Adapter 就宣布 P2 完成**。

本报告将“当前实现问题”“已知运行门槛”“下一阶段尚未实现的契约”分开表述。以下代码行号均针对 `f296da7`，链接打开文件，正文给出定位行号；未在本轮构造故障的项目明确标为静态证据或待验证。严重性中的高/中/低是复盘优先级，与路线图 P1/P2 编号无关。

- 本轮读取五份设计基准、阶段验收、关键实现与测试，并核对被忽略的 P1.7 原始报告；没有重做 30 分钟负载、浏览器闭环或进程/数据库重启。
- 74 项后端测试通过、0 跳过（29 纯/45 PostgreSQL）、构建 0 警告/错误、EF 无 pending、前端构建/去重通过，是 [P1.7 验收](P1_7_VALIDATION.md) 的既有记录，不冒充本轮新运行结果。
- `artifacts/p17/benchmark-results.json`、`browser-latency-results.json`、`collection-completeness-storage.txt` 已回读；截图和日志保留在本地被忽略目录。文档本身不嵌入密码、Cookie 或连接串。

## 发现：按严重性排列

### R1 · 高 · 七天容量与 Outbox 历史管理仍是阻断门槛

**证据：**[P1.7 验收](P1_7_VALIDATION.md) 第 20、58 行记录开始时 D 盘余量 41.3 GiB，以及测量后的外推：原始七天约 35.5 GiB、90 天分钟约 2.0 GiB、七天 Outbox 约 11.4 GiB，合计约 48.9 GiB，尚未包含 WAL、备份和安全余量。本轮 D 盘快照为 46.12 GiB，且 D 盘余量不等于 Docker 数据卷所在文件系统可用空间。原始报告只填充几十万行，未填充 6,048 万行。

[OutboxDispatcher.cs](../src/PlantOps.Infrastructure/Operations/OutboxDispatcher.cs) 第 10、18–19 行只领取未发送行并标记 `DispatchedAt`；[MeasurementMaintenance.cs](../src/PlantOps.Infrastructure/Telemetry/MeasurementMaintenance.cs) 第 71–95 行只清理测量/分钟，没有已发送 Outbox 清理。[KnowledgeModel.cs](../src/PlantOps.Infrastructure/Persistence/KnowledgeModel.cs) 第 22 行仅有未发送部分索引。即使 pending=0，已发送历史仍增长。

**影响/范围：**长期默认保留可能耗尽磁盘，影响采集与正常业务；这是已知容量缺口，不是已观察到的数据库损坏。开发负载 p95 通过不能推断完整七天容量通过。

**处理与验收：**先定位数据库卷/宿主文件系统，制定已发送 Outbox 保留策略，仅清理已发送且超过期限的记录；保留未发送/重试和业务审计。完成独立容量环境的七天满量、滚动清理、WAL/备份峰值与负载共存实测。进入条件见 [P2 计划 G0](plans/2026-10-05-p2-opcua.md#g0容量与历史管理门槛)。本轮没有清理任何数据。

### R2 · 高 · 当前 Worker 的有限批次循环不能直接承接持续订阅

**证据：**[MeasurementWorker.cs](../src/PlantOps.Worker/MeasurementWorker.cs) 第 11、25、29–37 行：关闭 `SimulationEnabled` 会退出整个采集 Worker；所有源依次 `await foreach`，只有达到 500 条或枚举结束才提交，没有 500 ms 超时 flush。[SimulationSource.cs](../src/PlantOps.Infrastructure/Telemetry/SimulationSource.cs) 第 24–35 行当前枚举一次配置后即结束，所以现有模拟模式不受长期等待影响。

**影响/范围：**静态确认的 P2 接入阻碍。若直接挂接不结束的 OPC UA 通知流，后续源及配置刷新被阻塞；100 点/秒仅按数量 flush 会等待约 5 秒，单点可能等约 500 秒；关模拟开关也会关掉 OPC UA。这些数字是代码推导，未做 OPC UA 实测，不是 P1 30 分钟试验中发生的故障。

**处理与验收：**P2.3 增加按源受控生命周期、独立模拟开关、回调有界缓冲、共享事务入库消费者及双触发批写。验证低吞吐首样本≤500 ms 开始提交、500 条立即 flush、两种源并存不饥饿、热停用及取消不泄漏任务。保留一个 Worker 实例，不扩大为分布式采集。

### R3 · 高 · 已有 gap 是恢复后推断，尚非完整损失账本

**证据：**[MeasurementIngestor.cs](../src/PlantOps.Infrastructure/Telemetry/MeasurementIngestor.cs) 第 88–89 行只有下一条非未来样本与最新观测相差 >1.5 倍间隔时新增 gap；[MeasurementWorker.cs](../src/PlantOps.Worker/MeasurementWorker.cs) 第 41–43 行失败只记录日志。[TelemetryEntities.cs](../src/PlantOps.Domain/Telemetry/TelemetryEntities.cs) 第 98–106 行已有可空 `To`，但没有恢复字段/丢弃数量。分钟 SQL 与趋势读取均未将显式 gap 区间作为独立输入：[MeasurementMaintenance.cs](../src/PlantOps.Infrastructure/Telemetry/MeasurementMaintenance.cs) 第 40–59 行、[TelemetryService.cs](../src/PlantOps.Infrastructure/Telemetry/TelemetryService.cs) 第 62–88 行。

**影响/范围：**P1 重启后 100 点间隔可记录，永久不恢复的源只会显示陈旧/空桶，尚不能查询一个立即打开的连接故障区间。P2 队列溢出、SDK Overflow、数据库离线和崩溃尾部即使剩余通知时间间隔不明显，也必须记录损失并中断规则计时。现有 gap 不足以证明这些情况已覆盖。

**处理与验收：**P2.3/P2.4 增加幂等打开/关闭的源与点位 gap、原因、受影响范围、已知丢弃数量/未知尾部标记；数据库离线时保留有界损失摘要，恢复后持久化。趋势、聚合和规则显式读取损失信息；证书/断线事件不以伪造 Bad 数值代替。崩溃只能保证“可能缺口”，不宣称离线无损。

### R4 · 中 · 修改采样间隔后，历史覆盖率可能按新配置重算

**证据：**[TelemetryService.cs](../src/PlantOps.Infrastructure/Telemetry/TelemetryService.cs) 第 120–132 行正确阻止已有历史的单位/量纲修改，但允许修改 `ExpectedIntervalMs`。[MeasurementMaintenance.cs](../src/PlantOps.Infrastructure/Telemetry/MeasurementMaintenance.cs) 第 41、53–59 行重算时 join 点位当前间隔，而 [MeasurementReading/MeasurementMinute](../src/PlantOps.Domain/Telemetry/TelemetryEntities.cs) 第 46–87 行未保存当时的采样间隔/通知模式。迟到读数会重新 dirty 旧分钟：[MeasurementIngestor.cs](../src/PlantOps.Infrastructure/Telemetry/MeasurementIngestor.cs) 第 97 行。

**可复现场景（静态推导，未本轮执行）：**历史每 2 秒一个 Good，完整分钟 30 个样本，原覆盖率 1；改为每秒采样，再给旧分钟补一个独立迟到样本，该分钟按新预期 60 重算为 31/60。未 dirty 的旧桶仍保持原值，导致历史解释不一致。原始曲线的断线容差也使用当前间隔：[TrendBuilder.cs](../src/PlantOps.Application/Telemetry/TrendBuilder.cs) 第 11 行。

**影响/范围：**历史采样覆盖和缺口语义，未发现原始值/单位被改写。P1.7 100 点固定 1 Hz 验收不受此问题影响。OPC UA 修正采样间隔与变化通知会放大这一问题。

**处理与验收：**P2.2 保存有生效区间的采样模式/修正间隔快照，按样本时刻的配置计算；无法证明旧配置时显示覆盖率未知，不假装完整。对已有 P1 历史按可证明的配置迁移；不直接把所有旧记录归为当前间隔。验证改间隔、同分钟切换、迟到重算、原始过期后的历史查询。

### R5 · 中 · 协议质量/时间语义尚需 Adapter 与管道共同补齐

**证据：**[SamplePolicy.cs](../src/PlantOps.Application/Telemetry/SamplePolicy.cs) 第 21–36 行按可空时间优先级选择 EffectiveAt，可信性看 Good/有限数值/未来异常，未识别零/无效协议时间或 Overflow 信息位；[TelemetryContracts.cs](../src/PlantOps.Application/Telemetry/TelemetryContracts.cs) 第 6–15 行是模拟配置/样本契约，未带连接健康、修正订阅参数或 gap 信号。[TelemetryService.cs](../src/PlantOps.Infrastructure/Telemetry/TelemetryService.cs) 第 142–178 行来源创建/映射验证仍限于 Simulation/Vibration/mm/s。

**影响/范围：**这是 P2 尚未实现的设计要求，不能据此称 P1 处理过真实 OPC UA 状态码。若 Adapter 仅把状态高位映射成 Good，Good+Overflow 仍可能驱动规则计时，造成跨未知区间持续性判断。

**处理与验收：**P2.1/P2.2 固定数值类型支持表、原始类型/值事件键、时间缺失与状态位解析；P2.4 以独立 gap/连续性信号重置 pending。保存完整 uint 原码与源/服务器时间，不把 Overflow 简单丢码，也不擅自改写源时间。Bad/Uncertain/陈旧均不能清除 Active。

### R6 · 中 · 大容量维护与采集互锁尚未被规模试验证明

**证据：**[MeasurementMaintenance.cs](../src/PlantOps.Infrastructure/Telemetry/MeasurementMaintenance.cs) 第 13、38、76–85 行：聚合/清理与入库共用全局维护事务锁，清理核对 raw count 与分钟 SampleCount；[MeasurementIngestor.cs](../src/PlantOps.Infrastructure/Telemetry/MeasurementIngestor.cs) 第 44 行取得该锁。安全顺序正确，但满日 864 万行时的核对、积压分钟重算与采集等待未有满量报告。

**影响/范围：**性能风险待实测，不认定死锁或必然丢失。几十万行的查询计划和热请求 p95 不能覆盖保留任务执行时的采集延迟/队列占用。

**处理与验收：**G0 和 P2.5 测量维护耗时、锁等待、队列水位、落库延迟、blocked 分区和 raw/aggregate 一致性；先测再决定分批聚合/核对策略。保留安全检查不得为了性能跳过。

### R7 · 低 · 部分架构文字仍保留旧阶段现状

**证据：**[ARCHITECTURE.md](../ARCHITECTURE.md) 基线第 92 行描述 P1.6 文档存储时仍称开发默认在 API 输出目录，而实际 [LocalDocumentFileStore.cs](../src/PlantOps.Infrastructure/Knowledge/LocalDocumentFileStore.cs) 第 10–17 行与 P1.7 记录采用用户 LocalApplicationData；[DOMAIN_MODEL.md](DOMAIN_MODEL.md) 基线第 142 行的 P1.2 历史段仍写“无实时连接”。本轮已澄清，引用描述的是审查基线。

**影响/处理：**时间标记不足可能误导运维。当前复盘将导航与这两处状态澄清同步修订；既有验收记录按当时事实保留。孤立文件清理、生产用户生命周期、HTTPS/密钥/联合备份恢复是已记录部署边界，不凭本次复盘扩大为生产改造。

## P1 交付核对

| 能力 | 实现与测试证据 | 实际验收及限制 |
|---|---|---|
| 骨架/身份/目录 | Identity + 真实角色/工厂交集；目录事务锁、version 与 AuditLog；`AssetManagementTests`、`DatabaseFoundationTests`、`HealthEndpointTests` | P1.2 浏览器登录、角色范围、目录编辑、双页面 409；健康检查区分 live/ready。非生产用户管理 |
| 统一模拟遥测 | `SimulationSource`、`SamplePolicy`、`MeasurementIngestor`；`TelemetryTests`、`SamplePolicyTests` | Worker 实际落库，Bad/Uncertain 不覆盖 LastGood，未来/乱序不回退；只有数值振动模拟映射 |
| 确定性告警 | `ThresholdEvaluator`、`AlarmBatchEvaluator`；`ThresholdEvaluatorTests`、`AlarmTests` | 阈值持续/回差、独立确认与恢复；重启清 pending，保留 Active；没有 OPC UA A&C |
| 维护闭环 | `WorkOrderService`、`WorkOrderTests`（9 工单用例） | P1.5 South 工程师闭环；P1.7 North/P-101 关联告警工单 Closed/v6；结果、备注与维修历史可回读 |
| 文档/实时 | `DocumentService`、受控文件路径、ACL；`DocumentTests`、`OutboxTests`（合计 7）、前端去重 | P1.6 实际发布/下载反馈、API 断线恢复；P1.7 原创十文档；未证明浏览器文件保存路径。PDF NeedsText，无解析/索引 |
| 聚合/保留/恢复 | `MeasurementMaintenanceTests` 5 用例；`TelemetryTests` 原始过期/单位保护、告警快照可用性测试 | 非空迁移 899 行保持；清理失败事务回滚；重复初始化不重填；进程+数据库重启保留工单/文档/最新投影 |

测试数量是全套最终 74 的不同子集，不将阶段累计数量相加。覆盖了授权、防伪、乐观并发、SQL 后故障回滚、去重和状态机；没有代码覆盖率报告，也没有针对真实 OPC UA Server 的测试。

## 负载证据与允许的结论

| 观测 | 结果 | 可以证明 / 不可以外推 |
|---|---|---|
| 五个独立 Cookie/SignalR 客户端，100 点 1 Hz 30 分钟 | 完整 UTC 秒窗口 1,799×100=179,900；实际=distinct；missing=0；各客户端无失败/越权/重复 | 证明该窗口完整；不代表所有历史或断线期间无损。首尾非完整秒排除 |
| 热列表/概览 | 最差列表 p95 39.35 ms、概览 32.98 ms | 当次开发规模 <500 ms；不是七天满量、冷启动或保留争锁结果 |
| HTTP 推送后补查 | p95 305–307 ms | Outbox 保存前时间到 HTTP 结束；不能称页面呈现时间 |
| 真实浏览器 1,049 观测 | ReceivedAt→Vue nextTick p95 541 ms、最大 1,745 ms | 接收到呈现是提交后延迟的保守上界，达到 <2 s；不是五个浏览器同时绘制测试 |
| 重启与闭环 | Closed/v6、关联/备注/结果、10 Published 版本保留；恢复后新增 100 点 gap | 证明已提交业务持久性；P1.7 恢复用新页，不称同一旧页自动重连 |
| 查询计划 | 当日原始索引 899 行 3.343 ms；分钟 PK 98 行 0.165 ms | 证明该数据量下使用索引/分区；不证明七天规模 |

已有 gap 在连续负载窗之外保留，未通过删记录“美化”完整性。累计行数与存储阶段采样量不同：最终报告 657,600 raw / 11,000 minute / 299,059 Outbox；行宽外推使用较早 568,200 / 9,500 / 209,277 采样。两组不可混算。

## 进入 P2 的决策

1. **保留 P1 演示，进入 P2 计划状态。** SDK/许可是 P2.1 的固定版本 spike，不表示已选定可再分发产物；隔离且有预算的 spike 与 G0 调查可进行，持续采集/满量测试需先满足容量门槛。
2. **先解决 R1 的容量/历史管理，再按 P2.1→P2.2→P2.3→P2.4→P2.5 推进。** R2/R3/R5 是必须在各任务完成时关闭的接入缺口，R4 是历史语义问题，R6 用满量试验判断是否需要优化。
3. **数据边界保持：**只允许 `plantops_tests` 独立 schema 验证，保留既有测试 schema、卷、演示文档；业务库 `plantops` 未获升级授权。当前 API 5080、Worker 5081、Vite 5173 与 `p17_ui_20261005` 演示不为复盘停用。

可执行任务、文件、门槛和测试矩阵见 [Phase 2 计划](plans/2026-10-05-p2-opcua.md)。本报告没有替代或扩大项目规格。

## 本轮文档增量验证

2026-10-05：七份新增/修改 Markdown 的 64 个本地链接目标存在，代码围栏成对，Git 空白检查通过；只修改文档。未重建后端，使用既有产物 `--no-build --no-restore` 定向运行 SamplePolicy、ThresholdEvaluator、HealthEndpoint 与初始化环境保护测试，23 项通过、0 跳过；前端去重 1 项通过。API 5080 / Worker 5081 的 live/ready 与 Vite 5173 均返回 200，未停止演示或操作数据库。完整 74 项和真实浏览器/30 分钟证据仍引用原 P1.7 记录。
