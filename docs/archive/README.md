# 历史资料索引

本目录收录旧版设计、已结束的实施计划与截至各轮的验收证据。当前状态和剩余任务见 [开发计划](../../ROADMAP.md)，新调查输出见 [v3 契约](../INVESTIGATION_RESPONSE_V3.md)。需要核对历史问题时，只打开对应记录。

## 基线与设计

- [旧路线图与详细验收基线](ROADMAP_20261029.md)：保留 P5–P8 详细指标、故障矩阵与原轮次记录；任务状态已经过期。当前路线图引用的验收细节仍须按对应任务核对，不据旧能力表重新建设已有功能。
- [旧 AI 设计](AI_DESIGN_LEGACY.md)：早期 v1 输出示例、格式修复约定和后续设计；仅供旧版本兼容或设计历史核对。
- [旧证据与工单质量记录](AI_INVESTIGATION_QUALITY_LEGACY.md)：v7/v8、固定摘要、预取及旧工作台验证；不作为 v3 输出或当前部署契约。
- [P6.2 已交付增量计划](plans/2026-10-29-p6-2-on-demand-evidence.md)：实现轮次已结束，未完成验收见当前路线图。
- [v3 已结束实施计划](plans/2026-10-30-investigation-response-v3.md)：实现与验证步骤已执行，真实验收限制继续保留。

## 调查与用量

- [P6 v3 迭代与近期补验](P6_V3_FOLLOW_UP_20261119.md)：各版本验证记录、11 月 18 日复验以及 11 月 19 日工具预算核查。
- [10 月 29 日真实模型](P6_1_REAL_VALIDATION_20261029.md) · [10 月 30 日续验](P6_1_REAL_VALIDATION_20261030.md) · [v3 初版实测](INVESTIGATION_V3_REAL_VALIDATION_20261030.md) · [11 月 3 日起的受控验收](P6_REAL_AGENT_ACCEPTANCE_20261103.md)。
- [逐点可信度](P6_POINT_TRUST_VALIDATION.md) · [原始观测升温](P6_HEATING_VALIDATION.md) · [按需取证](P6_2_ON_DEMAND_VALIDATION.md) · [当前发现目标增量](P6_CURRENT_FINDING_SUBJECTS.md) · [P8.1 运行版本与用量](P8_1_RUN_RECORD_VALIDATION.md)。
- [AI 工程清理评审](AI_ENGINEERING_CLEANUP.md) · [11 月 8 日暂停检查点](P6_PAUSED_PROGRESS_20261108.md) · [模型接入探测](AI_PROVIDER_PROBE_20261009.md) · [AI 模式切换的首轮验证](AI_SETTINGS_VALIDATION.md)。

## 采集、工单与页面

- [受控 OPC UA 初版](OPCUA_RUNTIME_VALIDATION.md) · [旧三点 spike](P5_3_MINIMAL_SCENARIO.md)。后续协议复验从 [开发计划 P5](../../ROADMAP.md#3-p5opc-ua-持续采集与实时数据底座) 定位。
- [调查转 Draft 工单](P4_WORK_ORDER_VALIDATION.md) · [追加调查备注](P4_NOTE_VALIDATION.md) · [工作台交互增量](UI_INTERACTION_VALIDATION.md)。

常规搜索通过仓库根目录的 `.ignore` 跳过本目录。需要搜索时，可对指定文件运行 `rg --no-ignore -n <关键词> docs/archive/<文件>`。历史请求额度与部署结论只适用于原轮次。`artifacts/` 下的运行证据为本机文件，按报告所列路径核对。
