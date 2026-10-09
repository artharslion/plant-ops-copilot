# P2.2 离线采样修正区间契约

2026-10-09，基线 `5e1c164`，分支 `codex/p2-opcua`。本独立点完成 **Application 纯函数的修正快照激活/关闭与历史保护**，不能作为真实 SDK 修正参数生产者或 Worker 生命周期的验收。计划：[实施边界](plans/2026-10-09-p2-sampling-transitions.md)。

## 契约与可验收行为

`SamplingRevision` 的点/映射/版本、生效时刻、模式及三个修正参数均须显式传入。没有默认值，不接受 `OpcUaMappingInput` / `OpcUaConnectionInput` 的请求配置，也不读取当前点间隔填补缺失历史。当前所有新输入来自固定离线 fixture；此类型本身不能证明值来自服务器。后续 producer 必须从服务结果读取修正值，并验证模式能力、身份/授权和生效屏障。

`SamplingProfileTransitions.Activate` 校验同一点历史后按时间返回所有实体的独立副本。仅关闭末尾开放区间并追加新 ID 快照；已经闭合的区间、原 ID、映射身份、版本、修正值保持不变。`Close` 仅关闭末尾开放区间，不追加伪造快照。区间为 `[EffectiveFrom, EffectiveTo)`；关闭与激活可相邻，也可保留空窗，空窗继续使覆盖率未知。

拒绝跨点、空身份、非正版本、重复历史 ID、倒置/重叠区间、中间开放区间、同一映射版本倒退，以及新生效时刻覆盖已有历史。不同映射版本独立；重连可在同一版本下使用新的显式修正值；切回旧映射不能回退其已记录版本。失败与成功都不修改输入；修改输出副本也不会改写输入历史。

Periodic 修正采样间隔必须正且有限；Changes 可以显式为零，但非有限或负值拒绝。发布间隔正且有限，队列 1–10000（现有 DB 约束）。真实修正值不按 UI 请求范围截断；测试包括 500.5ms / 750.25ms / 队列10000。这里只证明区间契约支持这些值，不证明服务器实际产生这些参数或这些参数符合当前容量预算。新 producer 应在超预算时拒绝启用，不能篡改修正值。

已有历史的可空发布间隔/队列和 Unknown 模式保持原样，不回填。周期切换 2s→1s 在同一分钟的期望值为45；后续再切换不会改变该旧分钟的结果。变化通知或停止后的空窗继续返回 unknown coverage；不把连接存活或通知计数当作周期连续性。

## 验证与数据保护

先写测试：缺少 `SamplingRevision` 导致真实编译失败，证据 `artifacts/p2/tests/p2-sampling-red.log`，没有生成或伪造 red TRX。首批28项全过（`p2-sampling-targeted.trx`），补入未知/乱序旧历史、历史身份/版本/参数与修正范围边界后 **37通过/0失败/0跳过**（`p2-sampling-targeted-final.trx`）。固定时钟与参数均为离线 fixture，没有协议会话。

解决方案 locked restore 成功，build **0 warning/error**（`p2-sampling-restore.log` / `p2-sampling-build.log`）。常规后端回归 **206通过/0失败/0跳过**，1分16秒，含本轮37项（`p2-sampling-regression.trx`）。显式 filter 排除 opt-in `SmallBackupRestoreTests`，本轮没有再次备份/恢复实验，不能把此前实验算作本轮测试。无模型/依赖/UI改动，未重复EF/前端检查。

只为常规DB/API回归新增并保留 `plantops_tests.p2_sampling_finaltests_20261009` 与 helper 随机 `p17_test_*` / `p17_isolated_*` schema；正确资料变量为 `PLANTOPS_TEST_DOCUMENT_ROOT=artifacts/p2/documents/sampling_finaltests_20261009`。测试初始化仅在新schema运行；新增纯函数没有数据库写入。顺序构建/测试，没有并行写工作区或停止无关进程。

只读前后保护结果完全相同：业务 `plantops` 仍只有 `20261003071706_InitialAssetDirectory`；原 `p17_ui_20261005` raw **1,609,600行**且无SamplingProfiles；原 `p2_capacity_20261008_accepted`仍 **12,000行**、Enabled来源0。此处仅核对行数/表存在/迁移/启用状态，不冒充原数据全文哈希校验。全部原schema、卷、资料、旧Outbox、失败证据保留；无push/PR。证据与SHA-256清单在 `artifacts/p2/tests/p2-sampling-manifest.json`，原日志保留本地。

## 范围与后续

没有改 EF 模型/迁移、API/UI/依赖或任何业务配置；纯函数不写数据库，不注册到 DI/Worker。后续持久化不能直接无锁读历史再写回：必须另验事务、同点并发、当前映射/版本核对、重试幂等、数据与配置屏障，以及迟到重算的一致性。关闭区间不等于已登记断线 gap，不保证旧回调停止。

P2.2 尚缺 Worker 授权分页 Browse、逐映射业务状态持久化与真实修正参数 producer/生命周期。M07/M08 只有离线区间语义增量，真实协议/业务生命周期要求仍开放。G0五项正式峰值、满量与完整 P2 均未通过；`BudgetComplete` / `FitsSnapshot` / `CapacityPassed`仍 false。本点不批准更大备份或60,480,000行试验。
