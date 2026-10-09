# P2.2 配置与标准化增量验收

2026-10-09 增量：[跨修正区间入库与规则消费](P2_INGEST_INTERVALS_VALIDATION.md) 在全新schema中验收实际可能空窗范围、原因/IsPossible与同批次规则重置；真实修正参数producer/生命周期与完整P2.2仍开放。

2026-10-09 增量：[离线周期缺口证据范围](P2_GAP_EVIDENCE_VALIDATION.md) 返回触发历史片段与快照身份；这是推断证据，不能直接作为物理损失起止。入库跨区间与规则消费仍须独立验收。

2026-10-09 增量：[离线raw趋势连续性](P2_RAW_CONTINUITY_VALIDATION.md) 修复相邻样本与查询边缘的跨周期判定及显式损失边界；入库跨区间推断、未知模式表达和真实生命周期仍须独立验收。

2026-10-09 增量：[离线分钟趋势区间消费](P2_TREND_SAMPLING_VALIDATION.md) 修复 TrendBuilder 分钟路径的集中观测漏报、跨慢周期误报和显式损失边界；当前 API 分钟分支读取聚合结果，本点不改变该分支或真实采样生命周期。

2026-10-09 增量：[离线采样修正区间契约](P2_SAMPLING_TRANSITIONS_VALIDATION.md) 新增纯函数的显式修正快照激活/关闭、历史副本保护与未知空窗；没有接入持久化服务或真实 SDK/Worker 生命周期，完整 P2.2 仍开放。

2026-10-06。基线 `9dcc65a`，本地分支 `codex/p2-opcua`。P2.1 固定 SDK/许可与最小安全协议实验通过；本轮推进 **P2.2 可离线验证的配置、标准化、历史采样语义**。P2.2 尚未完整验收，P2.3–P2.5 未完成。G0 未通过，应用持续 OPC UA 接入未启用。

## 已实现

- 新增 Integrations.OpcUa，固定 Client 1.5.378.182，只被测试引用，未注入 Worker；Domain/Application 不依赖 SDK。节点按 Namespace URI + Numeric/String/Guid 解析；Opaque、非规范整数/Guid 拒绝，不以显示名匹配。
- 有限标量数值转换，Int64/UInt64 超出 2^53 拒绝，字符串/布尔/数组不隐式转值；保留原始类型和有界原值。单位仅 mm/s↔m/s、degC↔K 及同单位恒等系数，未知量纲/系数不能保存映射。
- 保留完整状态码/符号，质量与 Overflow 独立。Good+DataValue Overflow 不推进 LastGood/pending、不参与可信分钟统计；事务登记 ServerOverflow，重复通知不重复登记，丢弃数量保持未知，图表不绘制为正常可信点。
- SDK MinValue/1601 零时刻作为缺失，Source→Server→Receive fallback；事件键包括映射/版本、优先时间、原始类型/值、完整状态。有源时间时忽略变化的 server 时间；无可信时间用 Envelope ID，重试复用原不可变样本。未来 >5s 不可信，现有保留/未来分区保护继续生效。
- 原授权/防伪/版本 API 扩展 OPC UA 停用配置：Kind 不可改；只接受部署批准 endpoint 引用，响应无 endpoint 地址。Namespace 和 String prefix / 精确 `Type:Identifier` Nodes 范围由部署约束，Admin UI 不能扩大。服务端拒绝 OPC UA 启用，UI 不能绕过门槛。
- 新 SamplingProfiles 保存生效区间、映射版本和修正参数；分钟 CoverageRatio 可空、增加 SamplingMode。没有猜测旧历史或批量回填修正参数。周期按各区间计算；变化通知、缺少/重叠区间覆盖率未知。样本数量足够但时间集中仍不能证明连续。
- 入库间隔缺口、原始趋势、分钟统计读取历史修正区间；不把变化通知静默称作物理丢失；迟到重算不采用当前 OPC UA 请求间隔。原 Simulation 固定间隔保护保留。
- UI 增加停用来源、URI 节点、请求间隔/队列/模式与覆盖率未知显示；HTTP 仍为权威读取。

## 验证与保护

最小失败测试：Good+Overflow 可信判定先失败；真实 DB 上修正 2 秒间隔和变化通知静默误登记缺口两例先失败，再修正。

**后端最终 114 通过、0 失败、0 跳过**，含 P1 目录/权限/防伪/事务/工单/资料/Outbox/管道回归。结果 `artifacts/p2/tests/p2-final.trx`。构建 0 warning/error，锁定恢复通过，EF 无 pending model changes。前端 format:check/lint/build 和 1 项去重测试通过；SignalR 两条既有 Rollup 注释提示不阻断构建。

新增重点：三种 identifier/URI 重排，精度/类型/原码/时间/去重，批准的停用配置，修正区间/迟到重算/未知覆盖率，Overflow 幂等/pending/LastGood/分钟。

非空升级从 ReviewIngestionAndOutboxSafety 到 OpcUaSamplingProfiles、OpcUaCoverageSemantics：raw、工单、文档对象一致，Outbox 数量不变，原分钟 coverage=1/Periodic，独立资料文件字节一致、0 pending；没有回填虚构历史配置。

主测试 schema `plantops_tests.p2_contracts_20261006`；旧测试 helper 另建随机 p17_test_/p17_isolated_（均是新增，非原验收 schema），升级另建 `p2_upgrade_<GUID>`。全部保留。资料 `artifacts/p2/documents/contracts_20261006`，升级证据为该目录 p2_upgrade_*.json。固定测试时钟不冒充持续运行。

独立 5180 API 生成 OpenAPI；5193 固定数据浏览器组件预览确认 URI/模式表单、不可改来源类型和停用且未勾选的启用控件。仅组件呈现验证，非登录/协议业务闭环。独立进程和预览页已关闭。

只在新增测试 schema 应用迁移。只读复核：plantops 仍只有 InitialAssetDirectory；原 p17_ui_20261005 中无 SamplingProfiles。没有停止其他实例、删库/卷/既有 schema 或清理原 Outbox。

## 未关闭项

2026-10-08 节点元数据独立增量见 [协议节点校验](P2_NODE_METADATA_VALIDATION.md)：Variable/Scalar/访问位与实际 Value 读取/内置数值 DataType/EngineeringUnits 和逐映射错误已实现，在普通/重排隔离会话各 17 个结果验证。后端回归增至 141 项全过。此方法未注入 Worker，不代表授权 Browse 或完整 P2.2 已完成。

1. **G0**：七天 60,480,000 raw、八天滚动、90 天分钟、并发维护/五客户端、WAL/联合备份恢复峰值与 30% 余量仍无证据，见 [容量调查](P2_CAPACITY_VALIDATION.md)。不能启动持续真实接入或称 P2 完成。
2. **P2.2 协议侧**：Worker 授权有界 Browse 与逐映射业务状态持久化未实现；节点元数据校验已完成独立隔离验证，simulator 已发布 EngineeringUnits，尚未接入业务 Worker。采样区间激活/关闭纯函数已完成离线验收；真实修正参数生产者、事务/并发持久化、生命周期区间关闭和配置切换屏障仍缺；DB 验证使用显式固定测试快照。
3. **P2.3**：业务 IMeasurementSource Adapter、SDK 回调与损失桥接、总字节预算、证书/host/URI/discovery 地址校验、Worker-only CredentialRef、服务调用记录器和独立只读用户名账号未验收。没有注册可绕过 G0 的 Source。
4. **P2.4**：证书批准/撤销 UI/API、完整证书故障矩阵、退避重连、Republish/Transfer 分支、恢复快照、断线即时 gap 等待真实协议验证；通用损失账本不能替代协议事件验收。
5. **P2.5**：权威连接健康/UI/指标、两来源 Golden Path、100 点 30 分钟/五客户端与完整浏览器验收未执行。

后续从 G0 满量验收环境/预算开始，满足门槛后完成 P2.2 协议校验和 P2.3→P2.4→P2.5。业务库升级仍需单独授权，本次本地提交不构成演示部署授权。
